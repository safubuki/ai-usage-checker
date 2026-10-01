using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AIUsageChecker.Models;
using AIUsageChecker.Views;

namespace AIUsageChecker.Controls;

/// <summary>カードのクリックとドラッグを区別し、同じ一覧内でだけ並べ替える。</summary>
internal sealed class UsageCardDragDrop
{
    private const string DragFormat = "AIUsageChecker.UsageCard";
    private readonly ItemsControl _itemsControl;
    private readonly Action<int, int> _moveItem;
    private readonly Action<AiUsageItem> _openDetail;
    private readonly Func<DependencyObject, DataObject, DragDropEffects, DragDropEffects> _runDrag;
    private UsageCardView? _pressedCard;
    private Point _pressPosition;
    private AiUsageItem? _activeItem;
    private bool _isDragging;
    private bool _dropCompleted;
    private InsertionAdorner? _indicator;
    private AdornerLayer? _adornerLayer;

    internal UsageCardDragDrop(ItemsControl itemsControl, Action<int, int> moveItem,
        Action<AiUsageItem> openDetail,
        Func<DependencyObject, DataObject, DragDropEffects, DragDropEffects>? runDrag = null)
    {
        _itemsControl = itemsControl;
        _moveItem = moveItem;
        _openDetail = openDetail;
        _runDrag = runDrag ?? ((source, data, effects) => DragDrop.DoDragDrop(source, data, effects));

        itemsControl.AllowDrop = true;
        itemsControl.PreviewMouseLeftButtonDown += (_, e) =>
            PointerDown((DependencyObject)e.OriginalSource, e.GetPosition(itemsControl));
        itemsControl.PreviewMouseMove += (_, e) =>
            PointerMove(e.GetPosition(itemsControl), e.LeftButton == MouseButtonState.Pressed);
        itemsControl.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_isDragging || PointerUp((DependencyObject)e.OriginalSource, e.GetPosition(itemsControl)))
                e.Handled = true;
        };
        itemsControl.PreviewDragOver += OnDragOver;
        itemsControl.PreviewDrop += OnDrop;
        itemsControl.DragLeave += (_, _) => ClearIndicator();
        itemsControl.LostMouseCapture += (_, _) => _pressedCard = null;
        itemsControl.Unloaded += (_, _) => CancelDrag();
    }

    internal void PointerDown(DependencyObject originalSource, Point position)
    {
        _pressedCard = null;
        if (_isDragging || FindAncestor<ButtonBase>(originalSource) != null) return;
        var card = FindAncestor<UsageCardView>(originalSource);
        if (card?.DataContext is not AiUsageItem item || !_itemsControl.Items.Contains(item)) return;
        _pressedCard = card;
        _pressPosition = position;
        Mouse.Capture(_itemsControl, CaptureMode.SubTree);
    }

    internal void PointerMove(Point position, bool leftPressed)
    {
        if (!leftPressed)
        {
            _pressedCard = null;
            ReleaseMouseCapture();
            return;
        }
        if (_pressedCard == null || !HasExceededDragThreshold(_pressPosition, position)) return;

        var card = _pressedCard;
        _pressedCard = null; // ドロップ・キャンセル時の MouseUp は詳細表示に使わない。
        ReleaseMouseCapture();
        if (card.DataContext is not AiUsageItem item || !_itemsControl.Items.Contains(item)) return;
        _activeItem = item;
        _isDragging = true;
        _dropCompleted = false;
        var originalOpacity = card.Opacity;
        card.SetCurrentValue(UIElement.OpacityProperty, 0.55);
        var data = new DataObject();
        data.SetData(DragFormat, new DragPayload(this, item), autoConvert: false);
        try
        {
            _runDrag(card, data, DragDropEffects.Move);
        }
        finally
        {
            card.SetCurrentValue(UIElement.OpacityProperty, originalOpacity);
            _isDragging = false;
            CancelDrag();
        }
    }

    internal bool PointerUp(DependencyObject originalSource, Point position)
    {
        var pressedCard = _pressedCard;
        _pressedCard = null;
        ReleaseMouseCapture();
        if (_isDragging || pressedCard == null || HasExceededDragThreshold(_pressPosition, position) ||
            FindAncestor<ButtonBase>(originalSource) != null ||
            FindAncestor<UsageCardView>(originalSource) != pressedCard ||
            pressedCard.DataContext is not AiUsageItem item || !_itemsControl.Items.Contains(item))
            return false;
        _openDetail(item);
        return true;
    }

    internal void CancelDrag()
    {
        _pressedCard = null;
        _activeItem = null;
        _dropCompleted = false;
        ReleaseMouseCapture();
        ClearIndicator();
    }

    private void ReleaseMouseCapture()
    {
        if (_itemsControl.IsMouseCaptured) _itemsControl.ReleaseMouseCapture();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if ((e.AllowedEffects & DragDropEffects.Move) == 0 ||
            !TryGetLocation(e.Data, e.GetPosition(_itemsControl), out var sourceIndex, out var location))
        {
            ClearIndicator();
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (GetMoveIndex(sourceIndex, location.InsertionIndex, _itemsControl.Items.Count) == sourceIndex)
            ClearIndicator();
        else
            ShowIndicator(location);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 &&
            TryDrop(e.Data, e.GetPosition(_itemsControl)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        ClearIndicator();
    }

    internal bool TryDrop(IDataObject data, Point position)
    {
        if (!TryGetLocation(data, position, out var sourceIndex, out var location)) return false;
        int destination = GetMoveIndex(sourceIndex, location.InsertionIndex, _itemsControl.Items.Count);
        if (destination < 0) return false;
        _dropCompleted = true;
        if (sourceIndex != destination) _moveItem(sourceIndex, destination);
        return true;
    }

    private bool TryGetLocation(IDataObject data, Point position, out int sourceIndex, out DropLocation location)
    {
        sourceIndex = -1;
        location = default;
        if (_activeItem == null || _dropCompleted || !data.GetDataPresent(DragFormat, autoConvert: false) ||
            data.GetData(DragFormat, autoConvert: false) is not DragPayload payload ||
            !ReferenceEquals(payload.Owner, this) || !ReferenceEquals(payload.Item, _activeItem) ||
            !new Rect(_itemsControl.RenderSize).Contains(position))
            return false;
        sourceIndex = _itemsControl.Items.IndexOf(payload.Item);
        if (sourceIndex < 0) return false;

        var bounds = new List<Rect>();
        for (int index = 0; index < _itemsControl.Items.Count; index++)
        {
            if (_itemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
                container.ActualWidth <= 0 || container.ActualHeight <= 0)
                return false;
            bounds.Add(new Rect(container.TranslatePoint(new Point(), _itemsControl), container.RenderSize));
        }
        var result = GetDropLocation(bounds, position);
        if (!result.HasValue) return false;
        location = result.Value;
        return true;
    }

    internal static bool HasExceededDragThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    internal static int GetMoveIndex(int sourceIndex, int insertionIndex, int itemCount)
    {
        if (sourceIndex < 0 || sourceIndex >= itemCount || insertionIndex < 0 || insertionIndex > itemCount)
            return -1;
        return insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
    }

    internal static DropLocation? GetDropLocation(IReadOnlyList<Rect> cardBounds, Point position)
    {
        if (cardBounds.Count == 0 || cardBounds.Any(rect => rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0))
            return null;

        // 先に行を選び、その行のカード中央を境に前後挿入を決める。
        // 下段の右余白も最終カードの後ろとして扱い、固定の列数には依存しない。
        var nearestRow = cardBounds.MinBy(rect => Math.Abs(position.Y - (rect.Top + rect.Height / 2)));
        var row = Enumerable.Range(0, cardBounds.Count)
            .Where(index => Math.Abs(cardBounds[index].Top - nearestRow.Top) < 1)
            .ToList();
        foreach (int index in row)
        {
            var rect = cardBounds[index];
            if (position.X < rect.Left + rect.Width / 2)
                return new DropLocation(index, new Point(rect.Left, rect.Top), rect.Height);
        }
        int lastIndex = row[^1];
        var lastRect = cardBounds[lastIndex];
        return new DropLocation(lastIndex + 1, new Point(lastRect.Right, lastRect.Top), lastRect.Height);
    }

    private T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null && source != _itemsControl)
        {
            if (source is T match) return match;
            source = source switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    private void ShowIndicator(DropLocation location)
    {
        if (_indicator == null)
        {
            _adornerLayer = AdornerLayer.GetAdornerLayer(_itemsControl);
            if (_adornerLayer == null) return;
            _indicator = new InsertionAdorner(_itemsControl);
            _adornerLayer.Add(_indicator);
        }
        _indicator.Location = location;
        _indicator.InvalidateVisual();
    }

    private void ClearIndicator()
    {
        if (_indicator != null) _adornerLayer?.Remove(_indicator);
        _indicator = null;
        _adornerLayer = null;
    }

    internal readonly record struct DropLocation(int InsertionIndex, Point MarkerStart, double MarkerHeight);
    private sealed record DragPayload(UsageCardDragDrop Owner, AiUsageItem Item);

    private sealed class InsertionAdorner : Adorner
    {
        private static readonly Brush MarkerBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        internal DropLocation Location { get; set; }

        internal InsertionAdorner(UIElement element) : base(element)
        {
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var start = Location.MarkerStart;
            var end = new Point(start.X, start.Y + Location.MarkerHeight);
            drawingContext.DrawLine(new Pen(MarkerBrush, 3), start, end);
            drawingContext.DrawEllipse(MarkerBrush, null, start, 3, 3);
            drawingContext.DrawEllipse(MarkerBrush, null, end, 3, 3);
        }
    }
}
