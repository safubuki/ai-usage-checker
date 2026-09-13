using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

public class ClaudeQuotaData
{
    public bool IsSubscribed { get; set; }
    public bool IsAuthRequired { get; set; }
    public string PlanName { get; set; } = "Claude";
    public bool HasFiveHourLimit { get; set; }
    public double FiveHourRemainingPercent { get; set; }
    public string FiveHourResetText { get; set; } = "";
    public bool HasWeeklyLimit { get; set; }
    public double WeeklyRemainingPercent { get; set; }
    public string WeeklyResetText { get; set; } = "";
    public string StatusMessage { get; set; } = "";
}

public class ClaudeQuotaClient
{
    public event Action<string>? LogOutputReceived;

    public static string GetConfigFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude.json");
    }

    public static bool IsConfigExists()
    {
        return File.Exists(GetConfigFilePath());
    }

    public async Task<ClaudeQuotaData> FetchClaudeQuotaAsync()
    {
        var result = new ClaudeQuotaData();

        try
        {
            var claudeJsonPath = GetConfigFilePath();

            if (!File.Exists(claudeJsonPath))
            {
                result.IsSubscribed = false;
                result.IsAuthRequired = true;
                result.StatusMessage = "未ログイン ('claude login' が必要)";
                LogOutputReceived?.Invoke("[Claude] ~/.claude.json が見つかりません（未ログイン）");
                return result;
            }

            var jsonText = await File.ReadAllTextAsync(claudeJsonPath);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // 1. アカウントおよび契約状態の判定
            bool isSubscribed = false;
            string planName = "Claude";

            if (root.TryGetProperty("oauthAccount", out var oauthElem) && oauthElem.ValueKind == JsonValueKind.Object)
            {
                string billingType = oauthElem.TryGetProperty("billingType", out var bt) ? (bt.GetString() ?? "") : "";
                string orgType = oauthElem.TryGetProperty("organizationType", out var ot) ? (ot.GetString() ?? "") : "";

                // billingType が "none" 以外、または有料プラン契約時
                if (!string.IsNullOrEmpty(billingType) && !billingType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    isSubscribed = true;
                }
                else if (orgType.Contains("pro", StringComparison.OrdinalIgnoreCase) && !billingType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    isSubscribed = true;
                }

                planName = orgType.Contains("pro", StringComparison.OrdinalIgnoreCase) ? "Claude Pro" : "Claude";
            }

            result.IsSubscribed = isSubscribed;
            result.PlanName = planName;

            // 2. 利用状況クォータ (cachedUsageUtilization) の取得
            if (root.TryGetProperty("cachedUsageUtilization", out var cuElem) && cuElem.ValueKind == JsonValueKind.Object)
            {
                if (cuElem.TryGetProperty("utilization", out var utElem) && utElem.ValueKind == JsonValueKind.Object)
                {
                    // five_hour (5時間制限)
                    if (utElem.TryGetProperty("five_hour", out var fhElem) && fhElem.ValueKind == JsonValueKind.Object)
                    {
                        double util = fhElem.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0.0;
                        result.FiveHourRemainingPercent = Math.Max(0.0, 100.0 - util);
                        result.HasFiveHourLimit = true;

                        if (fhElem.TryGetProperty("resets_at", out var rAt) && rAt.ValueKind == JsonValueKind.String)
                        {
                            result.FiveHourResetText = FormatIsoResetTime(rAt.GetString());
                        }
                    }

                    // seven_day (週次制限)
                    if (utElem.TryGetProperty("seven_day", out var sdElem) && sdElem.ValueKind == JsonValueKind.Object)
                    {
                        double util = sdElem.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0.0;
                        result.WeeklyRemainingPercent = Math.Max(0.0, 100.0 - util);
                        result.HasWeeklyLimit = true;

                        if (sdElem.TryGetProperty("resets_at", out var rAt) && rAt.ValueKind == JsonValueKind.String)
                        {
                            result.WeeklyResetText = FormatIsoResetTime(rAt.GetString());
                        }
                    }
                }
            }

            if (!result.IsSubscribed)
            {
                result.StatusMessage = "未契約 (プラン未加入)";
                LogOutputReceived?.Invoke("[Claude] 契約状況確認: 未契約 (プラン未加入)");
            }
            else
            {
                result.StatusMessage = $"プラン: {result.PlanName} (稼働中)";
                LogOutputReceived?.Invoke($"[Claude] 契約中確認: プラン={result.PlanName}, 5h枠={result.FiveHourRemainingPercent:F0}% ({result.FiveHourResetText}), 週次枠={result.WeeklyRemainingPercent:F0}% ({result.WeeklyResetText})");
            }

            return result;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Claude] 取得例外: {ex.Message}");
            result.IsSubscribed = false;
            result.StatusMessage = "未契約";
            return result;
        }
    }

    private string FormatIsoResetTime(string? isoTime)
    {
        if (string.IsNullOrWhiteSpace(isoTime)) return "常時更新";

        // 1. ISO 8601 等の日時文字列を解析
        if (DateTime.TryParse(isoTime, null, DateTimeStyles.AdjustToUniversal, out var dtUtc))
        {
            var dtLocal = dtUtc.ToLocalTime();
            var diff = dtLocal - DateTime.Now;

            // 24時間を超える場合 (週次制限など) は月日+時分、24時間以内 (5時間制限など) は時分のみでGPT・Geminiと統一
            if (diff.TotalHours > 24)
            {
                return $"{dtLocal:MM/dd HH:mm} リセット";
            }
            return $"{dtLocal:HH:mm} リセット";
        }

        // 2. 「あと 4時間23分」「あと 30分」などの相対時間文字列から現在時刻を元にリセット時刻を逆算
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
            return $"{targetTime:HH:mm} リセット";
        }

        var matchHoursOnly = System.Text.RegularExpressions.Regex.Match(isoTime, @"あと\s*(\d+)\s*時間");
        if (matchHoursOnly.Success && int.TryParse(matchHoursOnly.Groups[1].Value, out int h))
        {
            var targetTime = DateTime.Now.AddHours(h);
            return $"{targetTime:HH:mm} リセット";
        }

        return isoTime ?? "";
    }
}
