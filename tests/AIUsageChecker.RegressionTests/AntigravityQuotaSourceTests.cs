using System.IO;
using System.Text.Json;
using AIUsageChecker.Models;
using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class AntigravityQuotaSourceTests
{
    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("Antigravity: CLI成功時はIDEを呼び出さず取得元を表示", CliSuccessAsync);
        yield return new("Antigravity: CLI取得失敗時はIDE取得元を表示", () => IdeFallbackAsync(null));
        yield return new("Antigravity: CLIに利用枠がない場合もIDEで補助取得", () => IdeFallbackAsync(
            [new QuotaGroup { DisplayName = "Gemini", Buckets = [] }]));
        yield return new("Antigravity: IDE補助取得後のCLI復帰で取得元を更新", CliRecoveryAsync);
        yield return new("Antigravity: 初回のCLI・IDE失敗は未取得と表示", InitialFailureAsync);
        foreach (var source in new[] { UsageDataSource.Cli, UsageDataSource.Ide })
        {
            yield return new($"Antigravity: {source}成功後の取得失敗で前回の取得元を保持", () => PreviousSourceAfterFailureAsync(source));
            yield return new($"Antigravity: {source}の取得元をキャッシュ保存・再読込", () => CacheRoundTripAsync(source));
        }
        yield return new("Antigravity: 旧オブジェクトキャッシュの取得元は不明と表示", () => LegacyCacheAsync(isArray: false));
        yield return new("Antigravity: 旧配列キャッシュの取得元は不明と表示", () => LegacyCacheAsync(isArray: true));
    }

    private static AiUsageItem CreateItem() => new()
    {
        ServiceType = AiServiceType.Gemini,
        CliInfo = new CliInfo { IsInstalled = true, IsLoggedIn = true, IsSubscribed = true }
    };

    private static List<QuotaGroup> CreateQuota(double remainingFraction = .71) =>
    [new QuotaGroup
    {
        DisplayName = "Gemini",
        Buckets = [new QuotaBucket { Window = "5h", RemainingFraction = remainingFraction }]
    }];

    private static void CheckSource(AiUsageItem item, string expected)
    {
        Check.True(item.HasUsageSource, "詳細画面の取得元表示が非表示です");
        Check.Equal(expected, item.UsageSourceText, "実際の取得経路と取得元表示が一致しません");
    }

    private static async Task CliSuccessAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json")) { AgyResponse = CreateQuota() };
        var item = CreateItem();
        await fetcher.FetchUsageAsync(item);

        Check.Equal(1, fetcher.AgyCalls, "CLIを取得していません");
        Check.Equal(0, fetcher.IdeCalls, "CLIが成功したのにIDEへ問い合わせました");
        CheckSource(item, "取得元：CLI");
        Check.Equal(71d, item.PrimaryLimit.RemainingPercent, "CLIの利用枠を表示していません");
        Check.True(!item.CliInfo.HasUsageError, "CLI取得成功を取得エラー扱いしています");
    }

    private static async Task IdeFallbackAsync(List<QuotaGroup>? cliResponse)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"))
        {
            AgyResponse = cliResponse,
            IdeResponse = CreateQuota()
        };
        var item = CreateItem();
        await fetcher.FetchUsageAsync(item);

        Check.Equal(1, fetcher.AgyCalls, "IDE取得前にCLIを試していません");
        Check.Equal(1, fetcher.IdeCalls, "CLIに利用枠がない場合にIDEへ問い合わせていません");
        CheckSource(item, "取得元：IDE（CLI取得失敗時の補助）");
        Check.Equal(71d, item.PrimaryLimit.RemainingPercent, "IDEの利用枠を表示していません");
        Check.True(!item.CliInfo.HasUsageError, "IDE補助取得の成功を取得エラー扱いしています");
    }

    private static async Task CliRecoveryAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json")) { IdeResponse = CreateQuota() };
        var item = CreateItem();
        await fetcher.FetchUsageAsync(item);
        CheckSource(item, "取得元：IDE（CLI取得失敗時の補助）");

        fetcher.AgyResponse = CreateQuota(.62);
        await fetcher.FetchUsageAsync(item);
        Check.Equal(2, fetcher.AgyCalls, "IDE取得後もCLIの復帰を確認していません");
        Check.Equal(1, fetcher.IdeCalls, "CLI復帰後もIDEへ問い合わせました");
        CheckSource(item, "取得元：CLI");
        Check.Equal(62d, item.PrimaryLimit.RemainingPercent, "CLI復帰後もIDEの古い利用枠を表示しています");
    }

    private static async Task InitialFailureAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        var item = CreateItem();
        fetcher.ApplyUsage(item);
        CheckSource(item, "取得元：未取得");
        await fetcher.FetchUsageAsync(item);

        Check.Equal(1, fetcher.AgyCalls, "取得失敗時にCLIを試していません");
        Check.Equal(1, fetcher.IdeCalls, "取得失敗時にIDEを試していません");
        CheckSource(item, "取得元：未取得（CLI・IDEから取得できませんでした）");
        Check.True(item.CliInfo.HasUsageError, "初回取得失敗を取得エラーへ反映していません");
    }

    private static async Task PreviousSourceAfterFailureAsync(UsageDataSource source)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = CreateSuccessfulFetcher(temp.FilePath("cache.json"), source);
        var item = CreateItem();
        await fetcher.FetchUsageAsync(item);
        var lastSuccess = item.LastRefreshed;

        fetcher.AgyResponse = null;
        fetcher.IdeResponse = null;
        await fetcher.FetchUsageAsync(item);
        CheckSource(item, PreviousSourceText(source));
        Check.True(item.CliInfo.HasUsageError, "前回成功後の取得失敗を取得エラーへ反映していません");
        Check.True(item.LastRefreshed <= lastSuccess, "取得失敗で最終成功時刻を進めました");

        // 失敗の繰り返しでも、問い合わせた経路を成功した取得元として保存しない。
        await fetcher.FetchUsageAsync(item);
        CheckSource(item, PreviousSourceText(source));
    }

    private static async Task CacheRoundTripAsync(UsageDataSource source)
    {
        using var temp = new TemporaryDirectory();
        var cachePath = temp.FilePath("cache.json");
        var fetcher = CreateSuccessfulFetcher(cachePath, source);
        await fetcher.FetchUsageAsync(CreateItem());

        // 取得失敗でキャッシュを書き直しても、最後に成功した取得元を残す。
        fetcher.AgyResponse = null;
        fetcher.IdeResponse = null;
        await fetcher.FetchUsageAsync(CreateItem());
        var cache = JsonSerializer.Deserialize<AllQuotaCache>(File.ReadAllText(cachePath));
        Check.True(cache?.GeminiGroups?.Count > 0, "前回成功時の利用枠をキャッシュに保存していません");
        Check.Equal(source, cache!.GeminiQuotaSource, "キャッシュの取得元が前回成功時の経路と異なります");

        var reloaded = new FakeUsageFetcher(cachePath);
        var item = CreateItem();
        reloaded.ApplyUsage(item);
        CheckSource(item, source == UsageDataSource.Cli
            ? "取得元：CLI（前回取得データ）"
            : "取得元：IDE（前回取得データ）");
        Check.True(item.CliInfo.HasUsageError, "再読込したキャッシュを最新の取得結果として扱っています");
        Check.Equal(0, reloaded.AgyCalls, "キャッシュの表示でCLIを呼び出しました");
        Check.Equal(0, reloaded.IdeCalls, "キャッシュの表示でIDEを呼び出しました");

        await reloaded.FetchUsageAsync(item);
        CheckSource(item, PreviousSourceText(source));
    }

    private static async Task LegacyCacheAsync(bool isArray)
    {
        using var temp = new TemporaryDirectory();
        var cachePath = temp.FilePath("cache.json");
        var quota = CreateQuota();
        var json = isArray
            ? JsonSerializer.Serialize(quota)
            : JsonSerializer.Serialize(new { GeminiGroups = quota, GeminiFetchedAt = new DateTime(2026, 1, 2, 3, 4, 5) });
        File.WriteAllText(cachePath, json);

        var fetcher = new FakeUsageFetcher(cachePath);
        var item = CreateItem();
        fetcher.ApplyUsage(item);
        CheckSource(item, "取得元：不明（前回取得データ）");
        Check.True(item.CliInfo.HasUsageError, "旧キャッシュを最新の取得結果として扱っています");
        Check.Equal(0, fetcher.AgyCalls, "旧キャッシュの表示でCLIを呼び出しました");
        Check.Equal(0, fetcher.IdeCalls, "旧キャッシュの表示でIDEを呼び出しました");

        await fetcher.FetchUsageAsync(item);
        CheckSource(item, "取得失敗（前回の取得元：不明）");
    }

    private static FakeUsageFetcher CreateSuccessfulFetcher(string cachePath, UsageDataSource source) => new(cachePath)
    {
        AgyResponse = source == UsageDataSource.Cli ? CreateQuota() : null,
        IdeResponse = source == UsageDataSource.Ide ? CreateQuota() : null
    };

    private static string PreviousSourceText(UsageDataSource source) => source == UsageDataSource.Cli
        ? "取得失敗（前回の取得元：CLI）"
        : "取得失敗（前回の取得元：IDE）";

    private sealed class FakeUsageFetcher(string cacheFilePath) : UsageFetcherService(cacheFilePath)
    {
        public List<QuotaGroup>? AgyResponse { get; set; }
        public List<QuotaGroup>? IdeResponse { get; set; }
        public int AgyCalls { get; private set; }
        public int IdeCalls { get; private set; }

        protected override Task<List<QuotaGroup>?> FetchAgyQuotaAsync()
        {
            AgyCalls++;
            return Task.FromResult(AgyResponse);
        }

        protected override Task<List<QuotaGroup>?> FetchIdeQuotaAsync()
        {
            IdeCalls++;
            return Task.FromResult(IdeResponse);
        }
    }
}
