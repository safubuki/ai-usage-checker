using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIUsageChecker.Models;
using AIUsageChecker.Services;

namespace AIUsageChecker.ViewModels;

public class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan CliStatusRefreshInterval = TimeSpan.FromHours(6);

    private readonly CliManagerService _cliManager;
    private readonly UsageFetcherService _usageFetcher;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _autoRefreshTimer;

    private AppSettings _settings;
    private ObservableCollection<AiUsageItem> _items = new();
    private AiUsageItem? _selectedItem;
    private bool _isDetailOpen;
    private bool _isAlwaysOnTop = true;
    private bool _isDocked;
    private bool _isRefreshing;
    private string _statusText = "準備完了";
    private readonly List<LogEntry> _allLogEntries = new();
    private ObservableCollection<string> _consoleLogs = new();
    private bool _isShowAllLogs;
    private DateTime _lastCliStatusRefreshUtc = DateTime.MinValue;

    public ObservableCollection<AiUsageItem> Items
    {
        get => _items;
        set => SetProperty(ref _items, value);
    }

    public AiUsageItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                UpdateVisibleLogs();
            }
        }
    }

    public bool IsShowAllLogs
    {
        get => _isShowAllLogs;
        set
        {
            if (SetProperty(ref _isShowAllLogs, value))
            {
                UpdateVisibleLogs();
            }
        }
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set
        {
            if (SetProperty(ref _isDetailOpen, value))
            {
                if (value)
                {
                    _isShowAllLogs = false;
                    OnPropertyChanged(nameof(IsShowAllLogs));
                    UpdateVisibleLogs();
                }
            }
        }
    }

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => SetProperty(ref _isAlwaysOnTop, value);
    }

    public bool IsDocked
    {
        get => _isDocked;
        set => SetProperty(ref _isDocked, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public AppSettings Settings => _settings;

    public void SaveDockedTop(double top)
    {
        _settings.DockedTop = top;
        _settingsService.SaveSettings(_settings);
    }

    public void SaveMonitorDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return;
        if (string.Equals(_settings.MonitorDeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return;

        _settings.MonitorDeviceName = deviceName;
        _settingsService.SaveSettings(_settings);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ObservableCollection<string> ConsoleLogs
    {
        get => _consoleLogs;
        set => SetProperty(ref _consoleLogs, value);
    }

    // コマンド
    public RelayCommand RefreshAllCommand { get; }
    public RelayCommand<AiUsageItem> SelectItemCommand { get; }
    public RelayCommand CloseDetailCommand { get; }
    public RelayCommand<AiUsageItem> InstallCliCommand { get; }
    public RelayCommand<AiUsageItem> LoginCliCommand { get; }
    public RelayCommand<AiUsageItem> UpdateCliCommand { get; }
    public RelayCommand<AiUsageItem> CheckCliCommand { get; }
    public RelayCommand<AiUsageItem> MoveLeftCommand { get; }
    public RelayCommand<AiUsageItem> MoveRightCommand { get; }
    public RelayCommand ToggleDockCommand { get; }
    public RelayCommand ToggleAlwaysOnTopCommand { get; }
    public RelayCommand ShowFilteredLogsCommand { get; }
    public RelayCommand ShowAllLogsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }

    public MainViewModel()
    {
        _cliManager = new CliManagerService();
        _usageFetcher = new UsageFetcherService();
        _settingsService = new SettingsService();

        _cliManager.LogOutputReceived += AddLog;
        _usageFetcher.LogOutputReceived += AddLog;

        _settings = _settingsService.LoadSettings();
        _isAlwaysOnTop = _settings.IsAlwaysOnTop;
        _isDocked = false; // 起動時は常にメイン画面を表示

        // コマンド初期化
        RefreshAllCommand = new RelayCommand(async () => await RefreshAllAsync());
        SelectItemCommand = new RelayCommand<AiUsageItem>(item =>
        {
            if (item != null)
            {
                OpenDetail(item);
            }
        });
        CloseDetailCommand = new RelayCommand(CloseDetail);
        InstallCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null && !item.CliInfo.IsBusy)
            {
                await _cliManager.InstallCliAsync(item.CliInfo);
                await _usageFetcher.FetchUsageAsync(item);
            }
        });
        LoginCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null && !item.CliInfo.IsBusy)
            {
                var success = await _cliManager.LoginCliAsync(item.CliInfo);
                if (success)
                {
                    await RefreshAllAsync(isSilent: false);
                }
                else
                {
                    await _usageFetcher.FetchUsageAsync(item);
                }
            }
        });
        UpdateCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null && !item.CliInfo.IsBusy)
            {
                await _cliManager.UpdateCliAsync(item.CliInfo);
                await _usageFetcher.FetchUsageAsync(item);
            }
        });
        CheckCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null && !item.CliInfo.IsBusy)
            {
                await _cliManager.CheckCliStatusAsync(item.CliInfo);
                await _usageFetcher.FetchUsageAsync(item);
            }
        });

        MoveLeftCommand = new RelayCommand<AiUsageItem>(MoveItemLeft);
        MoveRightCommand = new RelayCommand<AiUsageItem>(MoveItemRight);

        ToggleDockCommand = new RelayCommand(() =>
        {
            IsDocked = !IsDocked;
        });

        ToggleAlwaysOnTopCommand = new RelayCommand(() =>
        {
            IsAlwaysOnTop = !IsAlwaysOnTop;
            _settings.IsAlwaysOnTop = IsAlwaysOnTop;
            _settingsService.SaveSettings(_settings);
        });

        ShowFilteredLogsCommand = new RelayCommand(() =>
        {
            IsShowAllLogs = false;
            UpdateVisibleLogs();
        });
        ShowAllLogsCommand = new RelayCommand(() =>
        {
            IsShowAllLogs = true;
            UpdateVisibleLogs();
        });
        ClearLogsCommand = new RelayCommand(ClearLogs);

        // 自動更新タイマー (5分ごと: バックグラウンドでサイレント更新)
        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.AutoRefreshMinutes))
        };
        _autoRefreshTimer.Tick += async (_, _) =>
        {
            var shouldCheckCliStatus = DateTime.UtcNow - _lastCliStatusRefreshUtc >= CliStatusRefreshInterval;
            await RefreshAllAsync(isSilent: true, checkCliStatus: shouldCheckCliStatus);
        };
        _autoRefreshTimer.Start();

        // データの初期化
        InitializeItems();
    }

    public void OpenDetail(AiUsageItem item)
    {
        if (item == null) return;
        item.DismissGlow();
        SelectedItem = item;
        IsShowAllLogs = false; // 詳細を開くときはそのサービス専用ログを初期表示
        IsDetailOpen = true;
        UpdateVisibleLogs();
    }

    public void CloseDetail()
    {
        IsDetailOpen = false;
        SelectedItem = null;
    }

    private void AddLog(string log)
    {
        var serviceType = DetectServiceType(log);
        var entry = new LogEntry(log, serviceType);

        App.Current?.Dispatcher.Invoke(() =>
        {
            _allLogEntries.Insert(0, entry);
            if (_allLogEntries.Count > 500)
            {
                _allLogEntries.RemoveAt(_allLogEntries.Count - 1);
            }

            if (ShouldDisplayLog(entry))
            {
                ConsoleLogs.Insert(0, entry.Message);
                if (ConsoleLogs.Count > 200)
                {
                    ConsoleLogs.RemoveAt(ConsoleLogs.Count - 1);
                }
            }
        });
    }

    private void ClearLogs()
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            if (IsShowAllLogs || SelectedItem == null)
            {
                _allLogEntries.Clear();
            }
            else
            {
                _allLogEntries.RemoveAll(x => x.ServiceType == SelectedItem.ServiceType);
            }
            UpdateVisibleLogs();
        });
    }

    private void UpdateVisibleLogs()
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            ConsoleLogs.Clear();
            var matching = _allLogEntries
                .Where(ShouldDisplayLog)
                .Take(200);

            foreach (var entry in matching)
            {
                ConsoleLogs.Add(entry.Message);
            }
        });
    }

    private bool ShouldDisplayLog(LogEntry entry)
    {
        if (IsShowAllLogs) return true;
        if (SelectedItem == null) return true;
        return entry.ServiceType == SelectedItem.ServiceType;
    }

    private static AiServiceType? DetectServiceType(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;

        if (log.Contains("[Antigravity", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("agy", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("[Gemini", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            return AiServiceType.Gemini;

        if (log.Contains("[Codex", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("[GPT", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
            return AiServiceType.GPT;

        if (log.Contains("[Claude", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
            return AiServiceType.Claude;

        if (log.Contains("[Grok", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Grok", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("xai", StringComparison.OrdinalIgnoreCase))
            return AiServiceType.Grok;

        if (log.Contains("[Copilot", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Copilot", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("gh api", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("gh auth", StringComparison.OrdinalIgnoreCase))
            return AiServiceType.Copilot;

        return null;
    }

    private void InitializeItems()
    {
        Items = InitialCatalogFactory.CreateInitialServices(_settings.DisplayOrder);

        // 起動時に非同期でCLI確認とUsage更新
        _ = RefreshAllAsync(isSilent: false);
    }

    public async Task RefreshAllAsync(bool isSilent = false, bool checkCliStatus = true)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;

        // サイレント定期更新の場合はヘッダーの「取得中...」表示も出さず、ユーザーに更新を意識させない
        if (!isSilent)
        {
            StatusText = "利用状況を取得中...";
        }

        try
        {
            // 既存カードのバッジを「確認中...」に戻さない（裏側で取得し、完了時にパッと切り替える）

            List<Task<(AiUsageItem Item, CliInfo TempCli)>>? cliTasks = null;
            if (checkCliStatus)
            {
                // CLIの探索・バージョン確認は起動時、手動更新時、および6時間ごとに限定する。
                // 画面上のCliInfoを直接更新しないため、個別完了時にバッジがバラバラ変わらない。
                cliTasks = Items.Select(async item =>
                {
                    var tempCli = new CliInfo
                    {
                        Name = item.CliInfo.Name,
                        CommandName = item.CliInfo.CommandName,
                        PackageName = item.CliInfo.PackageName,
                        UsageCheckCommand = item.CliInfo.UsageCheckCommand,
                        IsSubscribed = item.CliInfo.IsSubscribed
                    };
                    await _cliManager.CheckCliStatusAsync(tempCli);
                    return (Item: item, TempCli: tempCli);
                }).ToList();
            }

            // 利用状況（クォータ）は従来どおり5分ごとに取得する。
            var usageTask = _usageFetcher.FetchAllRawDataAsync();

            if (cliTasks != null)
            {
                await Task.WhenAll(usageTask, Task.WhenAll(cliTasks));
                var cliResults = await Task.WhenAll(cliTasks);
                foreach (var res in cliResults)
                {
                    // CLI状態を先に反映し、その後にプラン・利用枠状態を適用する。
                    res.Item.CliInfo.CopyFrom(res.TempCli);
                    _usageFetcher.ApplyUsage(res.Item);
                }

                _lastCliStatusRefreshUtc = DateTime.UtcNow;
            }
            else
            {
                await usageTask;
                foreach (var item in Items)
                {
                    _usageFetcher.ApplyUsage(item);
                }
            }

            StatusText = $"同期完了: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            if (!isSilent)
            {
                StatusText = $"更新エラー: {ex.Message}";
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void MoveItemLeft(AiUsageItem? item)
    {
        if (item == null) return;
        int index = Items.IndexOf(item);
        if (index > 0)
        {
            Items.Move(index, index - 1);
            SaveCurrentOrder();
        }
    }

    private void MoveItemRight(AiUsageItem? item)
    {
        if (item == null) return;
        int index = Items.IndexOf(item);
        if (index < Items.Count - 1)
        {
            Items.Move(index, index + 1);
            SaveCurrentOrder();
        }
    }

    public void MoveItem(int oldIndex, int newIndex)
    {
        if (oldIndex >= 0 && oldIndex < Items.Count && newIndex >= 0 && newIndex < Items.Count && oldIndex != newIndex)
        {
            Items.Move(oldIndex, newIndex);
            SaveCurrentOrder();
        }
    }

    private void SaveCurrentOrder()
    {
        _settings.DisplayOrder = Items.Select(x => x.DisplayName).ToList();
        _settingsService.SaveSettings(_settings);
    }
}

public class LogEntry
{
    public string Message { get; }
    public AiServiceType? ServiceType { get; }

    public LogEntry(string message, AiServiceType? serviceType)
    {
        Message = message;
        ServiceType = serviceType;
    }
}
