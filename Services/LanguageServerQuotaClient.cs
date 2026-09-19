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
    private DateTime _lastSuccessfulQuery = DateTime.MinValue;

    public async Task<List<QuotaGroup>?> FetchQuotaSummaryAsync()
    {
        // 1. キャッシュされたトークンとポートで試行
        if (!string.IsNullOrEmpty(_cachedToken) && _cachedPort.HasValue)
        {
            var cachedResult = await TryQueryEndpointAsync(_cachedPort.Value, _cachedToken);
            if (cachedResult != null)
            {
                _lastSuccessfulQuery = DateTime.Now;
                return cachedResult;
            }
        }

        // 2. 稼働中の language_server_windows_x64 プロセスからトークンとポートを探索
        try
        {
            var (token, pid) = FindServerProcessInfo();
            if (string.IsNullOrEmpty(token) || !pid.HasValue)
            {
                return null;
            }

            _cachedToken = token;

            // 当該 PID がリッスンしているポートを探索
            var ports = FindListeningPortsForPid(pid.Value);
            foreach (var port in ports)
            {
                var result = await TryQueryEndpointAsync(port, token);
                if (result != null)
                {
                    _cachedPort = port;
                    _lastSuccessfulQuery = DateTime.Now;
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error querying LanguageServer quota: {ex.Message}");
        }

        return null;
    }

    private (string? Token, int? Pid) FindServerProcessInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                var cmd = obj["CommandLine"]?.ToString();
                var pid = Convert.ToInt32(obj["ProcessId"]);
                if (!string.IsNullOrEmpty(cmd))
                {
                    var match = Regex.Match(cmd, @"--csrf_token\s+([a-f0-9\-]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        return (match.Groups[1].Value, pid);
                    }
                }
            }
        }
        catch { }

        return (null, null);
    }

    private List<int> FindListeningPortsForPid(int pid)
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

    private async Task<List<QuotaGroup>?> TryQueryEndpointAsync(int port, string csrfToken)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-codeium-csrf-token", csrfToken);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await HttpClient.SendAsync(request);
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
