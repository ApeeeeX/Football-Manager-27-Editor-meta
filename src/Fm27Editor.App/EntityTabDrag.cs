using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    const double TabDragThreshold = 6;
    readonly Border tabDropMarker = new() { Width = 2, Height = 30, Background = UiKit.Accent, IsVisible = false };
    readonly DispatcherTimer tabDragScroll = new() { Interval = TimeSpan.FromMilliseconds(35) };
    TabDrag? tabDrag;

    sealed class TabDrag(Border tab, IPointer pointer, Point origin)
    {
        public Border Tab { get; } = tab;
        public IPointer Pointer { get; } = pointer;
        public Point Origin { get; } = origin;
        public Point Position { get; set; } = origin;
        public bool Moved { get; set; }
        public int DropIndex { get; set; } = -1;
    }

    void AttachEntityTabInteractions(Border tab, Button close, Action activate)
    {
        tab.Focusable = true;
        tab.PointerPressed += (_, e) =>
        {
            if (busy || !e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed ||
                e.Source is Visual visual && (visual == close || close.IsVisualAncestorOf(visual))) return;
            ClearEntityTabDrag();
            tab.Focus();
            tabDrag = new(tab, e.Pointer, e.GetPosition(entityScroll));
            e.Pointer.Capture(tab);
            e.Handled = true;
        };
        tab.PointerMoved += (_, e) =>
        {
            if (tabDrag is not { } drag || drag.Tab != tab || drag.Pointer != e.Pointer) return;
            var position = e.GetPosition(entityScroll);
            if (!drag.Moved && Math.Abs(position.X - drag.Origin.X) < TabDragThreshold) return;
            drag.Moved = true;
            drag.Position = position;
            UpdateTabDropTarget(drag);
            if (!tabDragScroll.IsEnabled) tabDragScroll.Start();
            e.Handled = true;
        };
        tab.PointerReleased += (_, e) =>
        {
            if (tabDrag is not { } drag || drag.Tab != tab || drag.Pointer != e.Pointer) return;
            if (drag.Moved)
            {
                drag.Position = e.GetPosition(entityScroll);
                UpdateTabDropTarget(drag);
            }
            bool click = !drag.Moved && new Rect(tab.Bounds.Size).Contains(e.GetPosition(tab));
            ClearEntityTabDrag();
            if (click) activate();
            else if (drag.DropIndex >= 0) MoveEntityTab(tab, drag.DropIndex);
            e.Handled = true;
        };
        tab.PointerCaptureLost += (_, _) => { if (tabDrag?.Tab == tab) ClearEntityTabDrag(); };
        tab.DetachedFromVisualTree += (_, _) => { if (tabDrag?.Tab == tab) ClearEntityTabDrag(); };
        tab.KeyDown += (_, e) =>
        {
            if (busy || e.Source != tab) return;
            if (e.Key == Key.Enter) activate();
            else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right)
                MoveEntityTab(tab, entityStrip.Children.IndexOf(tab) + (e.Key == Key.Left ? -1 : 1));
            else return;
            e.Handled = true;
        };

        var left = new MenuItem { Header = Loc.S("Move tab left") };
        var right = new MenuItem { Header = Loc.S("Move tab right") };
        left.Click += (_, _) => MoveEntityTab(tab, entityStrip.Children.IndexOf(tab) - 1);
        right.Click += (_, _) => MoveEntityTab(tab, entityStrip.Children.IndexOf(tab) + 1);
        var menu = new ContextMenu { Items = { left, right } };
        menu.Opening += (_, e) =>
        {
            int index = entityStrip.Children.IndexOf(tab);
            e.Cancel = busy || index < 0 || entityStrip.Children.Count < 2;
            left.IsEnabled = index > 0;
            right.IsEnabled = index >= 0 && index < entityStrip.Children.Count - 1;
        };
        tab.ContextMenu = menu;
    }

    void ClearEntityTabDrag()
    {
        var drag = tabDrag;
        tabDrag = null;
        tabDragScroll.Stop();
        tabDropMarker.IsVisible = false;
        if (drag is not null && drag.Pointer.Captured == drag.Tab) drag.Pointer.Capture(null);
    }

    void UpdateTabDropTarget(TabDrag drag)
    {
        int from = entityStrip.Children.IndexOf(drag.Tab);
        double width = entityScroll.Viewport.Width;
        bool inside = drag.Position.X >= 0 && drag.Position.X <= width &&
            drag.Position.Y >= 0 && drag.Position.Y <= entityScroll.Bounds.Height;
        int to = from < 0 || !inside ? -1 :
            TabDropIndex(entityStrip.Children, from, drag.Position.X + entityScroll.Offset.X);
        drag.DropIndex = to;
        tabDropMarker.IsVisible = to >= 0 && to != from;
        if (!tabDropMarker.IsVisible) return;
        var neighbour = entityStrip.Children[to].Bounds;
        Canvas.SetLeft(tabDropMarker, Math.Clamp(to < from ? neighbour.Left : neighbour.Right, 0, Math.Max(0, entityStrip.Bounds.Width - tabDropMarker.Width)));
        Canvas.SetTop(tabDropMarker, 1);
    }

    static int TabDropIndex(IReadOnlyList<Control> tabs, int from, double x)
    {
        int to = 0;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (i == from) continue;
            var bounds = tabs[i].Bounds;
            if (x < bounds.Left + bounds.Width / 2) break;
            to++;
        }
        return to;
    }

    void ScrollDraggedTab()
    {
        if (tabDrag is not { Moved: true } drag || drag.DropIndex < 0) return;
        const double edge = 22;
        double width = entityScroll.Viewport.Width;
        double offset = entityScroll.Offset.X;
        double delta = drag.Position.X < edge ? -14 : drag.Position.X > width - edge ? 14 : 0;
        double next = Math.Clamp(offset + delta, 0, Math.Max(0, entityScroll.Extent.Width - width));
        if (next == offset) return;
        entityScroll.Offset = new Vector(next, 0);
        UpdateTabDropTarget(drag);
    }

    void MoveEntityTab(Border tab, int to)
    {
        int from = entityStrip.Children.IndexOf(tab);
        if (busy || from < 0 || to < 0 || to >= entityStrip.Children.Count || from == to) return;
        entityStrip.Children.Move(from, to);
        UpdateEntityRow();
        Dispatcher.UIThread.Post(() => tab.BringIntoView(), DispatcherPriority.Loaded);
    }
}
