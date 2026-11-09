using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIUsageChecker.Services;

public class QuotaBucket
{
    public string BucketId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Window { get; set; } = "";
    public double RemainingFraction { get; set; }
    public string ResetTime { get; set; } = "";
}

public class QuotaGroup
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<QuotaBucket> Buckets { get; set; } = new();
}

public class LanguageServerQuotaClient
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private string? _cachedToken;
    private int? _cachedPort;
    public event Action<string>? LogOutputReceived;

    public async Task<List<QuotaGroup>?> FetchQuotaSummaryAsync()
    {
        // 1. キャッシュされたトークンとポートで試行
        if (!string.IsNullOrEmpty(_cachedToken) && _cachedPort.HasValue)
        {
            var cachedResult = await TryQueryEndpointAsync(_cachedPort.Value, _cachedToken);
            if (HasQuota(cachedResult))
            {
                return cachedResult;
            }
            _cachedToken = null;
            _cachedPort = null;
        }

        // 2. 稼働中の language_server_windows_x64 プロセスからトークンとポートを探索
        try
        {
            var servers = FindServerProcesses();
            if (servers.Count == 0)
            {
                LogOutputReceived?.Invoke("[Antigravity IDE] 起動中の言語サーバーが見つかりません。");
                return null;
            }

            // 複数ウィンドウや再起動時、先頭のサーバーが未認証・終了中でも次を確認する。
            foreach (var (token, pid) in servers)
            {
                foreach (var port in FindListeningPortsForPid(pid))
                {
                    var result = await TryQueryEndpointAsync(port, token);
                    if (HasQuota(result))
                    {
                        _cachedToken = token;
                        _cachedPort = port;
                        return result;
                    }
                }
            }
            LogOutputReceived?.Invoke($"[Antigravity IDE] {servers.Count} 個の言語サーバーから利用枠を取得できませんでした。");
        }
        catch (Exception ex)
        {
            LogOutputReceived?.Invoke($"[Antigravity IDE] 探索エラー: {ex.GetType().Name}");
        }

        return null;
    }

    private static bool HasQuota(List<QuotaGroup>? groups) => UsageFetcherService.HasUsableGeminiQuota(groups);

    protected virtual List<(string Token, int Pid)> FindServerProcesses()
    {
        var servers = new List<(string Token, int Pid)>();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server%'");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                var cmd = obj["CommandLine"]?.ToString();
                var pid = Convert.ToInt32(obj["ProcessId"]);
                if (!string.IsNullOrEmpty(cmd) && cmd.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(cmd, "--csrf_token(?:\\s+|=)\"?([a-f0-9\\-]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        servers.Add((match.Groups[1].Value, pid));
                    }
                }
            }
        }
        return servers;
    }

    protected virtual List<int> FindListeningPortsForPid(int pid)
    {
        var ports = new List<int>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
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
                foreach (var line in lines)
                {
                    if (line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase) && line.EndsWith($" {pid}"))
                    {
                        var match = Regex.Match(line, @"127\.0\.0\.1:(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
                        {
                            ports.Add(port);
                        }
                    }
                }
            }
        }
        catch { }

        return ports;
    }

    protected virtual async Task<List<QuotaGroup>?> TryQueryEndpointAsync(int port, string csrfToken)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-codeium-csrf-token", csrfToken);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await HttpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("response", out var resp) && resp.TryGetProperty("groups", out var groupsElem))
                {
                    var groups = new List<QuotaGroup>();
                    foreach (var g in groupsElem.EnumerateArray())
                    {
                        var group = new QuotaGroup
                        {
                            DisplayName = g.GetProperty("displayName").GetString() ?? "",
                            Description = g.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                        };

                        if (g.TryGetProperty("buckets", out var bucketsElem))
                        {
                            foreach (var b in bucketsElem.EnumerateArray())
                            {
                                if (!b.TryGetProperty("remainingFraction", out var remainingElem) ||
                                    !remainingElem.TryGetDouble(out var remainingFraction))
                                {
                                    continue;
                                }

                                var bucket = new QuotaBucket
                                {
                                    BucketId = b.TryGetProperty("bucketId", out var bid) ? bid.GetString() ?? "" : "",
                                    DisplayName = b.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                                    Description = b.TryGetProperty("description", out var bdesc) ? bdesc.GetString() ?? "" : "",
                                    Window = b.TryGetProperty("window", out var win) ? win.GetString() ?? "" : "",
                                    RemainingFraction = remainingFraction,
                                    ResetTime = b.TryGetProperty("resetTime", out var rt) ? rt.GetString() ?? "" : ""
                                };
                                group.Buckets.Add(bucket);
                            }
                        }
                        groups.Add(group);
                    }
                    return groups;
                }
            }
        }
        catch { }

        return null;
    }
}
