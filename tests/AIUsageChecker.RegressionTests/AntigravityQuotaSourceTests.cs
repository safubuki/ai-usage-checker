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
        yield return new("Antigravity: Gemini 4 Argon取得・3段階カード切替・詳細表示順序", ArgonAndCardModeSwitchAsync);
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

    private static async Task ArgonAndCardModeSwitchAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"))
        {
            AgyResponse =
            [
                new QuotaGroup
                {
                    DisplayName = "Gemini Models",
                    Buckets =
                    [
                        new QuotaBucket { BucketId = "gemini-weekly", DisplayName = "Weekly Limit Remaining", Window = "weekly", RemainingFraction = 1.0 },
                        new QuotaBucket { BucketId = "gemini-5h", DisplayName = "Five Hour Limit Remaining", Window = "5h", RemainingFraction = 0.85 }
                    ]
                },
                new QuotaGroup
                {
                    DisplayName = "Claude and GPT models",
                    Buckets =
                    [
                        new QuotaBucket { BucketId = "3p-weekly", DisplayName = "Weekly Limit Remaining", Window = "weekly", RemainingFraction = 0.70 },
                        new QuotaBucket { BucketId = "3p-5h", DisplayName = "Five Hour Limit Remaining", Window = "5h", RemainingFraction = 0.60 }
                    ]
                },
                new QuotaGroup
                {
                    DisplayName = "Gemini 4 Argon",
                    Buckets =
                    [
                        new QuotaBucket { BucketId = "gemini-argon-wtu-weekly", DisplayName = "Gemini 4 Argon Weekly Limit", Window = "weekly", RemainingFraction = 0.93 }
                    ]
                }
            ]
        };

        var item = CreateItem();
        await fetcher.FetchUsageAsync(item);

        Check.True(item.HasArgonLimit, "Gemini 4 Argon の制限枠を取得できていません");
        Check.True(item.HasExternalLimits, "Claude & GPT の外部制限枠を取得できていません");
        Check.True(item.HasCardModeSwitch, "カード表示切替が有効になっていません");

        // 1. デフォルトは1つで Gemini 4 Argon を表示
        Check.Equal(AntigravityCardMode.Argon, item.EffectiveCardMode, "デフォルトモードが Gemini 4 Argon ではありません");
        Check.Equal("Argon", item.CardModeLabel, "Argonモード時の切替ラベルが一致しません");
        Check.Equal("Gemini 4 Argon", item.CardPrimaryLimit.Title, "デフォルトのカード主枠タイトルが Gemini 4 Argon ではありません");
        Check.Equal(93d, item.CardPrimaryLimit.RemainingPercent, "Gemini 4 Argon の残量が一致しません");
        Check.True(item.CardSecondaryLimit == null, "Argon表示時は単一枠(CardSecondaryLimit == null)である必要があります");

        // 2. 1回切り替え: Gemini モデルの 5時間と週次を表示
        item.CycleCardMode();
        Check.Equal(AntigravityCardMode.Gemini, item.EffectiveCardMode, "2番目のモードが Gemini ではありません");
        Check.Equal("Gemini", item.CardModeLabel, "Geminiモード時の切替ラベルが一致しません");
        Check.Equal("5時間制限", item.CardPrimaryLimit.Title, "Geminiモード上段が5時間制限ではありません");
        Check.Equal(85d, item.CardPrimaryLimit.RemainingPercent, "Gemini 5時間制限の残量が一致しません");
        Check.True(item.CardSecondaryLimit != null, "Geminiモード下段(週次制限)がありません");
        Check.Equal("週次制限", item.CardSecondaryLimit!.Title, "Geminiモード下段が週次制限ではありません");
        Check.Equal(100d, item.CardSecondaryLimit.RemainingPercent, "Gemini 週次制限の残量が一致しません");

        // 3. 2回切り替え: Claude & GPT モデルの 5時間と週次を表示
        item.CycleCardMode();
        Check.Equal(AntigravityCardMode.External, item.EffectiveCardMode, "3番目のモードが External (Claude & GPT) ではありません");
        Check.Equal("外部", item.CardModeLabel, "外部モデルモード時の切替ラベルが一致しません");
        Check.Equal("5時間制限 外部", item.CardPrimaryLimit.Title, "外部モデルモード上段が5時間制限 外部ではありません");
        Check.Equal(60d, item.CardPrimaryLimit.RemainingPercent, "外部モデル 5時間制限の残量が一致しません");
        Check.True(item.CardSecondaryLimit != null, "外部モデルモード下段(週次制限)がありません");
        Check.Equal("週次制限 外部", item.CardSecondaryLimit!.Title, "外部モデルモード下段が週次制限 外部ではありません");
        Check.Equal(70d, item.CardSecondaryLimit.RemainingPercent, "外部モデル 週次制限の残量が一致しません");

        // 4. 3回切り替え: 再び Gemini 4 Argon に戻る
        item.CycleCardMode();
        Check.Equal(AntigravityCardMode.Argon, item.EffectiveCardMode, "3回切替後に Gemini 4 Argon へ戻っていません");
        Check.True(item.CardSecondaryLimit == null, "Argon復帰時に単一枠表示へ戻っていません");

        // 5. 詳細表示 (AllLimits および LimitGroups) でも全5枠・3グループが順番通り構成されること
        Check.Equal(5, item.AllLimits.Count, "詳細表示用の AllLimits に5枠すべてが含まれていません");
        Check.Equal("Gemini 4 Argon", item.AllLimits[0].Title, "詳細表示1番目が Gemini 4 Argon ではありません");
        Check.Equal("5時間制限", item.AllLimits[1].Title, "詳細表示2番目が Gemini 5時間制限 ではありません");
        Check.Equal("週次制限", item.AllLimits[2].Title, "詳細表示3番目が Gemini 週次制限 ではありません");
        Check.Equal("5時間制限 外部", item.AllLimits[3].Title, "詳細表示4番目が 5時間制限 外部 ではありません");
        Check.Equal("週次制限 外部", item.AllLimits[4].Title, "詳細表示5番目が 週次制限 外部 ではありません");

        Check.True(item.HasLimitGroups, "詳細表示用の LimitGroups が有効になっていません");
        Check.Equal(3, item.LimitGroups.Count, "詳細表示用の LimitGroups が3グループではありません");
        Check.Equal("Gemini 4 Argon", item.LimitGroups[0].GroupTitle, "1番目のグループ名が Gemini 4 Argon ではありません");
        Check.Equal(1, item.LimitGroups[0].Limits.Count, "Gemini 4 Argon グループの枠数が1ではありません");
        Check.Equal("Gemini Model", item.LimitGroups[1].GroupTitle, "2番目のグループ名が Gemini Model ではありません");
        Check.Equal(2, item.LimitGroups[1].Limits.Count, "Gemini Model グループの枠数が2ではありません");
        Check.Equal("Claude & GPT Model", item.LimitGroups[2].GroupTitle, "3番目のグループ名が Claude & GPT Model ではありません");
        Check.Equal(2, item.LimitGroups[2].Limits.Count, "Claude & GPT Model グループの枠数が2ではありません");
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
