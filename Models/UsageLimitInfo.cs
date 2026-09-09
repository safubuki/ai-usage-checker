using AIUsageChecker.ViewModels;

namespace AIUsageChecker.Models;

public class UsageLimitInfo : ViewModelBase
{
    private string _title = "";
    private string _limitDescription = "";
    private double _remainingPercent = 100.0;
    private double _usedAmount;
    private double _totalLimit;
    private string _unit = "%";
    private string _resetTimeText = "";
    private bool _isWarning;
    private bool _isCritical;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string LimitDescription
    {
        get => _limitDescription;
        set => SetProperty(ref _limitDescription, value);
    }

    public double RemainingPercent
    {
        get => _remainingPercent;
        set
        {
            if (SetProperty(ref _remainingPercent, Math.Clamp(value, 0.0, 100.0)))
            {
                OnPropertyChanged(nameof(RemainingPercentInt));
                OnPropertyChanged(nameof(DisplayPercentString));
                UpdateStatusFlags();
            }
        }
    }

    public int RemainingPercentInt => (int)Math.Round(RemainingPercent);

    private string? _customDisplayPercentText;
    public string? CustomDisplayPercentText
    {
        get => _customDisplayPercentText;
        set
        {
            if (SetProperty(ref _customDisplayPercentText, value))
            {
                OnPropertyChanged(nameof(DisplayPercentString));
            }
        }
    }

    public string DisplayPercentString => CustomDisplayPercentText ?? $"{RemainingPercentInt}%";

    public double UsedAmount
    {
        get => _usedAmount;
        set => SetProperty(ref _usedAmount, value);
    }

    public double TotalLimit
    {
        get => _totalLimit;
        set => SetProperty(ref _totalLimit, value);
    }

    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    public string ResetTimeText
    {
        get => _resetTimeText;
        set => SetProperty(ref _resetTimeText, value);
    }

    public bool IsWarning
    {
        get => _isWarning;
        set => SetProperty(ref _isWarning, value);
    }

    public bool IsCritical
    {
        get => _isCritical;
        set => SetProperty(ref _isCritical, value);
    }

    private void UpdateStatusFlags()
    {
        IsCritical = RemainingPercent <= 20.0;
        IsWarning = RemainingPercent > 20.0 && RemainingPercent <= 40.0;
    }

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(RemainingPercent));
        OnPropertyChanged(nameof(RemainingPercentInt));
        OnPropertyChanged(nameof(DisplayPercentString));
        OnPropertyChanged(nameof(ResetTimeText));
        OnPropertyChanged(nameof(Title));
        UpdateStatusFlags();
    }
}
