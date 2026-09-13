using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

public class CopilotQuotaData
{
    public bool IsSuccess { get; set; }
    public bool IsAuthRequired { get; set; }
    public string PlanName { get; set; } = "Copilot Pro";
    public string QuotaTitle { get; set; } = "プレミアム要求";
    public string UnitName { get; set; } = "要求";
    public bool IsYearlySubscriber { get; set; } = true;
    public double RemainingPercent { get; set; } = 98.0;
    public double UsedPercent { get; set; } = 2.0;
    public int UsedCount { get; set; } = 6;
    public int TotalCount { get; set; } = 300;
    public string ResetTimeText { get; set; } = "10/01 09:00 リセット";
    public string RawResetNotice { get; set; } = "10月1日 の 9:00 にリセットされます";
}

public class CopilotQuotaClient
{
    public event Action<string>? LogOutputReceived;

    public static bool IsGhAuth()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth status",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2500);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CopilotQuotaData?> FetchCopilotQuotaAsync()
    {
        try
        {
            LogOutputReceived?.Invoke("[Copilot] 'gh api /copilot_internal/user' を実行中...");

            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "api /copilot_internal/user",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                LogOutputReceived?.Invoke("[Copilot] gh プロセスの起動に失敗しました");
                return new CopilotQuotaData { IsSuccess = false, IsAuthRequired = true };
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync();
            var json = await outputTask;
            var error = await errorTask;

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                bool isAuthErr = error.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                                 error.Contains("logged in", StringComparison.OrdinalIgnoreCase) ||
                                 error.Contains("401") || error.Contains("token", StringComparison.OrdinalIgnoreCase);
                LogOutputReceived?.Invoke($"[Copilot] gh api 失敗 (Code {proc.ExitCode}): {error.Trim()}");
                return new CopilotQuotaData { IsSuccess = false, IsAuthRequired = isAuthErr };
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = new CopilotQuotaData { IsSuccess = true };

            // 1. 契約タイプ (access_type_sku) の判定
            string accessType = root.TryGetProperty("access_type_sku", out var atElem) ? (atElem.GetString() ?? "") : "";
            bool isYearly = accessType.Contains("yearly", StringComparison.OrdinalIgnoreCase);
            data.IsYearlySubscriber = isYearly;

            // 年間契約者（ユーザー様環境）: 「プレミアム要求」 / 単位: プレミアムリクエスト (要求/回)
            // そうではない人 (月間、従量制等): 「AI Credits」 / 単位: Credits (クレジット)
            if (isYearly)
            {
                data.QuotaTitle = "プレミアム要求";
                data.UnitName = "要求";
            }
            else
            {
                data.QuotaTitle = "AI Credits";
                data.UnitName = "Credits";
            }

            // 2. 利用状況クォータの抽出 (premium_interactions または ai_credits / credits)
            JsonElement targetElem = default;
            bool found = false;

            if (root.TryGetProperty("quota_snapshots", out var qsElem) && qsElem.ValueKind == JsonValueKind.Object)
            {
                if (qsElem.TryGetProperty("premium_interactions", out var piElem))
                {
                    targetElem = piElem;
                    found = true;
                }
                else if (qsElem.TryGetProperty("ai_credits", out var acElem))
                {
                    targetElem = acElem;
                    found = true;
                    data.QuotaTitle = "AI Credits";
                    data.UnitName = "Credits";
                }
                else if (qsElem.TryGetProperty("credits", out var crElem))
                {
                    targetElem = crElem;
                    found = true;
                    data.QuotaTitle = "AI Credits";
                    data.UnitName = "Credits";
                }
            }

            if (found)
            {
                if (targetElem.TryGetProperty("percent_remaining", out var prElem))
                {
                    data.RemainingPercent = Math.Round(prElem.GetDouble(), 1);
                    data.UsedPercent = Math.Round(Math.Max(0.0, 100.0 - data.RemainingPercent), 1);
                }

                if (targetElem.TryGetProperty("credits_used", out var cuElem))
                {
                    data.UsedCount = cuElem.GetInt32();
                }

                if (targetElem.TryGetProperty("entitlement", out var entElem))
                {
                    data.TotalCount = entElem.GetInt32();
                }
            }

            if (root.TryGetProperty("quota_reset_date_utc", out var resetElem) &&
                DateTime.TryParse(resetElem.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dtUtc))
            {
                var dtLocal = dtUtc.ToLocalTime();
                data.ResetTimeText = $"{dtLocal:MM/dd HH:mm} リセット";
                data.RawResetNotice = $"{dtLocal:M月d日 の H:mm} にリセットされます";
            }

            string unitDisplay = isYearly ? "回" : " Credits";
            LogOutputReceived?.Invoke($"[Copilot] 取得成功: {data.QuotaTitle} {data.UsedPercent:F0}% 使用済み (残 {data.RemainingPercent:F0}% / {data.TotalCount - data.UsedCount}{unitDisplay}), リセット: {data.ResetTimeText}");

            return data;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Copilot] 例外発生: {ex.Message}");
            return null;
        }
    }
}
