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
    private ObservableCollection<string> _consoleLogs = new();

    public ObservableCollection<AiUsageItem> Items
    {
        get => _items;
        set => SetProperty(ref _items, value);
    }

    public AiUsageItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set => SetProperty(ref _isDetailOpen, value);
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
    public RelayCommand<AiUsageItem> UpdateCliCommand { get; }
    public RelayCommand<AiUsageItem> CheckCliCommand { get; }
    public RelayCommand<AiUsageItem> MoveLeftCommand { get; }
    public RelayCommand<AiUsageItem> MoveRightCommand { get; }
    public RelayCommand ToggleDockCommand { get; }
    public RelayCommand ToggleAlwaysOnTopCommand { get; }
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
                SelectedItem = item;
                item.DismissGlow();
                IsDetailOpen = true;
            }
        });
        CloseDetailCommand = new RelayCommand(() => IsDetailOpen = false);
        InstallCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null)
            {
                await _cliManager.InstallCliAsync(item.CliInfo);
                await _usageFetcher.FetchUsageAsync(item);
            }
        });
        UpdateCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null)
            {
                await _cliManager.UpdateCliAsync(item.CliInfo);
                await _usageFetcher.FetchUsageAsync(item);
            }
        });
        CheckCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null)
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

        ClearLogsCommand = new RelayCommand(() => ConsoleLogs.Clear());

        // 自動更新タイマー (5分ごと)
        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.AutoRefreshMinutes))
        };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        _autoRefreshTimer.Start();

        // データの初期化
        InitializeItems();
    }

    private void AddLog(string log)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            ConsoleLogs.Insert(0, log);
            if (ConsoleLogs.Count > 200)
            {
                ConsoleLogs.RemoveAt(ConsoleLogs.Count - 1);
            }
        });
    }

    private void InitializeItems()
    {
        // 5つの標準AIサービスを作成
        var allServices = new List<AiUsageItem>
        {
            new()
            {
                ServiceType = AiServiceType.GPT,
                DisplayName = "GPT",
                SubTitle = "OpenAI / Codex",
                IconGlyph = "⚡",
                CliInfo = new CliInfo
                {
                    Name = "Codex",
                    CommandName = "codex",
                    PackageName = "@openai/codex",
                    UsageCheckCommand = "/status"
                },
                PrimaryLimit = new UsageLimitInfo { Title = "5時間制限", RemainingPercent = 0.0, LimitDescription = "5h limit (resets 18:21)", ResetTimeText = "18:21 リセット (枯渇)" },
                SecondaryLimit = new UsageLimitInfo { Title = "週次制限", RemainingPercent = 52.0, LimitDescription = "Weekly limit (resets 13:15 on 15 Sep)", ResetTimeText = "09/15 13:15 リセット" }
            },
            new()
            {
                ServiceType = AiServiceType.Claude,
                DisplayName = "Claude",
                SubTitle = "Anthropic",
                IconGlyph = "✦",
                CliInfo = new CliInfo
                {
                    Name = "Claude",
                    CommandName = "claude",
                    PackageName = "@anthropic-ai/claude-code",
                    UsageCheckCommand = "/usage (/cost, /stats)",
                    IsSubscribed = false,
                    IsInstalled = false,
                    StatusMessage = "未契約 (プラン未加入)"
                },
                PrimaryLimit = new UsageLimitInfo { Title = "契約状況", RemainingPercent = 0.0, CustomDisplayPercentText = "--", LimitDescription = "Anthropic Claude 未契約", ResetTimeText = "未契約" },
                SecondaryLimit = null
            },
            new()
            {
                ServiceType = AiServiceType.Gemini,
                DisplayName = "Gemini",
                SubTitle = "Google DeepMind",
                IconGlyph = "✧",
                CliInfo = new CliInfo
                {
                    Name = "Gemini",
                    CommandName = "gemini",
                    PackageName = "@google/gemini-cli",
                    UsageCheckCommand = "/usage (agy status)"
                },
                PrimaryLimit = new UsageLimitInfo { Title = "5時間制限", RemainingPercent = 81.0, LimitDescription = "Five Hour Limit Remaining", ResetTimeText = $"{DateTime.Now.AddHours(4).AddMinutes(5):HH:mm} リセット" },
                SecondaryLimit = new UsageLimitInfo { Title = "週次制限", RemainingPercent = 74.0, LimitDescription = "Weekly Limit Remaining", ResetTimeText = "09/11 23:46 リセット" }
            },
            new()
            {
                ServiceType = AiServiceType.Grok,
                DisplayName = "Grok",
                SubTitle = "xAI / SuperGrok",
                IconGlyph = "𝕏",
                CliInfo = new CliInfo
                {
                    Name = "Grok",
                    CommandName = "grok",
                    PackageName = "grok",
                    UsageCheckCommand = "/usage"
                },
                PrimaryLimit = new UsageLimitInfo { Title = "週次制限", RemainingPercent = 100.0, LimitDescription = "Weekly limit (SuperGrok)", ResetTimeText = "09/15 21:53 リセット" },
                SecondaryLimit = null // 週次制限のみ
            },
            new()
            {
                ServiceType = AiServiceType.Copilot,
                DisplayName = "Copilot",
                SubTitle = "GitHub / Copilot Pro",
                IconGlyph = "🐙",
                CliInfo = new CliInfo
                {
                    Name = "Copilot",
                    CommandName = "copilot",
                    PackageName = "@github/copilot",
                    UsageCheckCommand = "/usage (/limits)",
                    IsSubscribed = true,
                    StatusMessage = "プラン: Copilot Pro (稼働中)"
                },
                PrimaryLimit = new UsageLimitInfo { Title = "プレミアム要求", RemainingPercent = 98.0, LimitDescription = "2% 使用済み (残 294 / 300)", ResetTimeText = "10/01 09:00 リセット" },
                SecondaryLimit = null // 単一枠制度
            }
        };

        // 保存された順序に従って並べ替え
        var orderList = _settings.DisplayOrder ?? new List<string>();
        var ordered = new List<AiUsageItem>();

        foreach (var name in orderList)
        {
            var match = allServices.FirstOrDefault(x => x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null && !ordered.Contains(match))
            {
                ordered.Add(match);
            }
        }

        // 残りを追加
        foreach (var item in allServices)
        {
            if (!ordered.Contains(item))
            {
                ordered.Add(item);
            }
        }

        Items = new ObservableCollection<AiUsageItem>(ordered);

        // 起動時に非同期でCLI確認とUsage更新
        _ = RefreshAllAsync();
    }

    public async Task RefreshAllAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        StatusText = "利用状況を取得中...";

        try
        {
            // 1. LanguageServer からリアルタイム生クォータを取得して各サービスに反映
            await _usageFetcher.FetchAllUsagesAsync(Items);

            // 2. CLI のステータス・バージョンを並列チェック
            var cliTasks = Items
                .Select(item => _cliManager.CheckCliStatusAsync(item.CliInfo));

            await Task.WhenAll(cliTasks);

            StatusText = $"同期完了: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"更新エラー: {ex.Message}";
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
