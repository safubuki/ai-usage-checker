using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

public class CodexQuotaData
{
    public bool IsSuccess { get; set; }
    public bool IsAuthRequired { get; set; }
    public string PlanType { get; set; } = "";
    public bool HasFiveHourLimit { get; set; }
    public double FiveHourRemainingPercent { get; set; }
    public string FiveHourResetText { get; set; } = "";
    public bool HasWeeklyLimit { get; set; }
    public double WeeklyRemainingPercent { get; set; }
    public string WeeklyResetText { get; set; } = "";
    public double ReserveRemainingPercent { get; set; }
    public string ReserveResetText { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}

public class CodexQuotaClient
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public event Action<string>? LogOutputReceived;

    public static string GetAuthFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex", "auth.json");
    }

    public static bool IsAuthFileExists()
    {
        return File.Exists(GetAuthFilePath());
    }

    public async Task<CodexQuotaData?> FetchCodexQuotaAsync()
    {
        try
        {
            var authFile = GetAuthFilePath();

            if (!File.Exists(authFile))
            {
                LogOutputReceived?.Invoke("[Codex] auth.json が見つかりません。'codex login' を実行してください。");
                return new CodexQuotaData
                {
                    IsSuccess = false,
                    IsAuthRequired = true,
                    ErrorMessage = "auth.json が見つかりません ('codex login' が必要)"
                };
            }

            LogOutputReceived?.Invoke($"[Codex] 認証情報確認: {authFile}");

            var authJson = await File.ReadAllTextAsync(authFile);
            using var authDoc = JsonDocument.Parse(authJson);

            string? accessToken = null;
            string? accountId = null;

            if (authDoc.RootElement.TryGetProperty("tokens", out var tokensElem))
            {
                if (tokensElem.TryGetProperty("access_token", out var atElem))
                    accessToken = atElem.GetString();
                if (tokensElem.TryGetProperty("account_id", out var accElem))
                    accountId = accElem.GetString();
            }

            if (string.IsNullOrEmpty(accessToken))
            {
                LogOutputReceived?.Invoke("[Codex] access_token が見つかりません。再ログインが必要です。");
                return new CodexQuotaData
                {
                    IsSuccess = false,
                    IsAuthRequired = true,
                    ErrorMessage = "access_token が見つかりません (再ログインが必要)"
                };
            }

            LogOutputReceived?.Invoke("[Codex] GET https://chatgpt.com/backend-api/wham/usage 送信中...");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("User-Agent", "codex/0.153.4");
            request.Headers.Add("Accept", "application/json");

            if (!string.IsNullOrEmpty(accountId))
            {
                request.Headers.Add("ChatGPT-Account-ID", accountId);
            }

            var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LogOutputReceived?.Invoke($"[Codex] HTTP エラー: {(int)response.StatusCode} {response.ReasonPhrase}");
                return new CodexQuotaData
                {
                    IsSuccess = false,
                    IsAuthRequired = response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                    ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                };
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new CodexQuotaData { IsSuccess = true };

            if (root.TryGetProperty("plan_type", out var planElem))
            {
                result.PlanType = planElem.GetString() ?? "";
            }

            // rate_limit 解析
            if (root.TryGetProperty("rate_limit", out var rlElem))
            {
                // primary_window
                if (rlElem.TryGetProperty("primary_window", out var pwElem) && pwElem.ValueKind != JsonValueKind.Null)
                {
                    long windowSec = pwElem.TryGetProperty("limit_window_seconds", out var lws) ? lws.GetInt64() : 18000;
                    double used = pwElem.TryGetProperty("used_percent", out var up) ? up.GetDouble() : 0.0;
                    double remaining = Math.Max(0.0, 100.0 - used);
                    string resetText = "";

                    if (pwElem.TryGetProperty("reset_at", out var rAt))
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(rAt.GetInt64()).LocalDateTime;
                        resetText = windowSec <= 21600 ? $"{dt:HH:mm} リセット" : $"{dt:MM/dd HH:mm} リセット";
                        if (remaining <= 0.0) resetText += " (枯渇)";
                    }

                    // 5時間枠 (<= 6時間) か 週次枠 (> 6時間) かを判定
                    if (windowSec <= 21600)
                    {
                        result.HasFiveHourLimit = true;
                        result.FiveHourRemainingPercent = remaining;
                        result.FiveHourResetText = resetText;
                    }
                    else
                    {
                        result.HasWeeklyLimit = true;
                        result.WeeklyRemainingPercent = remaining;
                        result.WeeklyResetText = resetText;
                    }
                }

                // secondary_window
                if (rlElem.TryGetProperty("secondary_window", out var swElem) && swElem.ValueKind != JsonValueKind.Null)
                {
                    long windowSec = swElem.TryGetProperty("limit_window_seconds", out var lws) ? lws.GetInt64() : 604800;
                    double used = swElem.TryGetProperty("used_percent", out var up) ? up.GetDouble() : 0.0;
                    double remaining = Math.Max(0.0, 100.0 - used);
                    string resetText = "";

                    if (swElem.TryGetProperty("reset_at", out var rAt))
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(rAt.GetInt64()).LocalDateTime;
                        resetText = $"{dt:MM/dd HH:mm} リセット";
                    }

                    result.HasWeeklyLimit = true;
                    result.WeeklyRemainingPercent = remaining;
                    result.WeeklyResetText = resetText;
                }
            }

            // additional_rate_limits (gpt-reserve)
            if (root.TryGetProperty("additional_rate_limits", out var addElem) && addElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in addElem.EnumerateArray())
                {
                    if (item.TryGetProperty("limit_name", out var ln) && ln.GetString() == "gpt-reserve")
                    {
                        if (item.TryGetProperty("rate_limit", out var subRl) && subRl.TryGetProperty("primary_window", out var subPw) && subPw.ValueKind != JsonValueKind.Null)
                        {
                            double used = subPw.TryGetProperty("used_percent", out var up) ? up.GetDouble() : 0.0;
                            result.ReserveRemainingPercent = Math.Max(0.0, 100.0 - used);

                            if (subPw.TryGetProperty("reset_at", out var rAt))
                            {
                                var dt = DateTimeOffset.FromUnixTimeSeconds(rAt.GetInt64()).LocalDateTime;
                                result.ReserveResetText = $"{dt:MM/dd HH:mm} リセット";
                            }
                        }
                    }
                }
            }

            string limitSummary = result.HasFiveHourLimit && result.HasWeeklyLimit 
                ? $"5h枠={result.FiveHourRemainingPercent:F0}% ({result.FiveHourResetText}), 週次枠={result.WeeklyRemainingPercent:F0}% ({result.WeeklyResetText})"
                : result.HasWeeklyLimit 
                    ? $"週次枠のみ={result.WeeklyRemainingPercent:F0}% ({result.WeeklyResetText})"
                    : $"5h枠のみ={result.FiveHourRemainingPercent:F0}% ({result.FiveHourResetText})";

            LogOutputReceived?.Invoke($"[Codex] 取得完了: プラン={result.PlanType.ToUpper()}, {limitSummary}, 予備枠={result.ReserveRemainingPercent:F0}%");

            return result;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Codex] 取得例外: {ex.Message}");
            return null;
        }
    }
}
