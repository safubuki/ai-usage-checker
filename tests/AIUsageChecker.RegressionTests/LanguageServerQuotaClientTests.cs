using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class LanguageServerQuotaClientTests
{
    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("Antigravity IDE: 先頭が空でも別サーバーから取得", MultipleServersAsync);
        yield return new("Antigravity IDE: 再起動後に接続先を探索し直す", ServerRestartAsync);
    }

    private static List<QuotaGroup> Quota() =>
        [new() { DisplayName = "Gemini", Buckets = [new() { Window = "5h", RemainingFraction = .5 }] }];

    private static async Task MultipleServersAsync()
    {
        var client = new FakeClient();
        var result = await client.FetchQuotaSummaryAsync();
        Check.Equal(.5, result![0].Buckets[0].RemainingFraction, "2番目のサーバーを確認していません");
        Check.True(client.Queried.SequenceEqual(new[] { 1001, 1002 }), "先頭の空応答で探索を打ち切りました");
        client.Queried.Clear();
        await client.FetchQuotaSummaryAsync();
        Check.True(client.Queried.SequenceEqual(new[] { 1002 }), "成功した接続先を再利用していません");
    }

    private static async Task ServerRestartAsync()
    {
        var client = new FakeClient();
        await client.FetchQuotaSummaryAsync();
        client.Restarted = true;
        client.Queried.Clear();
        var result = await client.FetchQuotaSummaryAsync();
        Check.True(result != null, "再起動後に取得できません");
        Check.True(client.Queried.SequenceEqual(new[] { 1002, 1003 }), "古い接続先の失敗後に再探索していません");
    }

    private sealed class FakeClient : LanguageServerQuotaClient
    {
        public bool Restarted { get; set; }
        public List<int> Queried { get; } = [];
        protected override List<(string Token, int Pid)> FindServerProcesses() => Restarted
            ? [("new-token", 3)] : [("empty-token", 1), ("working-token", 2)];
        protected override List<int> FindListeningPortsForPid(int pid) => [1000 + pid];
        protected override Task<List<QuotaGroup>?> TryQueryEndpointAsync(int port, string csrfToken)
        {
            Queried.Add(port);
            return Task.FromResult<List<QuotaGroup>?>(port == (Restarted ? 1003 : 1002) ? Quota() : []);
        }
    }
}
