using System.IO;
using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class ClaudeQuotaClientTests
{
    private const string QuotaJson = """
        {"oauthAccount":{"billingType":"subscription","organizationType":"pro"},
         "cachedUsageUtilization":{"utilization":{
           "five_hour":{"utilization":35,"resets_at":"2026-10-01T12:00:00Z"},
           "seven_day":{"utilization":62,"resets_at":"2026-10-08T00:00:00Z"}}}}
        """;

    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("Claude: 設定ファイルなしで認証を要求", MissingConfigFileAsync);
        yield return new("Claude: アカウントのない設定と過去の利用枠を未ログインと判定", MissingAccountAsync);
        foreach (var account in new[] { "null", "\"dummy-account\"", "[]" })
            yield return new($"Claude: Object以外のアカウント {account} を未ログインと判定",
                () => NonObjectAccountAsync(account));
        yield return new("Claude: 認証済みの未契約を認証エラーにしない", UnsubscribedAccountAsync);
        yield return new("Claude: 認証復帰後の再取得で新しいアカウントと利用枠を反映", ReloadAfterAuthenticationAsync);
    }

    private static async Task MissingConfigFileAsync()
    {
        using var temp = new TemporaryDirectory();
        var result = await new ClaudeQuotaClient().FetchClaudeQuotaAsync(temp.FilePath("missing-config.json"));
        CheckAuthRequired(result);
    }

    private static async Task MissingAccountAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("fake-config.json");
        await File.WriteAllTextAsync(path, """
            {"theme":"dark","cachedUsageUtilization":{"utilization":{
              "five_hour":{"utilization":35},"seven_day":{"utilization":62}}}}
            """);
        CheckAuthRequired(await new ClaudeQuotaClient().FetchClaudeQuotaAsync(path));
    }

    private static async Task NonObjectAccountAsync(string account)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("fake-config.json");
        await File.WriteAllTextAsync(path, $"{{\"oauthAccount\":{account}}}");
        CheckAuthRequired(await new ClaudeQuotaClient().FetchClaudeQuotaAsync(path));
    }

    private static async Task UnsubscribedAccountAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("fake-config.json");
        await File.WriteAllTextAsync(path, """
            {"oauthAccount":{"billingType":"none","organizationType":"pro"}}
            """);
        var result = await new ClaudeQuotaClient().FetchClaudeQuotaAsync(path);
        Check.True(result.IsSuccess && !result.IsAuthRequired && !result.IsSubscribed,
            "ログイン済みの未契約を認証エラーとして扱っています");
        Check.True(result.StatusMessage.Contains("未契約", StringComparison.Ordinal),
            "未契約の状態を案内していません");
    }

    private static async Task ReloadAfterAuthenticationAsync()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("fake-config.json");
        var client = new ClaudeQuotaClient();
        await File.WriteAllTextAsync(path, "{}");
        CheckAuthRequired(await client.FetchClaudeQuotaAsync(path));
        await File.WriteAllTextAsync(path, QuotaJson);
        var result = await client.FetchClaudeQuotaAsync(path);
        Check.True(result.IsSuccess && result.IsSubscribed && !result.IsAuthRequired,
            "認証復帰後も未ログイン状態が残っています");
        Check.Equal("Claude Pro", result.PlanName, "新しいアカウントのプランを反映していません");
        Check.True(result.HasFiveHourLimit && result.HasWeeklyLimit, "正常な利用枠を取得できません");
        Check.Equal(65.0, result.FiveHourRemainingPercent, "5時間の残量を正しく反映していません");
        Check.Equal(38.0, result.WeeklyRemainingPercent, "週次の残量を正しく反映していません");
    }

    private static void CheckAuthRequired(ClaudeQuotaData result)
    {
        Check.True(result.IsAuthRequired && !result.IsSuccess && !result.IsSubscribed,
            "アカウントのない設定を認証が必要な状態として返していません");
        Check.True(result.StatusMessage.Contains("未ログイン", StringComparison.Ordinal),
            "未ログインの状態を案内していません");
    }
}
