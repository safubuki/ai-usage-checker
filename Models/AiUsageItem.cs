using System.Collections.ObjectModel;
using AIUsageChecker.ViewModels;

namespace AIUsageChecker.Models;

public class AiUsageItem : ViewModelBase
{
    private AiServiceType _serviceType;
    private string _displayName = "";
    private string _subTitle = "";
    private CliInfo _cliInfo = new();
    private UsageLimitInfo _primaryLimit = new();
    private UsageLimitInfo? _secondaryLimit;
    private ObservableCollection<UsageLimitInfo> _allLimits = new();
    private readonly ObservableCollection<UsageLimitInfo> _usageBreakdown = new();
    private readonly ObservableCollection<ResetCreditInfo> _resetCredits = new();
    private int _resetCreditsAvailableCount;
    private string _earliestResetCreditExpireText = "";
    private int _displayOrder;
    private bool _isSelected;
    private DateTime _lastRefreshed = DateTime.Now;
    private bool _isDataLoaded;
    private bool _isWeeklyExhausted;
    private bool _isFiveHourExhausted;
    private bool _hasFiveHourLimit;
    private bool _isRecoveredGlowActive;
    private double? _prevPrimaryPercent;
    private double? _prevSecondaryPercent;
    private UsageLimitInfo? _externalFiveHourLimit;
    private UsageLimitInfo? _externalWeeklyLimit;
    private bool _hasExternalLimits;
    private bool _isShowingExternal;

    public AiUsageItem()
    {
        _usageBreakdown.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUsageBreakdown));
        _resetCredits.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResetCredits));
            OnPropertyChanged(nameof(ResetCreditsBadgeText));
        };
    }

    public bool IsDataLoaded
    {
        get => _isDataLoaded;
        set => SetProperty(ref _isDataLoaded, value);
    }

    public bool IsWeeklyExhausted
    {
        get => _isWeeklyExhausted;
        set => SetProperty(ref _isWeeklyExhausted, value);
    }

    public bool IsFiveHourExhausted
    {
        get => _isFiveHourExhausted;
        set => SetProperty(ref _isFiveHourExhausted, value);
    }

    public bool HasFiveHourLimit
    {
        get => _hasFiveHourLimit;
        set => SetProperty(ref _hasFiveHourLimit, value);
    }

    public bool IsRecoveredGlowActive
    {
        get => _isRecoveredGlowActive;
        set => SetProperty(ref _isRecoveredGlowActive, value);
    }

    public void DismissGlow()
    {
        if (IsRecoveredGlowActive)
        {
            IsRecoveredGlowActive = false;
        }
    }

    public void UpdateStatusAndCheckRecovery()
    {
        // データロード前、未契約時、未ログイン時、または表示が "--" の場合は警告枠（赤枠・黄色枠）や回復緑枠を表示しない
        if (!IsDataLoaded || !CliInfo.IsSubscribed || !CliInfo.IsLoggedIn || PrimaryLimit.CustomDisplayPercentText == "--")
        {
            IsWeeklyExhausted = false;
            IsFiveHourExhausted = false;
            IsRecoveredGlowActive = false;
            return;
        }

        // 週次制限の枯渇判定 (SecondaryLimit または 週次制限のPrimaryLimit)
        bool weeklyExhausted = false;
        if (SecondaryLimit != null && SecondaryLimit.Title.Contains("週次"))
        {
            weeklyExhausted = SecondaryLimit.RemainingPercent <= 0.0;
        }
        else if (PrimaryLimit.Title.Contains("週次") || ServiceType == AiServiceType.Grok)
        {
            weeklyExhausted = PrimaryLimit.RemainingPercent <= 0.0;
        }
        IsWeeklyExhausted = weeklyExhausted;

        // 5時間制限の有無および枯渇判定
        bool hasFiveHour = false;
        bool fiveHourExhausted = false;
        if (PrimaryLimit.Title.Contains("5時間"))
        {
            hasFiveHour = true;
            fiveHourExhausted = PrimaryLimit.RemainingPercent <= 0.0;
        }
        else if (SecondaryLimit != null && SecondaryLimit.Title.Contains("5時間"))
        {
            hasFiveHour = true;
            fiveHourExhausted = SecondaryLimit.RemainingPercent <= 0.0;
        }
        HasFiveHourLimit = hasFiveHour;
        IsFiveHourExhausted = fiveHourExhausted;

        // 0% -> 100% 回復時のホタル点滅判定 (週次制限枯渇時はAIが利用不可のため回復緑枠は出さない)
        double currPrimary = PrimaryLimit.RemainingPercent;
        double? currSecondary = SecondaryLimit?.RemainingPercent;

        bool primaryRecovered = _prevPrimaryPercent.HasValue && _prevPrimaryPercent.Value <= 0.0 && currPrimary >= 100.0;
        bool secondaryRecovered = _prevSecondaryPercent.HasValue && _prevSecondaryPercent.Value <= 0.0 && (currSecondary.HasValue && currSecondary.Value >= 100.0);

        if ((primaryRecovered || secondaryRecovered) && !IsWeeklyExhausted)
        {
            IsRecoveredGlowActive = true;
        }
        else if (IsRecoveredGlowActive)
        {
            // 週次制限枯渇時、または使用によって100%未満になった場合は点滅解除
            if (IsWeeklyExhausted || currPrimary < 100.0 || (currSecondary.HasValue && currSecondary.Value < 100.0))
            {
                IsRecoveredGlowActive = false;
            }
        }

        // 前回のパーセントを保存
        _prevPrimaryPercent = currPrimary;
        _prevSecondaryPercent = currSecondary;
    }

    public AiServiceType ServiceType
    {
        get => _serviceType;
        set => SetProperty(ref _serviceType, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string SubTitle
    {
        get => _subTitle;
        set => SetProperty(ref _subTitle, value);
    }

    public CliInfo CliInfo
    {
        get => _cliInfo;
        set => SetProperty(ref _cliInfo, value);
    }

    public UsageLimitInfo PrimaryLimit
    {
        get => _primaryLimit;
        set
        {
            if (SetProperty(ref _primaryLimit, value)) OnPropertyChanged(nameof(CardPrimaryLimit));
        }
    }

    public UsageLimitInfo? SecondaryLimit
    {
        get => _secondaryLimit;
        set
        {
            if (SetProperty(ref _secondaryLimit, value)) OnPropertyChanged(nameof(CardSecondaryLimit));
        }
    }

    public ObservableCollection<UsageLimitInfo> AllLimits
    {
        get => _allLimits;
        set => SetProperty(ref _allLimits, value);
    }

    /// <summary>
    /// 外部モデル(Claude / GPT)の5時間制限。Geminiカードの切替表示で使用する。
    /// </summary>
    public UsageLimitInfo? ExternalFiveHourLimit
    {
        get => _externalFiveHourLimit;
        set
        {
            if (SetProperty(ref _externalFiveHourLimit, value)) NotifyCardLimitsChanged();
        }
    }

    /// <summary>
    /// 外部モデル(Claude / GPT)の週次制限。Geminiカードの切替表示で使用する。
    /// </summary>
    public UsageLimitInfo? ExternalWeeklyLimit
    {
        get => _externalWeeklyLimit;
        set
        {
            if (SetProperty(ref _externalWeeklyLimit, value)) NotifyCardLimitsChanged();
        }
    }

    /// <summary>
    /// 外部モデル枠(5時間＋週次)が揃っており、カード上で切替ボタンを出せるか。
    /// </summary>
    public bool HasExternalLimits
    {
        get => _hasExternalLimits;
        set
        {
            if (SetProperty(ref _hasExternalLimits, value)) NotifyCardLimitsChanged();
        }
    }

    /// <summary>
    /// カードに外部モデル枠を表示中か (false = Gemini枠、既定)。切替ボタンと双方向バインドする。
    /// </summary>
    public bool IsShowingExternal
    {
        get => _isShowingExternal;
        set
        {
            if (SetProperty(ref _isShowingExternal, value)) NotifyCardLimitsChanged();
        }
    }

    private bool UseExternalOnCard => IsShowingExternal && HasExternalLimits;

    /// <summary>カード上段に表示する制限 (切替状態に応じて Gemini / 外部モデル)</summary>
    public UsageLimitInfo CardPrimaryLimit => UseExternalOnCard ? ExternalFiveHourLimit! : PrimaryLimit;

    /// <summary>カード下段に表示する制限 (切替状態に応じて Gemini / 外部モデル)</summary>
    public UsageLimitInfo? CardSecondaryLimit => UseExternalOnCard ? ExternalWeeklyLimit : SecondaryLimit;

    /// <summary>切替ボタンに表示する現在の表示対象</summary>
    public string CardModeLabel => UseExternalOnCard ? "外部" : "Gemini";

    private void NotifyCardLimitsChanged()
    {
        OnPropertyChanged(nameof(CardPrimaryLimit));
        OnPropertyChanged(nameof(CardSecondaryLimit));
        OnPropertyChanged(nameof(CardModeLabel));
    }

    /// <summary>
    /// 主制限を消費した機能別の内訳。独立した利用制限ではないため、AllLimits とは分けて表示する。
    /// </summary>
    public ObservableCollection<UsageLimitInfo> UsageBreakdown => _usageBreakdown;

    public bool HasUsageBreakdown => UsageBreakdown.Count > 0;

    /// <summary>
    /// リセット権（リセットチケット）一覧
    /// </summary>
    public ObservableCollection<ResetCreditInfo> ResetCredits => _resetCredits;

    public int ResetCreditsAvailableCount
    {
        get => _resetCreditsAvailableCount;
        set
        {
            if (SetProperty(ref _resetCreditsAvailableCount, value))
            {
                OnPropertyChanged(nameof(HasResetCredits));
                OnPropertyChanged(nameof(ResetCreditsBadgeText));
            }
        }
    }

    public bool HasResetCredits => ResetCreditsAvailableCount > 0 || ResetCredits.Count > 0;

    public string ResetCreditsBadgeText => ResetCreditsAvailableCount > 0 
        ? $"{ResetCreditsAvailableCount}件 保有" 
        : "0件";

    public string EarliestResetCreditExpireText
    {
        get => _earliestResetCreditExpireText;
        set => SetProperty(ref _earliestResetCreditExpireText, value);
    }

    /// <summary>
    /// リセット権（リセットチケット）機能をサポートしているプロバイダかどうか (GPT/Codex等)
    /// </summary>
    public bool SupportsResetCredits => ServiceType == AiServiceType.GPT;

    public int DisplayOrder
    {
        get => _displayOrder;
        set => SetProperty(ref _displayOrder, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public DateTime LastRefreshed
    {
        get => _lastRefreshed;
        set => SetProperty(ref _lastRefreshed, value);
    }

    public string ThemeAccentColor
    {
        get => ServiceType switch
        {
            AiServiceType.GPT => "#10A37F",
            AiServiceType.Claude => "#DE8B59",
            AiServiceType.Gemini => "#74AFFF",
            AiServiceType.Grok => "#F3F4F6",
            AiServiceType.Copilot => "#D5D9DF",
            _ => "#22C55E"
        };
    }
}
