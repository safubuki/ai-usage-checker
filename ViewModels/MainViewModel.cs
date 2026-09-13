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
    public RelayCommand<AiUsageItem> LoginCliCommand { get; }
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
        LoginCliCommand = new RelayCommand<AiUsageItem>(async item =>
        {
            if (item != null)
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

        // 自動更新タイマー (5分ごと: バックグラウンドでサイレント更新)
        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.AutoRefreshMinutes))
        };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAllAsync(isSilent: true);
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
        Items = InitialCatalogFactory.CreateInitialServices(_settings.DisplayOrder);

        // 起動時に非同期でCLI確認とUsage更新
        _ = RefreshAllAsync(isSilent: false);
    }

    public async Task RefreshAllAsync(bool isSilent = false)
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

            // 1. 各カードのCLIチェック用の一時CliInfoを用意して並列実行
            // （画面上のCliInfoを直接更新しないため、個別完了時にバッジがバラバラ変わらない）
            var cliTasks = Items.Select(async item =>
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

            // 2. 利用状況（クォータ）生データをバックグラウンドで並列取得
            var usageTask = _usageFetcher.FetchAllRawDataAsync();

            // 3. 利用状況取得と全CLIチェックが「すべて完了」するまで待機
            await Task.WhenAll(usageTask, Task.WhenAll(cliTasks));

            // 4. 【全て確認完了】この瞬間に全カードへ一斉にデータを反映する！
            var cliResults = await Task.WhenAll(cliTasks);
            foreach (var res in cliResults)
            {
                // クォータデータ反映（%・リング・リセット日時・枠線判定）
                _usageFetcher.ApplyUsage(res.Item);

                // CLIステータス反映（最新・未契約・未導入等のバッジ）
                res.Item.CliInfo.CopyFrom(res.TempCli);
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
