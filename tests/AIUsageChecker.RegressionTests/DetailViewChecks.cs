using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIUsageChecker.Models;
using AIUsageChecker.ViewModels;
using AIUsageChecker.Views;

namespace AIUsageChecker.RegressionTests;

internal static class DetailViewChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOnStaThread();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    if (Application.Current?.Dispatcher == dispatcher)
                        Application.Current.Shutdown();
                    if (!dispatcher.HasShutdownStarted)
                    {
                        // Application の終了 callback を先に処理してから STA の dispatcher も止める。
                        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                            new Action(dispatcher.InvokeShutdown));
                        Dispatcher.Run();
                    }
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            }
        }) { IsBackground = true, Name = "DetailViewChecks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("詳細画面の配置検証が完了しませんでした。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [STAThread]
    private static void RunOnStaThread()
    {
        // App の起動経路は使わず、テンプレートとスタイルだけを読み込む。
        if (Application.Current == null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = LoadApplicationResources();
        }

        var view = new DetailPopupView();
        var scenarios = new[]
        {
            (AiServiceType.Grok, "Grok", "grok"),
            (AiServiceType.GPT, "Codex", "codex"),
            (AiServiceType.Claude, "Claude", "claude"),
            (AiServiceType.Gemini, "Gemini", "gemini"),
            (AiServiceType.Gemini, "Antigravity", "agy"),
            (AiServiceType.Copilot, "Copilot", "copilot")
        };

        foreach (var (serviceType, name, commandName) in scenarios)
        {
            var item = CreateItem(serviceType, name, commandName);
            var context = new DetailTestContext(item);
            view.DataContext = context;
            Arrange(view);
            AssertUsageSourceControls(view, item, commandName, name);

            var buttons = Descendants<Button>(view).ToArray();
            var login = buttons.Single(button => ReferenceEquals(button.Command, context.LoginCliCommand));
            var refresh = buttons.Single(button => ReferenceEquals(button.Command, context.RefreshUsageCommand));
            var install = buttons.Single(button => ReferenceEquals(button.Command, context.InstallCliCommand));
            var update = buttons.Single(button => ReferenceEquals(button.Command, context.UpdateCliCommand));
            var checkUpdate = buttons.Single(button => ReferenceEquals(button.Command, context.CheckCliCommand));
            var actions = new[] { refresh, login, checkUpdate, update, install };
            var hint = view.FindName("AuthRecoveryHint") as TextBlock
                ?? throw new InvalidOperationException($"{name}: 認証後の案内TextBlockが見つかりません。");

            AssertAuthenticationState(item, login, hint, expected: false, $"{name}: 正常時");
            Check.Equal(Visibility.Visible, refresh.Visibility, $"{name}: 利用状況の再取得ボタンが必要です");
            foreach (var (button, label) in new[]
            {
                (refresh, "利用量再取得"), (login, "認証"), (checkUpdate, "CLI更新確認"),
                (update, "CLI更新"), (install, "インストール")
            })
            {
                Check.Equal(label, button.Content as string, $"{name}: ボタンの短いラベルが一致しません");
                Check.True(ReferenceEquals(item, button.CommandParameter), $"{name}: {label}の対象が選択中のAIではありません");
            }
            AssertActionButtonLayout(actions, name);
            AssertButtonsFitParent(buttons, name);

            Check.Equal(Visibility.Visible, update.Visibility, $"{name}: 更新検出時はCLI更新を表示してください");
            Check.Equal(Visibility.Collapsed, checkUpdate.Visibility, $"{name}: 更新検出時はCLI更新確認を隠してください");

            item.CliInfo.HasUpdate = false;
            Arrange(view);
            Check.Equal(Visibility.Collapsed, update.Visibility, $"{name}: 最新時はCLI更新を隠してください");
            Check.Equal(Visibility.Visible, checkUpdate.Visibility, $"{name}: 最新時はCLI更新確認を表示してください");
            AssertActionButtonLayout(actions, name);
            if (serviceType == AiServiceType.Grok)
            {
                ((ScrollViewer)view.FindName("DetailScrollViewer")).ScrollToVerticalOffset(45);
                Arrange(view);
                SaveImage(view, "detail-cli-current.png");
            }

            item.CliInfo.HasUpdate = true;
            Arrange(view);
            Check.Equal(Visibility.Visible, update.Visibility, $"{name}: 再検出時はCLI更新を再表示してください");
            Check.Equal(Visibility.Collapsed, checkUpdate.Visibility, $"{name}: 再検出時はCLI更新確認を再度隠してください");

            if (serviceType == AiServiceType.Grok)
            {
                var scrollViewer = (ScrollViewer)view.FindName("DetailScrollViewer");
                scrollViewer.ScrollToVerticalOffset(45);
                Arrange(view);
                SaveImage(view, "detail-auth-normal.png");
            }

            // DataContextを差し替えず、プロパティ変更の通知だけで認証ボタンと案内が切り替わることを検証。
            item.CliInfo.IsLoggedIn = false;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: true, $"{name}: 未ログインのみ");

            item.CliInfo.HasUsageError = true;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: true, $"{name}: 未ログインと取得エラー");

            item.CliInfo.IsLoggedIn = true;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: true, $"{name}: 取得エラーのみ");
            Check.True(login.IsEnabled && refresh.IsEnabled, $"{name}: 待機中は認証と再取得を操作できる必要があります");

            if (serviceType == AiServiceType.Grok)
            {
                item.CliInfo.StatusMessage = "認証情報を確認してください。";
                item.PrimaryLimit.CustomDisplayPercentText = "--";
                context.ConsoleLogs.Clear();
                context.ConsoleLogs.Add(new("[Grok] 認証後に再取得してください。", true));
                Arrange(view);
                SaveImage(view, "detail-auth-error.png");
            }

            item.CliInfo.IsBusy = true;
            Arrange(view);
            Check.True(actions.All(button => !button.IsEnabled), $"{name}: 処理中のCLI操作は無効になる必要があります");
            AssertAuthenticationState(item, login, hint, expected: true, $"{name}: 認証対応中");

            item.CliInfo.IsBusy = false;
            item.CliInfo.HasUsageError = false;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: false, $"{name}: ログインと利用枠取得の両方が回復");
            Check.True(actions.All(button => button.IsEnabled), $"{name}: 処理完了後もCLI操作が無効です");

            item.CliInfo.HasUsageError = true;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: true, $"{name}: 取得エラーが再発");

            item.CliInfo.IsInstalled = false;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: false, $"{name}: 未導入と取得エラー");
            Check.Equal(Visibility.Visible, install.Visibility, $"{name}: 未導入時にインストールボタンが必要です");

            item.CliInfo.IsLoggedIn = false;
            item.CliInfo.HasUsageError = false;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: false, $"{name}: 未導入と未ログイン");
            AssertActionButtonLayout(actions, name);

            item.CliInfo.IsInstalled = true;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: true, $"{name}: 導入後も未ログイン");
            Check.Equal(Visibility.Collapsed, install.Visibility, $"{name}: 導入後にインストールボタンが残っています");

            item.CliInfo.IsLoggedIn = true;
            Arrange(view);
            AssertAuthenticationState(item, login, hint, expected: false, $"{name}: 再ログイン後の正常状態");
            AssertButtonsFitParent(buttons, name);
            Console.WriteLine($"成功: 詳細画面: {name}の認証条件、短いラベル、共通ボタン寸法");
        }

        // 同じ STA と安全に読み込んだ共有 Resources をカード操作の検証でも使う。
        AssertAntigravityCardAndDetailModes(view);
        CardDragDropTests.RunOnStaThread();
        CliUpdateNotificationTests.RunOnStaThread();
        ResetTimeTests.RunOnStaThread();
        Check.True(Application.Current?.Windows.Count == 0,
            "カード操作の検証で実アプリのウィンドウを起動しないでください");
    }

    private static void AssertAntigravityCardAndDetailModes(DetailPopupView detailView)
    {
        var item = new AiUsageItem
        {
            ServiceType = AiServiceType.Gemini,
            DisplayName = "Antigravity",
            SubTitle = "Google DeepMind",
            UsageSourceText = "取得元：IDE",
            IsDataLoaded = true,
            CliInfo = new CliInfo
            {
                Name = "Gemini (agy)",
                CommandName = "agy",
                IsInstalled = true,
                IsLoggedIn = true,
                IsSubscribed = true,
                InstalledVersion = "1.0.0",
                UsageCheckCommand = "agy -p \"/usage\"",
                StatusMessage = "Google DeepMind 連携稼働中"
            },
            ArgonLimit = new UsageLimitInfo
            {
                Title = "Gemini 4 Argon",
                LimitDescription = "Gemini 4 Argon Weekly Limit",
                RemainingPercent = 93,
                ResetTimeText = "10/26 22:29 リセット"
            },
            PrimaryLimit = new UsageLimitInfo
            {
                Title = "5時間制限",
                LimitDescription = "Five Hour Limit Remaining",
                RemainingPercent = 100,
                ResetTimeText = "03:52 リセット"
            },
            SecondaryLimit = new UsageLimitInfo
            {
                Title = "週次制限",
                LimitDescription = "Weekly Limit Remaining",
                RemainingPercent = 100,
                ResetTimeText = "10/26 22:52 リセット"
            },
            ExternalFiveHourLimit = new UsageLimitInfo
            {
                Title = "5時間制限 外部",
                LimitDescription = "Claude / GPT models 5h limit",
                RemainingPercent = 100,
                ResetTimeText = "03:52 リセット"
            },
            ExternalWeeklyLimit = new UsageLimitInfo
            {
                Title = "週次制限 外部",
                LimitDescription = "Claude / GPT models Weekly limit",
                RemainingPercent = 100,
                ResetTimeText = "10/26 22:52 リセット"
            },
            HasExternalLimits = true
        };
        item.AllLimits.Add(item.ArgonLimit!);
        item.AllLimits.Add(item.PrimaryLimit);
        item.AllLimits.Add(item.SecondaryLimit!);
        item.AllLimits.Add(item.ExternalFiveHourLimit!);
        item.AllLimits.Add(item.ExternalWeeklyLimit!);

        item.LimitGroups.Add(new UsageLimitGroup
        {
            GroupTitle = "Gemini 4 Argon",
            Limits = new ObservableCollection<UsageLimitInfo> { item.ArgonLimit! }
        });
        item.LimitGroups.Add(new UsageLimitGroup
        {
            GroupTitle = "Gemini Model",
            Limits = new ObservableCollection<UsageLimitInfo> { item.PrimaryLimit, item.SecondaryLimit! }
        });
        item.LimitGroups.Add(new UsageLimitGroup
        {
            GroupTitle = "Claude & GPT Model",
            Limits = new ObservableCollection<UsageLimitInfo> { item.ExternalFiveHourLimit!, item.ExternalWeeklyLimit! }
        });

        var card = new UsageCardView { DataContext = item, Width = 154 };
        void ArrangeCard()
        {
            var size = new Size(154, 136);
            card.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            card.Measure(size);
            card.Arrange(new Rect(size));
            card.UpdateLayout();
            card.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            card.UpdateLayout();
        }

        ArrangeCard();
        var switchButton = card.FindName("CardModeSwitchButton") as Button
            ?? throw new InvalidOperationException("Antigravity: カード切替ボタンが見つかりません。");
        Check.Equal(Visibility.Visible, switchButton.Visibility, "Antigravity: 切替ボタンが非表示です");
        var pillText = Descendants<TextBlock>(switchButton).Single();

        static bool IsShownInCard(DependencyObject element, DependencyObject root)
        {
            for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
                if (ReferenceEquals(current, root)) return true;
            }
            return false;
        }

        // 1. デフォルト: Gemini 4 Argon (単一枠表示)
        Check.Equal("Argon ⇄", pillText.Text, "Antigravity: デフォルトのピル表示が Argon ⇄ ではありません");
        Check.True(Descendants<TextBlock>(card).Any(t => t.Text == "Gemini 4 Argon" && IsShownInCard(t, card)),
            "Antigravity: デフォルトでカードに Gemini 4 Argon が単一枠表示されていません");
        Check.True(!Descendants<TextBlock>(card).Any(t => t.Text == "週次制限" && IsShownInCard(t, card)),
            "Antigravity: Argon単一枠表示時に2段目の週次制限が表示されています");
        SaveImage(card, "card-antigravity-1-argon.png");

        // 2. 1回クリック: Gemini (5時間 + 週次)
        switchButton.Command.Execute(null);
        ArrangeCard();
        Check.Equal("Gemini ⇄", pillText.Text, "Antigravity: 2番目のピル表示が Gemini ⇄ ではありません");
        Check.True(Descendants<TextBlock>(card).Any(t => t.Text == "5時間制限" && IsShownInCard(t, card)),
            "Antigravity: Geminiモードで5時間制限が表示されていません");
        Check.True(Descendants<TextBlock>(card).Any(t => t.Text == "週次制限" && IsShownInCard(t, card)),
            "Antigravity: Geminiモードで週次制限が表示されていません");
        SaveImage(card, "card-antigravity-2-gemini.png");

        // 3. 2回クリック: Claude & GPT (5時間 外部 + 週次 外部)
        switchButton.Command.Execute(null);
        ArrangeCard();
        Check.Equal("外部 ⇄", pillText.Text, "Antigravity: 3番目のピル表示が 外部 ⇄ ではありません");
        Check.True(Descendants<TextBlock>(card).Any(t => t.Text == "5時間制限 外部" && IsShownInCard(t, card)),
            "Antigravity: 外部モデルモードで5時間制限 外部が表示されていません");
        Check.True(Descendants<TextBlock>(card).Any(t => t.Text == "週次制限 外部" && IsShownInCard(t, card)),
            "Antigravity: 外部モデルモードで週次制限 外部が表示されていません");
        SaveImage(card, "card-antigravity-3-external.png");

        // 4. 3回クリック: Gemini 4 Argon に戻る
        switchButton.Command.Execute(null);
        ArrangeCard();
        Check.Equal("Argon ⇄", pillText.Text, "Antigravity: 3回切替後に Argon ⇄ へ戻っていません");
        Check.True(Descendants<TextBlock>(card).Any(t => t.Text == "Gemini 4 Argon" && IsShownInCard(t, card)),
            "Antigravity: 3回切替後に Gemini 4 Argon の単一枠表示へ戻っていません");

        // 5. 詳細表示で3グループ枠(Gemini 4 Argon / Gemini Model / Claude & GPT Model)と全5枠が表示されること
        detailView.DataContext = new DetailTestContext(item);
        var scrollViewer = (ScrollViewer)detailView.FindName("DetailScrollViewer");
        scrollViewer.ScrollToTop();
        Arrange(detailView);
        var groupedControl = detailView.FindName("GroupedLimitsItemsControl") as ItemsControl
            ?? throw new InvalidOperationException("GroupedLimitsItemsControl が見つかりません。");
        var ungroupedControl = detailView.FindName("AllLimitsItemsControl") as ItemsControl
            ?? throw new InvalidOperationException("AllLimitsItemsControl が見つかりません。");
        Check.Equal(Visibility.Visible, groupedControl.Visibility, "Antigravity詳細画面: グループ枠表示が非表示です");
        Check.Equal(Visibility.Collapsed, ungroupedControl.Visibility, "Antigravity詳細画面: フラット表示が隠れていません");

        foreach (var expectedGroupTitle in new[] { "Gemini 4 Argon", "Gemini Model", "Claude & GPT Model" })
        {
            Check.True(Descendants<TextBlock>(groupedControl).Any(t => t.Text == expectedGroupTitle && IsShownInCard(t, detailView)),
                $"Antigravity詳細画面: グループ枠 {expectedGroupTitle} が表示されていません");
        }
        foreach (var expectedTitle in new[] { "Gemini 4 Argon", "5時間制限", "週次制限", "5時間制限 外部", "週次制限 外部" })
        {
            Check.True(Descendants<TextBlock>(groupedControl).Any(t => t.Text == expectedTitle && IsShownInCard(t, detailView)),
                $"Antigravity詳細画面: {expectedTitle} がグループ枠内に表示されていません");
        }
        SaveImage(detailView, "detail-antigravity-argon.png");
        Console.WriteLine("成功: Antigravity: Gemini 4 Argon / Gemini Model / Claude & GPT Model のカード3段階切替と詳細3グループ枠表示");
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "App.xaml"));
        var root = source.Root ?? throw new InvalidOperationException("App.xaml の root がありません。");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = root.Element(presentation + "Application.Resources")
            ?? throw new InvalidOperationException("App.xaml の Resources がありません。");
        var dictionary = new XElement(presentation + "ResourceDictionary");
        foreach (var declaration in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            var value = declaration.Value;
            if (value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                && !value.Contains(";assembly=", StringComparison.Ordinal))
                value += $";assembly={typeof(DetailPopupView).Assembly.GetName().Name}";
            dictionary.Add(new XAttribute(declaration.Name, value));
        }
        foreach (var resource in resources.Elements()) dictionary.Add(new XElement(resource));
        using var reader = dictionary.CreateReader();
        return (ResourceDictionary)XamlReader.Load(reader);
    }

    private static AiUsageItem CreateItem(AiServiceType serviceType, string name, string commandName)
    {
        var item = new AiUsageItem
        {
            ServiceType = serviceType,
            DisplayName = name,
            SubTitle = "詳細画面の操作確認",
            CliInfo = new CliInfo
            {
                Name = name,
                CommandName = commandName,
                IsInstalled = true,
                IsLoggedIn = true,
                IsBusy = false,
                HasUsageError = false,
                HasUpdate = true,
                InstalledVersion = "1.0.0",
                UsageCheckCommand = commandName == "agy" ? "agy -p \"/usage\"" : "/usage",
                StatusMessage = "連携済み。利用枠を取得しました。"
            },
            PrimaryLimit = new UsageLimitInfo
            {
                Title = "週次制限",
                LimitDescription = "29% 使用済み (残 71%)",
                RemainingPercent = 71
            }
        };
        item.AllLimits.Add(item.PrimaryLimit);
        return item;
    }

    private static void Arrange(FrameworkElement view)
    {
        // 実際の詳細ポップアップ幅。表示せず Measure/Arrange と binding 更新だけ行う。
        var size = new Size(468, 310);
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        view.UpdateLayout();
        Check.True(Application.Current?.Windows.Count == 0,
            "詳細画面の検証で実アプリのウィンドウを起動しないでください");
    }

    private static void AssertButtonsFitParent(IEnumerable<Button> buttons, string name)
    {
        foreach (var button in buttons.Where(button => button.Visibility == Visibility.Visible))
        {
            if (VisualTreeHelper.GetParent(button) is not FrameworkElement parent) continue;
            Check.True(button.ActualWidth > 0, $"{name}: ボタンを配置できませんでした");
            Check.True(button.ActualWidth <= parent.ActualWidth + 0.5,
                $"{name}: ボタン幅 {button.ActualWidth:F1} が親の幅 {parent.ActualWidth:F1} を超えています");
        }
    }

    private static void AssertUsageSourceControls(DetailPopupView view, AiUsageItem item, string commandName, string name)
    {
        var source = view.FindName("UsageSourceTextBlock") as TextBlock
            ?? throw new InvalidOperationException($"{name}: 取得元のTextBlockが見つかりません。");
        Check.Equal(Visibility.Collapsed, source.Visibility, $"{name}: 取得元がない場合は表示しないでください");
        if (commandName != "agy") return;

        var scrollViewer = (ScrollViewer)view.FindName("DetailScrollViewer");
        scrollViewer.ScrollToTop();

        // 同じ詳細画面のまま、取得元変更の通知でCLIとIDEの表示が切り替わることを検証。
        item.UsageSourceText = "取得元：CLI";
        Arrange(view);
        Check.Equal(Visibility.Visible, source.Visibility, "Antigravity: CLI取得元が表示されません");
        Check.Equal(item.UsageSourceText, source.Text, "Antigravity: CLI取得元の表示が一致しません");

        item.UsageSourceText = "取得元：IDE（CLI取得失敗時の補助）";
        Arrange(view);
        Check.Equal(Visibility.Visible, source.Visibility, "Antigravity: IDE取得元が表示されません");
        Check.Equal(item.UsageSourceText, source.Text, "Antigravity: IDE取得元への切り替えが反映されません");
        Check.Equal(TextWrapping.Wrap, source.TextWrapping, "Antigravity: 長い取得元は折り返してください");
        var ideSourceHeight = source.ActualHeight;
        Check.True(ideSourceHeight > 0, "Antigravity: IDE取得元を配置できませんでした");
        SaveImage(view, "detail-antigravity-ide.png");

        // 失敗時の前回取得元に説明が加わっても、カード内で折り返して表示する。
        item.CliInfo.HasUsageError = true;
        item.UsageSourceText = "取得失敗（前回の取得元：IDE）";
        Arrange(view);
        Check.Equal(Visibility.Visible, source.Visibility, "Antigravity: 失敗時の前回取得元が表示されません");
        Check.Equal(item.UsageSourceText, source.Text, "Antigravity: 失敗時の前回取得元が反映されません");
        SaveImage(view, "detail-antigravity-ide-stale.png");

        item.UsageSourceText = "取得失敗（前回の取得元：IDE）。最後に成功した取得元はIDEです。CLIとIDEの状態を確認してから、利用量を再取得してください。";
        Arrange(view);
        Check.True(source.ActualHeight > ideSourceHeight, "Antigravity: 長い前回取得元が折り返されていません");
        var parent = VisualTreeHelper.GetParent(source) as FrameworkElement
            ?? throw new InvalidOperationException("Antigravity: 取得元の親要素がありません。");
        Check.True(source.ActualWidth <= parent.ActualWidth + 0.5,
            "Antigravity: 取得元の表示が利用状況カードの幅を超えています");
        item.UsageSourceText = "";
        item.CliInfo.HasUsageError = false;
        Arrange(view);
        Check.Equal(Visibility.Collapsed, source.Visibility, "Antigravity: 取得元を消した後も表示が残っています");
    }

    private static void AssertAuthenticationState(AiUsageItem item, Button login, TextBlock hint, bool expected, string scenario)
    {
        Check.Equal(expected, item.CliInfo.CanAuthenticate, $"{scenario}: 認証操作の可否が正しくありません");
        var expectedVisibility = expected ? Visibility.Visible : Visibility.Collapsed;
        Check.Equal(expectedVisibility, login.Visibility, $"{scenario}: 認証ボタンの表示条件が正しくありません");
        Check.Equal(expectedVisibility, hint.Visibility, $"{scenario}: 認証後の案内は認証ボタンと同時に表示してください");
    }

    private static void AssertActionButtonLayout(IReadOnlyList<Button> buttons, string name)
    {
        var reference = buttons[0];
        foreach (var button in buttons)
        {
            var label = button.Content as string;
            Check.Equal(76d, button.Width, $"{name}: {label}のボタン幅が共通ではありません");
            Check.Equal(16d, button.Height, $"{name}: {label}のボタン高さが共通ではありません");
            Check.Equal(reference.Margin, button.Margin, $"{name}: {label}のボタン余白が共通ではありません");
            Check.Equal(reference.Padding, button.Padding, $"{name}: {label}のボタン内側余白が共通ではありません");
            Check.Equal(reference.FontSize, button.FontSize, $"{name}: {label}の文字サイズが共通ではありません");
            Check.Equal(reference.FontWeight, button.FontWeight, $"{name}: {label}の文字の太さが共通ではありません");
            Check.True(ReferenceEquals(reference.Style, button.Style), $"{name}: {label}のボタンスタイルが共通ではありません");
            if (button.Visibility == Visibility.Visible)
            {
                Check.Equal(76d, button.ActualWidth, $"{name}: {label}のボタンを共通幅で配置できません");
                Check.Equal(16d, button.ActualHeight, $"{name}: {label}のボタンを共通高さで配置できません");
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SaveImage(FrameworkElement view, string filename)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),
            (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(AppContext.BaseDirectory, filename);
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"詳細画面の描画確認: {path}");
    }

    private sealed class DetailTestContext : ViewModelBase
    {
        public DetailTestContext(AiUsageItem selectedItem)
        {
            SelectedItem = selectedItem;
            ConsoleLogs.Add(new($"[{selectedItem.DisplayName}] 利用枠の取得が完了しました。", false));
            ConsoleLogs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VisibleErrorCount));
        }

        public AiUsageItem SelectedItem { get; }
        public ObservableCollection<DetailTestLog> ConsoleLogs { get; } = [];
        public int VisibleErrorCount => ConsoleLogs.Count(log => log.IsError);
        public bool IsShowAllLogs => false;
        public ICommand LoginCliCommand { get; } = new RelayCommand(() => { });
        public ICommand RefreshUsageCommand { get; } = new RelayCommand(() => { });
        public ICommand InstallCliCommand { get; } = new RelayCommand(() => { });
        public ICommand UpdateCliCommand { get; } = new RelayCommand(() => { });
        public ICommand CheckCliCommand { get; } = new RelayCommand(() => { });
        public ICommand CloseDetailCommand { get; } = new RelayCommand(() => { });
        public ICommand ShowFilteredLogsCommand { get; } = new RelayCommand(() => { });
        public ICommand ShowAllLogsCommand { get; } = new RelayCommand(() => { });
        public ICommand ClearLogsCommand { get; } = new RelayCommand(() => { });
    }

    private sealed record DetailTestLog(string Message, bool IsError);
}
