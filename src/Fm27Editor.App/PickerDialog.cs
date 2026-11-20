using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Fm27Editor.App;

/// <summary>
/// The search-and-pick box FMRTE shows for "Change Owner", "Change Stadium Location" and the like: type part of a name, pick a line,
/// OK. <paramref name="search"/> turns the typed text into at most a few hundred lines.
/// </summary>
internal static class PickerDialog
{
    public static Task<T?> Show<T>(Window owner, string title, Func<string, IReadOnlyList<(string Text, T Value)>> search,
        string watermark = "Type the name to search", bool showAllInitially = false) where T : class =>
        ShowCore(owner, title, (query, _) => search(query), watermark, showAllInitially);

    public static Task<T?> ShowGrouped<T>(Window owner, string title, IReadOnlyList<string> groups,
        Func<string, int, IReadOnlyList<(string Text, T Value)>> search, string watermark, string acceptLabel) where T : class =>
        ShowCore(owner, title, search, watermark, showAllInitially: true, groups, acceptLabel);

    static async Task<T?> ShowCore<T>(Window owner, string title, Func<string, int, IReadOnlyList<(string Text, T Value)>> search,
        string watermark, bool showAllInitially, IReadOnlyList<string>? groups = null, string acceptLabel = "OK") where T : class
    {
        T? result = null;
        var input = new TextBox { Watermark = Loc.S(watermark), Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListBox { MinHeight = 260, SelectionMode = SelectionMode.Single };
        var ok = new Button { Content = Loc.S(acceptLabel), IsEnabled = false, IsDefault = true, Classes = { "accent" }, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = Loc.S("Cancel"), IsCancel = true, Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var group = groups is null ? null : new ComboBox
        {
            ItemsSource = groups, PlaceholderText = groups[0], Width = 248, MaxDropDownHeight = 260, Margin = new Thickness(0, 0, 0, 8),
        };
        var empty = new TextBlock
        {
            Text = Loc.S("No matching results"), Foreground = UiKit.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        IReadOnlyList<(string Text, T Value)> shown = [];
        void Refresh()
        {
            string query = input.Text?.Trim() ?? "";
            shown = query.Length == 0 && !showAllInitially ? [] : search(query, Math.Max(0, group?.SelectedIndex ?? 0));
            list.SelectedIndex = -1;
            list.ItemsSource = shown.Select(x => x.Text).ToArray();
            empty.IsVisible = showAllInitially && shown.Count == 0;
            ok.IsEnabled = false;
        }
        input.TextChanged += (_, _) => Refresh();
        if (group is not null) group.SelectionChanged += (_, _) => Refresh();
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedIndex >= 0;
        if (showAllInitially) Refresh();
        var window = new Window
        {
            Title = Loc.S(title), Width = group is null ? 480 : 520, Height = 460, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    Dock(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, ok } }, Avalonia.Controls.Dock.Bottom),
                    Dock<Control>(group is null ? input : new StackPanel { Children = { input, group } }, Avalonia.Controls.Dock.Top),
                    new Grid { Children = { list, empty } },
                },
            },
        };
        // The search box takes the keyboard as soon as the picker opens, so typing needs no extra click.
        window.Opened += (_, _) => input.Focus();
        void Accept()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) return;
            result = shown[list.SelectedIndex].Value;
            window.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => window.Close();
        list.DoubleTapped += (_, e) => { if (UiKit.OnRow(e)) Accept(); };
        await window.ShowDialog(owner);
        return result;
    }

    static T Dock<T>(T control, Dock side) where T : Control { DockPanel.SetDock(control, side); return control; }
}
