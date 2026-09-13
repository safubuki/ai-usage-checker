using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

public class GrokQuotaData
{
    public bool IsSuccess { get; set; }
    public bool IsAuthRequired { get; set; }
    public string PlanName { get; set; } = "SuperGrok";
    public double UsedPercent { get; set; }
    public double RemainingPercent { get; set; } = 100.0;
    public string ResetTimeText { get; set; } = "";
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public double GrokBuildPercent { get; set; }
    public double GrokChatPercent { get; set; }
    public double PrepaidBalance { get; set; }
    public double OnDemandUsed { get; set; }
    public string ErrorMessage { get; set; } = "";
}

public class GrokQuotaClient
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    public event Action<string>? LogOutputReceived;

    public static string GetAuthFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".grok", "auth.json");
    }

    public static bool IsAuthFileExists()
    {
        return File.Exists(GetAuthFilePath());
    }

    public async Task<GrokQuotaData?> FetchGrokQuotaAsync()
    {
        try
        {
            var authFilePath = GetAuthFilePath();

            if (!File.Exists(authFilePath))
            {
                LogOutputReceived?.Invoke("[Grok] ~/.grok/auth.json が見つかりません（未ログインまたはGrok CLI未インストール）");
                var fallback = TryFallbackFromLog();
                if (fallback != null) return fallback;
                return new GrokQuotaData { IsSuccess = false, IsAuthRequired = true, ErrorMessage = "未ログイン ('grok' ログインが必要)" };
            }

            var authJson = await File.ReadAllTextAsync(authFilePath);
            var authNode = JsonNode.Parse(authJson);
            if (authNode is not JsonObject authObj || authObj.Count == 0)
            {
                LogOutputReceived?.Invoke("[Grok] auth.json のパースに失敗しました");
                return TryFallbackFromLog();
            }

            // OIDC / APIキー エントリの取得
            string? tokenKey = null;
            string? refreshToken = null;
            string? clientId = null;
            string? issuer = null;
            DateTime? expiresAt = null;
            string entryKey = "";

            foreach (var kv in authObj)
            {
                if (kv.Value is JsonObject itemObj)
                {
                    entryKey = kv.Key;
                    tokenKey = itemObj["key"]?.ToString();
                    refreshToken = itemObj["refresh_token"]?.ToString();
                    clientId = itemObj["oidc_client_id"]?.ToString();
                    issuer = itemObj["oidc_issuer"]?.ToString() ?? "https://auth.x.ai";
                    
                    var expStr = itemObj["expires_at"]?.ToString();
                    if (!string.IsNullOrEmpty(expStr) && DateTime.TryParse(expStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
                    {
                        expiresAt = dt;
                    }

                    if (!string.IsNullOrEmpty(tokenKey))
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(tokenKey))
            {
                LogOutputReceived?.Invoke("[Grok] auth.json 内に認証キーが見つかりません");
                return TryFallbackFromLog();
            }

            // トークンの有効期限チェック（期限切れまたは3分以内の失効なら自動リフレッシュ）
            if (expiresAt.HasValue && DateTime.UtcNow.AddMinutes(3) >= expiresAt.Value && !string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(clientId))
            {
                LogOutputReceived?.Invoke("[Grok] トークンの有効期限が近づいているため、自動リフレッシュを実行します...");
                var refreshedToken = await RefreshTokenAsync(issuer ?? "https://auth.x.ai", clientId, refreshToken, authFilePath, entryKey);
                if (!string.IsNullOrEmpty(refreshedToken))
                {
                    tokenKey = refreshedToken;
                }
            }

            // API 呼び出し
            var data = await RequestBillingAsync(tokenKey);
            if (data == null && !string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(clientId))
            {
                // 401 等で失敗した可能性があるため、強制リフレッシュして再試行
                LogOutputReceived?.Invoke("[Grok] トークン強制リフレッシュして再試行します...");
                var refreshedToken = await RefreshTokenAsync(issuer ?? "https://auth.x.ai", clientId, refreshToken, authFilePath, entryKey);
                if (!string.IsNullOrEmpty(refreshedToken))
                {
                    data = await RequestBillingAsync(refreshedToken);
                }
            }

            if (data != null && data.IsSuccess)
            {
                return data;
            }

            // API が取れなかった場合はログからフォールバック
            return TryFallbackFromLog() ?? data;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Grok] 取得例外: {ex.Message}");
            return TryFallbackFromLog();
        }
    }

    private async Task<GrokQuotaData?> RequestBillingAsync(string token)
    {
        try
        {
            const string url = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("User-Agent", "grok-shell/1.0.25");
            req.Headers.Add("Accept", "application/json");

            var response = await HttpClient.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                LogOutputReceived?.Invoke($"[Grok] HTTP エラー: {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return ParseBillingJson(json);
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Grok] リクエスト失敗: {ex.Message}");
            return null;
        }
    }

    private GrokQuotaData? ParseBillingJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("config", out var configElem))
            {
                return null;
            }

            var result = new GrokQuotaData { IsSuccess = true };

            if (configElem.TryGetProperty("creditUsagePercent", out var cupElem))
            {
                result.UsedPercent = cupElem.GetDouble();
                result.RemainingPercent = Math.Max(0.0, 100.0 - result.UsedPercent);
            }

            if (configElem.TryGetProperty("subscriptionTier", out var stElem))
            {
                result.PlanName = stElem.GetString() ?? "SuperGrok";
            }

            // 期間とリセット日時
            if (configElem.TryGetProperty("currentPeriod", out var cpElem) && cpElem.ValueKind == JsonValueKind.Object)
            {
                if (cpElem.TryGetProperty("start", out var sElem) && DateTime.TryParse(sElem.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var sDate))
                {
                    result.PeriodStart = sDate;
                }
                if (cpElem.TryGetProperty("end", out var eElem) && DateTime.TryParse(eElem.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var eDate))
                {
                    result.PeriodEnd = eDate;
                    var localEnd = eDate.ToLocalTime();
                    result.ResetTimeText = $"{localEnd:MM/dd HH:mm} リセット";
                }
            }

            // プロダクト別内訳 (GrokBuild, GrokChat)
            if (configElem.TryGetProperty("productUsage", out var puElem) && puElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in puElem.EnumerateArray())
                {
                    string product = item.TryGetProperty("product", out var pName) ? (pName.GetString() ?? "") : "";
                    double usage = item.TryGetProperty("usagePercent", out var uPct) ? uPct.GetDouble() : 0.0;

                    if (product.Equals("GrokBuild", StringComparison.OrdinalIgnoreCase))
                    {
                        result.GrokBuildPercent = usage;
                    }
                    else if (product.Equals("GrokChat", StringComparison.OrdinalIgnoreCase))
                    {
                        result.GrokChatPercent = usage;
                    }
                }
            }

            // 追加クレジット情報
            if (configElem.TryGetProperty("onDemandUsed", out var oduElem) && oduElem.TryGetProperty("val", out var oduVal))
            {
                result.OnDemandUsed = oduVal.GetDouble();
            }
            if (configElem.TryGetProperty("prepaidBalance", out var pbElem) && pbElem.TryGetProperty("val", out var pbVal))
            {
                result.PrepaidBalance = pbVal.GetDouble();
            }

            LogOutputReceived?.Invoke($"[Grok] 取得成功: {result.PlanName} {result.UsedPercent:F0}%使用済 (残{result.RemainingPercent:F0}%), リセット: {result.ResetTimeText}, Build: {result.GrokBuildPercent:F0}%, Chat: {result.GrokChatPercent:F0}%");
            return result;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Grok] JSON解析エラー: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> RefreshTokenAsync(string issuer, string clientId, string refreshToken, string authFilePath, string entryKey)
    {
        try
        {
            var tokenUrl = $"{issuer.TrimEnd('/')}/oauth2/token";
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new FormUrlEncodedContent(form)
            };
            req.Headers.Add("User-Agent", "grok-shell/1.0.25");

            var resp = await HttpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                LogOutputReceived?.Invoke($"[Grok] トークンリフレッシュ失敗: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? newAccessToken = root.TryGetProperty("access_token", out var atElem) ? atElem.GetString() : null;
            string? newRefreshToken = root.TryGetProperty("refresh_token", out var rtElem) ? rtElem.GetString() : null;
            int expiresIn = root.TryGetProperty("expires_in", out var eiElem) ? eiElem.GetInt32() : 3600;

            if (string.IsNullOrEmpty(newAccessToken))
            {
                return null;
            }

            // auth.json を安全に更新
            try
            {
                var text = await File.ReadAllTextAsync(authFilePath);
                var node = JsonNode.Parse(text);
                if (node is JsonObject rootObj && rootObj.TryGetPropertyValue(entryKey, out var entryNode) && entryNode is JsonObject targetObj)
                {
                    targetObj["key"] = newAccessToken;
                    if (!string.IsNullOrEmpty(newRefreshToken))
                    {
                        targetObj["refresh_token"] = newRefreshToken;
                    }
                    targetObj["expires_at"] = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");

                    var updatedJson = rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(authFilePath, updatedJson);
                    LogOutputReceived?.Invoke("[Grok] auth.json のトークン更新に成功しました");
                }
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Grok] auth.json 保存失敗 (インメモリのみ使用): {ex.Message}");
            }

            return newAccessToken;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Grok] トークンリフレッシュ例外: {ex.Message}");
            return null;
        }
    }

    private GrokQuotaData? TryFallbackFromLog()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var logPath = Path.Combine(userProfile, ".grok", "logs", "unified.jsonl");
            if (!File.Exists(logPath)) return null;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? lastMatchingLine = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("billing: fetched credits config"))
                {
                    lastMatchingLine = line;
                }
            }

            if (string.IsNullOrEmpty(lastMatchingLine)) return null;

            using var doc = JsonDocument.Parse(lastMatchingLine);
            if (doc.RootElement.TryGetProperty("ctx", out var ctxElem))
            {
                var fallbackJson = ctxElem.GetRawText();
                var data = ParseBillingJson(fallbackJson);
                if (data != null)
                {
                    LogOutputReceived?.Invoke("[Grok] ローカルログから最新の利用枠情報を復元しました");
                    return data;
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
