using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
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
            _viewModel.OpenDetail(item);
        }
    }

    private void Backdrop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.CloseDetail();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}