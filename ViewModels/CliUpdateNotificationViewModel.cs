using System.Windows.Input;
using System.Windows.Threading;
using AIUsageChecker.Models;

namespace AIUsageChecker.ViewModels;

public class CliUpdateNotificationViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Func<AiUsageItem, Task<bool>> _updateCli;
    private readonly Func<bool> _canUpdate;
    private readonly Action<string> _reportStatus;
    private readonly DispatcherTimer _resultTimer;
    private List<UpdateOffer> _offers = new();
    private bool _isOpen;
    private bool _isResult;
    private bool _allUpdatesSucceeded;
    private bool _isUpdating;
    private string _message = "";
    private string _details = "";

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsResult
    {
        get => _isResult;
        private set
        {
            if (SetProperty(ref _isResult, value)) OnPropertyChanged(nameof(IsSuccessfulResult));
        }
    }

    public bool IsSuccessfulResult => IsResult && _allUpdatesSucceeded;

    public bool IsUpdating
    {
        get => _isUpdating;
        private set => SetProperty(ref _isUpdating, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    public RelayCommand UpdateAllCommand { get; }
    public RelayCommand CloseCommand { get; }

    internal CliUpdateNotificationViewModel(AppSettings settings, Action saveSettings,
        Func<AiUsageItem, Task<bool>> updateCli, Func<bool> canUpdate, Action<string> reportStatus)
    {
        _settings = settings;
        _settings.AnnouncedCliUpdates ??= new();
        _saveSettings = saveSettings;
        _updateCli = updateCli;
        _canUpdate = canUpdate;
        _reportStatus = reportStatus;
        UpdateAllCommand = new RelayCommand(async () => await UpdateAllAsync(), CanUpdate);
        CloseCommand = new RelayCommand(Close);
        _resultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _resultTimer.Tick += (_, _) => Close();
    }

    internal void Refresh(IEnumerable<AiUsageItem> items, bool includeAnnounced = false)
    {
        if (IsUpdating || IsResult) return;

        // 表示中の案内は維持する。閉じた案内は、同じCLI・更新先バージョンなら復活させない。
        var currentKeys = _offers.Select(offer => offer.Key).ToHashSet();
        _offers = items.Where(item => IsUpdateAvailable(item.CliInfo))
            .Select(item => new UpdateOffer(item, GetKey(item.CliInfo)))
            .Where(offer => includeAnnounced || currentKeys.Contains(offer.Key) || !_settings.AnnouncedCliUpdates.Contains(offer.Key))
            .DistinctBy(offer => offer.Key).ToList();
        Message = $"{_offers.Count}件のCLIを更新できます";
        Details = string.Join(Environment.NewLine,
            _offers.Select(offer => $"{offer.Item.CliInfo.Name}: v{offer.Item.CliInfo.LatestVersion}"));
        IsOpen = _offers.Count > 0;
        CommandManager.InvalidateRequerySuggested();
    }

    internal void ShowAgain(IEnumerable<AiUsageItem> items)
    {
        if (IsUpdating || !_canUpdate()) return;
        _resultTimer.Stop();
        IsResult = false;
        // 手動操作でだけ履歴のフィルターを外す。自動通知の案内済み履歴はそのまま保つ。
        Refresh(items, includeAnnounced: true);
    }

    internal static bool HasAvailableUpdates(IEnumerable<AiUsageItem> items) =>
        items.Any(item => IsUpdateAvailable(item.CliInfo));

    internal void MarkDisplayed()
    {
        if (!IsOpen || IsResult) return;

        bool changed = false;
        foreach (var offer in _offers)
            changed |= _settings.AnnouncedCliUpdates.Add(offer.Key);
        if (changed) _saveSettings();
    }

    private bool CanUpdate() =>
        IsOpen && !IsResult && !IsUpdating && _canUpdate() && _offers.Any(IsOfferAvailable);

    private static bool IsUpdateAvailable(CliInfo cli) =>
        // 契約の有無にかかわらず、カードに「更新あり」と表示するCLIを対象にする。
        cli.IsInstalled && cli.IsLoggedIn && !cli.HasUsageError &&
        cli.HasUpdate && !cli.IsBusy && !cli.IsUpdating &&
        !string.IsNullOrWhiteSpace(cli.CommandName) && !string.IsNullOrWhiteSpace(cli.LatestVersion);

    private static string GetKey(CliInfo cli) =>
        $"{cli.CommandName.Trim().ToLowerInvariant()}@{cli.LatestVersion.Trim().TrimStart('v', 'V')}";

    private static bool IsOfferAvailable(UpdateOffer offer) =>
        IsUpdateAvailable(offer.Item.CliInfo) && GetKey(offer.Item.CliInfo) == offer.Key;

    private void Close()
    {
        // クリックによる操作も、ユーザーが案内を見たことを確定できるタイミング。
        MarkDisplayed();
        _resultTimer.Stop();
        IsOpen = false;
        IsResult = false;
        _offers.Clear();
        CommandManager.InvalidateRequerySuggested();
    }

    internal async Task UpdateAllAsync()
    {
        if (!CanUpdate()) return;
        var targets = _offers.Where(IsOfferAvailable).Select(offer => offer.Item).ToList();
        Close();
        IsUpdating = true;
        CommandManager.InvalidateRequerySuggested();
        int succeeded = 0;
        var failed = new List<string>();
        try
        {
            // グローバルインストール処理同士が競合しないように、CLIは順番に更新する。
            for (int index = 0; index < targets.Count; index++)
            {
                var item = targets[index];
                item.CliInfo.IsUpdating = true;
                item.CliInfo.IsBusy = true;
                _reportStatus($"CLI更新中: {index + 1}/{targets.Count}");
                try
                {
                    if (await _updateCli(item)) succeeded++;
                    else failed.Add(item.CliInfo.Name);
                }
                catch (Exception ex)
                {
                    item.CliInfo.StatusMessage = $"アップデート失敗: {ex.Message}";
                    failed.Add(item.CliInfo.Name);
                }
                finally
                {
                    item.CliInfo.IsBusy = false;
                    item.CliInfo.IsUpdating = false;
                }
            }

            _allUpdatesSucceeded = failed.Count == 0;
            Message = _allUpdatesSucceeded
                ? $"{succeeded}件のCLIを更新しました"
                : $"CLI更新: {succeeded}件成功・{failed.Count}件失敗";
            Details = _allUpdatesSucceeded
                ? "すべてのCLI更新が完了しました。"
                : $"更新失敗: {string.Join("、", failed)}。各カードの詳細から再試行できます。";
            IsResult = true;
            IsOpen = true;
            _reportStatus(Message);
            _resultTimer.Start();
        }
        finally
        {
            IsUpdating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private sealed record UpdateOffer(AiUsageItem Item, string Key);
}
