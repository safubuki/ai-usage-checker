using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class GrokQuotaClientTests
{
    private const string BillingJson = """
        {"config":{"creditUsagePercent":29,"subscriptionTier":"SuperGrok",
        "currentPeriod":{"start":"2026-10-01T00:00:00Z","end":"2026-10-08T00:00:00Z"}}}
        """;
    private const string RefreshJson = """
        {"access_token":"dummy-new-token","refresh_token":"dummy-new-refresh-token","expires_in":3600}
        """;

    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("Grok: 認証ファイルなしで再認証を要求", MissingAuthFileAsync);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            yield return new($"Grok: HTTP {(int)status} を過去ログで隠さない", () => AuthFailureWithoutRefreshAsync(status));
            yield return new($"Grok: HTTP {(int)status} の後にトークン更新して復帰", () => RefreshAfterAuthFailureAsync(status));
        }
        yield return new("Grok: トークン更新失敗でも認証エラーを過去ログで隠さない", RefreshFailureAsync);
        yield return new("Grok: トークン更新後の認証エラーを過去ログで隠さない", RetryAuthFailureAsync);
        yield return new("Grok: 期限切れと invalid_grant を通信失敗で隠さない", ExpiredTokenRefreshFailureAsync);
        yield return new("Grok: 更新済みトークンでの失敗時に旧トークンを再更新しない", RefreshOnlyOnceAsync);
        yield return new("Grok: 通信失敗時には過去ログへフォールバック", NetworkFailureFallbackAsync);
        yield return new("Grok: 外部で認証復帰した後の再取得でファイルを再読込", ReloadAuthFileAfterRecoveryAsync);
    }

    private static async Task MissingAuthFileAsync()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new ScriptedHttpHandler([]);
        using var http = new HttpClient(handler);
        var client = new GrokQuotaClient(http);
        var result = await client.FetchGrokQuotaAsync(temp.FilePath("missing-auth.json"), temp.FilePath("missing-log.jsonl"));
        CheckAuthRequired(result);
        handler.CheckCompleted();
    }

    private static async Task AuthFailureWithoutRefreshAsync(HttpStatusCode status)
    {
        using var fixture = await GrokFixture.CreateAsync(withRefreshToken: false);
        using var handler = new ScriptedHttpHandler([Billing(status, token: "dummy-initial-token")]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        CheckAuthRequired(result);
        handler.CheckCompleted();
    }

    private static async Task RefreshAfterAuthFailureAsync(HttpStatusCode status)
    {
        using var fixture = await GrokFixture.CreateAsync();
        using var handler = new ScriptedHttpHandler([
            Billing(status, token: "dummy-initial-token"),
            Token(HttpStatusCode.OK, RefreshJson),
            Billing(HttpStatusCode.OK, BillingJson, "dummy-new-token")]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        CheckSuccess(result);
        using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.AuthPath));
        var entry = auth.RootElement.GetProperty("dummy-account");
        Check.Equal("dummy-new-token", entry.GetProperty("key").GetString(), "更新したアクセストークンを保存していません");
        Check.Equal("dummy-new-refresh-token", entry.GetProperty("refresh_token").GetString(), "更新したリフレッシュトークンを保存していません");
        handler.CheckCompleted();
    }

    private static async Task RefreshFailureAsync()
    {
        using var fixture = await GrokFixture.CreateAsync();
        using var handler = new ScriptedHttpHandler([
            Billing(HttpStatusCode.Unauthorized),
            Token(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        CheckAuthRequired(result);
        handler.CheckCompleted();
    }

    private static async Task RetryAuthFailureAsync()
    {
        using var fixture = await GrokFixture.CreateAsync();
        using var handler = new ScriptedHttpHandler([
            Billing(HttpStatusCode.Unauthorized), Token(HttpStatusCode.OK, RefreshJson),
            Billing(HttpStatusCode.Forbidden, token: "dummy-new-token")]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        CheckAuthRequired(result);
        handler.CheckCompleted();
    }

    private static async Task ExpiredTokenRefreshFailureAsync()
    {
        using var fixture = await GrokFixture.CreateAsync(expired: true);
        using var handler = new ScriptedHttpHandler([
            Token(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}"),
            new HttpStep(HttpMethod.Get, "/v1/billing", ThrowCommunicationError: true)]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        CheckAuthRequired(result);
        handler.CheckCompleted();
    }

    private static async Task RefreshOnlyOnceAsync()
    {
        using var fixture = await GrokFixture.CreateAsync(expired: true);
        using var handler = new ScriptedHttpHandler([
            Token(HttpStatusCode.OK, RefreshJson), Billing(HttpStatusCode.Unauthorized, token: "dummy-new-token")]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        CheckAuthRequired(result);
        handler.CheckCompleted();
    }

    private static async Task NetworkFailureFallbackAsync()
    {
        using var fixture = await GrokFixture.CreateAsync(withRefreshToken: false);
        using var handler = new ScriptedHttpHandler([Billing(HttpStatusCode.ServiceUnavailable)]);
        using var http = new HttpClient(handler);
        var result = await new GrokQuotaClient(http).FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath);
        Check.True(result?.IsSuccess == true && result.IsFromFallback, "通信失敗時の過去ログへフォールバックできません");
        Check.True(!result!.IsAuthRequired, "通信失敗を認証エラーとして扱っています");
        handler.CheckCompleted();
    }

    private static async Task ReloadAuthFileAfterRecoveryAsync()
    {
        using var fixture = await GrokFixture.CreateAsync(withRefreshToken: false);
        using var handler = new ScriptedHttpHandler([
            Billing(HttpStatusCode.Unauthorized, token: "dummy-initial-token"),
            Billing(HttpStatusCode.OK, BillingJson, "dummy-externally-updated-token")]);
        using var http = new HttpClient(handler);
        var client = new GrokQuotaClient(http);
        CheckAuthRequired(await client.FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath));
        await fixture.WriteAuthAsync("dummy-externally-updated-token", withRefreshToken: false, expired: false);
        CheckSuccess(await client.FetchGrokQuotaAsync(fixture.AuthPath, fixture.LogPath));
        handler.CheckCompleted();
    }

    private static HttpStep Billing(HttpStatusCode status, string json = "{}", string? token = null) =>
        new(HttpMethod.Get, "/v1/billing", status, json, token);

    private static HttpStep Token(HttpStatusCode status, string json) =>
        new(HttpMethod.Post, "/oauth2/token", status, json);

    private static void CheckAuthRequired(GrokQuotaData? result)
    {
        Check.True(result is { IsAuthRequired: true, IsSuccess: false, IsFromFallback: false },
            "認証失敗を再認証が必要な状態として返していません");
    }

    private static void CheckSuccess(GrokQuotaData? result)
    {
        Check.True(result is { IsSuccess: true, IsAuthRequired: false, IsFromFallback: false },
            "認証復帰後も取得エラーまたは過去ログ状態が残っています");
        Check.Equal(71d, result!.RemainingPercent, "認証復帰後の最新利用枠が正しくありません");
    }

    private sealed class GrokFixture : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        public string AuthPath => _temp.FilePath("dummy-auth.json");
        public string LogPath => _temp.FilePath("dummy-unified.jsonl");

        public static async Task<GrokFixture> CreateAsync(bool withRefreshToken = true, bool expired = false)
        {
            var fixture = new GrokFixture();
            await fixture.WriteAuthAsync("dummy-initial-token", withRefreshToken, expired);
            // 認証エラーが隠れる回帰を検出するため、成功済みログを必ず用意する。
            await File.WriteAllTextAsync(fixture.LogPath,
                "{\"msg\":\"billing: fetched credits config\",\"ctx\":" + BillingJson.Replace("\n", "").Replace("\r", "") + "}");
            return fixture;
        }

        public Task WriteAuthAsync(string token, bool withRefreshToken, bool expired)
        {
            var entry = new Dictionary<string, object?>
            {
                ["key"] = token,
                ["expires_at"] = DateTime.UtcNow.AddHours(expired ? -1 : 1).ToString("o")
            };
            if (withRefreshToken)
            {
                entry["refresh_token"] = "dummy-refresh-token";
                entry["oidc_client_id"] = "dummy-client";
                entry["oidc_issuer"] = "https://auth.invalid";
            }
            var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["dummy-account"] = entry });
            return File.WriteAllTextAsync(AuthPath, json);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed record HttpStep(HttpMethod Method, string Path, HttpStatusCode Status = HttpStatusCode.OK,
        string Json = "{}", string? BearerToken = null, bool ThrowCommunicationError = false);

    private sealed class ScriptedHttpHandler(IEnumerable<HttpStep> steps) : HttpMessageHandler
    {
        private readonly Queue<HttpStep> _steps = new(steps);
        private readonly List<string> _errors = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                Check.True(_steps.Count > 0, "予定外のHTTPリクエストが発生しました");
                var step = _steps.Dequeue();
                Check.Equal(step.Method, request.Method, "HTTPメソッドが正しくありません");
                Check.Equal(step.Path, request.RequestUri?.AbsolutePath, "HTTPパスが正しくありません");
                if (step.BearerToken != null)
                    Check.Equal(step.BearerToken, request.Headers.Authorization?.Parameter, "再取得で最新認証トークンを使っていません");
                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    Check.True(body.Contains("grant_type=refresh_token", StringComparison.Ordinal)
                        && body.Contains("refresh_token=dummy-refresh-token", StringComparison.Ordinal),
                        "リフレッシュ要求が正しくありません");
                }
                if (step.ThrowCommunicationError) throw new HttpRequestException("ダミー通信エラー");
                return new HttpResponseMessage(step.Status)
                {
                    Content = new StringContent(step.Json, Encoding.UTF8, "application/json")
                };
            }
            catch (InvalidOperationException ex)
            {
                _errors.Add(ex.Message);
                throw;
            }
        }

        public void CheckCompleted()
        {
            Check.True(_errors.Count == 0, string.Join("; ", _errors));
            Check.Equal(0, _steps.Count, "予定したHTTPリクエストが実行されていません");
        }
    }
}
