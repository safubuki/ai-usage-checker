using System.IO;
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
        yield return new("GPT契約: plan_type=free は未契約、利用枠は表示", () => GptPlanAsync("free", false));
        yield return new("GPT契約: plan_type=plus は契約中", () => GptPlanAsync("plus", true));
        yield return new("Copilot契約: copilot_plan=free は未契約", () => CopilotPlanAsync("free", "free_limited_copilot", false));
        yield return new("Copilot契約: copilot_plan=individual は契約中", () => CopilotPlanAsync("individual", "yearly_subscriber_quota", true));
        yield return new("Grok契約: subscriptionTier=Free は未契約", () => GrokPlanAsync("Free", false));
        yield return new("Grok契約: subscriptionTier=SuperGrok は契約中", () => GrokPlanAsync("SuperGrok", true));
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

        protected override Task<CodexQuotaData?> FetchCodexQuotaAsync() => Task.FromResult(Codex);
        protected override Task<CopilotQuotaData?> FetchCopilotQuotaAsync() => Task.FromResult(Copilot);
        protected override Task<GrokQuotaData?> FetchGrokQuotaAsync() => Task.FromResult(Grok);
        protected override Task<List<QuotaGroup>?> FetchGeminiQuotaAsync() => Task.FromResult<List<QuotaGroup>?>(null);

        protected override Task<ClaudeQuotaData> FetchClaudeQuotaAsync() => ClaudeThrows
            ? throw new IOException("設定ファイルを読み取れません")
            : Task.FromResult(new ClaudeQuotaData());
    }
}
