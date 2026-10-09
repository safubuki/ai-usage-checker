using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AIUsageChecker.ViewModels;

namespace AIUsageChecker.Views;

public partial class CliUpdateNoticeView : UserControl
{
    private Window? _ownerWindow;

    public CliUpdateNoticeView()
    {
        InitializeComponent();
    }

    private void Notice_Loaded(object sender, RoutedEventArgs e)
    {
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow != null) _ownerWindow.StateChanged += OwnerWindow_StateChanged;
        AcknowledgeAfterRender();
    }

    private void Notice_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow != null) _ownerWindow.StateChanged -= OwnerWindow_StateChanged;
        _ownerWindow = null;
    }

    private void OwnerWindow_StateChanged(object? sender, EventArgs e) => AcknowledgeAfterRender();
    private void Notice_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => AcknowledgeAfterRender();
    private void Notice_TargetUpdated(object sender, DataTransferEventArgs e) => AcknowledgeAfterRender();

    private void AcknowledgeAfterRender()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var window = Window.GetWindow(this);
            // 読み込み中・詳細表示中・ドック表示中・最小化中は案内済みにしない。
            if (IsVisible && window is { IsVisible: true } && window.WindowState != WindowState.Minimized &&
                DataContext is CliUpdateNotificationViewModel notification)
                notification.MarkDisplayed();
        }));
    }
}
