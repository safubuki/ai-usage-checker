using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly string _cacheFilePath;
    private List<QuotaGroup>? _cachedGroups;
    private CodexQuotaData? _lastCodexData;
    private CopilotQuotaData? _lastCopilotData;
    private ClaudeQuotaData? _lastClaudeData;
    private GrokQuotaData? _lastGrokData;

    public event Action<string>? LogOutputReceived;

    public UsageFetcherService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "AIUsageChecker");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _cacheFilePath = Path.Combine(dir, "quota_cache.json");

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
        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 全AI利用状況の取得を開始...");

        // 各AIサービスの生データ取得を並列実行して高速化
        var codexTask = Task.Run(async () =>
        {
            try
            {
                var data = await _codexClient.FetchCodexQuotaAsync();
                if (data != null && data.IsSuccess)
                {
                    _lastCodexData = data;
                    SaveCache();
                }
                else if (data != null && data.IsAuthRequired)
                {
                    // 認証切れ・未ログインが判明した場合はキャッシュに頼らず未ログイン状態を設定
                    _lastCodexData = data;
                }
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Codex] 取得例外: {ex.Message}");
            }
        });

        var claudeTask = Task.Run(async () =>
        {
            try
            {
                _lastClaudeData = await _claudeClient.FetchClaudeQuotaAsync();
                if (_lastClaudeData?.IsSubscribed == true)
                {
                    SaveCache();
                }
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Claude] 取得例外: {ex.Message}");
            }
        });

        var geminiTask = Task.Run(async () =>
        {
            try
            {
                // 1. Antigravity CLI ('agy') からの直接取得を優先試行
                var groups = await _agyClient.FetchQuotaSummaryAsync();

                // 2. CLI から取得できなかった場合は起動中の IDE 言語サーバーをフォールバックとして試行
                if (groups == null || groups.Count == 0)
                {
                    LogOutputReceived?.Invoke("[Antigravity] CLIより取得できなかったため、IDE言語サーバーをフォールバック確認します...");
                    groups = await _quotaClient.FetchQuotaSummaryAsync();
                }

                if (groups != null && groups.Count > 0)
                {
                    _cachedGroups = groups;
                    SaveCache();
                }
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Antigravity] クォータ取得例外: {ex.Message}");
            }
        });

        var copilotTask = Task.Run(async () =>
        {
            try
            {
                _lastCopilotData = await _copilotClient.FetchCopilotQuotaAsync();
                if (_lastCopilotData?.IsSuccess == true)
                {
                    SaveCache();
                }
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Copilot] 取得例外: {ex.Message}");
            }
        });

        var grokTask = Task.Run(async () =>
        {
            try
            {
                _lastGrokData = await _grokClient.FetchGrokQuotaAsync();
                if (_lastGrokData?.IsSuccess == true)
                {
                    SaveCache();
                }
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Grok] 取得例外: {ex.Message}");
            }
        });

        await Task.WhenAll(codexTask, claudeTask, geminiTask, copilotTask, grokTask);
        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 全AI利用状況の生データ取得完了。");
    }

    public void ApplyUsage(AiUsageItem item)
    {
        var groups = _cachedGroups;

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

        item.IsDataLoaded = true;
        item.PrimaryLimit.RefreshDisplay();
        item.SecondaryLimit?.RefreshDisplay();
        item.UpdateStatusAndCheckRecovery();
        item.LastRefreshed = DateTime.Now;
    }

    public async Task FetchUsageAsync(AiUsageItem item)
    {
        ApplyUsage(item);
        await Task.CompletedTask;
    }

    private void ApplyGptQuota(AiUsageItem item)
    {
        if (_lastCodexData != null && _lastCodexData.IsSuccess)
        {
            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = $"プラン: {_lastCodexData.PlanType.ToUpper()} (稼働中)";

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

            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
            item.AllLimits.Add(item.SecondaryLimit);
        }
    }

    private void ApplyClaudeQuota(AiUsageItem item)
    {
        if (_lastClaudeData != null && _lastClaudeData.IsSubscribed)
        {
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
            // 未契約または未ログイン
            bool isConfigExists = ClaudeQuotaClient.IsConfigExists();
            item.CliInfo.IsLoggedIn = isConfigExists;
            item.CliInfo.IsSubscribed = false;

            item.PrimaryLimit.Title = "契約ステータス";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";

            if (!isConfigExists)
            {
                item.PrimaryLimit.LimitDescription = "Claude 未ログイン ('claude login' で連携)";
                item.PrimaryLimit.ResetTimeText = "要ログイン";
                item.CliInfo.StatusMessage = "未ログイン ('claude login' が必要)";
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
        item.DisplayName = "Gemini";
        item.SubTitle = "Google DeepMind";

        var geminiGroup = groups?.FirstOrDefault(g => g.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        if (geminiGroup != null)
        {
            var weeklyBucket = geminiGroup.Buckets.FirstOrDefault(b => b.Window == "weekly" || b.DisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase));
            var fiveHourBucket = geminiGroup.Buckets.FirstOrDefault(b => b.Window == "5h" || b.DisplayName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase));

            if (fiveHourBucket != null)
            {
                item.PrimaryLimit.Title = "5時間制限";
                item.PrimaryLimit.LimitDescription = !string.IsNullOrEmpty(fiveHourBucket.Description) ? fiveHourBucket.Description : "Five Hour Limit Remaining";
                item.PrimaryLimit.RemainingPercent = fiveHourBucket.RemainingFraction * 100.0;
                item.PrimaryLimit.CustomDisplayPercentText = null;
                item.PrimaryLimit.ResetTimeText = FormatResetTime(fiveHourBucket.ResetTime, "リセット");
            }

            if (weeklyBucket != null)
            {
                if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
                item.SecondaryLimit.Title = "週次制限";
                item.SecondaryLimit.LimitDescription = !string.IsNullOrEmpty(weeklyBucket.Description) ? weeklyBucket.Description : "Weekly Limit Remaining";
                item.SecondaryLimit.RemainingPercent = weeklyBucket.RemainingFraction * 100.0;
                item.SecondaryLimit.CustomDisplayPercentText = null;
                item.SecondaryLimit.ResetTimeText = FormatResetTime(weeklyBucket.ResetTime, "リセット");
            }

            item.CliInfo.IsLoggedIn = true;
            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = "Google DeepMind 連携稼働中";
            LogOutputReceived?.Invoke($"[Antigravity] クォータ適用完了: 5h枠={item.PrimaryLimit.RemainingPercent:F0}%, 週次枠={item.SecondaryLimit?.RemainingPercent:F0}%");

            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
            if (item.SecondaryLimit != null) item.AllLimits.Add(item.SecondaryLimit);

            // 外部モデル枠 (Claude / GPT models) があれば詳細画面の内訳枠として追加
            var thirdPartyGroup = groups?.FirstOrDefault(g => 
                g.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || 
                g.DisplayName.Contains("GPT", StringComparison.OrdinalIgnoreCase) ||
                g.DisplayName.Contains("3p", StringComparison.OrdinalIgnoreCase));

            if (thirdPartyGroup != null)
            {
                var tp5h = thirdPartyGroup.Buckets.FirstOrDefault(b => b.Window == "5h" || b.DisplayName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase));
                var tpWeekly = thirdPartyGroup.Buckets.FirstOrDefault(b => b.Window == "weekly" || b.DisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase));

                if (tp5h != null)
                {
                    item.AllLimits.Add(new UsageLimitInfo
                    {
                        Title = "外部モデル 5時間制限",
                        LimitDescription = "Claude / GPT models 5h limit",
                        RemainingPercent = tp5h.RemainingFraction * 100.0,
                        ResetTimeText = FormatResetTime(tp5h.ResetTime, "リセット")
                    });
                }
                if (tpWeekly != null)
                {
                    item.AllLimits.Add(new UsageLimitInfo
                    {
                        Title = "外部モデル 週次制限",
                        LimitDescription = "Claude / GPT models Weekly limit",
                        RemainingPercent = tpWeekly.RemainingFraction * 100.0,
                        ResetTimeText = FormatResetTime(tpWeekly.ResetTime, "リセット")
                    });
                }
            }
        }
        else
        {
            // 言語サーバー未接続または未ログイン時（偽のハードコードフォールバックを撤廃）
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

            string breakdown = "";
            if (_lastGrokData.GrokBuildPercent > 0 || _lastGrokData.GrokChatPercent > 0)
            {
                breakdown = $" [Build: {_lastGrokData.GrokBuildPercent:F0}%, Chat: {_lastGrokData.GrokChatPercent:F0}%]";
            }
            item.PrimaryLimit.LimitDescription = $"{_lastGrokData.UsedPercent:F0}% 使用済み (残 {_lastGrokData.RemainingPercent:F0}%){breakdown}";

            item.CliInfo.IsLoggedIn = true;
            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = $"プラン: {_lastGrokData.PlanName} ({_lastGrokData.UsedPercent:F0}% 使用済)";

            item.SecondaryLimit = null;
            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);

            // 詳細ポップアップ向けに内訳を追加
            if (_lastGrokData.GrokBuildPercent > 0 || _lastGrokData.GrokChatPercent > 0)
            {
                var buildLimit = new UsageLimitInfo
                {
                    Title = "Grok Build",
                    RemainingPercent = Math.Max(0.0, 100.0 - _lastGrokData.GrokBuildPercent),
                    LimitDescription = $"{_lastGrokData.GrokBuildPercent:F0}% 使用",
                    ResetTimeText = _lastGrokData.ResetTimeText
                };
                buildLimit.RefreshDisplay();
                item.AllLimits.Add(buildLimit);

                var chatLimit = new UsageLimitInfo
                {
                    Title = "チャット",
                    RemainingPercent = Math.Max(0.0, 100.0 - _lastGrokData.GrokChatPercent),
                    LimitDescription = $"{_lastGrokData.GrokChatPercent:F0}% 使用",
                    ResetTimeText = _lastGrokData.ResetTimeText
                };
                chatLimit.RefreshDisplay();
                item.AllLimits.Add(chatLimit);
            }

            LogOutputReceived?.Invoke($"[Grok] 反映完了: {item.PrimaryLimit.Title} {_lastGrokData.UsedPercent:F0}%使用済み (残{_lastGrokData.RemainingPercent:F0}%), リセット: {_lastGrokData.ResetTimeText}");
        }
        else
        {
            // 未ログインまたは取得失敗（偽のハードコードフォールバックを撤廃）
            bool isLoggedIn = GrokQuotaClient.IsAuthFileExists();
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
        }
    }

    private void ApplyCopilotQuota(AiUsageItem item)
    {
        item.DisplayName = "Copilot";
        item.SubTitle = "GitHub / Copilot Pro";

        if (_lastCopilotData != null && _lastCopilotData.IsSuccess)
        {
            item.PrimaryLimit.Title = _lastCopilotData.QuotaTitle; // 年間契約: "プレミアム要求" / その他: "AI Credits"
            
            string unitSuffix = _lastCopilotData.IsYearlySubscriber ? "" : $" {_lastCopilotData.UnitName}";
            item.PrimaryLimit.LimitDescription = $"{_lastCopilotData.UsedPercent:F0}% 使用済み (残 {_lastCopilotData.TotalCount - _lastCopilotData.UsedCount} / {_lastCopilotData.TotalCount}{unitSuffix})";
            item.PrimaryLimit.RemainingPercent = _lastCopilotData.RemainingPercent;
            item.PrimaryLimit.ResetTimeText = _lastCopilotData.ResetTimeText;
            item.PrimaryLimit.CustomDisplayPercentText = null;

            item.CliInfo.IsLoggedIn = true;
            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = $"プラン: Copilot Pro ({_lastCopilotData.RawResetNotice})";

            LogOutputReceived?.Invoke($"[Copilot] 反映完了: {item.PrimaryLimit.Title} {_lastCopilotData.UsedPercent:F0}%使用済み (残{_lastCopilotData.RemainingPercent:F0}%), リセット: {_lastCopilotData.ResetTimeText}");
        }
        else
        {
            // 未ログインまたは取得失敗（偽のハードコードフォールバックを撤廃）
            bool isGhAuth = CopilotQuotaClient.IsGhAuth();
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

    private string FormatResetTime(string isoTime, string suffix)
    {
        if (string.IsNullOrWhiteSpace(isoTime)) return "常時更新";

        // 1. ISO 8601 等の日時文字列を解析
        if (DateTime.TryParse(isoTime, null, DateTimeStyles.AdjustToUniversal, out var dtUtc))
        {
            var dtLocal = dtUtc.ToLocalTime();
            var diff = dtLocal - DateTime.Now;

            // 24時間を超える場合 (週次制限など) は月日+時分、24時間以内 (5時間制限など) は時分のみでGPTと統一
            if (diff.TotalHours > 24)
            {
                return $"{dtLocal:MM/dd HH:mm} {suffix}";
            }
            return $"{dtLocal:HH:mm} {suffix}";
        }

        // 2. 「あと 4時間23分」「あと 35分」などの相対時間文字列から現在時刻を元にリセット時刻を逆算
        var matchHoursMins = System.Text.RegularExpressions.Regex.Match(isoTime, @"あと\s*(?:(\d+)\s*時間)?\s*(\d+)\s*分");
        if (matchHoursMins.Success)
        {
            int hours = 0;
            if (!string.IsNullOrEmpty(matchHoursMins.Groups[1].Value))
            {
                int.TryParse(matchHoursMins.Groups[1].Value, out hours);
            }
            int.TryParse(matchHoursMins.Groups[2].Value, out int mins);

            var targetTime = DateTime.Now.AddHours(hours).AddMinutes(mins);
            return $"{targetTime:HH:mm} {suffix}";
        }

        var matchHoursOnly = System.Text.RegularExpressions.Regex.Match(isoTime, @"あと\s*(\d+)\s*時間");
        if (matchHoursOnly.Success && int.TryParse(matchHoursOnly.Groups[1].Value, out int h))
        {
            var targetTime = DateTime.Now.AddHours(h);
            return $"{targetTime:HH:mm} {suffix}";
        }

        return isoTime;
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
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var cache = JsonSerializer.Deserialize<AllQuotaCache>(json);
                    if (cache != null)
                    {
                        _cachedGroups = cache.GeminiGroups;
                        if (cache.CodexData?.IsSuccess == true) _lastCodexData = cache.CodexData;
                        if (cache.ClaudeData?.IsSubscribed == true) _lastClaudeData = cache.ClaudeData;
                        if (cache.GrokData?.IsSuccess == true) _lastGrokData = cache.GrokData;
                        if (cache.CopilotData?.IsSuccess == true) _lastCopilotData = cache.CopilotData;
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
                CodexData = _lastCodexData?.IsSuccess == true ? _lastCodexData : null,
                ClaudeData = _lastClaudeData?.IsSubscribed == true ? _lastClaudeData : null,
                GrokData = _lastGrokData?.IsSuccess == true ? _lastGrokData : null,
                CopilotData = _lastCopilotData?.IsSuccess == true ? _lastCopilotData : null
            };
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch { }
    }
}

public class AllQuotaCache
{
    public List<QuotaGroup>? GeminiGroups { get; set; }
    public CodexQuotaData? CodexData { get; set; }
    public ClaudeQuotaData? ClaudeData { get; set; }
    public GrokQuotaData? GrokData { get; set; }
    public CopilotQuotaData? CopilotData { get; set; }
}
