using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIUsageChecker.Models;
using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class SubscriptionStatusTests
{
    // 再契約直後など、Claude Code がプロフィールを再取得する前の古い設定ファイル
    private const string StaleUnsubscribedConfig = """
        {"oauthAccount":{"billingType":"none","organizationType":""},
         "cachedUsageUtilization":{"utilization":{
           "five_hour":{"utilization":null,"resets_at":null},
           "seven_day":{"utilization":6,"resets_at":"2026-10-30T10:00:00Z"}}}}
        """;

    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("Claude契約: 設定ファイルが古くてもCLIの契約種別で契約中と判定", StaleConfigWithCliSubscriptionAsync);
        yield return new("Claude契約: CLIで未ログインなら設定ファイルより優先", CliLoggedOutAsync);
        yield return new("Claude契約: CLI・設定ファイルとも有料プランなしの場合だけ未契約", CliAndConfigUnsubscribedAsync);
        yield return new("Claude契約: CLIで契約中・利用枠なしは未契約にせず取得エラー", CliSubscribedWithoutUsageAsync);
        yield return new("Claude契約: CLIの結果が不正なら設定ファイルで判定", InvalidCliOutputAsync);
        yield return new("Claude契約: 判定前のデータなしを未契約と表示しない", ClaudeNoDataIsNotUnsubscribedAsync);
        yield return new("Claude利用量: 利用量APIの最新値を保存値より優先", LiveUsageOverridesSavedAsync);
        yield return new("Claude利用量: 利用量APIの失敗時は保存値を表示", () => LiveUsageFailureFallsBackAsync(HttpStatusCode.InternalServerError));
        yield return new("Claude利用量: 利用量APIの通信エラー時は保存値を表示", () => LiveUsageFailureFallsBackAsync(null));
        yield return new("Claude利用量: トークン期限切れならClaude Codeと同じ手順で更新して最新値を取得", ExpiredTokenRefreshAsync);
        yield return new("Claude利用量: 利用量APIの認証エラー時は1回だけトークンを更新して再試行", UnauthorizedUsageRefreshesOnceAsync);
        yield return new("Claude利用量: Claude Codeが更新中(ロック中)なら更新せず保存値を表示", RefreshLockHeldAsync);
        yield return new("Claude利用量: トークン更新が拒否されたら認証情報を変えず保存値を表示", RefreshRejectedAsync);
        yield return new("Claude利用量: 保存値は前回取得時刻付きで表示", SavedValueShownAsPreviousAsync);
        yield return new("GPT契約: plan_type=free は未契約、利用枠は表示", () => GptPlanAsync("free", false));
        yield return new("GPT契約: plan_type=plus は契約中", () => GptPlanAsync("plus", true));
        yield return new("Copilot契約: copilot_plan=free は未契約", () => CopilotPlanAsync("free", "free_limited_copilot", false));
        yield return new("Copilot契約: copilot_plan=individual は契約中", () => CopilotPlanAsync("individual", "yearly_subscriber_quota", true));
        yield return new("Grok契約: subscriptionTier=Free は未契約", () => GrokPlanAsync("Free", false));
        yield return new("Grok契約: subscriptionTier=SuperGrok は契約中", () => GrokPlanAsync("SuperGrok", true));
        yield return new("Grok内訳: 機能ごとに固定色を割り当て、合計に届かない分はその他で補う", GrokBreakdownSegmentsAsync);
    }

    private static Task<ClaudeQuotaData> FetchClaudeAsync(string configPath, string? cliJson)
    {
        return new ClaudeQuotaClient().FetchClaudeQuotaAsync(configPath, () => Task.FromResult(cliJson));
    }

    private static async Task StaleConfigWithCliSubscriptionAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("claude.json");
        await File.WriteAllTextAsync(path, StaleUnsubscribedConfig);
        var result = await FetchClaudeAsync(path, """{"loggedIn":true,"subscriptionType":"max"}""");
        Check.True(result.IsSuccess && result.IsSubscribed && !result.IsAuthRequired,
            "CLIで契約中なのに未契約と判定しています");
        Check.Equal("Claude Max", result.PlanName, "CLIの契約種別をプラン名に反映していません");
        Check.Equal(94.0, result.WeeklyRemainingPercent, "契約判定後に利用枠を表示していません");
        Check.Equal(100.0, result.FiveHourRemainingPercent, "使用率がnullの5時間枠を未使用として扱っていません");
    }

    private static async Task CliLoggedOutAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("claude.json");
        await File.WriteAllTextAsync(path, """{"oauthAccount":{"billingType":"stripe_subscription"}}""");
        var result = await FetchClaudeAsync(path, """{"loggedIn":false}""");
        Check.True(result.IsAuthRequired && !result.IsSuccess && !result.IsSubscribed,
            "CLIの未ログインを反映していません");
    }

    private static async Task CliAndConfigUnsubscribedAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("claude.json");
        await File.WriteAllTextAsync(path, StaleUnsubscribedConfig);
        var result = await FetchClaudeAsync(path, """{"loggedIn":true,"subscriptionType":null}""");
        Check.True(result.IsSuccess && !result.IsSubscribed && !result.IsAuthRequired,
            "有料プランなしを未契約と判定していません");
        Check.True(result.StatusMessage.Contains("未契約", StringComparison.Ordinal), "未契約の状態を案内していません");
    }

    private static async Task CliSubscribedWithoutUsageAsync()
    {
        using var temp = new TemporaryDirectory();
        var result = await FetchClaudeAsync(temp.FilePath("missing.json"), """{"loggedIn":true,"subscriptionType":"pro"}""");
        Check.True(!result.IsSuccess && !result.IsAuthRequired && result.IsSubscribed,
            "CLIで契約中・ログイン済みなのに未契約または未ログインと判定しています");
        Check.True(!result.StatusMessage.Contains("未契約", StringComparison.Ordinal), "利用枠の取得失敗を未契約と表示しています");
    }

    private static async Task InvalidCliOutputAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("claude.json");
        await File.WriteAllTextAsync(path, """
            {"oauthAccount":{"billingType":"stripe_subscription","organizationType":"claude_pro"},
             "cachedUsageUtilization":{"utilization":{"seven_day":{"utilization":10}}}}
            """);
        var result = await FetchClaudeAsync(path, "error: unknown command");
        Check.True(result.IsSuccess && result.IsSubscribed, "CLIを確認できない場合に設定ファイルで判定していません");
        Check.Equal("Claude Pro", result.PlanName, "設定ファイルのプランを反映していません");
    }

    private static async Task ClaudeNoDataIsNotUnsubscribedAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new PlanFetcher(temp.FilePath("cache.json")) { ClaudeThrows = true };
        var item = CreateItem(AiServiceType.Claude);
        await fetcher.FetchUsageAsync(item);
        Check.True(item.CliInfo.StatusBadgeText != "未契約", "判定できていないClaudeを未契約と表示しています");
    }

    private const string SubscribedConfig = """
        {"oauthAccount":{"billingType":"stripe_subscription","organizationType":"claude_pro"},
         "cachedUsageUtilization":{"fetchedAtMs":1792972710015,"utilization":{
           "five_hour":{"utilization":0,"resets_at":null},
           "seven_day":{"utilization":6,"resets_at":"2026-10-30T10:00:00Z"}}}}
        """;

    private const string LiveUsageJson = """
        {"five_hour":{"utilization":14,"resets_at":"2026-10-26T05:00:00Z"},
         "seven_day":{"utilization":8,"resets_at":"2026-10-30T10:00:00Z"}}
        """;

    private const string TokenResponseJson = """
        {"access_token":"new-access-token","refresh_token":"new-refresh-token","expires_in":28800,
         "scope":"user:profile user:inference"}
        """;

    private static async Task<(TemporaryDirectory Temp, string ConfigPath, string CredentialsPath)> CreateClaudeFilesAsync(DateTimeOffset tokenExpiresAt)
    {
        var temp = new TemporaryDirectory();
        var configPath = temp.FilePath("claude.json");
        var credentialsPath = temp.FilePath("credentials.json");
        await File.WriteAllTextAsync(configPath, SubscribedConfig);
        await File.WriteAllTextAsync(credentialsPath, $$$"""
            {"claudeAiOauth":{"accessToken":"dummy-access-token","refreshToken":"dummy-refresh-token",
              "expiresAt":{{{tokenExpiresAt.ToUnixTimeMilliseconds()}}},"scopes":["user:inference","user:profile","user:plugins"],
              "subscriptionType":"pro","rateLimitTier":"default_claude_ai"},
             "otherSetting":{"keep":true}}
            """);
        return (temp, configPath, credentialsPath);
    }

    private static Task<ClaudeQuotaData> FetchClaudeWithHttpAsync(HttpMessageHandler handler, string configPath, string credentialsPath)
    {
        var client = new ClaudeQuotaClient(new HttpClient(handler)) { LockRetryDelay = TimeSpan.FromMilliseconds(10) };
        return client.FetchClaudeQuotaAsync(configPath, null, credentialsPath);
    }

    private static void CheckLiveUsage(ClaudeQuotaData result)
    {
        Check.True(result.IsSuccess && result.IsSubscribed && !result.IsSavedValue, "利用量APIの最新値で契約中と判定していません");
        Check.Equal(86.0, result.FiveHourRemainingPercent, "5時間枠に最新値を反映していません");
        Check.Equal(92.0, result.WeeklyRemainingPercent, "週次枠に最新値を反映していません");
        Check.True(result.FiveHourResetText.Contains("リセット", StringComparison.Ordinal), "5時間枠のリセット時刻を表示していません");
    }

    private static void CheckSavedUsage(ClaudeQuotaData result)
    {
        Check.True(result.IsSuccess && result.IsSubscribed && !result.IsAuthRequired,
            "利用量APIの失敗を認証エラーまたは未契約として扱っています");
        Check.True(result.IsSavedValue && result.SavedAt.HasValue, "保存値を使ったことと保存時刻を返していません");
        Check.Equal(94.0, result.WeeklyRemainingPercent, "保存された利用枠を表示していません");
        Check.Equal("未使用", result.FiveHourResetText, "未使用の5時間枠を案内していません");
    }

    private static async Task CheckCredentialsUnchangedAsync(string credentialsPath)
    {
        var json = await File.ReadAllTextAsync(credentialsPath);
        Check.True(json.Contains("dummy-access-token", StringComparison.Ordinal) &&
            json.Contains("dummy-refresh-token", StringComparison.Ordinal), "更新に失敗したのに認証情報を書き換えました");
    }

    private static async Task LiveUsageOverridesSavedAsync()
    {
        var (temp, configPath, credentialsPath) = await CreateClaudeFilesAsync(DateTimeOffset.UtcNow.AddHours(1));
        using var _ = temp;
        using var handler = new ClaudeApiHandler([ClaudeApiHandler.Usage(HttpStatusCode.OK, LiveUsageJson)]);
        CheckLiveUsage(await FetchClaudeWithHttpAsync(handler, configPath, credentialsPath));
        handler.CheckCompleted();
        Check.Equal("dummy-access-token", handler.Requests[0].BearerToken, "認証情報のトークンを使っていません");
        Check.True(handler.Requests[0].HasBetaHeader, "利用量APIに必要なヘッダーがありません");
        await CheckCredentialsUnchangedAsync(credentialsPath);
    }

    private static async Task LiveUsageFailureFallsBackAsync(HttpStatusCode? status)
    {
        var (temp, configPath, credentialsPath) = await CreateClaudeFilesAsync(DateTimeOffset.UtcNow.AddHours(1));
        using var _ = temp;
        using var handler = new ClaudeApiHandler([ClaudeApiHandler.Usage(status, "{}")]);
        CheckSavedUsage(await FetchClaudeWithHttpAsync(handler, configPath, credentialsPath));
        handler.CheckCompleted();
    }

    private static async Task ExpiredTokenRefreshAsync()
    {
        var (temp, configPath, credentialsPath) = await CreateClaudeFilesAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        using var _ = temp;
        using var handler = new ClaudeApiHandler([
            ClaudeApiHandler.Token(HttpStatusCode.OK, TokenResponseJson),
            ClaudeApiHandler.Usage(HttpStatusCode.OK, LiveUsageJson)]);
        CheckLiveUsage(await FetchClaudeWithHttpAsync(handler, configPath, credentialsPath));
        handler.CheckCompleted();

        var body = handler.Requests[0].Body;
        Check.True(body.Contains("\"grant_type\":\"refresh_token\"", StringComparison.Ordinal) &&
            body.Contains("\"refresh_token\":\"dummy-refresh-token\"", StringComparison.Ordinal) &&
            body.Contains("\"client_id\":\"9d1c250a-e61b-44d9-88ed-5944d1962f5e\"", StringComparison.Ordinal) &&
            body.Contains("\"scope\":\"user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload\"", StringComparison.Ordinal),
            "Claude Code と同じ内容でトークン更新を要求していません");
        Check.Equal("new-access-token", handler.Requests[1].BearerToken, "更新後のトークンで利用量を取得していません");

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath));
        var oauth = saved.RootElement.GetProperty("claudeAiOauth");
        Check.Equal("new-access-token", oauth.GetProperty("accessToken").GetString(), "新しいアクセストークンを保存していません");
        Check.Equal("new-refresh-token", oauth.GetProperty("refreshToken").GetString(), "新しいリフレッシュトークンを保存していません");
        Check.True(DateTimeOffset.FromUnixTimeMilliseconds(oauth.GetProperty("expiresAt").GetInt64()) > DateTimeOffset.UtcNow.AddHours(7),
            "新しい有効期限を保存していません");
        Check.Equal("pro", oauth.GetProperty("subscriptionType").GetString(), "契約種別を保持していません");
        Check.Equal("default_claude_ai", oauth.GetProperty("rateLimitTier").GetString(), "レート制限区分を保持していません");
        Check.True(saved.RootElement.GetProperty("otherSetting").GetProperty("keep").GetBoolean(), "他の設定を保持していません");
        Check.True(!Directory.Exists(Path.Combine(temp.Path, ".oauth_refresh.lock")), "更新後にロックを解放していません");
    }

    private static async Task UnauthorizedUsageRefreshesOnceAsync()
    {
        var (temp, configPath, credentialsPath) = await CreateClaudeFilesAsync(DateTimeOffset.UtcNow.AddHours(1));
        using var _ = temp;
        using var handler = new ClaudeApiHandler([
            ClaudeApiHandler.Usage(HttpStatusCode.Unauthorized, "{}"),
            ClaudeApiHandler.Token(HttpStatusCode.OK, TokenResponseJson),
            ClaudeApiHandler.Usage(HttpStatusCode.OK, LiveUsageJson)]);
        CheckLiveUsage(await FetchClaudeWithHttpAsync(handler, configPath, credentialsPath));
        handler.CheckCompleted();
        Check.Equal("new-access-token", handler.Requests[2].BearerToken, "更新後のトークンで再試行していません");
    }

    private static async Task RefreshLockHeldAsync()
    {
        var (temp, configPath, credentialsPath) = await CreateClaudeFilesAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        using var _ = temp;
        var lockPath = Path.Combine(temp.Path, ".oauth_refresh.lock");
        Directory.CreateDirectory(lockPath);
        using var handler = new ClaudeApiHandler([]);
        CheckSavedUsage(await FetchClaudeWithHttpAsync(handler, configPath, credentialsPath));
        handler.CheckCompleted();
        await CheckCredentialsUnchangedAsync(credentialsPath);
        Check.True(Directory.Exists(lockPath), "他プロセスのロックを削除しました");
    }

    private static async Task RefreshRejectedAsync()
    {
        var (temp, configPath, credentialsPath) = await CreateClaudeFilesAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        using var _ = temp;
        using var handler = new ClaudeApiHandler([
            ClaudeApiHandler.Token(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]);
        CheckSavedUsage(await FetchClaudeWithHttpAsync(handler, configPath, credentialsPath));
        handler.CheckCompleted();
        await CheckCredentialsUnchangedAsync(credentialsPath);
        Check.True(!Directory.Exists(Path.Combine(temp.Path, ".oauth_refresh.lock")), "更新失敗後にロックを解放していません");
    }

    private static async Task SavedValueShownAsPreviousAsync()
    {
        using var temp = new TemporaryDirectory();
        var savedAt = new DateTime(2026, 10, 26, 8, 58, 0);
        var fetcher = new PlanFetcher(temp.FilePath("cache.json"))
        {
            Claude = new ClaudeQuotaData
            {
                IsSuccess = true, IsSubscribed = true, IsSavedValue = true, SavedAt = savedAt,
                HasFiveHourLimit = true, FiveHourRemainingPercent = 100, FiveHourResetText = "未使用",
                HasWeeklyLimit = true, WeeklyRemainingPercent = 94, WeeklyResetText = "10/30 19:00 リセット",
                StatusMessage = "プラン: Claude Pro (稼働中)"
            }
        };
        var item = CreateItem(AiServiceType.Claude);
        await fetcher.FetchUsageAsync(item);
        Check.Equal("前回 10/26 08:58 / 未使用", item.PrimaryLimit.ResetTimeText, "保存値に前回の取得時刻を表示していません");
        Check.Equal(94d, item.SecondaryLimit?.RemainingPercent ?? 0, "保存された週次枠を表示していません");
        Check.True(item.CliInfo.StatusMessage.Contains("前回取得 (10/26 08:58)", StringComparison.Ordinal), "古い値であることを案内していません");
        Check.Equal(savedAt, item.LastRefreshed, "保存時刻を最終更新時刻にしていません");
    }

    private sealed record ClaudeApiStep(string Path, HttpStatusCode? Status, string Json);

    private sealed record ClaudeApiRequest(string Path, string? BearerToken, bool HasBetaHeader, string Body);

    private sealed class ClaudeApiHandler(IEnumerable<ClaudeApiStep> steps) : HttpMessageHandler
    {
        private readonly Queue<ClaudeApiStep> _steps = new(steps);
        public List<ClaudeApiRequest> Requests { get; } = [];

        public static ClaudeApiStep Usage(HttpStatusCode? status, string json) => new("/api/oauth/usage", status, json);
        public static ClaudeApiStep Token(HttpStatusCode status, string json) => new("/v1/oauth/token", status, json);

        public void CheckCompleted() => Check.Equal(0, _steps.Count, "予定したHTTPリクエストが行われていません");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check.True(_steps.Count > 0, $"予定外のHTTPリクエストが発生しました: {request.RequestUri}");
            var step = _steps.Dequeue();
            Check.Equal(step.Path, request.RequestUri?.AbsolutePath, "HTTPパスが正しくありません");
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new ClaudeApiRequest(step.Path, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("anthropic-beta", out var values) && values.Contains("oauth-2025-04-20"), body));
            if (step.Status == null) throw new HttpRequestException("ダミー通信エラー");
            return new HttpResponseMessage(step.Status.Value)
            {
                Content = new StringContent(step.Json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task GptPlanAsync(string planType, bool expectedSubscribed)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new PlanFetcher(temp.FilePath("cache.json"))
        {
            Codex = new CodexQuotaData
            {
                IsSuccess = true, PlanType = planType,
                HasFiveHourLimit = true, FiveHourRemainingPercent = 71, HasWeeklyLimit = true, WeeklyRemainingPercent = 83
            }
        };
        var item = CreateItem(AiServiceType.GPT);
        await fetcher.FetchUsageAsync(item);
        CheckPlan(item, expectedSubscribed);
    }

    private static async Task CopilotPlanAsync(string plan, string sku, bool expectedSubscribed)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new PlanFetcher(temp.FilePath("cache.json"))
        {
            Copilot = new CopilotQuotaData
            {
                IsSuccess = true, CopilotPlan = plan, AccessTypeSku = sku,
                PlanName = expectedSubscribed ? "Copilot Pro" : "Copilot Free",
                RemainingPercent = 71, UsedPercent = 29, UsedCount = 29, TotalCount = 100
            }
        };
        var item = CreateItem(AiServiceType.Copilot);
        await fetcher.FetchUsageAsync(item);
        CheckPlan(item, expectedSubscribed);
    }

    private static async Task GrokPlanAsync(string tier, bool expectedSubscribed)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new PlanFetcher(temp.FilePath("cache.json"))
        {
            Grok = new GrokQuotaData { IsSuccess = true, PlanName = tier, RemainingPercent = 71, UsedPercent = 29 }
        };
        var item = CreateItem(AiServiceType.Grok);
        await fetcher.FetchUsageAsync(item);
        CheckPlan(item, expectedSubscribed);
    }

    private static async Task GrokBreakdownSegmentsAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new PlanFetcher(temp.FilePath("cache.json"))
        {
            Grok = new GrokQuotaData
            {
                IsSuccess = true, PlanName = "SuperGrok", UsedPercent = 32, RemainingPercent = 68,
                ProductUsage =
                [
                    new GrokProductUsage { Product = "GrokChat", DisplayName = "チャット", UsedPercent = 9 },
                    new GrokProductUsage { Product = "GrokBuild", DisplayName = "Grok Build", UsedPercent = 13 },
                    new GrokProductUsage { Product = "GrokImagine", DisplayName = "Imagine", UsedPercent = 8 }
                ]
            }
        };
        var item = CreateItem(AiServiceType.Grok);
        await fetcher.FetchUsageAsync(item);

        var segments = item.UsageBreakdown.Select(s => (s.Title, s.UsedAmount, s.SegmentColor)).ToArray();
        Check.Equal(4, segments.Length, "内訳の合計と使用率の差を示していません");
        Check.Equal(("チャット", 9.0, "#60A5FA"), segments[0], "チャットの色が取得順で変わっています");
        Check.Equal(("Grok Build", 13.0, "#34D399"), segments[1], "Grok Build の色が取得順で変わっています");
        Check.Equal(("Imagine", 8.0, "#F472B6"), segments[2], "Imagine の色が正しくありません");
        Check.Equal(("その他", 2.0, "#9CA3AF"), segments[3], "合計に届かない分をその他として表示していません");
        Check.Equal(32.0, segments.Sum(s => s.UsedAmount), "内訳の合計が週次の使用率と一致しません");
    }

    private static void CheckPlan(AiUsageItem item, bool expectedSubscribed)
    {
        Check.Equal(expectedSubscribed, item.CliInfo.IsSubscribed, "プラン情報による契約判定が正しくありません");
        Check.Equal(expectedSubscribed ? "最新" : "未契約", item.CliInfo.StatusBadgeText, "契約状況のバッジが正しくありません");
        Check.Equal(71d, item.PrimaryLimit.RemainingPercent, "契約判定後に利用枠を表示していません");
    }

    private static AiUsageItem CreateItem(AiServiceType service) => new()
    {
        ServiceType = service,
        CliInfo = new CliInfo { IsInstalled = true, IsBusy = false, IsLoggedIn = true, IsSubscribed = true }
    };

    private sealed class PlanFetcher(string cacheFilePath) : UsageFetcherService(cacheFilePath)
    {
        public CodexQuotaData? Codex { get; init; }
        public CopilotQuotaData? Copilot { get; init; }
        public GrokQuotaData? Grok { get; init; }
        public bool ClaudeThrows { get; init; }
        public ClaudeQuotaData? Claude { get; init; }

        protected override Task<CodexQuotaData?> FetchCodexQuotaAsync() => Task.FromResult(Codex);
        protected override Task<CopilotQuotaData?> FetchCopilotQuotaAsync() => Task.FromResult(Copilot);
        protected override Task<GrokQuotaData?> FetchGrokQuotaAsync() => Task.FromResult(Grok);
        protected override Task<List<QuotaGroup>?> FetchGeminiQuotaAsync() => Task.FromResult<List<QuotaGroup>?>(null);

        protected override Task<ClaudeQuotaData> FetchClaudeQuotaAsync() => ClaudeThrows
            ? throw new IOException("設定ファイルを読み取れません")
            : Task.FromResult(Claude ?? new ClaudeQuotaData());
    }
}
