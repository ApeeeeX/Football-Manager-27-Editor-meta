using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fm27Editor.App;

/// <summary>One line of a relationship list; its form stays alive so unsaved edits survive changing row or group.</summary>
internal sealed class RelationshipRow
{
    public required string Group { get; set; }
    public required string Name { get; set; }
    public required string Value { get; set; }
    public required Control Detail { get; init; }
    public bool CanRemove { get; init; } = true;
}

/// <summary>
/// The Relationships layout of players, staff and clubs: group buttons and a short table on the left, the selected row's
/// form on the right, one Add and one Remove under the list. Add and Remove are drafts; the owner writes them on Save.
/// </summary>
internal sealed class RelationshipSplitView
{
    public const string OtherGroup = "Other";
    const double ListWidth = 440, HeaderHeight = 28, RowHeight = 29, ValueInset = 18, InputWidth = 200;
    const int MaxVisibleRows = 18;

    readonly List<RelationshipRow> rows;
    readonly Action<RelationshipRow> onRemove;
    readonly Dictionary<string, Button> groupButtons = [];
    readonly DataGrid grid = ToolGrid.Create();
    readonly Border detailHost = new();
    readonly TextBlock title = new() { FontSize = 12.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button remove = new() { Content = Loc.S("Remove") };
    readonly Border detailCard;
    RelationshipRow? selected;
    bool updating;

    public string CurrentGroup { get; private set; }
    public Control View { get; }

    public RelationshipSplitView(IReadOnlyList<string> groups, IEnumerable<RelationshipRow> initial, Action<RelationshipRow> onRemove, Func<string, RelationshipRow?> onAdd)
    {
        rows = initial.ToList();
        this.onRemove = onRemove;
        CurrentGroup = groups.FirstOrDefault(g => rows.Any(r => r.Group == g)) ?? groups[0];

        grid.SelectionMode = DataGridSelectionMode.Single;
        // The cells keep their text colour when selected, so the selection is a soft tint they stay readable on.
        foreach (var key in new[] { "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedHoveredBackgroundBrush", "DataGridRowSelectedUnfocusedBackgroundBrush", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush" })
            grid.Resources[key] = UiKit.AccentSoft;
        grid.Columns.Add(ToolGrid.Text<RelationshipRow>("Name", r => r.Name, 0, "Name", bold: true, minWidth: 180));
        // The grid's vertical scroll bar floats over the last column; the inset keeps the numbers clear of it.
        grid.Columns.Add(ToolGrid.Number<RelationshipRow>("Value", r => r.Value, 76 + ValueInset, "Value", rightInset: ValueInset));
        ToolGrid.EnableSort<RelationshipRow>(grid, new Dictionary<string, Func<RelationshipRow, IComparable?>>
        {
            ["Name"] = r => r.Name,
            ["Value"] = r => int.TryParse(r.Value, out int v) ? v : null,
        });
        grid.SelectionChanged += (_, _) =>
        {
            if (updating || grid.SelectedItem is not RelationshipRow row) return;
            selected = row;
            ShowDetail();
        };

        var groupBar = new WrapPanel { Margin = new Thickness(10, 8, 10, 6) };
        foreach (var group in groups)
        {
            var button = new Button { Content = Loc.S(group), Margin = new Thickness(0, 0, 4, 4) };
            button.Click += (_, _) => { CurrentGroup = group; selected = null; Refresh(); };
            groupButtons[group] = button;
            groupBar.Children.Add(button);
        }

        var add = new Button { Content = Loc.S("Add") };
        add.Click += (_, _) =>
        {
            if (onAdd(CurrentGroup) is not { } row) return;
            rows.Add(row);
            Select(row);
        };
        remove.Click += (_, _) =>
        {
            if (selected is not { CanRemove: true } row) return;
            this.onRemove(row);
            rows.Remove(row);
            selected = null;
            Refresh();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 8), Children = { add, remove } };
        var tableHost = new Border { Margin = new Thickness(10, 0), Child = grid };
        var left = UiKit.Card("Relationships", new StackPanel { Children = { groupBar, tableHost, actions } });
        left.VerticalAlignment = VerticalAlignment.Top;
        var right = detailCard = UiKit.Card("", detailHost, customTitle: title);
        right.VerticalAlignment = VerticalAlignment.Top;
        // As wide as its labels and inputs, not the rest of the window.
        right.HorizontalAlignment = HorizontalAlignment.Left;

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions($"{ListWidth},10,*") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        layout.Children.Add(left);
        layout.Children.Add(right);
        View = layout;
        Refresh();
    }

    /// <summary>Opens the row's group and selects it.</summary>
    public void Select(RelationshipRow row)
    {
        CurrentGroup = row.Group;
        selected = row;
        Refresh();
    }

    /// <summary>Redraws the list after a row's group, name or value changed, keeping the selection.</summary>
    public void Refresh()
    {
        if (CurrentGroup == OtherGroup && !rows.Any(r => r.Group == OtherGroup)) CurrentGroup = groupButtons.Keys.First();
        foreach (var (group, button) in groupButtons)
        {
            button.IsVisible = group != OtherGroup || rows.Any(r => r.Group == OtherGroup);
            button.Classes.Set("accent", group == CurrentGroup);
        }
        var visible = rows.Where(r => r.Group == CurrentGroup).ToList();
        if (selected is null || !visible.Contains(selected)) selected = visible.FirstOrDefault();
        grid.Height = HeaderHeight + Math.Min(visible.Count, MaxVisibleRows) * RowHeight;
        updating = true;
        ToolGrid.SetItems(grid, visible);
        grid.SelectedItem = selected;
        updating = false;
        ShowDetail();
    }

    void ShowDetail()
    {
        if (!ReferenceEquals(detailHost.Child, selected?.Detail)) detailHost.Child = selected?.Detail;
        title.Text = selected?.Name ?? "";
        detailCard.IsVisible = selected is not null;
        remove.IsEnabled = selected?.CanRemove == true;
    }

    /// <summary>A labelled line of a row's form; hide it to hide both label and control.</summary>
    public static Grid FormRow(string label, Control control)
    {
        // The label column is shared by the whole form: as wide as its longest label in the current language.
        var line = new Grid { MinHeight = 42 };
        line.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "RelationLabel" });
        line.ColumnDefinitions.Add(new ColumnDefinition(InputWidth + 12, GridUnitType.Pixel));
        var name = Loc.BindText(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.Brush("#CBD5E1"),
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(12, 0, 24, 0),
        }, label);
        bool input = control is TextBox or ComboBox or Button;
        control.Width = input ? InputWidth : double.NaN;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0, 5, 12, 5);
        Grid.SetColumn(control, 1);
        line.Children.Add(name);
        line.Children.Add(control);
        return line;
    }

    public static StackPanel Form(params Control[] lines)
    {
        var form = new StackPanel();
        Grid.SetIsSharedSizeScope(form, true);
        for (int i = 0; i < lines.Length; i++)
        {
            var row = new Border
            {
                BorderBrush = UiKit.BorderSubtle,
                BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                Child = lines[i],
                IsVisible = lines[i].IsVisible,
            };
            // A hidden line takes its divider with it.
            var line = lines[i];
            line.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) row.IsVisible = line.IsVisible; };
            form.Children.Add(row);
        }
        return form;
    }

    /// <summary>The form's dropdown: fixed width for the longest label, capped height.</summary>
    public static ComboBox Choice(IEnumerable<string>? items, int selectedIndex) => new()
    {
        MaxDropDownHeight = 260,
        ItemsSource = items?.ToArray(),
        SelectedIndex = selectedIndex,
        PlaceholderText = Loc.S("Choose…"),
    };

    public static TextBox Number(string text) => new() { Text = text, HorizontalContentAlignment = HorizontalAlignment.Right };
}
