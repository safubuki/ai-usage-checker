using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class AgyQuotaClientTests
{
    private const string QuotaJson = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[
            {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.71},
            {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0}]}]}}}
        """;

    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("Antigravity CLI: 正常なJSONと残量0を取得し再試行しない", SuccessAsync);
        yield return new("Antigravity CLI: タイムアウト後の再試行で復帰", () => RetryAsync(new(-1, "", "", true)));
        yield return new("Antigravity CLI: 一時的な終了エラー後の再試行で復帰", () => RetryAsync(new(1, "", "503 Service unavailable")));
        yield return new("Antigravity CLI: 空の利用枠後の再試行で復帰", () => RetryAsync(new(0, "{\"command\":{\"data\":{\"groups\":[]}}}", "")));
        yield return new("Antigravity CLI: 再試行回数に上限を設ける", RetryLimitAsync);
        yield return new("Antigravity CLI: 認証エラーは再試行せず生応答をログに出さない", AuthenticationFailureAsync);
        yield return new("Antigravity CLI: JSON内のresponseをデコードして利用枠を読む", ResponseTextAsync);
        yield return new("Antigravity CLI: 失敗JSON・不正な残量を正常値として扱わない", InvalidOutputAsync);
        yield return new("Antigravity CLI: 未導入時はコマンドを実行しない", MissingExecutableAsync);
        yield return new("Antigravity CLI: 実プロセスの出力を取得して終了を待つ", ProcessOutputAsync);
        yield return new("Antigravity CLI: タイムアウトした実プロセスを終了する", ProcessTimeoutAsync);
    }

    private static AgyQuotaClient CreateClient(Func<ProcessStartInfo, TimeSpan, Task<AgyCommandResult>> runner) => new()
    {
        ExecutableResolver = () => "test-agy.exe",
        CommandRunner = runner,
        RetryDelay = TimeSpan.Zero
    };

    private static async Task SuccessAsync()
    {
        int calls = 0;
        var client = CreateClient((psi, timeout) =>
        {
            calls++;
            Check.True(timeout > TimeSpan.FromSeconds(15), "認証更新を待つ時間が旧制限のままです");
            Check.Equal("-p \"/usage\" --output-format json", psi.Arguments, "利用枠の読取以外を実行しています");
            Check.Equal("true", psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"], "子プロセスの自動更新を無効にしていません");
            Check.True(psi.CreateNoWindow && psi.RedirectStandardInput, "非対話の取得になっていません");
            return Task.FromResult(new AgyCommandResult(0, QuotaJson, ""));
        });
        var result = await client.FetchQuotaSummaryAsync();
        Check.Equal(1, calls, "成功時にも再試行しました");
        Check.Equal(.71, result![0].Buckets[0].RemainingFraction, "残量が異なります");
        Check.Equal(0d, result[0].Buckets[1].RemainingFraction, "枯渇した枠を落としました");
    }

    private static async Task RetryAsync(AgyCommandResult first)
    {
        int calls = 0;
        var client = CreateClient((_, _) => Task.FromResult(++calls == 1 ? first : new(0, QuotaJson, "")));
        var result = await client.FetchQuotaSummaryAsync();
        Check.Equal(2, calls, "一時的な失敗から再試行していません");
        Check.Equal(.71, result![0].Buckets[0].RemainingFraction, "再試行の正常値を反映していません");
    }

    private static async Task RetryLimitAsync()
    {
        int calls = 0;
        var client = CreateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(new AgyCommandResult(-1, "", "", true));
        });
        Check.True(await client.FetchQuotaSummaryAsync() == null, "失敗を正常値として返しました");
        Check.Equal(2, calls, "2回で再試行を打ち切っていません");
    }

    private static async Task AuthenticationFailureAsync()
    {
        int calls = 0;
        var logs = new List<string>();
        var client = CreateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(new AgyCommandResult(1,
                "{\"status\":\"ERROR\",\"error\":\"authentication required for secret@example.test\"}", ""));
        });
        client.LogOutputReceived += logs.Add;
        Check.True(await client.FetchQuotaSummaryAsync() == null, "認証エラーを成功としました");
        Check.Equal(1, calls, "認証エラーを再試行しています");
        Check.True(logs.All(log => !log.Contains("secret@")), "アカウント情報をログに出しました");
    }

    private static Task ResponseTextAsync()
    {
        const string text = "Gemini Models\tWeekly Limit Remaining\t71%\t2026-11-14T06:05:50Z\n";
        foreach (var output in new[] { text, JsonSerializer.Serialize(new { status = "SUCCESS", response = text }) })
        {
            var result = AgyQuotaClient.TryParseOutput(output);
            Check.Equal(.71, result![0].Buckets[0].RemainingFraction, "TSVの残量を読めません");
            Check.Equal("weekly", result[0].Buckets[0].Window, "週次枠を識別できません");
            Check.Equal("2026-11-14T06:05:50Z", result[0].Buckets[0].ResetTime, "リセット日時を保持していません");
        }
        return Task.CompletedTask;
    }

    private static Task InvalidOutputAsync()
    {
        foreach (var output in new[] { QuotaJson.Replace("SUCCESS", "ERROR"), "[]", "{broken}",
            "Gemini Models\tWeekly Limit Remaining\t-10%", "Gemini Models\tWeekly Limit Remaining\t101%" })
            Check.True(AgyQuotaClient.TryParseOutput(output) == null, "不正な応答を正常値としました");
        var partial = AgyQuotaClient.TryParseOutput(QuotaJson.Replace("0.71", "null"));
        Check.Equal(1, partial![0].Buckets.Count, "不正な残量だけを除外していません");
        return Task.CompletedTask;
    }

    private static async Task MissingExecutableAsync()
    {
        var client = new AgyQuotaClient
        {
            ExecutableResolver = () => null,
            CommandRunner = (_, _) => throw new InvalidOperationException("起動してはいけません")
        };
        Check.True(await client.FetchQuotaSummaryAsync() == null, "未導入時に値を返しました");
    }

    private static ProcessStartInfo PowerShell(string script) => new()
    {
        FileName = "powershell.exe",
        Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
    };

    private static async Task ProcessOutputAsync()
    {
        var result = await AgyQuotaClient.RunCommandAsync(
            PowerShell("[Console]::Out.Write('quota'); [Console]::Error.Write('diagnostic'); exit 7"), TimeSpan.FromSeconds(5));
        Check.Equal(7, result.ExitCode, "終了コードが失われました");
        Check.Equal("quota", result.Stdout, "標準出力を取得できません");
        Check.Equal("diagnostic", result.Stderr, "標準エラーを取得できません");
    }

    private static async Task ProcessTimeoutAsync()
    {
        using var temp = new TemporaryDirectory();
        var pidPath = temp.FilePath("pid.txt");
        var result = await AgyQuotaClient.RunCommandAsync(
            PowerShell($"[IO.File]::WriteAllText('{pidPath.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 30"),
            TimeSpan.FromSeconds(2));
        Check.True(result.TimedOut, "停止したプロセスをタイムアウトにしていません");
        Check.True(System.IO.File.Exists(pidPath), "テスト用プロセスが開始されませんでした");
        int pid = int.Parse(await System.IO.File.ReadAllTextAsync(pidPath));
        bool exited;
        try { using var process = Process.GetProcessById(pid); exited = process.HasExited; }
        catch (ArgumentException) { exited = true; }
        Check.True(exited, "タイムアウト後にプロセスが残っています");
    }
}
