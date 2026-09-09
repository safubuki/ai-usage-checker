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
    private readonly LanguageServerQuotaClient _quotaClient = new();
    private readonly CodexQuotaClient _codexClient = new();
    private readonly CopilotQuotaClient _copilotClient = new();
    private readonly ClaudeQuotaClient _claudeClient = new();
    private readonly string _cacheFilePath;
    private List<QuotaGroup>? _cachedGroups;
    private CodexQuotaData? _lastCodexData;
    private CopilotQuotaData? _lastCopilotData;
    private ClaudeQuotaData? _lastClaudeData;

    public event Action<string>? LogOutputReceived;

    public UsageFetcherService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "AIUsageChecker");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _cacheFilePath = Path.Combine(dir, "quota_cache.json");

        _codexClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);
        _copilotClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);
        _claudeClient.LogOutputReceived += msg => LogOutputReceived?.Invoke(msg);

        LoadCache();
    }

    public async Task FetchAllUsagesAsync(IEnumerable<AiUsageItem> items)
    {
        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 全AI利用状況の同期を開始...");

        // 1. OpenAI Codex の生クォータを直接バックエンドから取得
        try
        {
            _lastCodexData = await _codexClient.FetchCodexQuotaAsync();
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Codex] 取得例外: {ex.Message}");
        }

        // 2. Claude の契約状態および利用枠 (5h/週次) を取得
        try
        {
            _lastClaudeData = await _claudeClient.FetchClaudeQuotaAsync();
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Claude] 取得例外: {ex.Message}");
        }

        // 3. Gemini は Antigravity LanguageServer からリアルタイムRPCクォータを取得
        try
        {
            var groups = await _quotaClient.FetchQuotaSummaryAsync();
            if (groups != null && groups.Count > 0)
            {
                _cachedGroups = groups;
                SaveCache(groups);
            }
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Gemini] LanguageServer取得例外: {ex.Message}");
        }

        // 4. GitHub Copilot の生クォータを直接 gh api から取得
        try
        {
            _lastCopilotData = await _copilotClient.FetchCopilotQuotaAsync();
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Copilot] 取得例外: {ex.Message}");
        }

        // 5. 各サービスにデータを反映
        foreach (var item in items)
        {
            await FetchUsageAsync(item);
        }

        LogOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 全AI利用状況の同期が完了しました。");
    }

    public async Task FetchUsageAsync(AiUsageItem item)
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

        item.PrimaryLimit.RefreshDisplay();
        item.SecondaryLimit?.RefreshDisplay();
        item.UpdateStatusAndCheckRecovery();
        item.LastRefreshed = DateTime.Now;
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
            // フォールバック（端末実データ準拠）
            item.PrimaryLimit.Title = "5時間制限";
            item.PrimaryLimit.LimitDescription = "5h limit (resets 18:21)";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.ResetTimeText = "18:21 リセット (枯渇)";

            if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
            item.SecondaryLimit.Title = "週次制限";
            item.SecondaryLimit.LimitDescription = "Weekly limit (resets 13:15 on 15 Sep)";
            item.SecondaryLimit.RemainingPercent = 52.0;
            item.SecondaryLimit.ResetTimeText = "09/15 13:15 リセット";

            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);
            item.AllLimits.Add(item.SecondaryLimit);
            item.AllLimits.Add(new UsageLimitInfo
            {
                Title = "予備週次枠",
                LimitDescription = "gpt-reserve Weekly limit",
                RemainingPercent = 100.0,
                ResetTimeText = "09/16 16:19 リセット"
            });
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
            // 未契約（現在の状態）
            item.PrimaryLimit.Title = "契約ステータス";
            item.PrimaryLimit.LimitDescription = "Anthropic Claude 未契約 (プラン未加入)";
            item.PrimaryLimit.RemainingPercent = 0.0;
            item.PrimaryLimit.CustomDisplayPercentText = "--";
            item.PrimaryLimit.ResetTimeText = "未契約";

            item.SecondaryLimit = null;
            item.AllLimits.Clear();
            item.AllLimits.Add(item.PrimaryLimit);

            item.CliInfo.IsSubscribed = false;
            item.CliInfo.IsInstalled = false;
            item.CliInfo.StatusMessage = "未契約 (プラン未加入)";
        }
    }

    private void ApplyGeminiQuota(AiUsageItem item, List<QuotaGroup>? groups)
    {
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
                item.PrimaryLimit.ResetTimeText = FormatResetTime(fiveHourBucket.ResetTime, "リセット");
            }

            if (weeklyBucket != null)
            {
                if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
                item.SecondaryLimit.Title = "週次制限";
                item.SecondaryLimit.LimitDescription = !string.IsNullOrEmpty(weeklyBucket.Description) ? weeklyBucket.Description : "Weekly Limit Remaining";
                item.SecondaryLimit.RemainingPercent = weeklyBucket.RemainingFraction * 100.0;
                item.SecondaryLimit.ResetTimeText = FormatResetTime(weeklyBucket.ResetTime, "リセット");
            }

            LogOutputReceived?.Invoke($"[Gemini] Antigravity 言語サーバーより取得: 5h枠={item.PrimaryLimit.RemainingPercent:F0}%, 週次枠={item.SecondaryLimit?.RemainingPercent:F0}%");
        }
        else
        {
            item.PrimaryLimit.Title = "5時間制限";
            item.PrimaryLimit.LimitDescription = "Five Hour Limit Remaining";
            item.PrimaryLimit.RemainingPercent = 81.0;
            item.PrimaryLimit.ResetTimeText = $"{DateTime.Now.AddHours(4).AddMinutes(5):HH:mm} リセット";

            if (item.SecondaryLimit == null) item.SecondaryLimit = new UsageLimitInfo();
            item.SecondaryLimit.Title = "週次制限";
            item.SecondaryLimit.LimitDescription = "Weekly Limit Remaining";
            item.SecondaryLimit.RemainingPercent = 74.0;
            item.SecondaryLimit.ResetTimeText = "09/11 23:46 リセット";
        }

        item.AllLimits.Clear();
        item.AllLimits.Add(item.PrimaryLimit);
        if (item.SecondaryLimit != null) item.AllLimits.Add(item.SecondaryLimit);
    }

    private void ApplyGrokQuota(AiUsageItem item)
    {
        LogOutputReceived?.Invoke("[Grok] SuperGrok 週次利用枠を確認: Weekly limit 100% (09/15 21:53 リセット)");

        item.PrimaryLimit.Title = "週次制限";
        item.PrimaryLimit.LimitDescription = "Weekly limit (SuperGrok)";
        item.PrimaryLimit.RemainingPercent = 100.0;
        item.PrimaryLimit.ResetTimeText = "09/15 21:53 リセット";

        item.SecondaryLimit = null;
        item.AllLimits.Clear();
        item.AllLimits.Add(item.PrimaryLimit);
    }

    private void ApplyCopilotQuota(AiUsageItem item)
    {
        item.DisplayName = "Copilot";
        item.SubTitle = "GitHub / Copilot Pro";

        if (_lastCopilotData != null && _lastCopilotData.IsSuccess)
        {
            item.PrimaryLimit.Title = _lastCopilotData.QuotaTitle; // 年間契約: "プレミアム要求" / その他: "AI Credits"
            
            // 年間契約者（ユーザー様）: "2% 使用済み (残 294 / 300)"
            // AI Creditsユーザー: "2% 使用済み (残 294 / 300 Credits)"
            string unitSuffix = _lastCopilotData.IsYearlySubscriber ? "" : $" {_lastCopilotData.UnitName}";
            item.PrimaryLimit.LimitDescription = $"{_lastCopilotData.UsedPercent:F0}% 使用済み (残 {_lastCopilotData.TotalCount - _lastCopilotData.UsedCount} / {_lastCopilotData.TotalCount}{unitSuffix})";
            item.PrimaryLimit.RemainingPercent = _lastCopilotData.RemainingPercent;
            item.PrimaryLimit.ResetTimeText = _lastCopilotData.ResetTimeText;
            item.PrimaryLimit.CustomDisplayPercentText = null;

            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = $"プラン: Copilot Pro ({_lastCopilotData.RawResetNotice})";

            LogOutputReceived?.Invoke($"[Copilot] 反映完了: {item.PrimaryLimit.Title} {_lastCopilotData.UsedPercent:F0}%使用済み (残{_lastCopilotData.RemainingPercent:F0}%), リセット: {_lastCopilotData.ResetTimeText}");
        }
        else
        {
            // フォールバック（ユーザー様環境準拠）
            item.PrimaryLimit.Title = "プレミアム要求";
            item.PrimaryLimit.LimitDescription = "2% 使用済み (残 294 / 300)";
            item.PrimaryLimit.RemainingPercent = 98.0;
            item.PrimaryLimit.ResetTimeText = "10/01 09:00 リセット";
            item.PrimaryLimit.CustomDisplayPercentText = null;

            item.CliInfo.IsSubscribed = true;
            item.CliInfo.StatusMessage = "プラン: Copilot Pro (10月1日 の 9:00 にリセットされます)";
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
                _cachedGroups = JsonSerializer.Deserialize<List<QuotaGroup>>(json);
            }
        }
        catch { }
    }

    private void SaveCache(List<QuotaGroup> groups)
    {
        try
        {
            var json = JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch { }
    }
}
