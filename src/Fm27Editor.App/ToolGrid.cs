using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fm27Editor.App;

/// <summary>
/// The one look of every table that has a tick column and a Tools card beside it (squads, club staff, national staff):
/// the same grid styling, the same header text, the same columns, the same tick column and the same header sorting.
/// </summary>
internal static class ToolGrid
{
    static IBrush Brush(string hex) => UiKit.Brush(hex);

    public static DataGrid Create()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserResizeColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Brush("#1D2130"),
            RowHeight = 28,
            Background = Brush("#11131C"),
            BorderBrush = Brush("#252A3D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        grid.Styles.Add(new Style(x => x.OfType<DataGridColumnHeader>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 0)), new Setter(Layoutable.MinHeightProperty, 26.0) }
        });
        grid.Styles.Add(new Style(x => x.OfType<DataGridColumnHeader>().Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Layoutable.MarginProperty, new Thickness(0)) }
        });
        grid.Styles.Add(new Style(x => x.OfType<DataGridCell>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(0)) }
        });
        EnableRangeSelect(grid);
        return grid;
    }

    public static Control Header(string text, string tooltip, HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var tb = new TextBlock
        {
            Tag = text,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 4),
        };
        Loc.Bind(tb, () =>
        {
            tb.Text = Loc.S(text);
            ToolTip.SetTip(tb, Loc.S(tooltip));
        });
        return tb;
    }

    public const double ToolsWidth = 166.0;

    /// <summary>Standard side-panel action button with crisp height, no text wrap, ellipsis, and tooltip.</summary>
    public static Button ToolButton(string text, Action onClick, string? tooltip = null, bool enabled = true)
    {
        string labelText = Loc.S(text);
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = labelText,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
            },
            FontSize = 12,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = enabled,
        };
        ToolTip.SetTip(btn, tooltip is not null ? Loc.S(tooltip) : labelText);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Grid ToolPair(Control left, Control right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 5 };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    public static TextBlock ToolSectionHeader(string text, bool first = false) => new()
    {
        Text = Loc.S(text),
        FontSize = 10.5,
        FontWeight = FontWeight.Bold,
        Foreground = Brush("#7E8BA3"),
        LetterSpacing = 0.4,
        Margin = new Thickness(1, first ? 1 : 7, 0, 2),
    };

    /// <summary>Places a primary view beside a fixed-width right-hand Tools card without overflowing narrow windows.</summary>
    public static Grid WithTools(Control content, Control toolsCard)
    {
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(content, 0);
        layout.Children.Add(content);
        toolsCard.Width = ToolsWidth;
        toolsCard.VerticalAlignment = VerticalAlignment.Top;
        toolsCard.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(toolsCard, 1);
        layout.Children.Add(toolsCard);
        return layout;
    }

    /// <summary>A right-aligned number column.</summary>
    public static DataGridTemplateColumn Number<T>(string title, Func<T, string> text, double width, string tooltip, Func<T, string?>? cellTooltip = null, double rightInset = 5) => new()
    {
        Header = WithRightInset(Header(title, tooltip, HorizontalAlignment.Right), rightInset),
        Width = new DataGridLength(width),
        CellTemplate = new FuncDataTemplate<T>((row, _) =>
        {
            string value = text(row);
            var tb = new TextBlock
            {
                Text = value,
                Foreground = Brush("#F1F5F9"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, rightInset, 0),
            };
            if (cellTooltip is not null && cellTooltip(row) is { Length: > 0 } tip)
                ToolTip.SetTip(tb, tip);
            return tb;
        }),
    };

    /// <summary>Keeps a number header over its values when the column reserves room at its right edge.</summary>
    static Control WithRightInset(Control header, double inset)
    {
        // The header cell already sits 5 px further in than a value with the default inset.
        if (inset != 5) header.Margin = new Thickness(0, 4, inset - 5, 4);
        return header;
    }

    /// <summary>A left-aligned text column; <paramref name="width"/> &lt;= 0 makes it a star column.</summary>
    public static DataGridTemplateColumn Text<T>(string title, Func<T, string> text, double width, string tooltip, string foreground = "#CBD5E1", bool bold = false, double minWidth = 0) =>
        TextColumn(title, text, width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width), minWidth, tooltip, foreground, bold);

    /// <summary>A left-aligned star-weighted text column that proportions cleanly across window sizes without overflowing.</summary>
    public static DataGridTemplateColumn StarText<T>(string title, Func<T, string> text, double weight, double minWidth, string tooltip, string foreground = "#CBD5E1", bool bold = false) =>
        TextColumn(title, text, new DataGridLength(weight, DataGridLengthUnitType.Star), minWidth, tooltip, foreground, bold);

    static DataGridTemplateColumn TextColumn<T>(string title, Func<T, string> text, DataGridLength width, double minWidth, string tooltip, string foreground, bool bold) => new()
    {
        Header = Header(title, tooltip, HorizontalAlignment.Left),
        Width = width,
        MinWidth = minWidth,
        CellTemplate = new FuncDataTemplate<T>((row, _) =>
        {
            string value = text(row);
            var tb = new TextBlock
            {
                Text = value,
                FontWeight = bold ? FontWeight.Medium : FontWeight.Normal,
                Foreground = Brush(foreground),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
            };
            ToolTip.SetTip(tb, value);
            return tb;
        }),
    };

    /// <summary>
    /// The first column: a tick per row and a tick in the header for all rows. Every row starts ticked. A click anywhere on a row
    /// leaves only that row ticked; Ctrl+click ticks or unticks that row alone. <paramref name="excluded"/> holds the keys of the
    /// unticked rows; <paramref name="changed"/> runs after any change.
    /// </summary>
    public static Action AddTick<T>(DataGrid grid, HashSet<ulong> excluded, Func<T, ulong> key, Func<IEnumerable<T>> rows, Action changed) where T : class
    {
        var syncs = new List<Action>();
        const double tickColumnWidth = 36;
        CheckBox TickBox() => new()
        {
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var all = TickBox();
        all.IsChecked = true;
        bool syncingHeader = false;
        void Changed()
        {
            foreach (var sync in syncs.ToArray()) sync();
            var keys = rows().Select(key).ToArray();
            int selected = keys.Count(id => !excluded.Contains(id));
            syncingHeader = true;
            all.IsEnabled = keys.Length > 0;
            all.IsChecked = selected == 0 ? false : selected == keys.Length ? true : null;
            syncingHeader = false;
            changed();
        }
        ToolTip.SetTip(all, Loc.S("Include every row"));
        all.IsCheckedChanged += (_, _) =>
        {
            if (syncingHeader) return;
            if (all.IsChecked == true) excluded.Clear();
            else foreach (var r in rows()) excluded.Add(key(r));
            Changed();
        };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = all,
            Width = new DataGridLength(tickColumnWidth),
            MinWidth = tickColumnWidth,
            CanUserResize = false,
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<T>((row, _) =>
            {
                var box = TickBox();
                bool syncing = false;
                void Sync() { syncing = true; box.IsChecked = !excluded.Contains(key(row)); syncing = false; }
                Sync();
                box.IsCheckedChanged += (_, _) =>
                {
                    if (syncing) return;
                    if (box.IsChecked == true) excluded.Remove(key(row)); else excluded.Add(key(row));
                    Changed();
                };
                syncs.Add(Sync);
                box.DetachedFromVisualTree += (_, _) => syncs.Remove(Sync);
                return box;
            }),
        });

        grid.AddHandler(InputElement.PointerPressedEvent, (object? _, Avalonia.Input.PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed || e.Source is not Avalonia.Visual source) return;
            if (source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
            if (source.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is not T row) return;
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) return;
            ulong id = key(row);
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
            {
                if (!excluded.Remove(id)) excluded.Add(id);
            }
            else
            {
                excluded.Clear();
                foreach (var r in rows()) if (key(r) != id) excluded.Add(key(r));
            }
            Changed();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItems.Count <= 1) return;
            var selected = grid.SelectedItems.OfType<T>().Select(key).ToHashSet();
            if (selected.Count == 0) return;
            excluded.Clear();
            foreach (var r in rows()) if (!selected.Contains(key(r))) excluded.Add(key(r));
            Changed();
        };
        return Changed;
    }

    /// <summary>
    /// Range selection like a document: press and hold a row then drag across rows (up or down) to select the whole span,
    /// or Shift+click from the anchor row to the clicked row (Ctrl/Cmd preserves prior selection).
    /// </summary>
    public static void EnableRangeSelect(DataGrid grid)
    {
        if (grid.SelectionMode != DataGridSelectionMode.Extended) return;
        object? anchor = null, pressedItem = null, lastTarget = null;
        bool dragging = false, dragged = false, additive = false;
        object[] before = [];

        static bool IsAdditive(Avalonia.Input.KeyModifiers modifiers) =>
            modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta);

        List<object> OrderedItems() =>
            (grid.CollectionView as System.Collections.IEnumerable ?? grid.ItemsSource)?.Cast<object>().ToList() ?? [];

        static object? RowFromSource(Avalonia.Input.PointerEventArgs e)
        {
            if (e.Source is not Avalonia.Visual v) return null;
            if (v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not null) return null;
            if (v.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return null;
            if (v.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return null;
            return v.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext;
        }

        object? RowAtPointer(Avalonia.Input.PointerEventArgs e)
        {
            var pt = e.GetPosition(grid);
            double headerBottom = 0;
            foreach (var h in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
            {
                if (!h.IsVisible || h.Bounds.Height <= 0) continue;
                if (h.TranslatePoint(default, grid) is { } hp)
                    headerBottom = Math.Max(headerBottom, hp.Y + h.Bounds.Height);
            }

            if (pt.Y >= headerBottom)
            {
                foreach (var v in grid.GetVisualsAt(pt))
                {
                    if (v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not null) continue;
                    if (v.FindAncestorOfType<DataGridRow>(includeSelf: true) is { IsVisible: true, DataContext: { } dc })
                        return dc;
                }
            }

            // Realized visible rows sorted top-to-bottom cover drags across cell gaps, scrollbars, and above/below viewport.
            var visibleRows = grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Select(r => (Row: r, Pos: r.IsVisible && r.Bounds.Height > 0 && r.DataContext is not null
                    ? r.TranslatePoint(default, grid) : null))
                .Where(x => x.Pos is { } p && p.Y + x.Row.Bounds.Height > headerBottom)
                .Select(x => (Item: x.Row.DataContext!, Top: x.Pos!.Value.Y, Bottom: x.Pos!.Value.Y + x.Row.Bounds.Height))
                .OrderBy(x => x.Top)
                .ToList();

            if (visibleRows.Count == 0) return RowFromSource(e);

            foreach (var row in visibleRows)
                if (pt.Y >= row.Top && pt.Y < row.Bottom)
                    return row.Item;

            if (pt.Y < visibleRows[0].Top)
            {
                var items = OrderedItems();
                int idx = items.IndexOf(visibleRows[0].Item);
                if (idx > 0)
                {
                    var prev = items[idx - 1];
                    grid.ScrollIntoView(prev, null);
                    return prev;
                }
                return visibleRows[0].Item;
            }

            if (pt.Y >= visibleRows[^1].Bottom)
            {
                var items = OrderedItems();
                int idx = items.IndexOf(visibleRows[^1].Item);
                if (idx >= 0 && idx + 1 < items.Count)
                {
                    var next = items[idx + 1];
                    grid.ScrollIntoView(next, null);
                    return next;
                }
                return visibleRows[^1].Item;
            }

            return null;
        }

        void SelectRange(object from, object to)
        {
            if (grid.SelectionMode != DataGridSelectionMode.Extended) return;
            var items = OrderedItems();
            int a = items.IndexOf(from), b = items.IndexOf(to);
            if (a < 0 || b < 0) return;
            grid.SelectedItems.Clear();
            if (additive) foreach (var item in before) grid.SelectedItems.Add(item);
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                if (!grid.SelectedItems.Contains(items[i])) grid.SelectedItems.Add(items[i]);
        }

        grid.AddHandler(InputElement.PointerPressedEvent, (object? _, Avalonia.Input.PointerPressedEventArgs e) =>
        {
            if (grid.SelectionMode != DataGridSelectionMode.Extended) return;
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed || RowFromSource(e) is not { } item) return;
            pressedItem = item;
            lastTarget = null;
            dragged = false;
            dragging = e.ClickCount == 1;
            additive = IsAdditive(e.KeyModifiers);
            before = additive ? grid.SelectedItems.Cast<object>().ToArray() : [];
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) && anchor is not null)
            {
                SelectRange(anchor, item);
                lastTarget = item;
                e.Handled = true;
                return;
            }
            anchor = item;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        grid.AddHandler(InputElement.PointerMovedEvent, (object? _, Avalonia.Input.PointerEventArgs e) =>
        {
            if (grid.SelectionMode != DataGridSelectionMode.Extended || !dragging || pressedItem is null || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
            {
                dragging = false;
                dragged = false;
                pressedItem = null;
                lastTarget = null;
                return;
            }
            if (RowAtPointer(e) is not { } item) return;
            if (!dragged)
            {
                if (ReferenceEquals(item, pressedItem)) return;
                dragged = true;
                anchor = pressedItem;
            }
            if (ReferenceEquals(item, lastTarget))
            {
                e.Handled = true;
                return;
            }
            lastTarget = item;
            SelectRange(anchor!, item);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        grid.AddHandler(InputElement.PointerReleasedEvent, (object? _, Avalonia.Input.PointerReleasedEventArgs _) =>
        {
            dragging = false;
            dragged = false;
            pressedItem = null;
            lastTarget = null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Header sorting uses the grid's own: the arrow at the right of the header cell, as in the module lists. Each named column
    /// sorts by its key; rows with no key come first when ascending.
    /// </summary>
    public static void EnableSort<T>(DataGrid grid, IReadOnlyDictionary<string, Func<T, IComparable?>> keys) where T : class
    {
        grid.CanUserSortColumns = true;
        foreach (var column in grid.Columns)
        {
            if (column.Header is TextBlock tb && ((tb.Tag as string ?? tb.Text) is { } name) && keys.TryGetValue(name, out var key))
            {
                column.CustomSortComparer = new KeyComparer<T>(key);
                column.CanUserSort = true;
            }
        }
    }

    /// <summary>Replaces the rows and keeps the sort the user chose.</summary>
    public static void SetItems(DataGrid grid, System.Collections.IEnumerable items)
    {
        var saved = grid.CollectionView?.SortDescriptions.ToList();
        grid.ItemsSource = null;
        grid.ItemsSource = items;
        if (saved is { Count: > 0 } && grid.CollectionView is { } view)
            foreach (var description in saved) view.SortDescriptions.Add(description);
    }

    sealed class KeyComparer<T>(Func<T, IComparable?> key) : System.Collections.IComparer where T : class
    {
        public int Compare(object? x, object? y)
        {
            var a = x is T tx ? key(tx) : null;
            var b = y is T ty ? key(ty) : null;
            if (a is null) return b is null ? 0 : -1;
            return b is null ? 1 : a.CompareTo(b);
        }
    }
}
