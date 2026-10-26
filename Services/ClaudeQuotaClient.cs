using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

public class ClaudeQuotaData
{
    public bool IsSuccess { get; set; }
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

/// <summary>
/// `claude auth status --json` の結果のうち、契約判定に使う項目
/// </summary>
internal sealed class ClaudeAuthStatus
{
    public bool LoggedIn { get; init; }
    public string SubscriptionType { get; init; } = "";
}

public class ClaudeQuotaClient
{
    private const string UsageApiUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan AuthStatusTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _httpClient;

    public event Action<string>? LogOutputReceived;

    public ClaudeQuotaClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public static string GetConfigFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude.json");
    }

    public static string GetCredentialsFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", ".credentials.json");
    }

    public static bool IsConfigExists()
    {
        return File.Exists(GetConfigFilePath());
    }

    public Task<ClaudeQuotaData> FetchClaudeQuotaAsync()
    {
        return FetchClaudeQuotaAsync(GetConfigFilePath(), ReadAuthStatusFromCliAsync, GetCredentialsFilePath());
    }

    /// <param name="readAuthStatusJson">
    /// `claude auth status --json` の出力を返す処理。null の場合はCLIを確認せず設定ファイルだけで判定する。
    /// </param>
    /// <param name="credentialsFilePath">
    /// 利用量APIの認証に使う ~/.claude/.credentials.json。null の場合はAPIを呼ばず設定ファイルの保存値を使う。
    /// </param>
    internal async Task<ClaudeQuotaData> FetchClaudeQuotaAsync(string configFilePath, Func<Task<string?>>? readAuthStatusJson = null,
        string? credentialsFilePath = null)
    {
        var result = new ClaudeQuotaData();

        try
        {
            // 1. CLIでログイン・契約状態を確認する。
            // ~/.claude.json のプロフィールは Claude Code が再取得するまで古いままのため、再契約直後は未契約のまま残ることがある。
            var cliStatus = readAuthStatusJson == null ? null : ParseAuthStatus(await readAuthStatusJson());
            if (cliStatus != null)
            {
                var subscriptionText = string.IsNullOrEmpty(cliStatus.SubscriptionType) ? "なし" : cliStatus.SubscriptionType;
                LogOutputReceived?.Invoke($"[Claude] CLI確認 (claude auth status): ログイン={(cliStatus.LoggedIn ? "済" : "未")}, 契約種別={subscriptionText}");
            }

            if (cliStatus is { LoggedIn: false })
            {
                result.IsAuthRequired = true;
                result.StatusMessage = "未ログイン ('claude auth login' が必要)";
                LogOutputReceived?.Invoke("[Claude] CLIで未ログインを確認しました");
                return result;
            }

            bool cliLoggedIn = cliStatus?.LoggedIn == true;
            bool cliSubscribed = IsPaidSubscriptionType(cliStatus?.SubscriptionType);

            if (!File.Exists(configFilePath) && !cliLoggedIn)
            {
                result.IsSubscribed = false;
                result.IsAuthRequired = true;
                result.StatusMessage = "未ログイン ('claude auth login' が必要)";
                LogOutputReceived?.Invoke("[Claude] ~/.claude.json が見つかりません（未ログイン）");
                return result;
            }

            using var doc = File.Exists(configFilePath)
                ? JsonDocument.Parse(await File.ReadAllTextAsync(configFilePath))
                : null;
            var root = doc?.RootElement ?? default;
            var localNow = DateTime.Now;

            // 2. 設定ファイルのアカウント情報による契約判定
            JsonElement oauthElem = default;
            bool hasAccount = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("oauthAccount", out oauthElem) && oauthElem.ValueKind == JsonValueKind.Object;
            if (!hasAccount && !cliLoggedIn)
            {
                result.IsAuthRequired = true;
                result.StatusMessage = "未ログイン ('claude auth login' が必要)";
                LogOutputReceived?.Invoke("[Claude] 設定にログイン済みアカウントが見つかりません（未ログイン）");
                return result;
            }

            bool configSubscribed = false;
            string billingType = "";
            string orgType = "";
            if (hasAccount)
            {
                billingType = GetString(oauthElem, "billingType");
                orgType = GetString(oauthElem, "organizationType");

                // billingType が "none" 以外、または有料プラン契約時
                if (!string.IsNullOrEmpty(billingType) && !billingType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    configSubscribed = true;
                }
                else if (orgType.Contains("pro", StringComparison.OrdinalIgnoreCase) && !billingType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    configSubscribed = true;
                }
            }

            // どちらかで有料プランを確認できれば契約中。両方で確認できない場合だけ未契約とする。
            result.IsSubscribed = cliSubscribed || configSubscribed;
            result.PlanName = GetPlanName(cliStatus?.SubscriptionType, orgType);
            if (cliSubscribed && hasAccount && !configSubscribed)
            {
                LogOutputReceived?.Invoke($"[Claude] 設定ファイルの契約情報 (billingType={(string.IsNullOrEmpty(billingType) ? "なし" : billingType)}) は古いため、CLIの契約種別を優先します");
            }

            // 3. 利用状況クォータの取得
            // 設定ファイルの cachedUsageUtilization は Claude Code が保存した時点の値で更新が遅れるため、
            // 契約中は利用量APIの最新値を優先し、取得できない場合だけ保存値を使う。
            if (result.IsSubscribed && credentialsFilePath != null)
            {
                using var liveDoc = await FetchLiveUsageAsync(credentialsFilePath);
                if (liveDoc != null)
                {
                    ReadUtilization(liveDoc.RootElement, result, localNow);
                }
            }

            if (!result.HasFiveHourLimit && !result.HasWeeklyLimit &&
                root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("cachedUsageUtilization", out var cuElem) && cuElem.ValueKind == JsonValueKind.Object &&
                cuElem.TryGetProperty("utilization", out var utElem) && utElem.ValueKind == JsonValueKind.Object)
            {
                ReadUtilization(utElem, result, localNow);
                if (result.IsSubscribed && (result.HasFiveHourLimit || result.HasWeeklyLimit))
                {
                    var savedAt = cuElem.TryGetProperty("fetchedAtMs", out var faElem) && faElem.TryGetInt64(out var fetchedAtMs)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs).LocalDateTime.ToString("MM/dd HH:mm")
                        : "時刻不明";
                    LogOutputReceived?.Invoke($"[Claude] 設定ファイルに保存された利用枠を使用します (保存時刻: {savedAt})");
                }
            }

            if (result.IsSubscribed && !result.HasFiveHourLimit && !result.HasWeeklyLimit)
            {
                result.StatusMessage = "利用枠情報を取得できませんでした";
                LogOutputReceived?.Invoke("[Claude] 契約中ですが利用枠情報を解析できませんでした。取得エラーとして扱います");
                return result;
            }

            if (!result.IsSubscribed)
            {
                result.StatusMessage = "未契約 (プラン未加入)";
                LogOutputReceived?.Invoke(cliStatus != null
                    ? "[Claude] 契約状況確認: CLI・設定ファイルとも有料プランなし → 未契約 (プラン未加入)"
                    : "[Claude] 契約状況確認: 設定ファイルに有料プランなし (CLI確認不可) → 未契約 (プラン未加入)");
            }
            else
            {
                result.StatusMessage = $"プラン: {result.PlanName} (稼働中)";
                LogOutputReceived?.Invoke($"[Claude] 契約中確認: プラン={result.PlanName}, 5h枠={result.FiveHourRemainingPercent:F0}% ({result.FiveHourResetText}), 週次枠={result.WeeklyRemainingPercent:F0}% ({result.WeeklyResetText})");
            }

            result.IsSuccess = true;
            return result;
        }
        catch (Exception ex)
        {
            // 判定できなかった場合は未契約と決めつけず、取得エラーとして次回更新で再確認する。
            LogOutputReceived?.Invoke($"[Claude] 取得例外: {ex.Message}");
            result.IsSubscribed = false;
            result.StatusMessage = "利用枠の取得エラー (次回更新で再試行)";
            return result;
        }
    }

    private static void ReadUtilization(JsonElement utElem, ClaudeQuotaData result, DateTime localNow)
    {
        if (utElem.ValueKind != JsonValueKind.Object) return;

        // five_hour (5時間制限)
        if (utElem.TryGetProperty("five_hour", out var fhElem) && fhElem.ValueKind == JsonValueKind.Object)
        {
            double util = GetDouble(fhElem, "utilization");
            result.FiveHourRemainingPercent = Math.Max(0.0, 100.0 - util);
            result.HasFiveHourLimit = true;

            // 5時間枠は使い始めた時点で開始するため、未使用の間はリセット時刻がない。
            result.FiveHourResetText = fhElem.TryGetProperty("resets_at", out var rAt) && rAt.ValueKind == JsonValueKind.String
                ? ResetTimeFormatter.Format(rAt.GetString(), localNow)
                : "未使用";
        }

        // seven_day (週次制限)
        if (utElem.TryGetProperty("seven_day", out var sdElem) && sdElem.ValueKind == JsonValueKind.Object)
        {
            double util = GetDouble(sdElem, "utilization");
            result.WeeklyRemainingPercent = Math.Max(0.0, 100.0 - util);
            result.HasWeeklyLimit = true;

            if (sdElem.TryGetProperty("resets_at", out var rAt) && rAt.ValueKind == JsonValueKind.String)
            {
                result.WeeklyResetText = ResetTimeFormatter.Format(rAt.GetString(), localNow);
            }
        }
    }

    /// <summary>
    /// Claude Code の /usage と同じ利用量APIから最新の利用枠を取得する。
    /// トークンの更新は Claude Code に任せる（ここで更新するとClaude Codeのログイン情報と食い違うため）。
    /// </summary>
    private async Task<JsonDocument?> FetchLiveUsageAsync(string credentialsFilePath)
    {
        try
        {
            if (!File.Exists(credentialsFilePath))
            {
                LogOutputReceived?.Invoke("[Claude] 認証情報 (~/.claude/.credentials.json) がないため、保存された利用枠を使用します");
                return null;
            }

            string accessToken;
            long expiresAtMs = 0;
            using (var credDoc = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsFilePath)))
            {
                var root = credDoc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("claudeAiOauth", out var oauthElem) || oauthElem.ValueKind != JsonValueKind.Object)
                {
                    LogOutputReceived?.Invoke("[Claude] 認証情報にアクセストークンがないため、保存された利用枠を使用します");
                    return null;
                }

                accessToken = GetString(oauthElem, "accessToken");
                if (oauthElem.TryGetProperty("expiresAt", out var expElem) && expElem.ValueKind == JsonValueKind.Number)
                {
                    expElem.TryGetInt64(out expiresAtMs);
                }
            }

            if (string.IsNullOrEmpty(accessToken))
            {
                LogOutputReceived?.Invoke("[Claude] 認証情報にアクセストークンがないため、保存された利用枠を使用します");
                return null;
            }

            if (expiresAtMs > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                LogOutputReceived?.Invoke("[Claude] アクセストークンの有効期限が切れているため、保存された利用枠を使用します (Claude Code の起動時に更新されます)");
                return null;
            }

            LogOutputReceived?.Invoke($"[Claude] GET {UsageApiUrl} 送信中...");
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LogOutputReceived?.Invoke($"[Claude] 利用量API HTTP {(int)response.StatusCode} {response.ReasonPhrase}。保存された利用枠を使用します");
                return null;
            }

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                LogOutputReceived?.Invoke("[Claude] 利用量APIの応答を解析できないため、保存された利用枠を使用します");
                return null;
            }

            LogOutputReceived?.Invoke("[Claude] 利用量APIから最新の利用枠を取得しました");
            return doc;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Claude] 利用量APIの取得に失敗したため、保存された利用枠を使用します: {ex.Message}");
            return null;
        }
    }

    internal static ClaudeAuthStatus? ParseAuthStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("loggedIn", out var loggedInElem) ||
                loggedInElem.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            return new ClaudeAuthStatus
            {
                LoggedIn = loggedInElem.GetBoolean(),
                SubscriptionType = GetString(root, "subscriptionType")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPaidSubscriptionType(string? subscriptionType)
    {
        return !string.IsNullOrWhiteSpace(subscriptionType) &&
            !subscriptionType.Equals("free", StringComparison.OrdinalIgnoreCase) &&
            !subscriptionType.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPlanName(string? subscriptionType, string orgType)
    {
        var plan = IsPaidSubscriptionType(subscriptionType) ? subscriptionType! : orgType;
        if (plan.Contains("max", StringComparison.OrdinalIgnoreCase)) return "Claude Max";
        if (plan.Contains("pro", StringComparison.OrdinalIgnoreCase)) return "Claude Pro";
        if (plan.Contains("team", StringComparison.OrdinalIgnoreCase)) return "Claude Team";
        if (plan.Contains("enterprise", StringComparison.OrdinalIgnoreCase)) return "Claude Enterprise";
        return "Claude";
    }

    private static string GetString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var elem) && elem.ValueKind == JsonValueKind.String
            ? elem.GetString() ?? ""
            : "";
    }

    private static double GetDouble(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var elem) && elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out var value)
            ? value
            : 0.0;
    }

    private async Task<string?> ReadAuthStatusFromCliAsync()
    {
        try
        {
            // npm の claude.cmd とネイティブ版 claude.exe のどちらでも動くよう cmd.exe 経由で実行する。
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c claude auth status --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                LogOutputReceived?.Invoke("[Claude] CLIを起動できないため、設定ファイルで契約状況を判定します");
                return null;
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();
            if (await Task.WhenAny(waitTask, Task.Delay(AuthStatusTimeout)) != waitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                LogOutputReceived?.Invoke($"[Claude] 'claude auth status' がタイムアウトしました ({AuthStatusTimeout.TotalSeconds:F0}秒)。設定ファイルで判定します");
                return null;
            }

            await waitTask;
            var output = await outputTask;
            await errorTask;

            // 未ログイン時は終了コードが0以外でも loggedIn=false のJSONを返すため、終了コードでは判断しない。
            if (ParseAuthStatus(output) == null)
            {
                LogOutputReceived?.Invoke($"[Claude] 'claude auth status' の結果を解析できません (Code {proc.ExitCode})。設定ファイルで判定します");
                return null;
            }

            return output;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Claude] 'claude auth status' 実行失敗: {ex.Message}。設定ファイルで判定します");
            return null;
        }
    }
}
