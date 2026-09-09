using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageChecker.Models;
using AIUsageChecker.ViewModels;

namespace AIUsageChecker;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly MainViewModel _viewModel;
    private double _normalLeft;
    private double _normalTop;
    private Point _dockDragStartScreen;
    private double _dockDragStartTop;
    private bool _isDockDragging;
    private bool _hasDockMoved;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        _normalLeft = Math.Max(0, workArea.Right - Width - 24);
        _normalTop = Math.Max(0, workArea.Bottom - Height - 24);
        Left = _normalLeft;
        Top = _normalTop;

        // 最前面の初期適用
        ApplyTopmost(_viewModel.IsAlwaysOnTop);

        // ViewModelプロパティ変更の監視
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.StartsWith("--render")))
        {
            _viewModel.IsDocked = false;
        }

        if (args.Contains("--render-test"))
        {
            for (int i = 0; i < 30 && _viewModel.IsRefreshing; i++)
            {
                await Task.Delay(200);
            }
            await Task.Delay(500);
            SaveScreenshot("app_rendered.png");
            Application.Current.Shutdown();
        }
        else if (args.Contains("--render-docked"))
        {
            _viewModel.IsDocked = true;
            UpdateDockState(true);
            await Task.Delay(500);
            SaveScreenshot("app_docked.png");
            Application.Current.Shutdown();
        }
        else if (args.Contains("--render-detail-gpt"))
        {
            await Task.Delay(500);
            for (int i = 0; i < 30 && _viewModel.IsRefreshing; i++)
            {
                await Task.Delay(200);
            }
            await Task.Delay(1000);
            var gptItem = _viewModel.Items.FirstOrDefault(x => x.ServiceType == AiServiceType.GPT);
            if (gptItem != null)
            {
                _viewModel.SelectedItem = gptItem;
                _viewModel.IsDetailOpen = true;
            }
            await Task.Delay(500);
            SaveScreenshot("app_gpt_detail.png");

            // スクロール最下部のキャプチャも取得
            var scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(this);
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToBottom();
                await Task.Delay(300);
                SaveScreenshot("app_gpt_detail_scrolled.png");
            }

            Application.Current.Shutdown();
        }
        else if (args.Contains("--render-detail-copilot"))
        {
            await Task.Delay(500);
            for (int i = 0; i < 30 && _viewModel.IsRefreshing; i++)
            {
                await Task.Delay(200);
            }
            await Task.Delay(1000);
            var copilotItem = _viewModel.Items.FirstOrDefault(x => x.ServiceType == AiServiceType.Copilot);
            if (copilotItem != null)
            {
                _viewModel.SelectedItem = copilotItem;
                _viewModel.IsDetailOpen = true;
            }
            await Task.Delay(500);
            SaveScreenshot("app_copilot_detail.png");
            Application.Current.Shutdown();
        }
        else if (args.Contains("--render-glow-test"))
        {
            await Task.Delay(500);
            for (int i = 0; i < 30 && _viewModel.IsRefreshing; i++)
            {
                await Task.Delay(200);
            }
            await Task.Delay(500);

            // テスト状態の設定: GPTを週次枯渇（赤グロー）、Claudeを回復ホタル点滅（緑グロー）に設定
            var gptItem = _viewModel.Items.FirstOrDefault(x => x.ServiceType == AiServiceType.GPT);
            if (gptItem != null && gptItem.SecondaryLimit != null)
            {
                gptItem.SecondaryLimit.RemainingPercent = 0;
                gptItem.IsWeeklyExhausted = true;
            }

            var claudeItem = _viewModel.Items.FirstOrDefault(x => x.ServiceType == AiServiceType.Claude);
            if (claudeItem != null)
            {
                claudeItem.IsRecoveredGlowActive = true;
            }

            await Task.Delay(800);
            SaveScreenshot("app_glow_test.png");
            SaveScreenshot("app_rendered.png");
            Application.Current.Shutdown();
        }
        else if (args.Contains("--render-claude-subscribed"))
        {
            await Task.Delay(500);
            for (int i = 0; i < 30 && _viewModel.IsRefreshing; i++)
            {
                await Task.Delay(200);
            }
            await Task.Delay(500);

            var claudeItem = _viewModel.Items.FirstOrDefault(x => x.ServiceType == AiServiceType.Claude);
            if (claudeItem != null)
            {
                claudeItem.CliInfo.IsSubscribed = true;
                claudeItem.CliInfo.StatusMessage = "プラン: Claude Pro";
                claudeItem.PrimaryLimit.Title = "5時間制限";
                claudeItem.PrimaryLimit.LimitDescription = "5-hour session limit";
                claudeItem.PrimaryLimit.RemainingPercent = 88.0;
                claudeItem.PrimaryLimit.ResetTimeText = $"{DateTime.Now.AddHours(3).AddMinutes(45):HH:mm} リセット";
                claudeItem.PrimaryLimit.CustomDisplayPercentText = null;

                if (claudeItem.SecondaryLimit == null) claudeItem.SecondaryLimit = new UsageLimitInfo();
                claudeItem.SecondaryLimit.Title = "週次制限";
                claudeItem.SecondaryLimit.LimitDescription = "Weekly limit";
                claudeItem.SecondaryLimit.RemainingPercent = 70.0;
                claudeItem.SecondaryLimit.ResetTimeText = "09/14 18:00 リセット";
                claudeItem.SecondaryLimit.CustomDisplayPercentText = null;
            }

            await Task.Delay(500);
            SaveScreenshot("app_claude_subscribed_test.png");
            Application.Current.Shutdown();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAlwaysOnTop))
        {
            ApplyTopmost(_viewModel.IsAlwaysOnTop);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsDocked))
        {
            UpdateDockState(_viewModel.IsDocked);
        }
    }

    public void ApplyTopmost(bool isTopmost)
    {
        this.Topmost = isTopmost;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, isTopmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private void UpdateDockState(bool isDocked)
    {
        var workArea = SystemParameters.WorkArea;
        if (isDocked)
        {
            _normalLeft = this.Left;
            _normalTop = this.Top;

            this.Width = 42;
            this.Height = 140;
            this.Left = workArea.Right - 42;

            double savedTop = _viewModel.Settings.DockedTop;
            if (savedTop > 0 && savedTop <= workArea.Bottom - 140)
            {
                this.Top = savedTop;
            }
            else
            {
                this.Top = Math.Max(workArea.Top, workArea.Height / 2 - 70);
            }
        }
        else
        {
            this.Width = 498;
            this.Height = 334;

            double restoreLeft = _normalLeft > 0 ? _normalLeft : (workArea.Right - 498 - 24);
            double restoreTop = _normalTop > 0 ? _normalTop : (workArea.Bottom - 334 - 24);

            this.Left = Math.Clamp(restoreLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - 498));
            this.Top = Math.Clamp(restoreTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 334));
        }
    }

    private void DockedBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dockDragStartScreen = PointToScreen(e.GetPosition(this));
        _dockDragStartTop = this.Top;
        _isDockDragging = true;
        _hasDockMoved = false;
        DockedWidgetBorder.CaptureMouse();
        e.Handled = true;
    }

    private void DockedBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDockDragging)
        {
            var currentScreen = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this);
            double deltaDips = (currentScreen.Y - _dockDragStartScreen.Y) / dpi.DpiScaleY;

            if (Math.Abs(deltaDips) > 2)
            {
                _hasDockMoved = true;
                var workArea = SystemParameters.WorkArea;
                double targetTop = _dockDragStartTop + deltaDips;
                this.Top = Math.Clamp(targetTop, workArea.Top, workArea.Bottom - this.Height);
            }
        }
    }

    private void DockedBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDockDragging)
        {
            _isDockDragging = false;
            DockedWidgetBorder.ReleaseMouseCapture();

            if (!_hasDockMoved)
            {
                _viewModel.IsDocked = false;
            }
            else
            {
                _viewModel.SaveDockedTop(this.Top);
            }
            e.Handled = true;
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTopmost(_viewModel.IsAlwaysOnTop);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void SaveScreenshot(string fileName)
    {
        try
        {
            UpdateLayout();
            int width = (int)Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width);
            int height = (int)Math.Ceiling(ActualHeight > 0 ? ActualHeight : Height);

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            var outDir = @"C:\Users\kamep\.gemini\antigravity-ide\brain\5a0ad8a9-1118-4f88-b4ca-d5e0f8f491ce";
            var outPath = Path.Combine(outDir, fileName);
            using (var fs = new FileStream(outPath, FileMode.Create))
            {
                encoder.Save(fs);
            }
            File.WriteAllText(@"C:\git_home\ai-usage-checker\render.log", $"Success: {outPath}, width={width}, height={height}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(@"C:\git_home\ai-usage-checker\render.log", $"Failed: {ex}");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AiUsageItem item)
        {
            item.DismissGlow();
            _viewModel.SelectedItem = item;
            _viewModel.IsDetailOpen = true;
        }
    }

    private void Backdrop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsDetailOpen = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}