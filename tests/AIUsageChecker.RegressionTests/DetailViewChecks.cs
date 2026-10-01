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
        CardDragDropTests.RunOnStaThread();
        Check.True(Application.Current?.Windows.Count == 0,
            "カード操作の検証で実アプリのウィンドウを起動しないでください");
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
                UsageCheckCommand = "/usage",
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
