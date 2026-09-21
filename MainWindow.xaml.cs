using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorInfoPrimary = 1;
    private const int MonitorDpiEffective = 0;

    private readonly MainViewModel _viewModel;
    private double _normalLeft = double.NaN;
    private double _normalTop = double.NaN;
    private bool _suppressMonitorRemember;
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
        PlaceAtBottomRightOfRememberedMonitor();

        // 最前面の初期適用
        ApplyTopmost(_viewModel.IsAlwaysOnTop);

        // ViewModelプロパティ変更の監視
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        LocationChanged += (_, _) => RememberCurrentMonitor();
        Closing += (_, _) => RememberCurrentMonitor();
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
        _suppressMonitorRemember = true;
        try
        {
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

                if (!double.IsNaN(_normalLeft) && !double.IsNaN(_normalTop))
                {
                    this.Left = _normalLeft;
                    this.Top = _normalTop;
                }
                else
                {
                    this.Left = Math.Max(workArea.Left, workArea.Right - 498 - 24);
                    this.Top = Math.Max(workArea.Top, workArea.Bottom - 334 - 24);
                }
            }
        }
        finally
        {
            _suppressMonitorRemember = false;
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

    private void PlaceAtBottomRightOfRememberedMonitor()
    {
        var monitors = GetMonitors();
        var monitor = ResolveMonitor(monitors, _viewModel.Settings.MonitorDeviceName);
        bool isPrimary = monitor.Handle == IntPtr.Zero || (monitor.Flags & MonitorInfoPrimary) != 0;
        if (isPrimary)
        {
            PlaceAtBottomRight(SystemParameters.WorkArea);
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !TryPlaceOnMonitor(hwnd, monitor))
        {
            PlaceAtBottomRight(SystemParameters.WorkArea);
        }
    }

    private void PlaceAtBottomRight(Rect workArea)
    {
        double width = Width > 0 ? Width : 498;
        double height = Height > 0 ? Height : 334;
        _suppressMonitorRemember = true;
        try
        {
            _normalLeft = Math.Max(workArea.Left, workArea.Right - width - 24);
            _normalTop = Math.Max(workArea.Top, workArea.Bottom - height - 24);
            Left = _normalLeft;
            Top = _normalTop;
        }
        finally
        {
            _suppressMonitorRemember = false;
        }
    }

    private bool TryPlaceOnMonitor(IntPtr hwnd, MonitorDesc monitor)
    {
        uint dpiX = 96;
        uint dpiY = 96;
        if (GetDpiForMonitor(monitor.Handle, MonitorDpiEffective, out uint dx, out uint dy) == 0 && dx >= 96 && dy >= 96)
        {
            dpiX = dx;
            dpiY = dy;
        }

        double width = Width > 0 ? Width : 498;
        double height = Height > 0 ? Height : 334;
        int widthPx = Math.Max(1, (int)Math.Round(width * dpiX / 96.0));
        int heightPx = Math.Max(1, (int)Math.Round(height * dpiY / 96.0));
        int marginX = (int)Math.Round(24 * dpiX / 96.0);
        int marginY = (int)Math.Round(24 * dpiY / 96.0);
        int x = monitor.Work.Right - widthPx - marginX;
        int y = monitor.Work.Bottom - heightPx - marginY;
        if (x < monitor.Work.Left) x = monitor.Work.Left;
        if (y < monitor.Work.Top) y = monitor.Work.Top;

        _suppressMonitorRemember = true;
        try
        {
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
            {
                return false;
            }

            var current = GetCurrentMonitorDeviceName();
            if (!string.Equals(current, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            CaptureNormalPosition();
            Dispatcher.BeginInvoke(CaptureNormalPosition, DispatcherPriority.ApplicationIdle);
            return true;
        }
        finally
        {
            _suppressMonitorRemember = false;
        }
    }

    private void CaptureNormalPosition()
    {
        if (_viewModel.IsDocked || WindowState != WindowState.Normal) return;
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        _normalLeft = Left;
        _normalTop = Top;
    }

    private void RememberCurrentMonitor()
    {
        // サイド表示はプライマリ右端へ移るため、ここで覚えると通常表示のモニタを上書きする。
        if (_suppressMonitorRemember || _viewModel.IsDocked || WindowState != WindowState.Normal) return;

        var deviceName = GetCurrentMonitorDeviceName();
        if (string.IsNullOrWhiteSpace(deviceName)) return;

        _viewModel.SaveMonitorDeviceName(deviceName);
    }

    private string? GetCurrentMonitorDeviceName()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return null;

        var handle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (handle == IntPtr.Zero || !TryGetMonitorInfo(handle, out var info)) return null;

        return string.IsNullOrWhiteSpace(info.szDevice) ? null : info.szDevice;
    }

    private static MonitorDesc ResolveMonitor(List<MonitorDesc> monitors, string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            foreach (var monitor in monitors)
            {
                if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return monitor;
                }
            }
        }

        foreach (var monitor in monitors)
        {
            if ((monitor.Flags & MonitorInfoPrimary) != 0)
            {
                return monitor;
            }
        }

        return monitors.Count > 0 ? monitors[0] : default;
    }

    private static List<MonitorDesc> GetMonitors()
    {
        var monitors = new List<MonitorDesc>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data) =>
        {
            if (TryGetMonitorInfo(hMonitor, out var info))
            {
                monitors.Add(new MonitorDesc(hMonitor, info.szDevice ?? "", info.rcWork, info.dwFlags));
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return monitors;
    }

    private static bool TryGetMonitorInfo(IntPtr hMonitor, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(hMonitor, ref info);
    }

    private readonly record struct MonitorDesc(IntPtr Handle, string DeviceName, RECT Work, uint Flags);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}