using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fm27Editor.App;

/// <summary>
/// The review step of a mass edit: one table per scope that has changes (Player, Coach, Club, Nation). Each table is visited and can be
/// applied on its own or discarded; Apply all writes every remaining table once each has been visited. A line can be unticked to leave
/// that person out. Nothing reaches the game before an Apply.
/// </summary>
internal sealed class MassEditReviewWindow : Window
{
    const int LargeChange = 5000;

    sealed class Page
    {
        public required MassPlan Plan;
        public required Button Tab;
        public required Control Body;
        public required TextBlock Summary;
        public required CheckBox Confirm;
        public required Button Apply;
        public bool Visited;
    }

    readonly List<Page> pages = [];
    // The app's own tab look (text buttons with the 2px accent underline, as Mass Edit's Player / Coach / Club / Nation),
    // never the system TabControl.
    readonly StackPanel tabBar = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(12, 8, 12, 0) };
    readonly ContentControl tabBody = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    Page? selected;
    readonly Button applyAll = new() { Content = Loc.S("Apply all"), Classes = { "accent" }, MinWidth = 100 };
    readonly TextBlock status = new() { Foreground = UiKit.TextMuted, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    readonly Func<IReadOnlyList<MassPlan>, IProgress<(int Done, int Total)>?, CancellationToken, Task<int>> write;
    readonly ProgressBar progressBar = new() { Width = 160, Height = 6, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly Button cancelApply = new() { Content = Loc.S("Cancel"), MinWidth = 70, IsVisible = false };
    CancellationTokenSource? applyCts;

    /// <param name="write">Applies the given plans in one history group, reloads their screens and returns how many values it wrote.</param>
    public MassEditReviewWindow(IReadOnlyList<MassPlan> plans, Func<IReadOnlyList<MassPlan>, IProgress<(int Done, int Total)>?, CancellationToken, Task<int>> write)
    {
        this.write = write;
        Title = Loc.S("Review mass edit");
        Width = 940; Height = 640; MinWidth = 760; MinHeight = 480;
        Background = UiKit.WindowBg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var plan in plans) pages.Add(BuildPage(plan));
        foreach (var page in pages) tabBar.Children.Add(page.Tab);
        Select(pages.FirstOrDefault());
        applyAll.Click += async (_, _) => await ApplyAsync(pages.ToList());
        var close = new Button { Content = Loc.S("Close"), MinWidth = 80 };
        close.Click += (_, _) => Close();
        cancelApply.Click += (_, _) => applyCts?.Cancel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { progressBar, cancelApply, close, applyAll } };
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(12, 6, 12, 10), Children = { buttons, status } };
        var root = new DockPanel { Background = UiKit.WindowBg };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(tabBar, Dock.Top);
        root.Children.Add(bottom); root.Children.Add(tabBar); root.Children.Add(tabBody);
        Content = root;
        Closing += (_, e) => { if (applyCts is not null) { applyCts.Cancel(); e.Cancel = true; } };
        Opened += (_, _) => { Visit(); Refresh(); };
    }

    static string ScopeNoun(string scope) => Loc.S(scope switch { "Player" => "players", "Coach" => "coaches", "Staff" => "staff", "Club" => "clubs", _ => "nations" });

    Page BuildPage(MassPlan plan)
    {
        var noun = ScopeNoun(plan.Scope);
        var summary = new TextBlock { Text = plan.Summary(noun), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        var confirm = new CheckBox { Content = Loc.F("I have checked this table ({0:N0} changes)", plan.Selected), IsVisible = plan.Selected > LargeChange };
        var apply = new Button { Content = Loc.F("Apply {0}", Loc.S(plan.Scope)), Classes = { "accent" } };
        var discard = new Button { Content = Loc.F("Discard {0}", Loc.S(plan.Scope)) };
        var grid = ToolGrid.Create();
        grid.ItemsSource = plan.Rows; grid.IsReadOnly = false; grid.CanUserSortColumns = false;
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "", Binding = new Binding(nameof(MassRow.Include)), Width = new DataGridLength(48) });
        grid.Columns.Add(ToolGrid.Text<MassRow>("Name", r => r.Name, 0, "Name", minWidth: 160));
        grid.Columns.Add(ToolGrid.Text<MassRow>("Field", r => r.FieldDisplay, 180, "Field"));
        grid.Columns.Add(ToolGrid.Text<MassRow>("Before", r => r.Before, 110, "Before"));
        grid.Columns.Add(ToolGrid.Text<MassRow>("After", r => r.After, 160, "After"));
        grid.Columns.Add(ToolGrid.Text<MassRow>("", r => r.Note, 220, ""));
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8), Children = { summary, confirm } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 6), Children = { discard, apply } };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(actions, Dock.Bottom);
        var body = new DockPanel { Children = { top, actions, grid } };
        var page = new Page { Plan = plan, Summary = summary, Confirm = confirm, Apply = apply, Body = body, Tab = new Button
        {
            Content = $"{Loc.S(plan.Scope)} ({plan.Rows.Count:N0})", FontSize = 13, Padding = new Thickness(11, 3), MinHeight = 25, Height = 25,
            CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent, Foreground = UiKit.TextMuted,
        } };
        page.Body.IsEnabled = applyCts is null;
        page.Tab.Click += (_, _) => Select(page);
        grid.CellEditEnded += (_, _) => Refresh();
        confirm.IsCheckedChanged += (_, _) => Refresh();
        apply.Click += async (_, _) => await ApplyAsync([page]);
        discard.Click += (_, _) => Remove(page);
        return page;
    }

    Page? Current => selected;

    void Select(Page? page)
    {
        selected = page;
        tabBody.Content = page?.Body;
        foreach (var p in pages)
        {
            bool active = ReferenceEquals(p, page);
            p.Tab.Foreground = active ? UiKit.TextPrimary : UiKit.TextMuted;
            p.Tab.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
            p.Tab.Background = active ? UiKit.Brush("#201B38") : Brushes.Transparent;
            p.Tab.BorderBrush = active ? UiKit.Accent : Brushes.Transparent;
        }
        Visit();
        Refresh();
    }

    void Visit() { if (Current is { } page) page.Visited = true; }

    bool Ready(Page page) => applyCts is null && page.Visited && (!page.Confirm.IsVisible || page.Confirm.IsChecked == true) && page.Plan.Selected > 0;

    void Refresh()
    {
        foreach (var page in pages)
        {
            page.Body.IsEnabled = applyCts is null;
            page.Tab.IsEnabled = applyCts is null;
            page.Summary.Text = page.Plan.Summary(ScopeNoun(page.Plan.Scope));
            page.Apply.IsEnabled = Ready(page);
        }
        applyAll.IsEnabled = pages.Count > 0 && pages.All(Ready);
        var waiting = pages.Count(p => !p.Visited);
        if (pages.Count == 0) Close();
        else if (waiting > 0) status.Text = Loc.F("{0} table(s) not opened yet", waiting);
    }

    void Remove(Page page)
    {
        if (applyCts is not null) return;
        pages.Remove(page);
        tabBar.Children.Remove(page.Tab);
        if (ReferenceEquals(selected, page)) Select(pages.FirstOrDefault());
        Refresh();
    }

    async Task ApplyAsync(IReadOnlyList<Page> chosen)
    {
        if (applyCts is not null || chosen.Count == 0 || chosen.Any(p => !Ready(p))) return;
        using var cts = new CancellationTokenSource();
        applyCts = cts;
        Refresh();
        progressBar.Value = 0;
        progressBar.IsVisible = true;
        cancelApply.IsVisible = true;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            progressBar.Maximum = Math.Max(1, p.Total);
            progressBar.Value = p.Done;
            status.Text = $"{p.Done:N0} / {p.Total:N0}";
        });
        try
        {
            int written = await write(chosen.Select(p => p.Plan).ToList(), progress, cts.Token);
            status.Text = cts.IsCancellationRequested
                ? Loc.F("Cancelled: {0:N0} value(s) written. Continue in FM to see updates.", written)
                : Loc.F("{0:N0} value(s) written. Continue in FM to see updates.", written);
            foreach (var page in chosen) { pages.Remove(page); tabBar.Children.Remove(page.Tab); }
            if (selected is null || !pages.Contains(selected)) Select(pages.FirstOrDefault());
            // Done when nothing applicable is left (tables holding only refused lines do not keep the window open).
            if (!cts.IsCancellationRequested && pages.All(p => p.Plan.Selected == 0)) { applyCts = null; Close(); return; }
            Visit();
            Refresh();
        }
        catch (Exception ex)
        {
            status.Text = UserMessage.For(ex);
            Refresh();
        }
        finally
        {
            progressBar.IsVisible = false;
            cancelApply.IsVisible = false;
            applyCts = null;
            Refresh();
        }
    }
}
