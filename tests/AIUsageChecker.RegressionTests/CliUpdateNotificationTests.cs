using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using AIUsageChecker.Models;
using AIUsageChecker.Services;
using AIUsageChecker.ViewModels;
using AIUsageChecker.Views;

namespace AIUsageChecker.RegressionTests;

internal static class CliUpdateNotificationTests
{
    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("CLI更新通知: 1件でも表示し、未導入・処理中・最新のCLIを除外", () =>
        {
            var notification = Create(new());
            var items = new[] { Item("codex"), Item("claude"), Item("grok"), Item("copilot") };
            items[1].CliInfo.IsInstalled = false;
            items[2].CliInfo.IsBusy = true;
            items[3].CliInfo.HasUpdate = false;
            notification.Refresh(items);
            Check.True(notification.IsOpen, "1件の更新通知が必要です");
            Check.Equal("1件のCLIを更新できます", notification.Message, "対象件数が一致しません");
            Check.True(notification.Details.Contains("codex") && !notification.Details.Contains("claude"), "対象外のCLIを案内しています");
            return Task.CompletedTask;
        });

        yield return new("CLI更新通知: 未契約Claudeも更新あり3件に含め、更新後は未契約に戻す", async () =>
        {
            var codex = Item("codex");
            var copilot = Item("copilot");
            var claude = Item("claude");
            claude.CliInfo.IsSubscribed = false;
            var items = new[] { codex, copilot, claude };
            var calls = new List<string>();
            var notification = Create(new(), update: item =>
            {
                calls.Add(item.CliInfo.Name);
                item.CliInfo.InstalledVersion = item.CliInfo.LatestVersion;
                item.CliInfo.HasUpdate = false;
                return Task.FromResult(true);
            });
            Check.Equal("更新あり", claude.CliInfo.StatusBadgeText, "未契約でもCLI更新を表示してください");
            Check.Equal(3, items.Count(item => item.CliInfo.StatusBadgeText == "更新あり"), "カードの更新表示は3件です");
            notification.Refresh(items);
            Check.Equal("3件のCLIを更新できます", notification.Message, "未契約Claudeも通知に含めてください");
            Check.True(notification.Details.Contains("claude"), "未契約Claudeの更新を案内していません");
            Check.True(CliUpdateNotificationViewModel.HasAvailableUpdates([claude]), "未契約ClaudeのCLI更新を手動再表示できません");
            await notification.UpdateAllAsync();
            Check.Equal("codex,copilot,claude", string.Join(",", calls), "未契約Claudeを一括更新から除外しないでください");
            Check.Equal("未契約", claude.CliInfo.StatusBadgeText, "CLI更新完了後は未契約表示に戻してください");
            Check.True(!claude.CliInfo.IsSubscribed, "CLI更新で契約状況が変わりました");
            Check.True(!CliUpdateNotificationViewModel.HasAvailableUpdates([claude]), "更新完了後も通知の対象に残っています");
            Check.True(notification.IsSuccessfulResult, "すべて成功した更新の結果を成功表示にしてください");
            notification.CloseCommand.Execute(null);
        });

        yield return new("CLI更新通知: 未契約ClaudeのCLI更新失敗時は更新あり表示を維持", async () =>
        {
            var claude = Item("claude");
            claude.CliInfo.IsSubscribed = false;
            var notification = Create(new(), update: _ => Task.FromResult(false));
            notification.Refresh([claude]);
            await notification.UpdateAllAsync();
            Check.Equal("更新あり", claude.CliInfo.StatusBadgeText, "失敗した更新を未契約表示で隠さないでください");
            Check.True(!claude.CliInfo.IsSubscribed && !claude.CliInfo.IsBusy && !claude.CliInfo.IsUpdating,
                "失敗後の契約状況・操作中状態が正しくありません");
            Check.Equal("CLI更新: 0件成功・1件失敗", notification.Message, "更新失敗の結果を表示してください");
            Check.True(!notification.IsSuccessfulResult, "失敗した更新を成功表示にしないでください");
            notification.CloseCommand.Execute(null);
        });

        yield return new("CLI更新通知: 未ログイン・取得エラー・更新中のCLIもカード表示に合わせて除外", () =>
        {
            var login = Item("codex");
            login.CliInfo.IsLoggedIn = false;
            var error = Item("claude");
            error.CliInfo.HasUsageError = true;
            var updating = Item("grok");
            updating.CliInfo.IsUpdating = true;
            var notification = Create(new());
            notification.Refresh([login, error, updating]);
            Check.True(!notification.IsOpen, "更新ありを表示していないCLIを通知対象に含めています");
            return Task.CompletedTask;
        });

        yield return new("CLI更新通知: 表示時に保存し、未操作のまま終了しても再起動後に再表示しない", () =>
        {
            using var directory = new TemporaryDirectory();
            string filename = directory.FilePath("settings.json");
            var settings = new AppSettings();
            int saves = 0;
            var notification = Create(settings, () =>
            {
                saves++;
                File.WriteAllText(filename, JsonSerializer.Serialize(settings));
            });
            var items = new[] { Item("codex"), Item("claude"), Item("grok") };
            notification.Refresh(items);
            Check.Equal(0, saves, "まだ表示されていない案内を保存しました");
            notification.MarkDisplayed();
            notification.MarkDisplayed();
            Check.Equal(1, saves, "同じ案内の保存が繰り返されています");
            notification.Refresh(items);
            Check.True(notification.IsOpen, "表示中の通知を更新確認だけで閉じないでください");
            var restarted = Create(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filename))!);
            restarted.Refresh(items);
            Check.True(!restarted.IsOpen, "×を押さずに終了した案内が再起動後に復活しました");
            return Task.CompletedTask;
        });

        yield return new("CLI更新通知: ×で閉じた更新を再案内せず、新バージョンと別CLIだけを案内", () =>
        {
            var settings = new AppSettings();
            var notification = Create(settings);
            var codex = Item("codex");
            notification.Refresh([codex]);
            notification.CloseCommand.Execute(null);
            notification.Refresh([codex]);
            Check.True(!notification.IsOpen, "閉じた案内が同じ起動中に復活しました");
            codex.CliInfo.LatestVersion = "v2.0.0";
            codex.CliInfo.CommandName = "CODEX";
            notification.Refresh([codex]);
            Check.True(!notification.IsOpen, "バージョンのv接頭辞やCLI名の大文字だけで再案内しました");
            codex.CliInfo.LatestVersion = "2.1.0";
            notification.Refresh([codex]);
            Check.True(notification.IsOpen, "新しい更新先バージョンを案内してください");
            notification.CloseCommand.Execute(null);
            notification.Refresh([codex, Item("claude")]);
            Check.Equal("1件のCLIを更新できます", notification.Message, "見送った更新を別CLIの案内に混ぜないでください");
            Check.True(notification.Details.Contains("claude") && !notification.Details.Contains("codex", StringComparison.OrdinalIgnoreCase),
                "未案内のCLIだけを対象にしてください");
            return Task.CompletedTask;
        });

        yield return new("CLI更新通知: 未表示の更新は次回起動でも案内でき、旧設定も読み込める", () =>
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("{\"IsAlwaysOnTop\":false}")!;
            var notification = Create(settings);
            notification.Refresh([Item("codex")]);
            var restarted = Create(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!);
            restarted.Refresh([Item("codex")]);
            Check.True(restarted.IsOpen, "実際に表示していない通知を既読扱いしました");
            Check.True(!settings.IsAlwaysOnTop, "既存の設定が失われました");
            return Task.CompletedTask;
        });

        yield return new("CLI更新通知: 手動再表示で案内済み更新を確認でき、自動通知履歴は保持", () =>
        {
            var items = new[] { Item("codex"), Item("claude") };
            var settings = new AppSettings();
            int saves = 0;
            var notification = Create(settings, () => saves++);
            notification.Refresh(items);
            notification.MarkDisplayed();
            notification.CloseCommand.Execute(null);
            string savedSettings = JsonSerializer.Serialize(settings);
            notification.Refresh(items);
            Check.True(!notification.IsOpen, "案内済みの更新を自動で表示しました");
            notification.ShowAgain(items);
            Check.True(notification.IsOpen && notification.UpdateAllCommand.CanExecute(null), "手動で案内済みの更新を表示・操作できません");
            Check.Equal("2件のCLIを更新できます", notification.Message, "手動再表示の対象件数が一致しません");
            notification.MarkDisplayed();
            notification.CloseCommand.Execute(null);
            Check.Equal(savedSettings, JsonSerializer.Serialize(settings), "手動再表示で設定・案内済み履歴が変わりました");
            Check.Equal(1, saves, "手動再表示のたびに同じ履歴を保存しています");
            notification.Refresh(items);
            Check.True(!notification.IsOpen, "手動再表示後に同じ更新を自動表示しました");
            var restarted = Create(JsonSerializer.Deserialize<AppSettings>(savedSettings)!);
            restarted.Refresh(items);
            Check.True(!restarted.IsOpen, "手動再表示後の再起動で同じ更新を再案内しました");
            return Task.CompletedTask;
        });

        yield return new("CLI更新通知: 更新対象なし・同期中は手動再表示しない", () =>
        {
            var current = Item("codex");
            current.CliInfo.HasUpdate = false;
            var notification = Create(new());
            Check.True(!CliUpdateNotificationViewModel.HasAvailableUpdates([current]), "最新のCLIを更新対象にしています");
            notification.ShowAgain([current]);
            Check.True(!notification.IsOpen, "更新対象がないのに通知を表示しました");
            var busy = Create(new(), canUpdate: () => false);
            busy.ShowAgain([Item("codex")]);
            Check.True(!busy.IsOpen, "同期中に通知を再表示しました");
            return Task.CompletedTask;
        });

        yield return new("CLI一括更新: 順番に実行し、失敗・例外後も続行して操作状態を解除", async () =>
        {
            var calls = new List<string>();
            int active = 0;
            var items = new[] { Item("codex"), Item("claude"), Item("grok"), Item("copilot") };
            var notification = Create(new(), update: async item =>
            {
                Check.Equal(0, active++, "CLI更新が並列に実行されています");
                Check.True(item.CliInfo.IsBusy && item.CliInfo.IsUpdating, "カードの更新中表示がありません");
                calls.Add(item.CliInfo.Name);
                await Task.Yield();
                active--;
                if (item.CliInfo.Name == "claude") return false;
                if (item.CliInfo.Name == "grok") throw new InvalidOperationException("テスト用の失敗");
                item.CliInfo.HasUpdate = false;
                return true;
            });
            notification.Refresh(items);
            await notification.UpdateAllAsync();
            Check.Equal("codex,claude,grok,copilot", string.Join(",", calls), "失敗後も残りのCLIを更新してください");
            Check.True(!notification.IsUpdating && items.All(item => !item.CliInfo.IsBusy && !item.CliInfo.IsUpdating),
                "一括更新後も操作中状態が残っています");
            Check.Equal("CLI更新: 2件成功・2件失敗", notification.Message, "結果件数が正しくありません");
            Check.True(!notification.IsSuccessfulResult, "一部失敗を含む結果を成功表示にしないでください");
            Check.True(notification.IsResult && !notification.UpdateAllCommand.CanExecute(null), "結果通知で再実行しないでください");
            notification.CloseCommand.Execute(null);
            notification.Refresh(items);
            Check.True(!notification.IsOpen, "更新失敗を理由に同じ通知が繰り返されています");
        });

        yield return new("CLI一括更新: 個別操作・同期中と二重クリックの競合を防ぐ", async () =>
        {
            bool allowed = false;
            int calls = 0;
            var completion = new TaskCompletionSource<bool>();
            var notification = Create(new(), update: _ => { calls++; return completion.Task; }, canUpdate: () => allowed);
            notification.Refresh([Item("codex")]);
            Check.True(!notification.UpdateAllCommand.CanExecute(null), "同期中に一括更新できます");
            await notification.UpdateAllAsync();
            Check.Equal(0, calls, "同期中に更新処理を開始しました");
            allowed = true;
            var running = notification.UpdateAllAsync();
            Check.True(!notification.IsOpen && notification.IsUpdating, "開始時に通知を閉じて処理中にしてください");
            notification.ShowAgain([Item("codex")]);
            Check.True(!notification.IsOpen, "一括更新中に通知が復活しました");
            await notification.UpdateAllAsync();
            Check.Equal(1, calls, "二重クリックで同じCLIを重複更新しました");
            completion.SetResult(true);
            await running;
            notification.CloseCommand.Execute(null);
        });
    }

    internal static void RunOnStaThread()
    {
        var notification = Create(new());
        notification.Refresh([Item("codex")]);
        var notice = new CliUpdateNoticeView { DataContext = notification, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform(0, -32) };
        var host = new Grid { Width = 475, Height = 280, Background = new SolidColorBrush(Color.FromRgb(22, 27, 25)) };
        var cards = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var items = InitialCatalogFactory.CreateInitialServices(null);
        foreach (var item in items)
            cards.Children.Add(new UsageCardView { Width = 154, Margin = new Thickness(2), DataContext = item });
        host.Children.Add(cards);
        notice.Visibility = Visibility.Collapsed;
        host.Children.Add(notice);
        Arrange(host);
        var bounds = cards.Children.Cast<FrameworkElement>().Select(card => Bounds(card, host)).ToArray();
        notice.Visibility = Visibility.Visible;
        Arrange(host);
        Check.True(bounds.SequenceEqual(cards.Children.Cast<FrameworkElement>().Select(card => Bounds(card, host))),
            "通知を表示したときにカードの寸法・位置が変わりました");
        var update = (Button)notice.FindName("UpdateAllButton");
        var close = (Button)notice.FindName("DismissButton");
        Check.True(update.IsEnabled && close.IsEnabled, "1件の案内を操作できません");
        Check.True(ReferenceEquals(update.Command, notification.UpdateAllCommand) && ReferenceEquals(close.Command, notification.CloseCommand),
            "通知ボタンのコマンドが一致しません");
        Check.Equal(42d, notice.ActualHeight, "通知の高さが変わっています");
        Check.True(Bounds(notice, host).Contains(Bounds(update, host)) && Bounds(notice, host).Contains(Bounds(close, host)),
            "通知ボタンが枠からはみ出しています");
        var bitmap = new RenderTargetBitmap(475, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/cli-update-notice.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = File.Create(output)) encoder.Save(stream);
        var surface = (Border)notice.FindName("NoticeSurface");
        var announcementColor = ((SolidColorBrush)surface.Background).Color;
        var partial = Create(new(), update: item => Task.FromResult(item.CliInfo.Name == "codex"));
        notice.DataContext = partial;
        partial.Refresh([Item("codex"), Item("claude")]);
        partial.UpdateAllAsync().GetAwaiter().GetResult();
        Arrange(host);
        Check.Equal(announcementColor, ((SolidColorBrush)surface.Background).Color,
            "失敗を含む更新結果はオレンジ系で表示してください");
        partial.CloseCommand.Execute(null);
        notice.DataContext = notification;
        close.Command.Execute(null);
        Check.True(!notification.IsOpen, "×操作で通知が閉じません");
        CheckMainWindowPresentation();
        Console.WriteLine("成功: CLI更新通知: 重ね表示でカード寸法と位置を維持、ボタンの配置と操作");
    }

    private static void CheckMainWindowPresentation()
    {
        // 実際のXAMLを使うが、MainWindowのコンストラクター・設定・CLIは実行しない。
        var document = XDocument.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../MainWindow.xaml")));
        var source = document.Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        source.Attribute(x + "Class")!.Remove();
        source.SetAttributeValue("Icon", "pack://application:,,,/AIUsageChecker;component/assets/app.ico");
        foreach (var attribute in source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            if (attribute.Value.StartsWith("clr-namespace:") && !attribute.Value.Contains(";assembly="))
                attribute.Value += ";assembly=AIUsageChecker";
        foreach (var element in source.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName.StartsWith("clr-namespace:") && !element.Name.NamespaceName.Contains(";assembly="))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=AIUsageChecker");
            foreach (var attribute in element.Attributes().Where(attribute =>
                         attribute.Name.LocalName is "Click" or "MouseLeftButtonDown" or "MouseLeftButtonUp" or "MouseMove").ToArray())
                attribute.Remove();
            if (element.Name.LocalName == "BitmapImage")
                element.SetAttributeValue("UriSource", "pack://application:,,,/AIUsageChecker;component/assets/icon-images.png");
        }
        using var reader = document.CreateReader();
        var window = (Window)XamlReader.Load(reader);
        try
        {
            var settings = new AppSettings();
            var context = new MainWindowContext(Create(settings, update: item =>
            {
                item.CliInfo.HasUpdate = false;
                item.CliInfo.InstalledVersion = item.CliInfo.LatestVersion;
                return Task.FromResult(true);
            }));
            window.DataContext = context;
            var root = (FrameworkElement)window.Content;
            void Layout()
            {
                root.Measure(new Size(498, 334));
                root.Arrange(new Rect(0, 0, 498, 334));
                root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
            Layout();
            var notice = (CliUpdateNoticeView)window.FindName("CliUpdateNotice");
            var loading = (Grid)window.FindName("StartupLoadingOverlay");
            var cards = (ItemsControl)window.FindName("UsageCardsControl");
            context.CliUpdates.Refresh(context.Items);
            Check.Equal("3件のCLIを更新できます", context.CliUpdates.Message,
                "未契約Claudeも含めた更新3件を案内してください");
            Layout();
            Check.Equal(Visibility.Collapsed, notice.Visibility, "読み込み中にCLI更新通知を表示しています");
            var bounds = Enumerable.Range(0, context.Items.Count)
                .Select(index => Bounds((FrameworkElement)cards.ItemContainerGenerator.ContainerFromIndex(index), root)).ToArray();
            context.IsStartupLoading = false;
            Layout();
            Check.Equal(Visibility.Collapsed, notice.Visibility, "読み込み画面のフェード中にCLI更新通知を表示しています");
            var frame = new DispatcherFrame();
            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            fadeTimer.Tick += (_, _) => { fadeTimer.Stop(); frame.Continue = false; };
            fadeTimer.Start();
            Dispatcher.PushFrame(frame);
            Layout();
            Check.Equal(Visibility.Visible, notice.Visibility,
                $"読み込み終了後にCLI更新通知を表示してください (context={notice.DataContext?.GetType().Name}, open={context.CliUpdates.IsOpen}, loading={loading.Visibility}, startup={context.IsStartupLoading})");
            Check.True(bounds.SequenceEqual(Enumerable.Range(0, context.Items.Count)
                .Select(index => Bounds((FrameworkElement)cards.ItemContainerGenerator.ContainerFromIndex(index), root))),
                "実際のメイン画面で通知がカードの寸法・位置を変えました");
            Check.Equal(498d, window.Width, "アプリの幅を変えないでください");
            Check.Equal(334d, window.Height, "アプリの高さを変えないでください");
            var noticeBounds = Bounds(notice, root);
            var shadowBounds = noticeBounds;
            shadowBounds.Inflate(0, 4);
            var cardViews = Descendants<UsageCardView>(cards).ToArray();
            Check.Equal(context.Items.Count, cardViews.Length, "すべてのカードの更新バッジを検証してください");
            foreach (var card in cardViews)
            {
                var badge = (Border)card.FindName("CliStatusBadge");
                Check.True(!shadowBounds.IntersectsWith(Bounds(badge, root)),
                    $"{((AiUsageItem)card.DataContext).DisplayName}: 通知がCLI更新バッジを隠しています");
            }
            var claudeCard = cardViews.Single(card => ((AiUsageItem)card.DataContext).CliInfo.CommandName == "claude");
            var claudeBadge = (TextBlock)((Border)claudeCard.FindName("CliStatusBadge")).Child;
            Check.Equal("更新あり", claudeBadge.Text, "ClaudeのバッジにCLI更新を表示してください");
            Check.True(Descendants<TextBlock>(claudeCard).Any(text => text.Text == "未契約"),
                "CLI更新があるときも、Claudeの契約状況をカード本文に残してください");
            Check.True(noticeBounds.Top + noticeBounds.Height / 2 < root.ActualHeight / 2 &&
                noticeBounds.Top + noticeBounds.Height / 2 > root.ActualHeight / 3,
                "通知を画面中央より少し上に配置してください");
            var detailView = new DetailPopupView();
            detailView.Measure(new Size(468, 310));
            var detailUpdate = Descendants<Button>(detailView).Single(button => Equals(button.Content, "CLI更新"));
            var bulkUpdate = (Button)notice.FindName("UpdateAllButton");
            Check.Equal(((SolidColorBrush)detailUpdate.Background).Color, ((SolidColorBrush)bulkUpdate.Background).Color,
                "一括更新の背景色を既存のCLI更新ボタンに揃えてください");
            Check.Equal(((SolidColorBrush)detailUpdate.Foreground).Color, ((SolidColorBrush)bulkUpdate.Foreground).Color,
                "一括更新の文字色を既存のCLI更新ボタンに揃えてください");
            Check.Equal(((SolidColorBrush)detailUpdate.BorderBrush).Color, ((SolidColorBrush)bulkUpdate.BorderBrush).Color,
                "一括更新の枠色を既存のCLI更新ボタンに揃えてください");
            context.IsDetailOpen = true;
            Layout();
            Check.Equal(Visibility.Collapsed, notice.Visibility, "詳細画面にCLI更新通知が重なっています");
            context.IsDetailOpen = false;
            Layout();
            Check.Equal(Visibility.Visible, notice.Visibility, "詳細を閉じた後にCLI更新通知が戻りません");
            Check.Equal(0, settings.AnnouncedCliUpdates.Count, "実際には表示していないウィンドウの通知を既読にしました");
            var bitmap = new RenderTargetBitmap(498, 334, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/cli-update-main.png"))))
                encoder.Save(stream);
            context.CliUpdates.CloseCommand.Execute(null);
            context.CliUpdates.Refresh(context.Items);
            Layout();
            Check.Equal(Visibility.Collapsed, notice.Visibility, "案内済みの更新を自動で表示しました");
            var refreshButton = (Button)window.FindName("RefreshAllButton");
            refreshButton.ContextMenu.PlacementTarget = refreshButton;
            Layout();
            var showNotice = (MenuItem)refreshButton.ContextMenu.Items[0];
            Check.True(ReferenceEquals(showNotice.Command, context.ShowCliUpdateNotificationCommand),
                "右クリックの再表示操作がViewModelに接続されていません");
            var shortcut = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.U &&
                binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            Check.True(ReferenceEquals(shortcut.Command, context.ShowCliUpdateNotificationCommand),
                "Ctrl+Shift+Uが再表示操作に接続されていません");
            showNotice.Command.Execute(null);
            Layout();
            Check.Equal(Visibility.Visible, notice.Visibility, "メニュー操作で案内済みの更新を再表示できません");
            context.CliUpdates.UpdateAllAsync().GetAwaiter().GetResult();
            Layout();
            Check.Equal(Visibility.Collapsed, ((Button)notice.FindName("UpdateAllButton")).Visibility,
                "結果通知に一括更新ボタンを表示しないでください");
            var successSurface = (Border)notice.FindName("NoticeSurface");
            var successBackground = ((SolidColorBrush)successSurface.Background).Color;
            var successBorder = ((SolidColorBrush)successSurface.BorderBrush).Color;
            var successText = ((SolidColorBrush)((TextBlock)notice.FindName("NoticeMessage")).Foreground).Color;
            Check.True(successBackground.G > successBackground.R && successBackground.G > successBackground.B &&
                successBorder.G > successBorder.R && successText.G > successText.R,
                "すべて成功した更新結果の背景・枠・文字をグリーン系にしてください");
            Check.Equal(noticeBounds, Bounds(notice, root), "更新結果の配色変更で通知の位置・寸法が変わりました");
            bitmap = new RenderTargetBitmap(498, 334, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/cli-update-main-success.png"))))
                encoder.Save(stream);
            context.CliUpdates.CloseCommand.Execute(null);
            Layout();
            Check.Equal(Visibility.Collapsed, notice.Visibility, "結果通知を閉じられません");
            Check.Equal("未契約", claudeBadge.Text, "CLI更新完了後にClaudeのバッジが未契約に戻りません");
            Check.True(Descendants<TextBlock>(claudeCard).Any(text => text.Text == "未契約"),
                "CLI更新でClaudeの未契約情報が消えました");
            bitmap = new RenderTargetBitmap(498, 334, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/cli-update-main-complete.png"))))
                encoder.Save(stream);
            CheckBadgeAlignment(cardViews[0], root, Layout);
        }
        finally
        {
            window.Close();
        }
    }

    private static void CheckBadgeAlignment(UsageCardView card, FrameworkElement root, Action layout)
    {
        var item = (AiUsageItem)card.DataContext;
        var originalCli = item.CliInfo;
        var cardBounds = Bounds(card, root);
        var detail = new DetailPopupView { DataContext = new { SelectedItem = item } };
        var states = new (string Text, Action<CliInfo> SetState)[]
        {
            ("最新", _ => { }),
            ("更新あり", cli => cli.HasUpdate = true),
            ("更新中...", cli => cli.IsUpdating = true),
            ("確認中...", cli => cli.IsBusy = true),
            ("未契約", cli => cli.IsSubscribed = false),
            ("未導入", cli => cli.IsInstalled = false),
            ("要ログイン", cli => cli.IsLoggedIn = false),
            ("取得エラー", cli => cli.HasUsageError = true)
        };
        try
        {
            foreach (var (text, setState) in states)
            {
                var cli = new CliInfo { IsInstalled = true, IsBusy = false };
                setState(cli);
                item.CliInfo = cli;
                layout();
                detail.Measure(new Size(468, 310));
                detail.Arrange(new Rect(0, 0, 468, 310));
                detail.UpdateLayout();
                foreach (var (badge, host) in new[]
                {
                    ((Border)card.FindName("CliStatusBadge"), root),
                    ((Border)detail.FindName("CliStatusBadge"), (FrameworkElement)detail)
                })
                {
                    var label = (TextBlock)badge.Child;
                    Check.Equal(text, label.Text, "検証対象のCLIバッジ状態が一致しません");
                    var badgeBounds = Bounds(badge, host);
                    var labelBounds = Bounds(label, host);
                    Check.True(Math.Abs(labelBounds.Top + labelBounds.Height / 2 -
                        (badgeBounds.Top + badgeBounds.Height / 2)) < .5,
                        $"{text}: CLIバッジの文字が帯の縦中央にありません");
                }
                Check.Equal(cardBounds, Bounds(card, root), $"{text}: バッジの中央揃えでカードの位置・寸法が変わりました");
            }
        }
        finally
        {
            item.CliInfo = originalCli;
            layout();
        }
        Console.WriteLine("成功: CLI状態バッジ: すべての状態をカードと詳細画面で縦中央に表示");
    }

    private sealed class MainWindowContext : ViewModelBase
    {
        private bool _isStartupLoading = true;
        private bool _isDetailOpen;
        public CliUpdateNotificationViewModel CliUpdates { get; }
        public RelayCommand ShowCliUpdateNotificationCommand { get; }
        public System.Collections.ObjectModel.ObservableCollection<AiUsageItem> Items { get; } =
            InitialCatalogFactory.CreateInitialServices(["GPT/Codex", "Antigravity", "Grok", "Claude", "Copilot"]);
        public bool IsStartupLoading { get => _isStartupLoading; set => SetProperty(ref _isStartupLoading, value); }
        public bool IsDetailOpen { get => _isDetailOpen; set => SetProperty(ref _isDetailOpen, value); }
        public bool IsDocked => false;
        public bool IsAlwaysOnTop => false;
        public string StatusText => "準備完了";
        public MainWindowContext(CliUpdateNotificationViewModel notification)
        {
            CliUpdates = notification;
            ShowCliUpdateNotificationCommand = new RelayCommand(() => CliUpdates.ShowAgain(Items));
            foreach (var item in Items)
            {
                item.CliInfo.IsBusy = false;
                item.CliInfo.IsInstalled = true;
            }
            foreach (var item in Items.Where(item => item.CliInfo.CommandName is "codex" or "claude" or "copilot"))
            {
                item.CliInfo.HasUpdate = true;
                item.CliInfo.LatestVersion = "2.0.0";
            }
            var claude = Items.Single(item => item.CliInfo.CommandName == "claude");
            claude.CliInfo.IsSubscribed = false;
            claude.PrimaryLimit.ResetTimeText = "未契約";
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Arrange(FrameworkElement element)
    {
        element.Measure(new Size(475, 280));
        element.Arrange(new Rect(0, 0, 475, 280));
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static Rect Bounds(FrameworkElement element, Visual host) =>
        element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));

    private static CliUpdateNotificationViewModel Create(AppSettings settings, Action? save = null,
        Func<AiUsageItem, Task<bool>>? update = null, Func<bool>? canUpdate = null) =>
        new(settings, save ?? (() => { }), update ?? (_ => Task.FromResult(true)), canUpdate ?? (() => true), _ => { });

    private static AiUsageItem Item(string commandName) => new()
    {
        DisplayName = commandName,
        CliInfo = new CliInfo { Name = commandName, CommandName = commandName, IsInstalled = true,
            HasUpdate = true, InstalledVersion = "1.0.0", LatestVersion = "2.0.0", IsBusy = false }
    };
}
