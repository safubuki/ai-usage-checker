using System.Collections.ObjectModel;
using AIUsageChecker.ViewModels;

namespace AIUsageChecker.Models;

public enum AntigravityCardMode
{
    Argon = 0,
    Gemini = 1,
    External = 2
}

public class AiUsageItem : ViewModelBase
{
    private AiServiceType _serviceType;
    private string _displayName = "";
    private string _subTitle = "";
    private string _usageSourceText = "";
    private CliInfo _cliInfo = new();
    private UsageLimitInfo _primaryLimit = new();
    private UsageLimitInfo? _secondaryLimit;
    private ObservableCollection<UsageLimitInfo> _allLimits = new();
    private readonly ObservableCollection<UsageLimitGroup> _limitGroups = new();
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
    private UsageLimitInfo? _argonLimit;
    private UsageLimitInfo? _externalFiveHourLimit;
    private UsageLimitInfo? _externalWeeklyLimit;
    private bool _hasExternalLimits;
    private AntigravityCardMode _cardMode = AntigravityCardMode.Argon;
    private RelayCommand? _cycleCardModeCommand;

    public AiUsageItem()
    {
        _limitGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLimitGroups));
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
        if (!IsDataLoaded || !CliInfo.IsSubscribed || !CliInfo.IsLoggedIn || CardPrimaryLimit.CustomDisplayPercentText == "--")
        {
            IsWeeklyExhausted = false;
            IsFiveHourExhausted = false;
            IsRecoveredGlowActive = false;
            return;
        }

        UpdateWarningFlagsForCurrentMode();

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

    private void UpdateWarningFlagsForCurrentMode()
    {
        if (!IsDataLoaded || !CliInfo.IsSubscribed || !CliInfo.IsLoggedIn || CardPrimaryLimit.CustomDisplayPercentText == "--")
        {
            IsWeeklyExhausted = false;
            IsFiveHourExhausted = false;
            return;
        }

        var primary = CardPrimaryLimit;
        var secondary = CardSecondaryLimit;

        // 週次制限の枯渇判定 (CardSecondaryLimit または 週次制限/ArgonのCardPrimaryLimit)
        bool weeklyExhausted = false;
        if (secondary != null && secondary.Title.Contains("週次"))
        {
            weeklyExhausted = secondary.RemainingPercent <= 0.0;
        }
        else if (primary.Title.Contains("週次") || primary.Title.Contains("Argon", StringComparison.OrdinalIgnoreCase) || ServiceType == AiServiceType.Grok)
        {
            weeklyExhausted = primary.RemainingPercent <= 0.0;
        }
        IsWeeklyExhausted = weeklyExhausted;

        // 5時間制限の有無および枯渇判定
        bool hasFiveHour = false;
        bool fiveHourExhausted = false;
        if (primary.Title.Contains("5時間"))
        {
            hasFiveHour = true;
            fiveHourExhausted = primary.RemainingPercent <= 0.0;
        }
        else if (secondary != null && secondary.Title.Contains("5時間"))
        {
            hasFiveHour = true;
            fiveHourExhausted = secondary.RemainingPercent <= 0.0;
        }
        HasFiveHourLimit = hasFiveHour;
        IsFiveHourExhausted = fiveHourExhausted;
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

    public string UsageSourceText
    {
        get => _usageSourceText;
        set
        {
            if (SetProperty(ref _usageSourceText, value)) OnPropertyChanged(nameof(HasUsageSource));
        }
    }

    public bool HasUsageSource => !string.IsNullOrEmpty(UsageSourceText);

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
    /// 詳細画面でモデルグループごとに枠で囲んで表示するためのコレクション (Gemini 4 Argon / Gemini Model / Claude &amp; GPT Model 等)。
    /// </summary>
    public ObservableCollection<UsageLimitGroup> LimitGroups => _limitGroups;

    public bool HasLimitGroups => _limitGroups.Count > 0;

    /// <summary>
    /// Gemini 4 Argon の週次制限枠。Antigravityカードの既定(1枠)表示および詳細表示で使用する。
    /// </summary>
    public UsageLimitInfo? ArgonLimit
    {
        get => _argonLimit;
        set
        {
            if (SetProperty(ref _argonLimit, value))
            {
                OnPropertyChanged(nameof(HasArgonLimit));
                OnPropertyChanged(nameof(HasCardModeSwitch));
                NotifyCardLimitsChanged();
            }
        }
    }

    public bool HasArgonLimit => _argonLimit != null;

    /// <summary>
    /// 外部モデル(Claude / GPT)の5時間制限。Antigravityカードの切替表示で使用する。
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
    /// 外部モデル(Claude / GPT)の週次制限。Antigravityカードの切替表示で使用する。
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
    /// 外部モデル枠(5時間＋週次)が揃っているか。
    /// </summary>
    public bool HasExternalLimits
    {
        get => _hasExternalLimits;
        set
        {
            if (SetProperty(ref _hasExternalLimits, value))
            {
                OnPropertyChanged(nameof(HasCardModeSwitch));
                NotifyCardLimitsChanged();
            }
        }
    }

    /// <summary>
    /// カード上で表示対象切替ボタンを出せるか (Argon枠または外部モデル枠がある場合)。
    /// </summary>
    public bool HasCardModeSwitch => HasArgonLimit || HasExternalLimits;

    /// <summary>
    /// カードで選択されている表示モード (既定: Argon -> Gemini -> External の順で切替)。
    /// </summary>
    public AntigravityCardMode CardMode
    {
        get => _cardMode;
        set
        {
            if (SetProperty(ref _cardMode, value))
            {
                NotifyCardLimitsChanged();
                UpdateWarningFlagsForCurrentMode();
            }
        }
    }

    /// <summary>
    /// 利用可能なデータに応じて実際にカードへ適用される表示モード。
    /// </summary>
    public AntigravityCardMode EffectiveCardMode => _cardMode switch
    {
        AntigravityCardMode.Argon when HasArgonLimit => AntigravityCardMode.Argon,
        AntigravityCardMode.External when HasExternalLimits => AntigravityCardMode.External,
        AntigravityCardMode.Gemini => AntigravityCardMode.Gemini,
        _ => HasArgonLimit ? AntigravityCardMode.Argon : AntigravityCardMode.Gemini
    };

    /// <summary>
    /// カードに外部モデル枠を表示中か (互換用プロパティ)。
    /// </summary>
    public bool IsShowingExternal
    {
        get => EffectiveCardMode == AntigravityCardMode.External;
        set => CardMode = value
            ? AntigravityCardMode.External
            : (HasArgonLimit ? AntigravityCardMode.Argon : AntigravityCardMode.Gemini);
    }

    public RelayCommand CycleCardModeCommand => _cycleCardModeCommand ??= new RelayCommand(CycleCardMode);

    /// <summary>
    /// Gemini 4 Argon -> Gemini (5h+週次) -> Claude &amp; GPT (5h+週次) の順で表示対象を切り替える。
    /// </summary>
    public void CycleCardMode()
    {
        var modes = new System.Collections.Generic.List<AntigravityCardMode>();
        if (HasArgonLimit) modes.Add(AntigravityCardMode.Argon);
        modes.Add(AntigravityCardMode.Gemini);
        if (HasExternalLimits) modes.Add(AntigravityCardMode.External);

        if (modes.Count <= 1) return;

        var current = EffectiveCardMode;
        var index = modes.IndexOf(current);
        var nextIndex = (index + 1) % modes.Count;
        CardMode = modes[nextIndex];
    }

    /// <summary>カード上段(または単一枠)に表示する制限 (切替状態に応じて Argon / Gemini / 外部モデル)</summary>
    public UsageLimitInfo CardPrimaryLimit => EffectiveCardMode switch
    {
        AntigravityCardMode.Argon => ArgonLimit!,
        AntigravityCardMode.External => (ExternalFiveHourLimit ?? ExternalWeeklyLimit)!,
        _ => PrimaryLimit
    };

    /// <summary>カード下段に表示する制限 (Argon時はnullで単一表示、Gemini / 外部モデル時は週次制限)</summary>
    public UsageLimitInfo? CardSecondaryLimit => EffectiveCardMode switch
    {
        AntigravityCardMode.Argon => null,
        AntigravityCardMode.External => ExternalFiveHourLimit != null ? ExternalWeeklyLimit : null,
        _ => SecondaryLimit
    };

    /// <summary>切替ボタンに表示する現在の表示対象</summary>
    public string CardModeLabel => EffectiveCardMode switch
    {
        AntigravityCardMode.Argon => "Argon",
        AntigravityCardMode.External => "外部",
        _ => "Gemini"
    };

    private void NotifyCardLimitsChanged()
    {
        OnPropertyChanged(nameof(EffectiveCardMode));
        OnPropertyChanged(nameof(IsShowingExternal));
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
