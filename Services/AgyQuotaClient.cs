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

public class AgyQuotaClient
{
    private readonly string _agyBinPath;
    private readonly string _geminiBinPath;

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
        var agyExe = FindAgyExecutable();
        if (string.IsNullOrEmpty(agyExe))
        {
            LogOutputReceived?.Invoke("[Antigravity CLI] 'agy.exe' が見つかりません (未インストールまたはPATH未登録)");
            return null;
        }

        try
        {
            LogOutputReceived?.Invoke($"[Antigravity CLI] 実行中: \"{agyExe}\" -p \"/usage\" --output-format json");

            var psi = new ProcessStartInfo
            {
                FileName = agyExe,
                Arguments = "-p \"/usage\" --output-format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // 利用状況の定期取得からCLI本体のバックグラウンド更新を派生させない。
            // この子プロセスだけに適用し、手動起動や明示的な `agy update` は従来どおり有効にする。
            psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
            var completedTask = await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync()), timeoutTask);

            if (completedTask == timeoutTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                LogOutputReceived?.Invoke("[Antigravity CLI] コマンド実行がタイムアウトしました (15秒)");
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                LogOutputReceived?.Invoke($"[Antigravity CLI] 終了コードエラー ({process.ExitCode}): {stderr.Trim()}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                LogOutputReceived?.Invoke("[Antigravity CLI] 標準出力が空でした");
                return null;
            }

            // 1. JSON レスポンスのパース
            var groups = TryParseJsonOutput(stdout);
            if (groups != null && groups.Count > 0)
            {
                LogOutputReceived?.Invoke($"[Antigravity CLI] 取得成功: {groups.Count} 個のグループを検出");
                return groups;
            }

            // 2. 万が一プレーンテキストの場合のフォールバックパース
            groups = TryParsePlainTextOutput(stdout);
            if (groups != null && groups.Count > 0)
            {
                LogOutputReceived?.Invoke($"[Antigravity CLI] テキスト形式より取得成功: {groups.Count} 個のグループを検出");
                return groups;
            }

            LogOutputReceived?.Invoke("[Antigravity CLI] クォータ情報の解析に失敗しました");
            return null;
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Antigravity CLI] 実行例外: {ex.Message}");
            return null;
        }
    }

    private List<QuotaGroup>? TryParseJsonOutput(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // command.data.groups 構造の確認
            if (!root.TryGetProperty("command", out var cmdElem) ||
                !cmdElem.TryGetProperty("data", out var dataElem) ||
                !dataElem.TryGetProperty("groups", out var groupsElem))
            {
                return null;
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
                            !remainingElem.TryGetDouble(out var remainingFraction))
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

    private List<QuotaGroup>? TryParsePlainTextOutput(string text)
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

                if (!groupsDict.TryGetValue(groupName, out var group))
                {
                    group = new QuotaGroup { DisplayName = groupName };
                    groupsDict[groupName] = group;
                }

                var percentMatch = Regex.Match(percentStr, @"(\d+(\.\d+)?)%");
                if (!percentMatch.Success ||
                    !double.TryParse(percentMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    continue;
                }
                double fraction = pct / 100.0;

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
