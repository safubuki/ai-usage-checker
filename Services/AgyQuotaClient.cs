using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

internal sealed record AgyCommandResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false);

public class AgyQuotaClient
{
    private readonly string _agyBinPath;
    private readonly string _geminiBinPath;

    // 起動直後・スリープ復帰後は認証更新とモデル設定の再取得が重なる。
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    internal Func<string?>? ExecutableResolver { get; init; }
    internal Func<ProcessStartInfo, TimeSpan, Task<AgyCommandResult>> CommandRunner { get; init; } = RunCommandAsync;

    public event Action<string>? LogOutputReceived;

    public AgyQuotaClient()
    {
        _agyBinPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin");
        _geminiBinPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin");
    }

    public string? FindAgyExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(_agyBinPath, "agy.exe"),
            Path.Combine(_agyBinPath, "agy"),
            Path.Combine(_geminiBinPath, "agy.exe"),
            Path.Combine(_geminiBinPath, "agy")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // PATH からの探索
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "agy",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1500);
                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0 && File.Exists(lines[0]))
                {
                    return lines[0].Trim();
                }
            }
        }
        catch { }

        return null;
    }

    public async Task<List<QuotaGroup>?> FetchQuotaSummaryAsync()
    {
        var agyExe = ExecutableResolver != null ? ExecutableResolver() : FindAgyExecutable();
        if (string.IsNullOrEmpty(agyExe))
        {
            LogOutputReceived?.Invoke("[Antigravity CLI] 'agy.exe' が見つかりません (未インストールまたはPATH未登録)");
            return null;
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                LogOutputReceived?.Invoke($"[Antigravity CLI] /usage 取得中 ({attempt}/2、上限{CommandTimeout.TotalSeconds:F0}秒)");
                var psi = new ProcessStartInfo
                {
                    FileName = agyExe,
                    Arguments = "-p \"/usage\" --output-format json",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // 定期取得からCLI本体のバックグラウンド更新を派生させない。
                psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";

                var timer = Stopwatch.StartNew();
                var result = await CommandRunner(psi, CommandTimeout);
                var groups = !result.TimedOut && result.ExitCode == 0 ? TryParseOutput(result.Stdout) : null;
                if (UsageFetcherService.HasUsableGeminiQuota(groups))
                {
                    LogOutputReceived?.Invoke($"[Antigravity CLI] 取得成功: {groups!.Count} グループ ({timer.Elapsed.TotalSeconds:F1}秒)");
                    return groups;
                }

                // CLIの生応答にはアカウント情報が混ざることがあるため、ログには理由だけを残す。
                bool permanentFailure = !result.TimedOut && IsPermanentFailure(result);
                var reason = result.TimedOut ? "タイムアウト" : permanentFailure ? "認証または設定のエラー"
                    : result.ExitCode != 0 ? $"終了コードエラー ({result.ExitCode})"
                    : string.IsNullOrWhiteSpace(result.Stdout) ? "標準出力が空" : "有効な利用枠を取得できませんでした";
                LogOutputReceived?.Invoke($"[Antigravity CLI] {reason} ({timer.Elapsed.TotalSeconds:F1}秒)");
                if (permanentFailure || attempt == 2) return null;
            }
            catch (Exception ex)
            {
                LogOutputReceived?.Invoke($"[Antigravity CLI] 実行例外: {ex.GetType().Name}");
                return null;
            }
            LogOutputReceived?.Invoke("[Antigravity CLI] 一時的な取得失敗のため、1回だけ再試行します。");
            await Task.Delay(RetryDelay);
        }
        return null;
    }

    internal static async Task<AgyCommandResult> RunCommandAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        if (startInfo.RedirectStandardInput) process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var completion = Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
        try
        {
            await completion.WaitAsync(timeout);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // 終了とパイプの排出を待ってから再試行し、子プロセスを重複させない。
            // 終了できない場合は例外を上位へ返し、新たなCLIを起動しない。
            await completion.WaitAsync(TimeSpan.FromSeconds(2));
            return new(-1, "", "", TimedOut: true);
        }
    }

    private static bool IsPermanentFailure(AgyCommandResult result)
    {
        var error = result.Stderr;
        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var errorElement))
                error += " " + errorElement.ToString();
        }
        catch (JsonException) { }
        return Regex.IsMatch(error,
            @"authentication required|not logged in|unauthenticated|invalid_grant|permission.denied|unknown (option|flag|argument)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static List<QuotaGroup>? TryParseOutput(string output)
    {
        var trimmed = output.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return TryParseJsonOutput(trimmed);
        return TryParsePlainTextOutput(trimmed);
    }

    private static List<QuotaGroup>? TryParseJsonOutput(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("status", out var status) &&
                !string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase)) return null;

            // command.data.groups 構造の確認
            if (!root.TryGetProperty("command", out var cmdElem) ||
                !cmdElem.TryGetProperty("data", out var dataElem) ||
                !dataElem.TryGetProperty("groups", out var groupsElem))
            {
                // JSONのresponse内に入ったTSVも、JSONデコードしてから読み取る。
                return root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String
                    ? TryParsePlainTextOutput(response.GetString() ?? "") : null;
            }

            var groups = new List<QuotaGroup>();

            foreach (var g in groupsElem.EnumerateArray())
            {
                var group = new QuotaGroup
                {
                    DisplayName = g.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Description = g.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                };

                if (g.TryGetProperty("buckets", out var bucketsElem))
                {
                    foreach (var b in bucketsElem.EnumerateArray())
                    {
                        if (!b.TryGetProperty("remaining_fraction", out var remainingElem) ||
                            remainingElem.ValueKind != JsonValueKind.Number ||
                            !remainingElem.TryGetDouble(out var remainingFraction) ||
                            !double.IsFinite(remainingFraction) || remainingFraction < 0 || remainingFraction > 1)
                        {
                            continue;
                        }

                        var bucket = new QuotaBucket
                        {
                            BucketId = b.TryGetProperty("id", out var bid) ? bid.GetString() ?? "" : "",
                            DisplayName = b.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "",
                            Description = b.TryGetProperty("description", out var bdesc) ? bdesc.GetString() ?? "" : "",
                            Window = b.TryGetProperty("window", out var win) ? win.GetString() ?? "" : "",
                            RemainingFraction = remainingFraction,
                            ResetTime = b.TryGetProperty("reset_time", out var rt) ? rt.GetString() ?? "" : ""
                        };
                        group.Buckets.Add(bucket);
                    }
                }

                groups.Add(group);
            }

            return groups;
        }
        catch
        {
            return null;
        }
    }

    private static List<QuotaGroup>? TryParsePlainTextOutput(string text)
    {
        try
        {
            var groupsDict = new Dictionary<string, QuotaGroup>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                var groupName = parts[0].Trim();
                var limitName = parts[1].Trim();
                var percentStr = parts[2].Trim();
                var resetTime = parts.Length > 3 ? parts[3].Trim() : "";

                var percentMatch = Regex.Match(percentStr, @"^(\d+(\.\d+)?)%$");
                if (!percentMatch.Success ||
                    !double.TryParse(percentMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ||
                    !double.IsFinite(pct) || pct < 0 || pct > 100 || string.IsNullOrWhiteSpace(groupName))
                {
                    continue;
                }
                double fraction = pct / 100.0;

                if (!groupsDict.TryGetValue(groupName, out var group))
                {
                    group = new QuotaGroup { DisplayName = groupName };
                    groupsDict[groupName] = group;
                }

                string window = "5h";
                if (limitName.Contains("Weekly", StringComparison.OrdinalIgnoreCase))
                {
                    window = "weekly";
                }

                group.Buckets.Add(new QuotaBucket
                {
                    BucketId = $"{groupName.ToLowerInvariant()}-{window}",
                    DisplayName = limitName,
                    Window = window,
                    RemainingFraction = fraction,
                    ResetTime = resetTime
                });
            }

            return groupsDict.Values.Count > 0 ? new List<QuotaGroup>(groupsDict.Values) : null;
        }
        catch
        {
            return null;
        }
    }
}
