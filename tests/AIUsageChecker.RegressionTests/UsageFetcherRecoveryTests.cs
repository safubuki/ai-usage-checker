using System.IO;
using AIUsageChecker.Models;
using AIUsageChecker.Services;

namespace AIUsageChecker.RegressionTests;

internal static class UsageFetcherRecoveryTests
{
    private static readonly AiServiceType[] Services =
        [AiServiceType.GPT, AiServiceType.Claude, AiServiceType.Gemini, AiServiceType.Grok, AiServiceType.Copilot];

    public static IEnumerable<TestCase> GetCases()
    {
        foreach (var service in Services)
        {
            yield return new($"{service}: 詳細の更新で対象AIだけを再取得", () => FetchOnlySelectedServiceAsync(service));
            yield return new($"{service}: 認証復帰後の更新で表示を回復", () => AuthenticationRecoveryAsync(service));
            yield return new($"{service}: 通信失敗後の更新で表示を回復", () => CommunicationRecoveryAsync(service));
        }
        yield return new("全AI更新でも認証状態を回復", FetchAllRecoveryAsync);
        yield return new("全AI更新中の詳細更新を直列化", SerializeAllAndIndividualRefreshAsync);
        yield return new("設定画面の更新でAI取得を呼ばない", SettingsRefreshAsync);
    }

    private static AiUsageItem CreateItem(AiServiceType service) => new()
    {
        ServiceType = service,
        CliInfo = new CliInfo { IsInstalled = true, IsBusy = false, IsLoggedIn = true, IsSubscribed = true }
    };

    private static void CheckRecovered(AiUsageItem item)
    {
        Check.True(item.CliInfo.IsLoggedIn, "取得成功後も未ログイン状態が残っています");
        Check.True(!item.CliInfo.HasUsageError, "取得成功後も取得エラー状態が残っています");
        Check.True(item.CliInfo.IsSubscribed, "取得成功後も未契約状態が残っています");
        Check.True(item.IsDataLoaded, "取得後に表示が読み込み済みになっていません");
        Check.True(item.PrimaryLimit.CustomDisplayPercentText != "--", "取得成功後も利用枠が非表示です");
        Check.Equal(71d, item.PrimaryLimit.RemainingPercent, "最新の利用枠が反映されていません");
        Check.True(item.CliInfo.StatusBadgeText != "要ログイン" && item.CliInfo.StatusBadgeText != "取得エラー",
            "取得成功後のステータスバッジが回復していません");
    }

    private static async Task FetchOnlySelectedServiceAsync(AiServiceType service)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        var item = CreateItem(service);
        await fetcher.FetchUsageAsync(item);
        foreach (var candidate in Services)
            Check.Equal(candidate == service ? 1 : 0, fetcher.CallCount(candidate), "対象AI以外の取得を呼び出しました");
        CheckRecovered(item);
    }

    private static async Task AuthenticationRecoveryAsync(AiServiceType service)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        var item = CreateItem(service);
        fetcher.Response = ResponseKind.AuthenticationRequired;
        // Geminiは取得結果に認証エラー区分がないため、CLIの検出結果に相当する状態を設定する。
        if (service == AiServiceType.Gemini) item.CliInfo.IsLoggedIn = false;
        await fetcher.FetchUsageAsync(item);
        Check.True(!item.CliInfo.IsLoggedIn, "認証エラーを未ログイン状態へ反映していません");
        Check.Equal("--", item.PrimaryLimit.CustomDisplayPercentText, "認証失敗中に利用枠を表示しています");

        fetcher.Response = ResponseKind.Success;
        await fetcher.FetchUsageAsync(item);
        Check.Equal(2, fetcher.CallCount(service), "認証復帰後の更新で再取得していません");
        CheckRecovered(item);
    }

    private static async Task CommunicationRecoveryAsync(AiServiceType service)
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        var item = CreateItem(service);
        await fetcher.FetchUsageAsync(item);
        var lastSuccess = item.LastRefreshed;

        fetcher.Response = ResponseKind.CommunicationFailure;
        await fetcher.FetchUsageAsync(item);
        Check.True(item.CliInfo.HasUsageError, "通信失敗を取得エラーへ反映していません");
        // 表示を適用した時刻と生データの最終成功時刻には僅かな差がある。
        Check.True(item.LastRefreshed <= lastSuccess, "通信失敗で最終成功時刻を新しい時刻へ進めました");

        fetcher.Response = ResponseKind.Success;
        await fetcher.FetchUsageAsync(item);
        Check.Equal(3, fetcher.CallCount(service), "通信復帰後の更新で再取得していません");
        CheckRecovered(item);
    }

    private static async Task FetchAllRecoveryAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        var items = Services.Select(CreateItem).ToArray();
        fetcher.Response = ResponseKind.AuthenticationRequired;
        items.Single(item => item.ServiceType == AiServiceType.Gemini).CliInfo.IsLoggedIn = false;
        await fetcher.FetchAllUsagesAsync(items);
        Check.True(items.All(item => !item.CliInfo.IsLoggedIn), "全AI更新で認証失敗を反映していません");

        fetcher.Response = ResponseKind.Success;
        await fetcher.FetchAllUsagesAsync(items);
        foreach (var item in items)
        {
            Check.Equal(2, fetcher.CallCount(item.ServiceType), "全AIの再取得回数が正しくありません");
            CheckRecovered(item);
        }
    }

    private static async Task SerializeAllAndIndividualRefreshAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        var firstGrokStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstGrok = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fetcher.OnFetch = async (service, count) =>
        {
            if (service == AiServiceType.Grok && count == 1)
            {
                firstGrokStarted.SetResult();
                await releaseFirstGrok.Task;
            }
        };

        var items = Services.Select(CreateItem).ToArray();
        var fullRefresh = fetcher.FetchAllUsagesAsync(items);
        await firstGrokStarted.Task;
        var grok = items.Single(item => item.ServiceType == AiServiceType.Grok);
        var individualRefresh = fetcher.FetchUsageAsync(grok);
        try
        {
            await Task.Yield();
            Check.Equal(1, fetcher.CallCount(AiServiceType.Grok), "全AI更新中に同じAIの再取得が競合しました");
            Check.True(!individualRefresh.IsCompleted, "詳細更新が全AI更新の完了を待っていません");
        }
        finally
        {
            releaseFirstGrok.TrySetResult();
            await Task.WhenAll(fullRefresh, individualRefresh);
        }
        Check.Equal(2, fetcher.CallCount(AiServiceType.Grok), "待機後の詳細更新が実行されていません");
        foreach (var service in Services.Where(service => service != AiServiceType.Grok))
            Check.Equal(1, fetcher.CallCount(service), "待機中の詳細更新が対象以外のAIを再取得しました");
        CheckRecovered(grok);
    }

    private static async Task SettingsRefreshAsync()
    {
        using var temp = new TemporaryDirectory();
        var fetcher = new FakeUsageFetcher(temp.FilePath("cache.json"));
        await fetcher.FetchUsageAsync(CreateItem(AiServiceType.Settings));
        Check.True(Services.All(service => fetcher.CallCount(service) == 0), "設定画面の更新がAIを取得しています");
    }

    private enum ResponseKind { Success, AuthenticationRequired, CommunicationFailure }

    private sealed class FakeUsageFetcher(string cacheFilePath) : UsageFetcherService(cacheFilePath)
    {
        private readonly int[] _calls = new int[6];
        public ResponseKind Response { get; set; }
        public Func<AiServiceType, int, Task>? OnFetch { get; set; }
        public int CallCount(AiServiceType service) => Volatile.Read(ref _calls[(int)service]);

        private async Task RecordAsync(AiServiceType service)
        {
            int count = Interlocked.Increment(ref _calls[(int)service]);
            if (OnFetch != null) await OnFetch(service, count);
            if (Response == ResponseKind.CommunicationFailure) throw new IOException("ダミー通信エラー");
        }

        protected override async Task<CodexQuotaData?> FetchCodexQuotaAsync()
        {
            await RecordAsync(AiServiceType.GPT);
            return new CodexQuotaData
            {
                IsSuccess = Response == ResponseKind.Success,
                IsAuthRequired = Response == ResponseKind.AuthenticationRequired,
                PlanType = "plus", HasFiveHourLimit = true, FiveHourRemainingPercent = 71,
                HasWeeklyLimit = true, WeeklyRemainingPercent = 83
            };
        }

        protected override async Task<ClaudeQuotaData> FetchClaudeQuotaAsync()
        {
            await RecordAsync(AiServiceType.Claude);
            return new ClaudeQuotaData
            {
                IsSuccess = Response == ResponseKind.Success, IsSubscribed = Response == ResponseKind.Success,
                IsAuthRequired = Response == ResponseKind.AuthenticationRequired,
                HasFiveHourLimit = true, FiveHourRemainingPercent = 71,
                HasWeeklyLimit = true, WeeklyRemainingPercent = 83
            };
        }

        protected override async Task<List<QuotaGroup>?> FetchGeminiQuotaAsync()
        {
            await RecordAsync(AiServiceType.Gemini);
            return Response == ResponseKind.Success
                ? [new QuotaGroup
                {
                    DisplayName = "Gemini",
                    Buckets = [new QuotaBucket { Window = "5h", RemainingFraction = .71 },
                        new QuotaBucket { Window = "weekly", RemainingFraction = .83 }]
                }]
                : null;
        }

        protected override async Task<CopilotQuotaData?> FetchCopilotQuotaAsync()
        {
            await RecordAsync(AiServiceType.Copilot);
            return new CopilotQuotaData
            {
                IsSuccess = Response == ResponseKind.Success,
                IsAuthRequired = Response == ResponseKind.AuthenticationRequired,
                RemainingPercent = 71, UsedPercent = 29, UsedCount = 29, TotalCount = 100
            };
        }

        protected override async Task<GrokQuotaData?> FetchGrokQuotaAsync()
        {
            await RecordAsync(AiServiceType.Grok);
            return new GrokQuotaData
            {
                IsSuccess = Response == ResponseKind.Success,
                IsAuthRequired = Response == ResponseKind.AuthenticationRequired,
                RemainingPercent = 71, UsedPercent = 29,
                ProductUsage = [new GrokProductUsage { Product = "GrokBuild", DisplayName = "Grok Build", UsedPercent = 29 }]
            };
        }
    }
}
