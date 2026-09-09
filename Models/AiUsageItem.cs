using System.Collections.ObjectModel;
using AIUsageChecker.ViewModels;

namespace AIUsageChecker.Models;

public class AiUsageItem : ViewModelBase
{
    private AiServiceType _serviceType;
    private string _displayName = "";
    private string _subTitle = "";
    private string _iconGlyph = "";
    private CliInfo _cliInfo = new();
    private UsageLimitInfo _primaryLimit = new();
    private UsageLimitInfo? _secondaryLimit;
    private ObservableCollection<UsageLimitInfo> _allLimits = new();
    private int _displayOrder;
    private bool _isSelected;
    private DateTime _lastRefreshed = DateTime.Now;
    private bool _isWeeklyExhausted;
    private bool _isRecoveredGlowActive;
    private double? _prevPrimaryPercent;
    private double? _prevSecondaryPercent;

    public bool IsWeeklyExhausted
    {
        get => _isWeeklyExhausted;
        set => SetProperty(ref _isWeeklyExhausted, value);
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

        // 0% -> 100% 回復時のホタル点滅判定
        double currPrimary = PrimaryLimit.RemainingPercent;
        double? currSecondary = SecondaryLimit?.RemainingPercent;

        bool primaryRecovered = _prevPrimaryPercent.HasValue && _prevPrimaryPercent.Value <= 0.0 && currPrimary >= 100.0;
        bool secondaryRecovered = _prevSecondaryPercent.HasValue && _prevSecondaryPercent.Value <= 0.0 && (currSecondary.HasValue && currSecondary.Value >= 100.0);

        if (primaryRecovered || secondaryRecovered)
        {
            IsRecoveredGlowActive = true;
        }
        else if (IsRecoveredGlowActive)
        {
            // 使用によって100%未満になった場合は点滅解除
            if (currPrimary < 100.0 || (currSecondary.HasValue && currSecondary.Value < 100.0))
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

    public string IconGlyph
    {
        get => _iconGlyph;
        set => SetProperty(ref _iconGlyph, value);
    }

    public CliInfo CliInfo
    {
        get => _cliInfo;
        set => SetProperty(ref _cliInfo, value);
    }

    public UsageLimitInfo PrimaryLimit
    {
        get => _primaryLimit;
        set => SetProperty(ref _primaryLimit, value);
    }

    public UsageLimitInfo? SecondaryLimit
    {
        get => _secondaryLimit;
        set => SetProperty(ref _secondaryLimit, value);
    }

    public ObservableCollection<UsageLimitInfo> AllLimits
    {
        get => _allLimits;
        set => SetProperty(ref _allLimits, value);
    }

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
            AiServiceType.GPT => "#10A37F",       // OpenAI Emerald
            AiServiceType.Claude => "#D97706",    // Claude Amber/Orange
            AiServiceType.Gemini => "#3B82F6",    // Gemini Blue/Violet
            AiServiceType.Grok => "#EC4899",      // Grok Pink/Neon
            AiServiceType.Copilot => "#8B5CF6",   // GitHub Copilot Purple
            _ => "#22C55E"
        };
    }
}
