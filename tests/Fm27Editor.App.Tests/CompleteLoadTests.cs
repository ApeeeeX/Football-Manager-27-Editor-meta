using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fm27Editor.App;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;
using BindingFlags = System.Reflection.BindingFlags;

namespace Fm27Editor.App.Tests;

public sealed class CompleteLoadTests
{
    sealed record Item(ulong Address, string Name, int Id = 0);

    sealed class Screen(IScreenHost host, Func<IReadOnlyList<Item>> source, Action? publish = null)
        : EntityScreen<Item, Item, object>(host, "test", "Test", "items", "Search")
    {
        protected override void ConfigureColumns(DataGrid grid) { }
        protected override void RowsLoaded() => publish?.Invoke();
        protected override IReadOnlyList<Item> LoadRows(GameSession session, LoadedGameData? data) => source();
        protected override bool Matches(Item row, string query) => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        protected override Item RecordOf(Item row) => row;
        protected override ulong AddressOf(Item item) => item.Address;
        protected override EntityTabIdentity IdentityOf(Item item) => EntityTabIdentity.Database(item.Id);
        protected override string NameOf(Item item) => item.Name;
        protected override object NewDraft() => new();
        protected override bool HasChanges(Item item, object draft) => false;
        protected override void BuildEditor(Item item, object draft, Action changed) => Editor.Children.Add(new TextBlock { Text = item.Name });
        protected override string? Validate(Item item, object draft, out Func<int> write) { write = () => 0; return null; }
        protected override Item? Reload(Item item) => item;
        protected override Item Refresh(Item row, Item fresh) => fresh;
    }

    sealed class Host : IScreenHost
    {
        public EditorBackend Backend { get; } = new();
        public GraphicsCatalog Graphics { get; } = new();
        public void SetStatus(string text) { }
        public void SetUnsaved(bool dirty) { }
        public void AddModule(string title, Control root, object navItem) { }
        public object? SelectedNav => null;
        public event EventHandler? NavChanged { add { } remove { } }
        public bool IsShowing(Control body) => false;
        public void OpenTab(string kind, EntityTabIdentity identity, EntityTabContent content) { }
        public void SwitchModule(string title) { }
        public IReadOnlyList<NationRecord> NationCatalogue => [];
        public Task OpenNationAsync(ulong address) => Task.CompletedTask;
        public Task OpenClubAsync(ulong address) => Task.CompletedTask;
        public void OpenCity(ulong address) { }
        public void OpenStadium(ulong address) { }
        public void OpenStaff(StaffRecord staff) { }
        public Window Window => throw new NotSupportedException();
        public Control BuildStaffTable(IReadOnlyList<StaffListRow> rows, IStaffContractScope scope, Action refresh) => new Control();
        public Control BuildKits(ulong owner, IReadOnlyList<ClubKitItem> items, Action changed) => new Control();
        public IReadOnlyDictionary<ulong, KitDraft>? KitDraftOf(ulong owner) => null;
        public void ClearKitDraft(ulong owner) { }
        public Control BuildSquad(Func<IReadOnlyList<PlayerRecord>> getSquad) => new Control();
        public IReadOnlyList<PlayerRecord> Players() => [];
        public IReadOnlyList<ClubEntry> Clubs() => [];
    }

    sealed class ClubPanels : IClubPanels
    {
        public ListBox SectionRail { get; } = new();
        public IReadOnlyList<ClubEntry>? CachedClubs => null;
        public IReadOnlyList<PlayerRecord> ShownPlayers => [];
        public List<(IReadOnlyList<ClubEntry> Clubs, DateOnly? Date)> Publications { get; } = [];
        public void ClubsLoaded(IReadOnlyList<ClubEntry> clubs, IReadOnlyList<PlayerRecord>? freshPlayers, DateOnly? date) => Publications.Add((clubs, date));
        public void Reconnected() { }
        public void BuildClub(ClubEntry club, ClubDraft draft) { }
        public bool HasPending(ClubEntry club) => false;
        public int SaveClub(ClubEntry club, ClubDraft draft) => 0;
        public void SaveFailed(ClubEntry club, Exception error) { }
        public ulong TeamOf(ClubEntry club) => 0;
        public bool IsValid(ulong address) => true;
    }

    sealed class DelayedClubScreen(IScreenHost host, IClubPanels panels, Func<IReadOnlyList<MainWindow.ClubPickerItem>> source)
        : ClubScreen(host, panels)
    {
        protected override IReadOnlyList<MainWindow.ClubPickerItem> LoadRows(GameSession session, LoadedGameData? data) => source();
    }

    // This screen's source is controlled by the test and never accesses a game session or process memory.
    static LoadedGameData Data() => new(null!);

    [Fact]
    public void Reused_generated_person_id_needs_the_same_person_in_the_new_save()
    {
        var shown = new EntityTabIdentity(2_002_040_995, "Jesse Gray", new DateOnly(2010, 4, 23), Generated: true);
        Assert.False(shown.Matches(shown with { Name = "Louie Hall" }));
        Assert.False(shown.Matches(shown with { BirthDate = new DateOnly(2011, 4, 23) }));
        Assert.True(shown.Matches(shown with { Generated = false }));
    }

    [Fact]
    public async Task Reload_keeps_tab_headers_order_and_selection_and_renders_new_records_by_id()
    {
        Item[] available = [new(0x1230, "Arsenal", 1), new(0x4560, "Barcelona", 2),
            new(0x7890, "Unavailable club", 3)];
        MainWindow app = null!;
        Screen screen = null!;
        StackPanel strip = null!;
        ContentControl body = null!;
        Task loading = null!;
        Control[] headers = [];

        AvaloniaTestThread.Run(() =>
        {
            app = new MainWindow();
            strip = TabField<StackPanel>(app, "entityStrip");
            body = TabField<ContentControl>(app, "bodyHost");
            screen = new Screen(app, () => available);
            TabField<List<IEntityScreen>>(app, "screens").Add(screen);
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        try
        {
            await loading.WaitAsync(TimeSpan.FromSeconds(10));
            AvaloniaTestThread.Run(() =>
            {
                foreach (var item in available) screen.Pick(item);
                var first = (Border)strip.Children[0];
                var second = (Border)strip.Children[1];
                // Reorder via the same move used by drag and the menu, then keep the second club open.
                TabMethod(app, "MoveEntityTab", first, 2);
                TabMethod(app, "ActivateEntityButton", second);
                headers = strip.Children.ToArray();
                TabMethod(app, "ResetLoadedGame");
                Assert.Equal(headers, strip.Children);
                Assert.Equal(second, TabField<Border>(app, "activeEntityButton"));
                Assert.Null(screen.Selected);
                Assert.IsType<TextBlock>(body.Content);

                // Reuse the old addresses for other clubs to catch a rebind by pointer.
                available = [new(0x4560, "Arsenal in the new save", 1), new(0x1230, "Barcelona in the new save", 2)];
                loading = screen.PreloadAsync(Data());
            });
            await loading.WaitAsync(TimeSpan.FromSeconds(10));
            AvaloniaTestThread.Run(() =>
            {
                TabMethod(app, "RebindEntityTabs", new GameLoadReport());
                Assert.Equal(headers, strip.Children);
                Assert.Equal(headers[0], TabField<Border>(app, "activeEntityButton"));
                Assert.Equal(available[1], screen.Selected);
                Assert.Equal("Barcelona in the new save", TitleOf(headers[0]));
                Assert.Contains(((Control)body.Content!).GetLogicalDescendants().OfType<TextBlock>(),
                    block => block.Text == "Barcelona in the new save");

                TabMethod(app, "ActivateEntityButton", headers[1]);
                Assert.Equal(headers[1], TabField<Border>(app, "activeEntityButton"));
                Assert.IsType<TextBlock>(body.Content);
                Assert.Equal(headers, strip.Children);
                TabMethod(app, "ActivateEntityButton", headers[2]);
                Assert.Equal(available[0], screen.Selected);
            });
        }
        finally { AvaloniaTestThread.Run(() => app.Close()); }
    }

    [Fact]
    public void Pointer_reorders_tabs_in_both_directions_without_activating_the_dragged_tab()
    {
        AvaloniaTestThread.Run(() =>
        {
            var app = new MainWindow();
            var strip = TabField<StackPanel>(app, "entityStrip");
            IScreenHost tabs = app;
            for (int id = 1; id <= 3; id++)
                tabs.OpenTab("test", new(id), new((ulong)id, $"Club {id}", new Panel(), () => { }));
            var original = strip.Children.ToArray();
            var shell = app.Content;
            app.Content = null;
            var window = new Window { Width = 1100, Height = 650, Content = shell };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                if (!OperatingSystem.IsWindows()) AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                var active = TabField<Border>(app, "activeEntityButton");
                Drag(original[0], original[2], toRight: true);
                Assert.Equal(new[] { original[1], original[2], original[0] }, strip.Children);
                Assert.Equal(active, TabField<Border>(app, "activeEntityButton"));
                Dispatcher.UIThread.RunJobs();
                if (!OperatingSystem.IsWindows()) AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                Drag(original[0], original[1], toRight: false);
                Assert.Equal(original, strip.Children);
                Assert.Equal(active, TabField<Border>(app, "activeEntityButton"));
            }
            finally { window.Close(); app.Close(); }

            void Drag(Control from, Control to, bool toRight)
            {
                var start = from.TranslatePoint(new Point(8, from.Bounds.Height / 2), window)!.Value;
                var end = to.TranslatePoint(new Point(to.Bounds.Width * (toRight ? .75 : .25), to.Bounds.Height / 2), window)!.Value;
                if (!OperatingSystem.IsWindows())
                {
                    window.MouseMove(start);
                    window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
                    Assert.True(TabField<object?>(app, "tabDrag") is not null,
                        $"No drag at {start}. Hit: {window.InputHitTest(start)}, source bounds {from.Bounds}, viewport {TabField<ScrollViewer>(app, "entityScroll").Viewport}");
                    window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                    window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
                    return;
                }
                var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
                var moved = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
                var released = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
                from.RaiseEvent(new PointerPressedEventArgs(from, pointer, window, start, 1, pressed, KeyModifiers.None));
                from.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, from, pointer, window, end, 2, moved, KeyModifiers.None));
                from.RaiseEvent(new PointerReleasedEventArgs(from, pointer, window, end, 3, released, KeyModifiers.None, MouseButton.Left));
            }
        });
    }

    static T TabField<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    static void TabMethod(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    static string? TitleOf(Control tab) => ((TextBlock)((StackPanel)((Border)tab).Child!).Children[0]).Text;

    [Fact]
    public async Task Inactive_empty_list_is_ready_only_after_its_read_finishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Screen screen = null!;
        Task loading = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return []; });
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        AvaloniaTestThread.Run(() => { Assert.False(screen.IsLoaded); Assert.False(loading.IsCompleted); });
        release.Set();
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() => { Assert.True(screen.IsLoaded); Assert.Empty(screen.Rows); Assert.Empty(screen.Visible); });
    }

    [Fact]
    public async Task Delayed_old_connection_rows_cannot_overwrite_the_reloaded_screen()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reads = 0;
        Screen screen = null!;
        Task oldLoad = null!, newLoad = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    return [new Item(101, "Old save")];
                }
                return [new Item(202, "New save")];
            });
            screen.Register();
            oldLoad = screen.PreloadAsync(Data());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        AvaloniaTestThread.Run(() =>
        {
            screen.ResetForConnection();
            Assert.False(screen.IsLoaded);
            newLoad = screen.PreloadAsync(Data());
        });
        await newLoad.WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            Assert.True(screen.IsLoaded);
            Assert.Equal(new Item(202, "New save"), Assert.Single(screen.Rows));
            Assert.Equal(new Item(202, "New save"), Assert.Single(screen.Visible));
        });
    }

    [Fact]
    public async Task Failed_list_read_is_not_published_as_an_empty_success()
    {
        Screen screen = null!;
        Task loading = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () => throw new IOException("Cannot read fixture"));
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        await Assert.ThrowsAsync<IOException>(() => loading);
        AvaloniaTestThread.Run(() => Assert.False(screen.IsLoaded));
    }

    [Fact]
    public async Task Publication_failure_does_not_mark_a_screen_ready()
    {
        Screen screen = null!;
        Task loading = null!;
        AvaloniaTestThread.Run(() =>
        {
            screen = new Screen(new Host(), () => [new(101, "Player")], () => throw new IOException("Cannot publish fixture"));
            screen.Register();
            loading = screen.PreloadAsync(Data());
        });
        await Assert.ThrowsAsync<IOException>(() => loading);
        AvaloniaTestThread.Run(() => Assert.False(screen.IsLoaded));
    }

    [Fact]
    public async Task Club_metadata_from_a_delayed_old_load_never_reaches_the_current_save()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var oldClub = new ClubEntry(101, 1, "Old club", "Old club", 0, []);
        var newClub = new ClubEntry(202, 2, "New club", "New club", 0, []);
        var oldDate = new DateOnly(2026, 1, 1);
        var newDate = new DateOnly(2027, 2, 2);
        ClubPanels panels = null!;
        DelayedClubScreen screen = null!;
        Task oldLoad = null!, newLoad = null!;
        int calls = 0;
        AvaloniaTestThread.Run(() =>
        {
            panels = new ClubPanels();
            screen = new DelayedClubScreen(new Host(), panels, () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    return new ClubScreen.LoadedRows([oldClub], null, oldDate) { new(oldClub) };
                }
                return new ClubScreen.LoadedRows([newClub], null, newDate) { new(newClub) };
            });
            screen.Register();
            oldLoad = screen.PreloadAsync(Data());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        AvaloniaTestThread.Run(() =>
        {
            screen.ResetForConnection();
            newLoad = screen.PreloadAsync(Data());
        });
        await newLoad.WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));
        AvaloniaTestThread.Run(() =>
        {
            var published = Assert.Single(panels.Publications);
            Assert.Equal(newDate, published.Date);
            Assert.Same(newClub, Assert.Single(published.Clubs));
            Assert.Same(newClub, Assert.Single(screen.Clubs));
        });
    }

    [Fact]
    public void A_failed_catalogue_keeps_the_full_load_incomplete_after_other_stages_succeed()
    {
        var report = new GameLoadReport();
        Assert.Empty(report.Read<Item>("staff", "Reading staff…", () => throw new IOException("Cannot read staff fixture")));
        Assert.Equal(new Item(101, "Known player"), Assert.Single(report.Read<Item>("players", "Reading players…", () => [new(101, "Known player")])));
        Assert.False(report.Succeeded);
        Assert.Contains("staff", Assert.Single(report.Errors));
    }
}
