using AIUsageChecker.ViewModels;

namespace AIUsageChecker.Models;

public class CliInfo : ViewModelBase
{
    private string _name = "";
    private string _commandName = "";
    private string _packageName = "";
    private bool _isInstalled;
    private string _installedVersion = "";
    private string _latestVersion = "";
    private bool _hasUpdate;
    private bool _isBusy = true;
    private string _statusMessage = "";
    private string _executablePath = "";
    private string _usageCheckCommand = "";
    private bool _isSubscribed = true;
    private bool _isLoggedIn = true;

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set
        {
            if (SetProperty(ref _isLoggedIn, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public bool IsSubscribed
    {
        get => _isSubscribed;
        set
        {
            if (SetProperty(ref _isSubscribed, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public string UsageCheckCommand
    {
        get => _usageCheckCommand;
        set => SetProperty(ref _usageCheckCommand, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string CommandName
    {
        get => _commandName;
        set => SetProperty(ref _commandName, value);
    }

    public string PackageName
    {
        get => _packageName;
        set => SetProperty(ref _packageName, value);
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        set => SetProperty(ref _installedVersion, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        set => SetProperty(ref _latestVersion, value);
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (SetProperty(ref _hasUpdate, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    public string StatusBadgeText
    {
        get
        {
            if (IsBusy) return "確認中...";
            if (!IsSubscribed) return "未契約";
            if (!IsInstalled) return "未導入";
            if (!IsLoggedIn) return "要ログイン";
            if (HasUpdate) return "更新あり";
            return "最新";
        }
    }

    public void CopyFrom(CliInfo other)
    {
        IsInstalled = other.IsInstalled;
        IsLoggedIn = other.IsLoggedIn;
        InstalledVersion = other.InstalledVersion;
        LatestVersion = other.LatestVersion;
        HasUpdate = other.HasUpdate;
        ExecutablePath = other.ExecutablePath;
        if (IsSubscribed)
        {
            StatusMessage = other.StatusMessage;
        }
        IsBusy = false;
    }
}
