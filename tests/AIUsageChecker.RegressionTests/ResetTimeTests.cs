using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageChecker.Models;
using AIUsageChecker.Services;
using AIUsageChecker.Views;

namespace AIUsageChecker.RegressionTests;

internal static class ResetTimeTests
{
    private static readonly DateTime LateEvening = new(2026, 10, 9, 23, 0, 0);

    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("リセット日時: 当日の短期枠は時刻のみ", () => CheckDate(
            new(2026, 10, 9, 23, 50, 0), LateEvening, "23:50 リセット"));
        yield return new("リセット日時: 24時間以内でも翌日は日付付き", () => CheckDate(
            new(2026, 10, 10, 2, 50, 0), LateEvening, "10/10 02:50 リセット"));
        yield return new("リセット日時: 月末をまたぐ日時", () => CheckDate(
            new(2026, 11, 1, 1, 0, 0), new(2026, 10, 31, 23, 0, 0), "11/01 01:00 リセット"));
        yield return new("リセット日時: 年末をまたぐ日時", () => CheckDate(
            new(2027, 1, 1, 1, 0, 0), new(2026, 12, 31, 23, 0, 0), "01/01 01:00 リセット"));
        yield return new("リセット日時: 週次・予備枠は当日も日付を維持", () =>
        {
            Check.Equal("10/09 23:50 リセット", ResetTimeFormatter.Format(
                LateEvening.AddMinutes(50), LateEvening, alwaysShowDate: true), "週次枠の日付が省略されました");
            return Task.CompletedTask;
        });
        foreach (var (input, expected) in new[]
        {
            ("02:50", "02:50"),
            ("04:16 リセット", "04:16 リセット"),
            ("あと 3時間50分", "あと 3時間50分"),
            ("", "常時更新"),
            (" ", "常時更新"),
            ("取得できません", "取得できません")
        })
            yield return new($"リセット日時: 日時以外の「{input}」からリセット日を推測しない", () =>
            {
                Check.Equal(expected, ResetTimeFormatter.Format(input, LateEvening), "取得値から日時を推測して書き換えました");
                return Task.CompletedTask;
            });

        yield return new("リセット日時: UTCをローカルへ変換して日付を比較", UtcToLocalAsync);
        yield return new("リセット日時: 過去の明示日付を翌日へ変更しない", () => CheckDate(
            new(2026, 10, 8, 2, 50, 0), LateEvening, "10/08 02:50 リセット"));
        yield return new("リセット日時: オフセットと秒以下の精度を持つISO日時を整形", OffsetAndFractionAsync);
        yield return new("GPT: 実際の応答解析で翌日の5時間枠と枯渇表示を反映", CodexNextDayAsync);
        yield return new("Claude: 実際の設定読込で翌日のリセット日を反映", ClaudeNextDayAsync);
        yield return new("Gemini: CLI取得・キャッシュ保存でリセット日を保持", GeminiNextDayAsync);
    }

    private static Task CheckDate(DateTime reset, DateTime now, string expected)
    {
        Check.Equal(expected, ResetTimeFormatter.Format(reset, now), "リセット日の表示が違います");
        return Task.CompletedTask;
    }

    private static Task UtcToLocalAsync()
    {
        var resetLocal = new DateTime(2026, 10, 10, 4, 16, 0, DateTimeKind.Local);
        var resetUtc = new DateTimeOffset(resetLocal).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        Check.Equal("10/10 04:16 リセット", ResetTimeFormatter.Format(resetUtc, LateEvening),
            "UTCのまま日付を比較しています");
        // ローカル日時にオフセットがない場合も、UTC扱いで時刻をずらさない。
        Check.Equal("10/10 04:16 リセット", ResetTimeFormatter.Format("2026-10-10T04:16:00", LateEvening),
            "オフセットのないローカル日時が変わりました");
        return Task.CompletedTask;
    }

    private static Task OffsetAndFractionAsync()
    {
        var resetLocal = new DateTime(2026, 10, 10, 4, 16, 33, DateTimeKind.Local).AddTicks(1234567);
        var resetWithOffset = new DateTimeOffset(resetLocal).ToOffset(TimeSpan.FromHours(9));
        foreach (var format in new[] { "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.fffzzz", "O" })
            Check.Equal("10/10 04:16 リセット", ResetTimeFormatter.Format(
                resetWithOffset.ToString(format, CultureInfo.InvariantCulture), LateEvening),
                "オフセット・小数秒を含むISO日時を整形できません");
        return Task.CompletedTask;
    }

    private static async Task CodexNextDayAsync()
    {
        using var temp = new TemporaryDirectory();
        var authPath = temp.FilePath("fake-auth.json");
        await File.WriteAllTextAsync(authPath, """{"tokens":{"access_token":"dummy-token"}}""");
        var resetLocal = DateTime.Now.Date.AddDays(1).AddHours(2).AddMinutes(50);
        var resetUnix = new DateTimeOffset(DateTime.SpecifyKind(resetLocal, DateTimeKind.Local)).ToUnixTimeSeconds();
        var response = JsonSerializer.Serialize(new
        {
            rate_limit = new
            {
                primary_window = new { limit_window_seconds = 18000, used_percent = 100, reset_at = resetUnix },
                secondary_window = new { limit_window_seconds = 604800, used_percent = 20, reset_at = resetUnix }
            }
        });
        using var handler = new QuotaHttpHandler(response);
        using var http = new HttpClient(handler);
        var result = await new CodexQuotaClient(http).FetchCodexQuotaAsync(authPath);
        Check.True(result?.IsSuccess == true, "偽のGPT応答を取得できません");
        Check.Equal($"{resetLocal:MM/dd HH:mm} リセット (枯渇)", result!.FiveHourResetText,
            "GPTの5時間枠で翌日の日付または枯渇表示がありません");
        Check.Equal($"{resetLocal:MM/dd HH:mm} リセット", result.WeeklyResetText, "GPTの週次枠の日付が変わりました");
    }

    private static async Task ClaudeNextDayAsync()
    {
        using var temp = new TemporaryDirectory();
        var configPath = temp.FilePath("fake-config.json");
        var resetLocal = DateTime.SpecifyKind(DateTime.Now.Date.AddDays(1).AddHours(2).AddMinutes(50), DateTimeKind.Local);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            oauthAccount = new { billingType = "subscription", organizationType = "pro" },
            cachedUsageUtilization = new
            {
                utilization = new { five_hour = new { utilization = 35, resets_at = new DateTimeOffset(resetLocal).ToUniversalTime() } }
            }
        }));
        var result = await new ClaudeQuotaClient().FetchClaudeQuotaAsync(configPath);
        Check.True(result.IsSuccess, "偽のClaude設定を取得できません");
        Check.Equal($"{resetLocal:MM/dd HH:mm} リセット", result.FiveHourResetText, "Claudeで翌日のリセット日を省略しています");
    }

    private static async Task GeminiNextDayAsync()
    {
        using var temp = new TemporaryDirectory();
        var cachePath = temp.FilePath("cache.json");
        var resetLocal = DateTime.SpecifyKind(DateTime.Now.Date.AddDays(1).AddHours(4).AddMinutes(16), DateTimeKind.Local);
        var fetcher = new FakeGeminiFetcher(cachePath)
        {
            Quota = [new QuotaGroup
            {
                DisplayName = "Gemini",
                Buckets = [new QuotaBucket
                {
                    Window = "5h", RemainingFraction = 1,
                    ResetTime = new DateTimeOffset(resetLocal).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                }]
            }]
        };
        var item = new AiUsageItem { ServiceType = AiServiceType.Gemini };
        await fetcher.FetchUsageAsync(item);
        var expected = $"{resetLocal:MM/dd HH:mm} リセット";
        Check.Equal(expected, item.PrimaryLimit.ResetTimeText, "GeminiのCLI取得後に翌日の日付を省略しています");
        var cache = JsonSerializer.Deserialize<AllQuotaCache>(await File.ReadAllTextAsync(cachePath));
        var cachedResetTime = cache!.GeminiGroups![0].Buckets[0].ResetTime;
        Check.Equal(expected, ResetTimeFormatter.Format(cachedResetTime, DateTime.Now),
            "キャッシュへの保存でGeminiの日付が消えました");
    }

    internal static void RunOnStaThread()
    {
        var items = InitialCatalogFactory.CreateInitialServices(["GPT/Codex", "Antigravity"])
            .Where(item => item.ServiceType is AiServiceType.GPT or AiServiceType.Gemini);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(22, 28, 25)) };
        foreach (var item in items)
        {
            item.CliInfo.IsBusy = false;
            item.CliInfo.IsInstalled = true;
            item.CliInfo.IsLoggedIn = true;
            item.CliInfo.IsSubscribed = true;
            item.PrimaryLimit.RemainingPercent = item.ServiceType == AiServiceType.GPT ? 43 : 100;
            item.PrimaryLimit.CustomDisplayPercentText = null;
            item.PrimaryLimit.ResetTimeText = item.ServiceType == AiServiceType.GPT
                ? "10/10 02:50 リセット" : "10/10 04:16 リセット";
            item.SecondaryLimit!.RemainingPercent = item.ServiceType == AiServiceType.GPT ? 73 : 100;
            item.SecondaryLimit.CustomDisplayPercentText = null;
            item.SecondaryLimit.ResetTimeText = "10/14 21:33 リセット";
            var card = new UsageCardView { DataContext = item, Width = 154, Margin = new Thickness(2) };
            panel.Children.Add(card);
        }
        void Layout()
        {
            panel.Measure(new Size(316, 140));
            panel.Arrange(new Rect(0, 0, 316, 140));
            panel.UpdateLayout();
        }
        Layout();
        foreach (UsageCardView card in panel.Children)
        {
            var item = (AiUsageItem)card.DataContext;
            var text = Descendants<TextBlock>(card).Single(text => text.FontSize == 9 && text.Text == item.PrimaryLimit.ResetTimeText);
            var dateAndTime = item.PrimaryLimit.ResetTimeText[..11];
            var formatted = new FormattedText(dateAndTime + "…", CultureInfo.CurrentCulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Check.True(text.Visibility == Visibility.Visible && text.ActualWidth >= formatted.WidthIncludingTrailingWhitespace,
                $"{item.DisplayName}: 日付と時刻がカード内で省略されています");
            var boundsWithDate = card.RenderSize;
            item.PrimaryLimit.ResetTimeText = "02:50 リセット";
            Layout();
            Check.Equal(boundsWithDate, card.RenderSize, "日付表示でカード寸法が変わりました");
            item.PrimaryLimit.ResetTimeText = dateAndTime + " リセット";
            Layout();
        }
        var bitmap = new RenderTargetBitmap(316, 140, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/reset-time-next-day.png"));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("成功: リセット日時: 日付と時刻を表示してカード寸法を維持");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class FakeGeminiFetcher(string cachePath) : UsageFetcherService(cachePath)
    {
        public List<QuotaGroup>? Quota { get; init; }
        protected override Task<List<QuotaGroup>?> FetchAgyQuotaAsync() => Task.FromResult(Quota);
    }

    private sealed class QuotaHttpHandler(string quotaJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? quotaJson : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
