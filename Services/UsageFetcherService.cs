using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIUsageChecker.Models;

namespace AIUsageChecker.Services;

public class UsageFetcherService
{
    private readonly AgyQuotaClient _agyClient = new();
    private readonly LanguageServerQuotaClient _quotaClient = new();
    private readonly CodexQuotaClient _codexClient = new();
    private readonly CopilotQuotaClient _copilotClient = new();
    private readonly ClaudeQuotaClient _claudeClient = new();
    private readonly GrokQuotaClient _grokClient = new();
    private readonly System.Threading.SemaphoreSlim _fetchLock = new(1, 1);
    private readonly string _cacheFilePath;
    private List<QuotaGroup>? _cachedGroups;
    private UsageDataSource _geminiQuotaSource;
    private bool _geminiFetchAttempted;
    private CodexQuotaData? _lastCodexData;
    private CopilotQuotaData? _lastCopilotData;
    private ClaudeQuotaData? _lastClaudeData;
    private GrokQuotaData? _lastGrokData;
    private bool _codexDataIsStale;
    private bool _claudeDataIsStale;
    private bool _geminiDataIsStale;
    private bool _copilotDataIsStale;
    private bool _grokDataIsStale;
    private DateTime? _codexLastSuccessAt;
    private DateTime? _claudeLastSuccessAt;
    private DateTime? _geminiLastSuccessAt;
    private DateTime? _copilotLastSuccessAt;
    private DateTime? _grokLastSuccessAt;

    public event Action<string>? LogOutputReceived;

    public UsageFetcherService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIUsageChecker", "quota_cache.json"))
    {
    }

    protected UsageFetcherService(string cacheFilePath)
    {
        _cacheFilePath = cacheFilePath;
        var dir = Path.GetDirectoryName(cacheFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _agyClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);
        _codexClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);
        _copilotClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);
        _claudeClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);
        _grokClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);

        LoadCache();
    }

    public async Task FetchAllUsagesAsync(IEnumerable<AiUsageItem> items)
    {
        await FetchAllRawDataAsync();
        foreach (var item in items)
        {
            ApplyUsage(item);
        }
    }

    public async Task FetchAllRawDataAsync()
    {
        await FetchRawDataAsync(null);
    }

    private async Task FetchRawDataAsync(AiServiceType? serviceType)
    {
        // 全体更新と詳細からの再取得が、認証情報の更新やキャッシュ保存で競合しないようにする。
        await _fetchLock.WaitAsync();
        try
        {
            await FetchRawDataCoreAsync(serviceType);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task FetchRawDataCoreAsync(AiServiceType? serviceType)
    {
        var targetName = serviceType?.ToString() ?? "全AI";
        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] [{targetName}] 利用状況の取得を開始...");

        // 各AIサービスの生データ取得を並列実行して高速化
        var codexTask = serviceType is null or AiServiceType.GPT ? Task.Run(async () =>
        {
            try
            {
                var data = await FetchCodexQuotaAsync();
                if (data != null && data.IsSuccess)
                {
                    _lastCodexData = data;
                    _codexDataIsStale = false;
                    _codexLastSuccessAt = DateTime.Now;
                }
                else if (data != null && data.IsAuthRequired)
                {
                    // 認証切れ・未ログインが判明した場合はキャッシュに頼らず未ログイン状態を設定
                    _lastCodexData = data;
                    _codexDataIsStale = false;
                }
                else
                {
                    _codexDataIsStale = true;
                }
            }
            catch (Exception ex)
            {
                _codexDataIsStale = true;
                LogOutputReceived?.Invoke($"[Codex] 取得例外: {ex.Message}");
            }
        }) : Task.CompletedTask;

        var claudeTask = serviceType is null or AiServiceType.Claude ? Task.Run(async () =>
        {
            try
            {
                var data = await FetchClaudeQuotaAsync();
                if (data.IsSuccess && data.IsSavedValue)
                {
                    // 利用量APIで取得できず Claude Code の保存値を使った場合は、古いデータとして保存時刻を表示する。
                    // 以前に利用量APIで取得した値の方が新しければ、そちらを表示し続ける。
                    bool hasNewerLiveData = _lastClaudeData?.IsSuccess == true && _lastClaudeData.IsSubscribed &&
                        !_lastClaudeData.IsSavedValue && _claudeLastSuccessAt > data.SavedAt;
                    if (!hasNewerLiveData)
                    {
                        _lastClaudeData = data;
                        _claudeLastSuccessAt = data.SavedAt ?? _claudeLastSuccessAt;
                    }
                    _claudeDataIsStale = true;
                }
                else if (data.IsSuccess)
                {
                    _lastClaudeData = data;
                    _claudeDataIsStale = false;
                    _claudeLastSuccessAt = DateTime.Now;
                }
                else if (data.IsAuthRequired)
                {
                    _lastClaudeData = data;
                    _claudeDataIsStale = false;
                }
                else
                {
                    if (_lastClaudeData?.IsSuccess == true)
                    {
                        _claudeDataIsStale = true;
                    }
                    else
                    {
                        _lastClaudeData = data;
                        _claudeDataIsStale = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _claudeDataIsStale = true;
                LogOutputReceived?.Invoke($"[Claude] 取得例外: {ex.Message}");
            }
        }) : Task.CompletedTask;

        var geminiTask = serviceType is null or AiServiceType.Gemini ? Task.Run(async () =>
        {
            _geminiFetchAttempted = true;
            try
            {
                var groups = await FetchGeminiQuotaAsync();

                if (HasUsableGeminiQuota(groups))
                {
                    _cachedGroups = groups;
                    _geminiDataIsStale = false;
                    _geminiLastSuccessAt = DateTime.Now;
                }
                else
                {
                    _geminiDataIsStale = true;
                }
            }
            catch (Exception ex)
            {
                _geminiDataIsStale = true;
                LogOutputReceived?.Invoke($"[Antigravity] クォータ取得例外: {ex.Message}");
            }
        }) : Task.CompletedTask;

        var copilotTask = serviceType is null or AiServiceType.Copilot ? Task.Run(async () =>
        {
            try
            {
                var data = await FetchCopilotQuotaAsync();
                if (data?.IsSuccess == true)
                {
                    _lastCopilotData = data;
                    _copilotDataIsStale = false;
                    _copilotLastSuccessAt = DateTime.Now;
                }
                else if (data?.IsAuthRequired == true)
                {
                    _lastCopilotData = data;
                    _copilotDataIsStale = false;
                }
                else
                {
                    _copilotDataIsStale = true;
                }
            }
            catch (Exception ex)
            {
                _copilotDataIsStale = true;
                LogOutputReceived?.Invoke($"[Copilot] 取得例外: {ex.Message}");
            }
        }) : Task.CompletedTask;

        var grokTask = serviceType is null or AiServiceType.Grok ? Task.Run(async () =>
        {
            try
            {
                var data = await FetchGrokQuotaAsync();
                if (data?.IsSuccess == true)
                {
                    _lastGrokData = data;
                    _grokDataIsStale = data.IsFromFallback;
                    if (data.IsFromFallback)
                    {
                        _grokLastSuccessAt ??= data.SourceTimestamp;
                    }
                    else
                    {
                        _grokLastSuccessAt = DateTime.Now;
                    }
                }
                else if (data?.IsAuthRequired == true)
                {
                    _lastGrokData = data;
                    _grokDataIsStale = false;
                }
                else
                {
                    _grokDataIsStale = true;
                }
            }
            catch (Exception ex)
            {
                _grokDataIsStale = true;
                LogOutputReceived?.Invoke($"[Grok] 取得例外: {ex.Message}");
            }
        }) : Task.CompletedTask;

        await Task.WhenAll(codexTask, claudeTask, geminiTask, copilotTask, grokTask);
        await Task.Run(SaveCache);
        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] [{targetName}] 利用状況の生データ取得完了。");
    }

    protected virtual Task<CodexQuotaData?> FetchCodexQuotaAsync() => _codexClient.FetchCodexQuotaAsync();
    protected virtual Task<ClaudeQuotaData> FetchClaudeQuotaAsync() => _claudeClient.FetchClaudeQuotaAsync();
    protected virtual Task<CopilotQuotaData?> FetchCopilotQuotaAsync() => _copilotClient.FetchCopilotQuotaAsync();
    protected virtual Task<GrokQuotaData?> FetchGrokQuotaAsync() => _grokClient.FetchGrokQuotaAsync();
    protected virtual Task<List<QuotaGroup>?> FetchAgyQuotaAsync() => _agyClient.FetchQuotaSummaryAsync();
    protected virtual Task<List<QuotaGroup>?> FetchIdeQuotaAsync() => _quotaClient.FetchQuotaSummaryAsync();

    protected virtual async Task<List<QuotaGroup>?> FetchGeminiQuotaAsync()
    {
        var groups = await FetchAgyQuotaAsync();
        if (HasUsableGeminiQuota(groups))
        {
            _geminiQuotaSource = UsageDataSource.Cli;
            return groups;
        }

        LogOutputReceived?.Invoke("[Antigravity] CLIより取得できなかったため、IDE言語サーバーをフォールバック確認します...");
        groups = await FetchIdeQuotaAsync();
        if (HasUsableGeminiQuota(groups))
        {
            _geminiQuotaSource = UsageDataSource.Ide;
            LogOutputReceived?.Invoke("[Antigravity] IDEから利用枠を取得しました（CLI取得失敗時の補助）。");
        }
        return groups;
    }

    public void ApplyUsage(AiUsageItem item)
    {
        var groups = _cachedGroups;
        bool hasUsageError = IsUsingStaleData(item.ServiceType);
        item.CliInfo.HasUsageError = hasUsageError;
        item.UsageSourceText = item.ServiceType == AiServiceType.Gemini
            ? GetGeminiUsageSourceText()
            : "";

        if (hasUsageError)
        {
            // 一時的な通信失敗では、直近の正常値を消さず取得時刻を明示する。
            if (item.ServiceType == AiServiceType.GPT && _lastCodexData?.IsSuccess == true)
            {
                ApplyGptQuota(item);
                MarkPreviousValue(item, _codexLastSuccessAt);
            }
            else if (item.ServiceType == AiServiceType.Claude && _lastClaudeData?.IsSuccess == true && _lastClaudeData.IsSubscribed)
            {
                ApplyClaudeQuota(item);
                MarkPreviousValue(item, _claudeLastSuccessAt);
            }
            else
            {
                ApplyUsageError(item);
            }
        }
        else
        {
            switch (item.ServiceType)
            {
                case AiServiceType.GPT:
                    ApplyGptQuota(item);
                    break;
                case AiServiceType.Gemini:
                    ApplyGeminiQuota(item, groups);
                    break;
                case AiServiceType.Claude:
                    ApplyClaudeQuota(item);
                    break;
                case AiServiceType.Grok:
                    ApplyGrokQuota(item);
                    break;
                case AiServiceType.Copilot:
                    ApplyCopilotQuota(item);
                    break;
                case AiServiceType.Settings:
                    ApplySettingsStatus(item);
                    break;
            }
        }

        item.IsDataLoaded = true;
        item.ArgonLimit?.RefreshDisplay();
        item.PrimaryLimit.RefreshDisplay();
        item.SecondaryLimit?.RefreshDisplay();
        item.ExternalFiveHourLimit?.RefreshDisplay();
        item.ExternalWeeklyLimit?.RefreshDisplay();
        item.UpdateStatusAndCheckRecovery();
        item.LastRefreshed = hasUsageError
            ? GetLastSuccessfulRefresh(item.ServiceType) ?? item.LastRefreshed
            : DateTime.Now;
    }

    private static void MarkPreviousValue(AiUsageItem item, DateTime? lastSuccessAt)
    {
        var lastSuccessText = lastSuccessAt?.ToString("MM/dd HH:mm") ?? "時刻不明";
        item.CliInfo.StatusMessage = $"取得失敗。前回取得 ({lastSuccessText}) の利用枠を表示中";
        item.PrimaryLimit.ResetTimeText = $"前回 {lastSuccessText} / {item.PrimaryLimit.ResetTimeText}";
        if (item.SecondaryLimit != null)
        {
            item.SecondaryLimit.ResetTimeText = $"前回 {lastSuccessText} / {item.SecondaryLimit.ResetTimeText}";
        }
    }

    private static void ApplyUsageError(AiUsageItem item)
    {
        if (item.CliInfo.IsInstalled && item.CliInfo.IsLoggedIn)
        {
            item.CliInfo.StatusMessage = "利用枠の取得エラー (次回更新で再試行)";
        }

        item.ArgonLimit = null;
        item.ExternalFiveHourLimit = null;
        item.ExternalWeeklyLimit = null;
        item.HasExternalLimits = false;

        ResetLimitForError(item.PrimaryLimit);
        if (item.SecondaryLimit != null)
        {
            ResetLimitForError(item.SecondaryLimit);
        }

        item.AllLimits.Clear();
        item.LimitGroups.Clear();
        item.AllLimits.Add(item.PrimaryLimit);
        if (item.SecondaryLimit != null)
        {
            item.AllLimits.Add(item.SecondaryLimit);
        }
    }

    private static void ResetLimitForError(UsageLimitInfo limit)
    {
        limit.LimitDescription = "取得エラー";
        limit.RemainingPercent = 0.0;
        limit.CustomDisplayPercentText = "--";
        limit.ResetTimeText = "再試行待ち";
    }

    public async Task FetchUsageAsync(AiUsageItem item)
    {
        await FetchRawDataAsync(item.ServiceType);
        ApplyUsage(item);
    }

    private void ApplyGptQuota(AiUsageItem item)
    {
        if (_lastCodexData != null && _lastCodexData.IsSuccess)
        {
            if (!_codexDataIsStale) item.CliInfo.IsLoggedIn = true;
            // 契約状況はサーバー応答の plan_type で判定する。無料プランでも利用枠があれば表示する。
            item.CliInfo.IsSubscribed = _lastCodexData.IsSubscribed;
            item.CliInfo.StatusMessage = _lastCodexData.IsSubscribed
                ? $"プラン: {_lastCodexData.PlanType.ToUpper()} (稼働中)"
                : $"未契約 (無料プラン: {_lastCodexData.PlanType.ToUpper()})";

            item.AllLimits.Clear();

            // 1. 5時間制限と週次制限の両方がある場合 (Plus契約等)
            if (_lastCodexData.HasFiveHourLimit && _lastCodexData.HasWeeklyLimit)
            {
                item.PrimaryLimit.Title = "5時間制限";
                item.PrimaryLimit.LimitDescription = "5h limit (直近5時間ローリング)";
                item.PrimaryLimit.RemainingPercent = _lastCodexData.FiveHourRemainingPercent;
                item.PrimaryLimit.ResetTimeText = _lastCodexData.FiveHourResetText;
                item.PrimaryLimit.CustomDisplayPercentText = null;

                if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
                item.SecondaryLimit.Title = "週次制限";
                item.SecondaryLimit.LimitDescription = "Weekly limit (7日間上限)";
                item.SecondaryLimit.RemainingPercent = _lastCodexData.WeeklyRemainingPercent;
                item.SecondaryLimit.ResetTimeText = _lastCodexData.WeeklyResetText;
                item.SecondaryLimit.CustomDisplayPercentText = null;

                item.AllLimits.Add(item.PrimaryLimit);
                item.AllLimits.Add(item.SecondaryLimit);
            }
            // 2. 週次制限のみの場合 (Pro契約等で短期制限がないプラン)
            else if (_lastCodexData.HasWeeklyLimit && !_lastCodexData.HasFiveHourLimit)
            {
                item.PrimaryLimit.Title = "週次制限";
                item.PrimaryLimit.LimitDescription = $"Weekly limit ({_lastCodexData.PlanType.ToUpper()}プラン上限)";
                item.PrimaryLimit.RemainingPercent = _lastCodexData.WeeklyRemainingPercent;
                item.PrimaryLimit.ResetTimeText = _lastCodexData.WeeklyResetText;
                item.PrimaryLimit.CustomDisplayPercentText = null;

                item.SecondaryLimit = null; // 単一枠表示に自動切り替え

                item.AllLimits.Add(item.PrimaryLimit);
            }
            // 3. 5時間制限のみの場合
            else if (_lastCodexData.HasFiveHourLimit && !_lastCodexData.HasWeeklyLimit)
            {
                item.PrimaryLimit.Title = "5時間制限";
                item.PrimaryLimit.LimitDescription = "5h limit (ローリング制限)";
                item.PrimaryLimit.RemainingPercent = _lastCodexData.FiveHourRemainingPercent;
                item.PrimaryLimit.ResetTimeText = _lastCodexData.FiveHourResetText;
                item.PrimaryLimit.CustomDisplayPercentText = null;

                item.SecondaryLimit = null; // 単一枠表示に自動切り替え

                item.AllLimits.Add(item.PrimaryLimit);
            }
            else
            {
                // 万一両方検出されなかった場合、週次枠または利用枠として表示
                item.PrimaryLimit.Title = "利用枠";
                item.PrimaryLimit.LimitDescription = $"{_lastCodexData.PlanType.ToUpper()} 利用制限";
                item.PrimaryLimit.RemainingPercent = _lastCodexData.WeeklyRemainingPercent > 0 ? _lastCodexData.WeeklyRemainingPercent : 100.0;
                item.PrimaryLimit.ResetTimeText = !string.IsNullOrEmpty(_lastCodexData.WeeklyResetText) ? _lastCodexData.WeeklyResetText : "利用可能";
                item.SecondaryLimit = null;
                item.AllLimits.Add(item.PrimaryLimit);
            }

            // 予備週次枠 (gpt-reserve)
            if (_lastCodexData.ReserveRemainingPercent > 0 || !string.IsNullOrEmpty(_lastCodexData.ReserveResetText))
            {
                item.AllLimits.Add(new UsageLimitInfo
                {
                    Title = "予備週次枠",
                    LimitDescription = "gpt-reserve Weekly limit (予備枠)",
                    RemainingPercent = _lastCodexData.ReserveRemainingPercent,
                    ResetTimeText = _lastCodexData.ReserveResetText
                });
            }

            // リセット権（リセットチケット）の反映
            item.ResetCreditsAvailableCount = _lastCodexData.ResetCreditsAvailableCount;
            item.ResetCredits.Clear();
            foreach (var credit in _lastCodexData.ResetCredits)
            {
                item.ResetCredits.Add(credit);
            }

            if (item.ResetCredits.Count > 0 && item.ResetCredits[0].ExpiresAt.HasValue)
            {
                var first = item.ResetCredits[0];
                item.EarliestResetCreditExpireText = $"最短失効: {first.FormattedExpiresAt} ({first.RemainingTimeText})";
            }
            else if (item.ResetCreditsAvailableCount > 0)
            {
                item.EarliestResetCreditExpireText = $"{item.ResetCreditsAvailableCount}件 保有";
            }
            else
            {
                item.EarliestResetCreditExpireText = "保有なし (0件)";
            }
        }
        else
        {
            // 未ログインまたは取得失敗（偽のハードコードサンプル値を撤廃）
            bool isAuthIssue = _lastCodexData != null && _lastCodexData.IsAuthRequired;
            item.CliInfo.IsLoggedIn = !isAuthIssue && CodexQuotaClient.IsAuthFileExists();
            item.CliInfo.StatusMessage = item.CliInfo.IsLoggedIn 
                ? "ChatGPT接続待機中 (通信エラー)" 
                : "未ログイン ('codex login' が必要)";

            item.PrimaryLimit.Title = "5時間制限";
            item.PrimaryLimit.LimitDescription = item.CliInfo.IsLoggedIn ? "接続待機中..." : "未ログイン ('codex login' で連携)";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";
            item.PrimaryLimit.ResetTimeText = item.CliInfo.IsLoggedIn ? "取得待機" : "要ログイン";

            if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
            item.SecondaryLimit.Title = "週次制限";
            item.SecondaryLimit.LimitDescription = item.CliInfo.IsLoggedIn ? "接続待機中..." : "未ログイン ('codex login' で連携)";
            item.SecondaryLimit.RemainingPercent = 0.0;
            item.SecondaryLimit.CustomDisplayPercentText = "--";
            item.SecondaryLimit.ResetTimeText = item.CliInfo.IsLoggedIn ? "取得待機" : "要ログイン";

            item.ResetCreditsAvailableCount = 0;
            item.ResetCredits.Clear();
            item.EarliestResetCreditExpireText = "";

            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
            item.AllLimits.Add(item.SecondaryLimit);
        }
    }

    private void ApplyClaudeQuota(AiUsageItem item)
    {
        if (_lastClaudeData?.IsSuccess == true && _lastClaudeData.IsSubscribed)
        {
            item.CliInfo.IsLoggedIn = true;
            // 契約中（Claude Pro / Max / Team等）
            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = _lastClaudeData.StatusMessage;

            item.AllLimits.Clear();

            // 5時間制限と週次制限の両方がある場合
            if (_lastClaudeData.HasFiveHourLimit && _lastClaudeData.HasWeeklyLimit)
            {
                item.PrimaryLimit.Title = "5時間制限";
                item.PrimaryLimit.LimitDescription = "5-hour session limit (5時間枠)";
                item.PrimaryLimit.RemainingPercent = _lastClaudeData.FiveHourRemainingPercent;
                item.PrimaryLimit.ResetTimeText = _lastClaudeData.FiveHourResetText;
                item.PrimaryLimit.CustomDisplayPercentText = null;

                if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
                item.SecondaryLimit.Title = "週次制限";
                item.SecondaryLimit.LimitDescription = "Weekly limit (7日間枠)";
                item.SecondaryLimit.RemainingPercent = _lastClaudeData.WeeklyRemainingPercent;
                item.SecondaryLimit.ResetTimeText = _lastClaudeData.WeeklyResetText;
                item.SecondaryLimit.CustomDisplayPercentText = null;

                item.AllLimits.Add(item.PrimaryLimit);
                item.AllLimits.Add(item.SecondaryLimit);

                LogOutputReceived?.Invoke($"[Claude] 契約確認: 5時間枠={item.PrimaryLimit.RemainingPercent:F0}%, 週次枠={item.SecondaryLimit.RemainingPercent:F0}%");
            }
            else if (_lastClaudeData.HasWeeklyLimit)
            {
                item.PrimaryLimit.Title = "週次制限";
                item.PrimaryLimit.LimitDescription = "Weekly limit";
                item.PrimaryLimit.RemainingPercent = _lastClaudeData.WeeklyRemainingPercent;
                item.PrimaryLimit.ResetTimeText = _lastClaudeData.WeeklyResetText;
                item.PrimaryLimit.CustomDisplayPercentText = null;

                item.SecondaryLimit = null;
                item.AllLimits.Add(item.PrimaryLimit);
            }
            else
            {
                item.PrimaryLimit.Title = "5時間制限";
                item.PrimaryLimit.LimitDescription = "5-hour limit";
                item.PrimaryLimit.RemainingPercent = _lastClaudeData.FiveHourRemainingPercent;
                item.PrimaryLimit.ResetTimeText = _lastClaudeData.FiveHourResetText;
                item.PrimaryLimit.CustomDisplayPercentText = null;

                item.SecondaryLimit = null;
                item.AllLimits.Add(item.PrimaryLimit);
            }
        }
        else
        {
            // 未契約、未ログイン、または初回の一時的な取得失敗
            // 未契約はCLI・設定ファイルで確認できた場合だけ表示し、判定前のデータなしは取得待機として扱う。
            bool isLoggedIn = _lastClaudeData != null
                ? !_lastClaudeData.IsAuthRequired
                : ClaudeQuotaClient.IsConfigExists();
            bool isTemporaryFailure = isLoggedIn && _lastClaudeData?.IsSuccess != true;
            item.CliInfo.IsLoggedIn = isLoggedIn;
            item.CliInfo.IsSubscribed = isTemporaryFailure;

            item.PrimaryLimit.Title = "契約ステータス";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";

            if (isTemporaryFailure)
            {
                item.PrimaryLimit.LimitDescription = "Claude 利用枠の取得待機中";
                item.PrimaryLimit.ResetTimeText = "取得待機";
                item.CliInfo.StatusMessage = "Claude 接続待機中 (通信・形式エラー)";
            }
            else if (!item.CliInfo.IsLoggedIn)
            {
                item.PrimaryLimit.LimitDescription = "Claude 未ログイン ('claude auth login' で連携)";
                item.PrimaryLimit.ResetTimeText = "要ログイン";
                item.CliInfo.StatusMessage = "未ログイン ('claude auth login' が必要)";
            }
            else
            {
                item.PrimaryLimit.LimitDescription = "Anthropic Claude 未契約 (プラン未加入)";
                item.PrimaryLimit.ResetTimeText = "未契約";
                item.CliInfo.StatusMessage = "未契約 (プラン未加入)";
            }

            item.SecondaryLimit = null;
            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
        }
    }

    private void ApplyGeminiQuota(AiUsageItem item, List<QuotaGroup>? groups)
    {
        item.DisplayName = "Antigravity";
        item.SubTitle = "Google DeepMind";

        var argonGroup = FindArgonGroup(groups);
        var geminiGroup = FindGeminiModelsGroup(groups);

        var argonBucket = argonGroup?.Buckets.FirstOrDefault(b =>
            b.Window.Equals("weekly", StringComparison.OrdinalIgnoreCase) ||
            b.DisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase) ||
            b.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase))
            ?? argonGroup?.Buckets.FirstOrDefault();

        if (geminiGroup != null || argonBucket != null)
        {
            if (argonBucket != null)
            {
                item.ArgonLimit = new UsageLimitInfo
                {
                    Title = "Gemini 4 Argon",
                    LimitDescription = !string.IsNullOrEmpty(argonBucket.DisplayName)
                        ? argonBucket.DisplayName
                        : "Gemini 4 Argon Weekly Limit",
                    RemainingPercent = argonBucket.RemainingFraction * 100.0,
                    ResetTimeText = FormatResetTime(argonBucket.ResetTime, "リセット")
                };
            }
            else
            {
                item.ArgonLimit = null;
            }

            bool hasGeminiBuckets = false;
            if (geminiGroup != null)
            {
                var weeklyBucket = geminiGroup.Buckets.FirstOrDefault(b => b.Window == "weekly" || b.DisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase));
                var fiveHourBucket = geminiGroup.Buckets.FirstOrDefault(b => b.Window == "5h" || b.DisplayName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase));

                if (fiveHourBucket != null)
                {
                    hasGeminiBuckets = true;
                    item.PrimaryLimit.Title = "5時間制限";
                    item.PrimaryLimit.LimitDescription = !string.IsNullOrEmpty(fiveHourBucket.Description) ? fiveHourBucket.Description : "Five Hour Limit Remaining";
                    item.PrimaryLimit.RemainingPercent = fiveHourBucket.RemainingFraction * 100.0;
                    item.PrimaryLimit.CustomDisplayPercentText = null;
                    item.PrimaryLimit.ResetTimeText = FormatResetTime(fiveHourBucket.ResetTime, "リセット");

                    if (weeklyBucket != null)
                    {
                        if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
                        item.SecondaryLimit.Title = "週次制限";
                        item.SecondaryLimit.LimitDescription = !string.IsNullOrEmpty(weeklyBucket.Description) ? weeklyBucket.Description : "Weekly Limit Remaining";
                        item.SecondaryLimit.RemainingPercent = weeklyBucket.RemainingFraction * 100.0;
                        item.SecondaryLimit.CustomDisplayPercentText = null;
                        item.SecondaryLimit.ResetTimeText = FormatResetTime(weeklyBucket.ResetTime, "リセット");
                    }
                    else
                    {
                        item.SecondaryLimit = null;
                    }
                }
                else if (weeklyBucket != null)
                {
                    hasGeminiBuckets = true;
                    item.PrimaryLimit.Title = "週次制限";
                    item.PrimaryLimit.LimitDescription = !string.IsNullOrEmpty(weeklyBucket.Description) ? weeklyBucket.Description : "Weekly Limit Remaining";
                    item.PrimaryLimit.RemainingPercent = weeklyBucket.RemainingFraction * 100.0;
                    item.PrimaryLimit.CustomDisplayPercentText = null;
                    item.PrimaryLimit.ResetTimeText = FormatResetTime(weeklyBucket.ResetTime, "リセット");
                    item.SecondaryLimit = null;
                }
            }

            if (!hasGeminiBuckets && item.ArgonLimit != null)
            {
                item.PrimaryLimit.Title = item.ArgonLimit.Title;
                item.PrimaryLimit.LimitDescription = item.ArgonLimit.LimitDescription;
                item.PrimaryLimit.RemainingPercent = item.ArgonLimit.RemainingPercent;
                item.PrimaryLimit.CustomDisplayPercentText = null;
                item.PrimaryLimit.ResetTimeText = item.ArgonLimit.ResetTimeText;
                item.SecondaryLimit = null;
            }

            item.CliInfo.IsLoggedIn = !_geminiDataIsStale || CliManagerService.IsAntigravityAuthExists();
            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = "Google DeepMind 連携稼働中";

            var logSummary = item.ArgonLimit != null
                ? $"Argon枠={item.ArgonLimit.RemainingPercent:F0}%, 5h枠={item.PrimaryLimit.RemainingPercent:F0}%, 週次枠={item.SecondaryLimit?.RemainingPercent:F0}%"
                : $"5h枠={item.PrimaryLimit.RemainingPercent:F0}%, 週次枠={item.SecondaryLimit?.RemainingPercent:F0}%";
            LogOutputReceived?.Invoke($"[Antigravity] クォータ適用完了: {logSummary}");

            // 詳細表示・カード切替用の順で格納:
            // 1. Gemini 4 Argon (週次)
            // 2. Gemini Model (5時間、週次)
            // 3. Claude & GPT Model (5時間、週次)
            item.AllLimits.Clear();
            item.LimitGroups.Clear();
            if (item.ArgonLimit != null)
            {
                item.AllLimits.Add(item.ArgonLimit);
                item.LimitGroups.Add(new UsageLimitGroup
                {
                    GroupTitle = "Gemini 4 Argon",
                    Limits = new System.Collections.ObjectModel.ObservableCollection<UsageLimitInfo> { item.ArgonLimit }
                });
            }
            if (hasGeminiBuckets)
            {
                item.AllLimits.Add(item.PrimaryLimit);
                if (item.SecondaryLimit != null) item.AllLimits.Add(item.SecondaryLimit);

                var geminiGroupLimits = new System.Collections.ObjectModel.ObservableCollection<UsageLimitInfo> { item.PrimaryLimit };
                if (item.SecondaryLimit != null) geminiGroupLimits.Add(item.SecondaryLimit);
                item.LimitGroups.Add(new UsageLimitGroup
                {
                    GroupTitle = "Gemini Model",
                    Limits = geminiGroupLimits
                });
            }

            item.ExternalFiveHourLimit = null;
            item.ExternalWeeklyLimit = null;
            item.HasExternalLimits = false;

            // 外部モデル枠 (Claude / GPT models) があれば詳細画面の内訳枠および切替枠として追加
            var thirdPartyGroup = groups?.FirstOrDefault(g => 
                g.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || 
                g.DisplayName.Contains("GPT", StringComparison.OrdinalIgnoreCase) ||
                g.DisplayName.Contains("3p", StringComparison.OrdinalIgnoreCase));

            if (thirdPartyGroup != null)
            {
                var tp5h = thirdPartyGroup.Buckets.FirstOrDefault(b => b.Window == "5h" || b.DisplayName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase));
                var tpWeekly = thirdPartyGroup.Buckets.FirstOrDefault(b => b.Window == "weekly" || b.DisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase));

                var externalGroupLimits = new System.Collections.ObjectModel.ObservableCollection<UsageLimitInfo>();
                if (tp5h != null)
                {
                    var limit = new UsageLimitInfo
                    {
                        Title = "5時間制限 外部",
                        LimitDescription = "Claude / GPT models 5h limit",
                        RemainingPercent = tp5h.RemainingFraction * 100.0,
                        ResetTimeText = FormatResetTime(tp5h.ResetTime, "リセット")
                    };
                    item.AllLimits.Add(limit);
                    externalGroupLimits.Add(limit);
                    item.ExternalFiveHourLimit = limit;
                }
                if (tpWeekly != null)
                {
                    var limit = new UsageLimitInfo
                    {
                        Title = "週次制限 外部",
                        LimitDescription = "Claude / GPT models Weekly limit",
                        RemainingPercent = tpWeekly.RemainingFraction * 100.0,
                        ResetTimeText = FormatResetTime(tpWeekly.ResetTime, "リセット")
                    };
                    item.AllLimits.Add(limit);
                    externalGroupLimits.Add(limit);
                    item.ExternalWeeklyLimit = limit;
                }

                if (externalGroupLimits.Count > 0)
                {
                    item.LimitGroups.Add(new UsageLimitGroup
                    {
                        GroupTitle = "Claude & GPT Model",
                        Limits = externalGroupLimits
                    });
                }

                // 外部モデル枠 (5時間＋週次) が揃っている場合のみ切替対象に追加
                item.HasExternalLimits = item.ExternalFiveHourLimit != null
                    && item.ExternalWeeklyLimit != null;
            }
        }
        else
        {
            // 言語サーバー未接続または未ログイン時（偽のハードコードフォールバックを撤廃）
            item.ArgonLimit = null;
            item.ExternalFiveHourLimit = null;
            item.ExternalWeeklyLimit = null;
            item.HasExternalLimits = false;

            item.PrimaryLimit.Title = "5時間制限";
            item.PrimaryLimit.LimitDescription = "Antigravity 言語サーバー接続待機中";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";
            item.PrimaryLimit.ResetTimeText = "待機中";

            if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
            item.SecondaryLimit.Title = "週次制限";
            item.SecondaryLimit.LimitDescription = "Antigravity 言語サーバー接続待機中";
            item.SecondaryLimit.RemainingPercent = 0.0;
            item.SecondaryLimit.CustomDisplayPercentText = "--";
            item.SecondaryLimit.ResetTimeText = "待機中";

            item.CliInfo.IsLoggedIn = CliManagerService.IsAntigravityAuthExists();
            item.CliInfo.StatusMessage = "Antigravity 接続待機中";

            item.AllLimits.Clear();
            item.LimitGroups.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
            if (item.SecondaryLimit != null) item.AllLimits.Add(item.SecondaryLimit);
        }
    }

    private void ApplyGrokQuota(AiUsageItem item)
    {
        item.DisplayName = "Grok";
        item.SubTitle = "xAI / SuperGrok";

        if (_lastGrokData != null && _lastGrokData.IsSuccess)
        {
            item.SubTitle = $"xAI / {_lastGrokData.PlanName}";
            item.PrimaryLimit.Title = "週次制限";
            item.PrimaryLimit.RemainingPercent = _lastGrokData.RemainingPercent;
            item.PrimaryLimit.ResetTimeText = _lastGrokData.ResetTimeText;
            item.PrimaryLimit.CustomDisplayPercentText = null;

            item.PrimaryLimit.LimitDescription = $"{_lastGrokData.UsedPercent:F0}% 使用済み (残 {_lastGrokData.RemainingPercent:F0}%)";

            item.CliInfo.IsLoggedIn = !_lastGrokData.IsFromFallback || GrokQuotaClient.IsAuthFileExists();
            item.CliInfo.IsSubscribed = _lastGrokData.IsSubscribed;
            item.CliInfo.StatusMessage = _lastGrokData.IsSubscribed
                ? $"プラン: {_lastGrokData.PlanName} ({_lastGrokData.UsedPercent:F0}% 使用済)"
                : $"未契約 ({_lastGrokData.PlanName})";

            item.SecondaryLimit = null;
            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);

            // Build / Chat / Imagine などは独立した制限ではなく、週次枠を消費した機能別内訳。
            item.UsageBreakdown.Clear();
            var productUsage = _lastGrokData.ProductUsage ?? new List<GrokProductUsage>();

            // 旧キャッシュからの移行時だけ、従来の固定フィールドを内訳へ復元する。
            if (productUsage.Count == 0 && (_lastGrokData.GrokBuildPercent > 0 || _lastGrokData.GrokChatPercent > 0))
            {
                if (_lastGrokData.GrokBuildPercent > 0)
                {
                    productUsage.Add(new GrokProductUsage
                    {
                        Product = "GrokBuild",
                        DisplayName = "Grok Build",
                        UsedPercent = _lastGrokData.GrokBuildPercent
                    });
                }
                if (_lastGrokData.GrokChatPercent > 0)
                {
                    productUsage.Add(new GrokProductUsage
                    {
                        Product = "GrokChat",
                        DisplayName = "チャット",
                        UsedPercent = _lastGrokData.GrokChatPercent
                    });
                }
            }

            // 内訳は週次枠の使用率を機能別に分けたもの。1本の積み上げグラフで表示するため色を割り当てる。
            int paletteIndex = 0;
            double breakdownTotal = 0;
            foreach (var product in productUsage)
            {
                double used = Math.Clamp(product.UsedPercent, 0.0, 100.0);
                breakdownTotal += used;
                item.UsageBreakdown.Add(new UsageLimitInfo
                {
                    Title = product.DisplayName,
                    UsedAmount = used,
                    LimitDescription = "週次使用量の内訳",
                    SegmentColor = GetBreakdownColor(product.Product, ref paletteIndex)
                });
            }

            // 内訳の合計が週次の使用率に届かない分は「その他」として示し、バー全体を使用率と一致させる。
            double unassigned = _lastGrokData.UsedPercent - breakdownTotal;
            if (item.UsageBreakdown.Count > 0 && unassigned >= 1.0)
            {
                item.UsageBreakdown.Add(new UsageLimitInfo
                {
                    Title = "その他",
                    UsedAmount = unassigned,
                    LimitDescription = "週次使用量の内訳",
                    SegmentColor = OtherBreakdownColor
                });
            }

            LogOutputReceived?.Invoke($"[Grok] 反映完了: {item.PrimaryLimit.Title} {_lastGrokData.UsedPercent:F0}%使用済み (残{_lastGrokData.RemainingPercent:F0}%), リセット: {_lastGrokData.ResetTimeText}");
        }
        else
        {
            // 未ログインまたは取得失敗（偽のハードコードフォールバックを撤廃）
            bool isLoggedIn = _lastGrokData?.IsAuthRequired != true && GrokQuotaClient.IsAuthFileExists();
            item.CliInfo.IsLoggedIn = isLoggedIn;
            item.CliInfo.IsSubscribed = isLoggedIn;
            item.CliInfo.StatusMessage = isLoggedIn 
                ? "xAI 接続待機中 (通信エラー)" 
                : "未ログイン ('grok' 認証が必要)";

            item.PrimaryLimit.Title = "週次制限";
            item.PrimaryLimit.LimitDescription = isLoggedIn ? "接続待機中..." : "未ログイン ('grok' で連携)";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";
            item.PrimaryLimit.ResetTimeText = isLoggedIn ? "取得待機" : "要ログイン";

            item.SecondaryLimit = null;
            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
            item.UsageBreakdown.Clear();
        }
    }

    private const string OtherBreakdownColor = "#9CA3AF";
    private static readonly string[] BreakdownPalette = ["#FBBF24", "#A78BFA", "#22D3EE", "#FB923C"];

    private static string GetBreakdownColor(string product, ref int paletteIndex)
    {
        // 主要機能は取得順に関係なく同じ色にし、その他の機能は順に割り当てる。
        return product.ToLowerInvariant() switch
        {
            "grokbuild" => "#34D399",
            "grokchat" => "#60A5FA",
            "grokimagine" or "grokimage" or "imagine" or "image" => "#F472B6",
            _ => BreakdownPalette[paletteIndex++ % BreakdownPalette.Length]
        };
    }

    private void ApplyCopilotQuota(AiUsageItem item)
    {
        item.DisplayName = "Copilot";
        item.SubTitle = "GitHub / Copilot Pro";

        if (_lastCopilotData != null && _lastCopilotData.IsSuccess)
        {
            item.PrimaryLimit.Title = _lastCopilotData.QuotaTitle; // 年間契約: "プレミアム要求" / その他: "AI Credits"
            
            string unitSuffix = _lastCopilotData.IsYearlySubscriber ? "" : $" {_lastCopilotData.UnitName}";
            item.PrimaryLimit.LimitDescription = _lastCopilotData.TotalCount > 0
                ? $"{_lastCopilotData.UsedPercent:F0}% 使用済み (残 {Math.Max(0, _lastCopilotData.TotalCount - _lastCopilotData.UsedCount)} / {_lastCopilotData.TotalCount}{unitSuffix})"
                : $"{_lastCopilotData.UsedPercent:F0}% 使用済み (残 {_lastCopilotData.RemainingPercent:F0}%){unitSuffix}";
            item.PrimaryLimit.RemainingPercent = _lastCopilotData.RemainingPercent;
            item.PrimaryLimit.ResetTimeText = _lastCopilotData.ResetTimeText;
            item.PrimaryLimit.CustomDisplayPercentText = null;

            item.SubTitle = $"GitHub / {_lastCopilotData.PlanName}";
            item.CliInfo.IsLoggedIn = true;
            item.CliInfo.IsSubscribed = _lastCopilotData.IsSubscribed;
            item.CliInfo.StatusMessage = !_lastCopilotData.IsSubscribed
                ? $"未契約 ({_lastCopilotData.PlanName})"
                : string.IsNullOrWhiteSpace(_lastCopilotData.RawResetNotice)
                    ? $"プラン: {_lastCopilotData.PlanName} (稼働中)"
                    : $"プラン: {_lastCopilotData.PlanName} ({_lastCopilotData.RawResetNotice})";

            LogOutputReceived?.Invoke($"[Copilot] 反映完了: {item.PrimaryLimit.Title} {_lastCopilotData.UsedPercent:F0}%使用済み (残{_lastCopilotData.RemainingPercent:F0}%), リセット: {_lastCopilotData.ResetTimeText}");
        }
        else
        {
            // 未ログインまたは取得失敗（偽のハードコードフォールバックを撤廃）
            bool isGhAuth = _lastCopilotData?.IsAuthRequired != true && CopilotQuotaClient.IsGhAuth();
            item.CliInfo.IsLoggedIn = isGhAuth;
            item.CliInfo.IsSubscribed = isGhAuth;
            item.CliInfo.StatusMessage = isGhAuth 
                ? "GitHub 接続待機中 (通信エラー)" 
                : "未ログイン ('gh auth login' が必要)";

            item.PrimaryLimit.Title = "プレミアム要求";
            item.PrimaryLimit.LimitDescription = isGhAuth ? "接続待機中..." : "未ログイン ('gh auth login' で連携)";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";
            item.PrimaryLimit.ResetTimeText = isGhAuth ? "取得待機" : "要ログイン";
        }

        item.SecondaryLimit = null;
        item.AllLimits.Clear();
        item.AllLimits.Add(item.PrimaryLimit);
    }

    private void ApplySettingsStatus(AiUsageItem item)
    {
        item.PrimaryLimit.Title = "正常稼働";
        item.PrimaryLimit.LimitDescription = "全AIサービス・CLI監視中";
        item.PrimaryLimit.RemainingPercent = 100.0;
        item.PrimaryLimit.ResetTimeText = "リアルタイム同期中";

        item.AllLimits.Clear();
        item.AllLimits.Add(item.PrimaryLimit);
    }

    private string GetGeminiUsageSourceText()
    {
        if (!HasUsableGeminiQuota(_cachedGroups))
        {
            return _geminiFetchAttempted
                ? "取得元：未取得（CLI・IDEから取得できませんでした）"
                : "取得元：未取得";
        }

        var source = _geminiQuotaSource switch
        {
            UsageDataSource.Cli => "CLI",
            UsageDataSource.Ide => "IDE",
            _ => "不明"
        };

        if (_geminiDataIsStale)
        {
            return _geminiFetchAttempted
                ? $"取得失敗（前回の取得元：{source}）"
                : $"取得元：{source}（前回取得データ）";
        }

        return _geminiQuotaSource == UsageDataSource.Ide
            ? "取得元：IDE（CLI取得失敗時の補助）"
            : $"取得元：{source}";
    }

    private static QuotaGroup? FindArgonGroup(List<QuotaGroup>? groups)
    {
        return groups?.FirstOrDefault(g =>
            g.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase) ||
            g.Buckets.Any(b => b.BucketId.Contains("argon", StringComparison.OrdinalIgnoreCase) ||
                               b.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase)));
    }

    private static QuotaGroup? FindGeminiModelsGroup(List<QuotaGroup>? groups)
    {
        return groups?.FirstOrDefault(g =>
            g.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase) &&
            !g.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUsableGeminiQuota(List<QuotaGroup>? groups)
    {
        static bool HasUsableBucket(QuotaGroup? group) =>
            group?.Buckets.Any(b =>
                double.IsFinite(b.RemainingFraction) &&
                (b.Window.Equals("5h", StringComparison.OrdinalIgnoreCase) ||
                 b.Window.Equals("weekly", StringComparison.OrdinalIgnoreCase) ||
                 b.DisplayName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase) ||
                 b.DisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase) ||
                 b.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase))) == true;

        return HasUsableBucket(FindGeminiModelsGroup(groups)) || HasUsableBucket(FindArgonGroup(groups));
    }

    private bool IsUsingStaleData(AiServiceType serviceType)
    {
        return serviceType switch
        {
            AiServiceType.GPT => _codexDataIsStale,
            AiServiceType.Claude => _claudeDataIsStale,
            AiServiceType.Gemini => _geminiDataIsStale,
            AiServiceType.Copilot => _copilotDataIsStale,
            AiServiceType.Grok => _grokDataIsStale,
            _ => false
        };
    }

    private DateTime? GetLastSuccessfulRefresh(AiServiceType serviceType)
    {
        return serviceType switch
        {
            AiServiceType.GPT => _codexLastSuccessAt,
            AiServiceType.Claude => _claudeLastSuccessAt,
            AiServiceType.Gemini => _geminiLastSuccessAt,
            AiServiceType.Copilot => _copilotLastSuccessAt,
            AiServiceType.Grok => _grokLastSuccessAt,
            _ => null
        };
    }

    private string FormatResetTime(string isoTime, string suffix)
    {
        return ResetTimeFormatter.Format(isoTime, DateTime.Now, suffix);
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    _cachedGroups = JsonSerializer.Deserialize<List<QuotaGroup>>(json);
                    if (HasUsableGeminiQuota(_cachedGroups))
                    {
                        _geminiDataIsStale = true;
                        _geminiLastSuccessAt = File.GetLastWriteTime(_cacheFilePath);
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var cache = JsonSerializer.Deserialize<AllQuotaCache>(json);
                    if (cache != null)
                    {
                        var fallbackTimestamp = File.GetLastWriteTime(_cacheFilePath);
                        _cachedGroups = cache.GeminiGroups;
                        if (HasUsableGeminiQuota(_cachedGroups))
                        {
                            _geminiDataIsStale = true;
                            _geminiLastSuccessAt = cache.GeminiFetchedAt ?? fallbackTimestamp;
                            _geminiQuotaSource = cache.GeminiQuotaSource is UsageDataSource.Cli or UsageDataSource.Ide
                                ? cache.GeminiQuotaSource
                                : UsageDataSource.Unknown;
                        }
                        if (cache.CodexData?.IsSuccess == true)
                        {
                            _lastCodexData = cache.CodexData;
                            _codexDataIsStale = true;
                            _codexLastSuccessAt = cache.CodexFetchedAt ?? fallbackTimestamp;
                        }
                        if (cache.ClaudeData?.IsSubscribed == true)
                        {
                            // 旧形式のキャッシュにはIsSuccessがないため、契約済みデータは正常値として移行する。
                            cache.ClaudeData.IsSuccess = true;
                            _lastClaudeData = cache.ClaudeData;
                            _claudeDataIsStale = true;
                            _claudeLastSuccessAt = cache.ClaudeFetchedAt ?? fallbackTimestamp;
                        }
                        if (cache.GrokData?.IsSuccess == true)
                        {
                            _lastGrokData = cache.GrokData;
                            _grokDataIsStale = true;
                            _grokLastSuccessAt = cache.GrokFetchedAt ?? cache.GrokData.SourceTimestamp ?? fallbackTimestamp;
                        }
                        if (cache.CopilotData?.IsSuccess == true)
                        {
                            _lastCopilotData = cache.CopilotData;
                            _copilotDataIsStale = true;
                            _copilotLastSuccessAt = cache.CopilotFetchedAt ?? fallbackTimestamp;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void SaveCache()
    {
        try
        {
            var cache = new AllQuotaCache
            {
                GeminiGroups = _cachedGroups,
                GeminiQuotaSource = _geminiQuotaSource,
                CodexData = _lastCodexData?.IsSuccess == true ? _lastCodexData : null,
                ClaudeData = _lastClaudeData?.IsSubscribed == true ? _lastClaudeData : null,
                GrokData = _lastGrokData?.IsSuccess == true ? _lastGrokData : null,
                CopilotData = _lastCopilotData?.IsSuccess == true ? _lastCopilotData : null,
                GeminiFetchedAt = _geminiLastSuccessAt,
                CodexFetchedAt = _codexLastSuccessAt,
                ClaudeFetchedAt = _claudeLastSuccessAt,
                GrokFetchedAt = _grokLastSuccessAt,
                CopilotFetchedAt = _copilotLastSuccessAt
            };
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _cacheFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[キャッシュ] 保存失敗: {ex.Message}");
        }
    }
}

public class AllQuotaCache
{
    public List<QuotaGroup>? GeminiGroups { get; set; }
    public UsageDataSource GeminiQuotaSource { get; set; }
    public CodexQuotaData? CodexData { get; set; }
    public ClaudeQuotaData? ClaudeData { get; set; }
    public GrokQuotaData? GrokData { get; set; }
    public CopilotQuotaData? CopilotData { get; set; }
    public DateTime? GeminiFetchedAt { get; set; }
    public DateTime? CodexFetchedAt { get; set; }
    public DateTime? ClaudeFetchedAt { get; set; }
    public DateTime? GrokFetchedAt { get; set; }
    public DateTime? CopilotFetchedAt { get; set; }
}
