using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageChecker.Controls;
using AIUsageChecker.Models;
using AIUsageChecker.Views;

namespace AIUsageChecker.RegressionTests;

internal static class CardDragDropTests
{
    public static IEnumerable<TestCase> GetCases()
    {
        yield return new("カードドラッグ: 移動閾値と縦横方向", () =>
        {
            CheckDragThreshold();
            return Task.CompletedTask;
        });
        yield return new("カードドラッグ: 前方・後方・同位置の移動先", () =>
        {
            CheckMoveIndices();
            return Task.CompletedTask;
        });
        yield return new("カードドラッグ: 2段配置と末尾余白の挿入位置", () =>
        {
            CheckDropLocations();
            return Task.CompletedTask;
        });
    }

    internal static void RunOnStaThread()
    {
        Check.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState(), "カード操作は共通STAで検証する必要があります");
        Run("中央クリックで対象カードの詳細を開く", CheckCentralClick);
        Run("閾値未満の移動はクリックとして扱う", CheckSmallMovement);
        Run("縦方向の閾値到達でもドラッグを開始", CheckVerticalDrag);
        Run("ボタン解放後の移動ではドラッグを開始しない", CheckReleasedButtonMovement);
        Run("左右移動ボタンをドラッグ・詳細操作から除外", CheckArrowButtons);
        Run("前方へのドラッグで順序保存を一度呼ぶ", () => CheckReorder(4, 0, after: false, "EABCD"));
        Run("後方へのドラッグで順序保存を一度呼ぶ", () => CheckReorder(0, 4, after: true, "BCDEA"));
        Run("段を跨ぐドラッグで順序保存を一度呼ぶ", () => CheckReorder(1, 3, after: false, "ACBDE"));
        Run("最終行の余白へのドラッグで末尾へ移動", CheckDropInLastRowSpace);
        Run("同位置ドロップで保存・詳細操作を呼ばない", CheckDropOnSameCard);
        Run("Esc取消で順序と保存回数を維持", CheckCanceledDrag);
        Run("画面外ドロップで順序と保存回数を維持", CheckOutsideDrop);
        Run("外部データのドロップで並べ替えない", CheckExternalDrop);
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"成功: カード操作: {name}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"カード操作: {name}: {ex.Message}", ex);
        }
    }

    private static void CheckDragThreshold()
    {
        var start = new Point(100, 100);
        double horizontal = SystemParameters.MinimumHorizontalDragDistance;
        double vertical = SystemParameters.MinimumVerticalDragDistance;
        Check.True(!UsageCardDragDrop.HasExceededDragThreshold(start, start), "静止中にドラッグを開始しました");
        Check.True(!UsageCardDragDrop.HasExceededDragThreshold(start,
            new Point(start.X + horizontal / 2, start.Y + vertical / 2)), "閾値未満でドラッグを開始しました");
        foreach (var delta in new[] { new Vector(horizontal, 0), new Vector(-horizontal, 0),
            new Vector(0, vertical), new Vector(0, -vertical) })
            Check.True(UsageCardDragDrop.HasExceededDragThreshold(start, start + delta), "縦横いずれかの閾値に到達してもドラッグを開始しません");
    }

    private static void CheckMoveIndices()
    {
        foreach (var (source, insertion, expected) in new[]
        {
            (0, 0, 0), (0, 1, 0), (0, 2, 1), (0, 5, 4),
            (4, 0, 0), (4, 4, 4), (4, 5, 4),
            (1, 3, 2), (3, 1, 1), (2, 2, 2), (2, 3, 2)
        })
            Check.Equal(expected, UsageCardDragDrop.GetMoveIndex(source, insertion, 5),
                $"カード{source}を挿入位置{insertion}へ移す最終indexが正しくありません");
    }

    private static void CheckDropLocations()
    {
        IReadOnlyList<Rect> bounds =
        [new(2, 2, 154, 136), new(160, 2, 154, 136), new(318, 2, 154, 136),
            new(2, 142, 154, 136), new(160, 142, 154, 136)];
        foreach (var (point, insertion) in new[]
        {
            (new Point(25, 65), 0), (new Point(130, 65), 1),
            (new Point(180, 65), 1), (new Point(290, 65), 2),
            (new Point(340, 65), 2), (new Point(450, 65), 3),
            (new Point(25, 200), 3), (new Point(130, 200), 4),
            (new Point(180, 200), 4), (new Point(290, 200), 5),
            (new Point(450, 200), 5), (new Point(450, 290), 5)
        })
        {
            var location = UsageCardDragDrop.GetDropLocation(bounds, point);
            Check.True(location != null, $"2段配置の座標 {point} の挿入位置がありません");
            Check.Equal(insertion, location!.Value.InsertionIndex, $"2段配置の座標 {point} の挿入位置が正しくありません");
            Check.True(location.Value.MarkerHeight > 0, "挿入位置のマーカーを表示できません");
        }
        Check.True(UsageCardDragDrop.GetDropLocation([], new Point(0, 0)) == null, "カードなしで挿入位置を返しました");
    }

    private static void CheckCentralClick()
    {
        var fixture = new CardFixture();
        var source = fixture.CentralSource(2);
        var position = fixture.CardBounds(2).Center();
        fixture.Controller.PointerDown(source, position);
        Check.True(fixture.Controller.PointerUp(source, position), "カードクリックを処理できません");
        Check.Equal(1, fixture.Opened.Count, "中央クリックが詳細表示を一度だけ開いていません");
        Check.True(ReferenceEquals(fixture.Items[2], fixture.Opened[0]), "詳細がクリックしたカードの対象ではありません");
        Check.Equal(0, fixture.DragCount, "中央クリックがドラッグになりました");
        Check.Equal(0, fixture.SaveCount, "中央クリックが順序を保存しました");
    }

    private static void CheckSmallMovement()
    {
        var fixture = new CardFixture();
        var source = fixture.CentralSource(1);
        var start = fixture.CardBounds(1).Center();
        var end = start + new Vector(SystemParameters.MinimumHorizontalDragDistance / 2,
            SystemParameters.MinimumVerticalDragDistance / 2);
        fixture.Controller.PointerDown(source, start);
        fixture.Controller.PointerMove(end, leftPressed: true);
        fixture.Controller.PointerUp(source, end);
        Check.Equal(0, fixture.DragCount, "小さな移動がドラッグになりました");
        Check.Equal(1, fixture.Opened.Count, "小さな移動で通常クリックが失われました");
    }

    private static void CheckVerticalDrag()
    {
        var fixture = new CardFixture();
        fixture.Drag(0, new Vector(0, SystemParameters.MinimumVerticalDragDistance));
        Check.Equal(1, fixture.DragCount, "縦方向の移動でドラッグを開始しません");
        Check.Equal(0, fixture.Opened.Count, "ドラッグ後に詳細を開きました");
        fixture.CheckUnchanged();
    }

    private static void CheckReleasedButtonMovement()
    {
        var fixture = new CardFixture();
        var source = fixture.CentralSource(0);
        var start = fixture.CardBounds(0).Center();
        fixture.Controller.PointerDown(source, start);
        fixture.Controller.PointerMove(start + new Vector(60, 0), leftPressed: false);
        Check.Equal(0, fixture.DragCount, "左ボタン解放後にドラッグを開始しました");
        fixture.CheckUnchanged();
    }

    private static void CheckArrowButtons()
    {
        foreach (var arrow in new[] { "◀", "▶" })
        {
            var fixture = new CardFixture();
            var card = fixture.Card(1);
            var button = Descendants<Button>(card).Single(button => Equals(button.Content, arrow));
            var content = Descendants<ContentPresenter>(button).First();
            foreach (DependencyObject source in new DependencyObject[] { button, content })
            {
                var point = fixture.CardBounds(1).Center();
                fixture.Controller.PointerDown(source, point);
                fixture.Controller.PointerMove(point + new Vector(60, 0), leftPressed: true);
                fixture.Controller.PointerUp(source, point);
            }
            Check.Equal(0, fixture.DragCount, $"{arrow}ボタン操作がドラッグになりました");
            Check.Equal(0, fixture.Opened.Count, $"{arrow}ボタン操作が詳細を開きました");
            fixture.CheckUnchanged();
        }
    }

    private static void CheckReorder(int sourceIndex, int targetIndex, bool after, string expected)
    {
        var fixture = new CardFixture();
        var destination = fixture.SidePoint(targetIndex, after);
        fixture.DragAction = data =>
        {
            bool dropped = fixture.Controller.TryDrop(data, destination);
            Check.True(!fixture.Controller.TryDrop(data, destination), "同じドラッグのドロップを二度受け入れました");
            return dropped ? DragDropEffects.Move : DragDropEffects.None;
        };
        fixture.Drag(sourceIndex);
        Check.Equal(expected, fixture.Order, "ドロップ後のカード順が正しくありません");
        Check.Equal(1, fixture.SaveCount, "有効な移動が内蔵の順序保存経路を一度だけ呼んでいません");
        Check.Equal(expected, fixture.SavedOrder, "保存へ渡ったカード順が画面の順序と一致しません");
        Check.Equal(0, fixture.Opened.Count, "並べ替えた後に詳細を開きました");
    }

    private static void CheckDropInLastRowSpace()
    {
        var fixture = new CardFixture();
        var point = new Point(fixture.Control.ActualWidth - 4, fixture.CardBounds(4).Center().Y);
        fixture.DragAction = data => fixture.Controller.TryDrop(data, point) ? DragDropEffects.Move : DragDropEffects.None;
        fixture.Drag(0);
        Check.Equal("BCDEA", fixture.Order, "最終行右側の余白へドロップして末尾へ移動できません");
        Check.Equal(1, fixture.SaveCount, "末尾へ移動した順序を保存していません");
        Check.Equal(0, fixture.Opened.Count, "末尾へ移動した後に詳細を開きました");
    }

    private static void CheckDropOnSameCard()
    {
        var fixture = new CardFixture();
        var position = fixture.SidePoint(2, after: false);
        fixture.DragAction = data => fixture.Controller.TryDrop(data, position) ? DragDropEffects.Move : DragDropEffects.None;
        fixture.Drag(2);
        fixture.CheckUnchanged();
        Check.Equal(0, fixture.Opened.Count, "同位置ドラッグ後に詳細を開きました");
    }

    private static void CheckCanceledDrag()
    {
        var fixture = new CardFixture();
        fixture.DragAction = _ =>
        {
            fixture.Controller.CancelDrag();
            return DragDropEffects.None;
        };
        fixture.Drag(0);
        fixture.CheckUnchanged();
        Check.Equal(0, fixture.Opened.Count, "取消したドラッグ後に詳細を開きました");
        var source = fixture.CentralSource(1);
        var point = fixture.CardBounds(1).Center();
        fixture.Controller.PointerDown(source, point);
        fixture.Controller.PointerUp(source, point);
        Check.Equal(1, fixture.Opened.Count, "ドラッグ取消後に通常クリックへ復帰できません");
    }

    private static void CheckOutsideDrop()
    {
        foreach (var point in new[] { new Point(-1, 10), new Point(500, 10), new Point(10, -1), new Point(10, 350) })
        {
            var fixture = new CardFixture();
            fixture.DragAction = data =>
            {
                Check.True(!fixture.Controller.TryDrop(data, point), "コントロール外へのドロップを受け入れました");
                return DragDropEffects.None;
            };
            fixture.Drag(0);
            fixture.CheckUnchanged();
            Check.Equal(0, fixture.Opened.Count, "画面外へドロップした後に詳細を開きました");
        }
    }

    private static void CheckExternalDrop()
    {
        var fixture = new CardFixture();
        var data = new DataObject(DataFormats.Text, "外部からのテキスト");
        Check.True(!fixture.Controller.TryDrop(data, fixture.SidePoint(0, after: false)), "外部データでカードを並べ替えました");
        fixture.CheckUnchanged();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static Point Center(this Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private static bool HasVisibleAncestors(DependencyObject element, DependencyObject stopAt)
    {
        // 非表示ハーネスでは IsVisible が false なので、テンプレート内の折り畳み状態だけを確認する。
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, stopAt)) return true;
        }
        return false;
    }

    private sealed class CardFixture
    {
        public ObservableCollection<AiUsageItem> Items { get; } = new("ABCDE".Select(name => new AiUsageItem
        {
            DisplayName = name.ToString(),
            ServiceType = AiServiceType.Grok,
            CliInfo = new CliInfo { IsInstalled = true, IsBusy = false },
            PrimaryLimit = new UsageLimitInfo { Title = "テスト利用枠", RemainingPercent = 71 }
        }));
        public ItemsControl Control { get; }
        public UsageCardDragDrop Controller { get; }
        public List<AiUsageItem> Opened { get; } = [];
        public Func<IDataObject, DragDropEffects> DragAction { get; set; } = _ => DragDropEffects.None;
        public int DragCount { get; private set; }
        public int SaveCount { get; private set; }
        public string SavedOrder { get; private set; } = "";
        public string Order => string.Concat(Items.Select(item => item.DisplayName));
        private readonly AdornerDecorator _host;

        public CardFixture()
        {
            var wrapPanel = new FrameworkElementFactory(typeof(WrapPanel));
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(FrameworkElement.WidthProperty, 154d);
            grid.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            grid.AppendChild(new FrameworkElementFactory(typeof(UsageCardView)));
            Control = new ItemsControl
            {
                Width = 480, Height = 320, ItemsSource = Items,
                ItemsPanel = new ItemsPanelTemplate(wrapPanel),
                ItemTemplate = new DataTemplate { VisualTree = grid }
            };
            _host = new AdornerDecorator { Child = Control };
            Arrange();
            Controller = new UsageCardDragDrop(Control, (oldIndex, newIndex) =>
            {
                Items.Move(oldIndex, newIndex);
                SaveCount++;
                SavedOrder = Order;
            }, Opened.Add, (_, data, allowedEffects) =>
            {
                Check.Equal(DragDropEffects.Move, allowedEffects, "ドラッグでカードの複製を許可しています");
                DragCount++;
                return DragAction(data);
            });
        }

        public UsageCardView Card(int index) => Descendants<UsageCardView>(
            Control.ItemContainerGenerator.ContainerFromIndex(index)).Single();

        public DependencyObject CentralSource(int index)
        {
            var card = Card(index);
            return Descendants<TextBlock>(card)
                .First(text => text.Text == "テスト利用枠" && HasVisibleAncestors(text, card));
        }

        public Rect CardBounds(int index)
        {
            var container = (FrameworkElement)Control.ItemContainerGenerator.ContainerFromIndex(index);
            return container.TransformToAncestor(Control).TransformBounds(new Rect(container.RenderSize));
        }

        public Point SidePoint(int index, bool after)
        {
            var bounds = CardBounds(index);
            return new Point(bounds.Left + bounds.Width * (after ? .75 : .25), bounds.Center().Y);
        }

        public void Drag(int sourceIndex, Vector? delta = null)
        {
            var source = CentralSource(sourceIndex);
            var start = CardBounds(sourceIndex).Center();
            var end = start + (delta ?? new Vector(SystemParameters.MinimumHorizontalDragDistance + 5, 0));
            Controller.PointerDown(source, start);
            Controller.PointerMove(end, leftPressed: true);
            Controller.PointerUp(source, end);
        }

        public void CheckUnchanged()
        {
            Check.Equal("ABCDE", Order, "取消・無効操作でカード順が変わりました");
            Check.Equal(0, SaveCount, "取消・無効操作で順序を保存しました");
        }

        private void Arrange()
        {
            var size = new Size(480, 320);
            _host.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            _host.Measure(size);
            _host.Arrange(new Rect(size));
            _host.UpdateLayout();
            _host.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            _host.UpdateLayout();
        }
    }
}
