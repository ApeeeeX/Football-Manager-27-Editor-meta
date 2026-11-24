using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App.Tests;

public sealed class BulkPlayerActionsTests
{
    sealed class FakeEditService : IEditService
    {
        int writes;
        public List<(string Kind, string Entity, string Title)> Groups { get; } = [];
        public IReadOnlyList<EditRecord> History => [];
        public long WriteCount => writes;
        public PlayerRecord Apply(PlayerRecord player, FieldDescriptor<PlayerRecord> field, int value) => player;
        public PlayerRecord Undo(PlayerRecord player, FieldDescriptor<PlayerRecord> field, EditRecord record) => player;
        public void Execute(Action action) => action();
        public void RecordWrite() => writes++;

        public IDisposable Group(string kind, string entity, string title, Func<ulong, int, ChangeLabel?>? labeler = null)
        {
            Groups.Add((kind, entity, title));
            return new GroupScope();
        }

        sealed class GroupScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    static PlayerRecord Player(ulong address, string name) => new()
    {
        Address = address,
        VtableRva = 0x4CD6318,
        CommonName = name,
        CurrentAbility = 120,
        PotentialAbility = 140,
        HomeReputation = 4000,
        CurrentReputation = 4000,
        WorldReputation = 4000,
        Height = 180,
        RawAttributes = new byte[50],
        AttributeScale = 5,
        Positions = new byte[20],
        Personality = new byte[10],
    };

    [Fact]
    public void RunBulk_deduplicates_targets_groups_edits_and_collects_failures()
    {
        var edits = new FakeEditService();
        var p1 = Player(0x1000, "Player One");
        var p1Dup = Player(0x1000, "Player One Dup");
        var p2 = Player(0x2000, "Player Two");
        var p3 = Player(0x3000, "Player Three");
        int labelCalls = 0;

        var report = MainWindow.RunBulk(edits, [p1, p2, p1Dup, p3], "Test Action", p =>
        {
            if (p.Address == 0x2000) throw new InvalidOperationException("boom");
            return p.Address == 0x1000;
        }, _ => labelCalls++);

        Assert.Equal(1, report.Changed);
        Assert.Equal(1, report.Unchanged);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("Player Two", failure.Player);
        Assert.Equal("boom", failure.Error);
        Assert.Equal(3, labelCalls);
        Assert.Equal(4, edits.Groups.Count);
        Assert.Equal(("Players", "Bulk Edit", "Test Action"), edits.Groups[0]);
        Assert.Equal(["Player One", "Player Two", "Player Three"], edits.Groups.Skip(1).Select(g => g.Entity));
    }

    [Fact]
    public void RunBulkVoid_tracks_write_count_changes()
    {
        var edits = new FakeEditService();
        var p1 = Player(0x1000, "Changed");
        var p2 = Player(0x2000, "Unchanged");

        var report = MainWindow.RunBulkVoid(edits, [p1, p2], "Heal", p =>
        {
            if (p.Address == 0x1000) edits.RecordWrite();
        });

        Assert.Equal(1, report.Changed);
        Assert.Equal(1, report.Unchanged);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void FormatStatus_covers_all_result_shapes()
    {
        Assert.Equal("Nothing changed (3 checked)", new BulkActionReport(0, 3, []).FormatStatus("Freeze"));
        Assert.Equal("Freeze: applied to 2 players", new BulkActionReport(2, 0, []).FormatStatus("Freeze"));
        Assert.Equal("Freeze: applied to 2, 1 unchanged", new BulkActionReport(2, 1, []).FormatStatus("Freeze"));
        var failures = new BulkActionReport(1, 0, [
            ("A", "e1"), ("B", "e2"), ("C", "e3"), ("D", "e4"),
        ]).FormatStatus("Heal");
        Assert.Equal("Heal: 1 ok, 4 failed (A: e1; B: e2; C: e3, +1)", failures);
    }

    [Fact]
    public void SelectedPlayersFrom_unwraps_multiselect_and_falls_back_to_single_selection()
    {
        AvaloniaTestThread.Run(() =>
        {
            var p1 = Player(0x1000, "First");
            var p2 = Player(0x2000, "Second");
            var r1 = new RetiringPlayer(p1, new DateOnly(2027, 6, 30), RetirementReason.PlannedRetirement);
            var r1Dup = new RetiringPlayer(p1, new DateOnly(2027, 6, 30), RetirementReason.PlannedRetirement);
            var r2 = new RetiringPlayer(p2, null, RetirementReason.WillRetireAtTop);

            var grid = new DataGrid { ItemsSource = new[] { r1, r1Dup, r2 }, SelectionMode = DataGridSelectionMode.Extended };
            grid.SelectedItems.Add(r1);
            grid.SelectedItems.Add(r1Dup);
            grid.SelectedItems.Add(r2);

            var multi = MainWindow.SelectedPlayersFrom(grid, item => (item as RetiringPlayer)?.Player);
            Assert.Equal([0x1000UL, 0x2000UL], multi.Select(p => p.Address));

            grid.SelectedItems.Clear();
            grid.SelectedItem = r2;
            var fallback = MainWindow.SelectedPlayersFrom(grid, item => (item as RetiringPlayer)?.Player);
            Assert.Equal([0x2000UL], fallback.Select(p => p.Address));
        });
    }

    [Fact]
    public void Tick_header_tracks_teams_selected_by_buttons_and_toggles_all_rows()
    {
        AvaloniaTestThread.Run(() =>
        {
            PlayerRecord[] rows = [Player(0x1000, "Home"), Player(0x2000, "Away")];
            var grid = ToolGrid.Create();
            var excluded = rows.Select(row => row.Address).ToHashSet();
            var sync = ToolGrid.AddTick(grid, excluded, (PlayerRecord row) => row.Address, () => rows, () => { });
            var header = Assert.IsType<CheckBox>(grid.Columns[0].Header);

            sync();
            Assert.False(header.IsChecked);
            excluded.Remove(rows[0].Address);
            sync();
            Assert.Null(header.IsChecked);
            Assert.Equal(new[] { rows[1].Address }, excluded);
            header.IsChecked = true;
            Assert.Empty(excluded);
            header.IsChecked = false;
            Assert.Equal(rows.Select(row => row.Address), excluded.Order());
        });
    }

    [Fact]
    public void ToolGrid_drag_selects_range_even_when_pointer_is_captured_by_start_row_and_syncs_ticks()
    {
        AvaloniaTestThread.Run(() =>
        {
            var r0 = Player(0x1000, "Row 0");
            var r1 = Player(0x2000, "Row 1");
            var r2 = Player(0x3000, "Row 2");
            var r3 = Player(0x4000, "Row 3");
            PlayerRecord[] rows = [r0, r1, r2, r3];

            var grid = ToolGrid.Create();
            var excluded = new HashSet<ulong>();
            int tickChanges = 0;
            ToolGrid.AddTick<PlayerRecord>(grid, excluded, r => r.Address, () => rows, () => tickChanges++);
            grid.Columns.Add(ToolGrid.Text<PlayerRecord>("Name", r => r.DisplayName, 160, "Name"));
            ToolGrid.SetItems(grid, rows);

            var window = new Window { Width = 400, Height = 300, Content = grid };
            window.Show();
            try
            {
                AvaloniaTestThread.RunJobs();
                var realized = grid.GetVisualDescendants()
                    .OfType<DataGridRow>()
                    .Where(r => r.IsVisible && r.DataContext is PlayerRecord)
                    .OrderBy(r => r.Index)
                    .ToList();
                Assert.True(realized.Count >= 4);

                Point CenterInWindow(DataGridRow r) => r.TranslatePoint(new Point(120, r.Bounds.Height / 2), window)!.Value;
                var p0 = CenterInWindow(realized[0]);
                var p1 = CenterInWindow(realized[1]);
                var p2 = CenterInWindow(realized[2]);

                var startVisual = realized[0].GetVisualDescendants().OfType<TextBlock>().First();
                var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var downProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
                var moveProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
                var upProps = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);

                // Keep e.Source stuck on row 0 (simulating pointer capture) while moving across rows 1 and 2.
                startVisual.RaiseEvent(new PointerPressedEventArgs(startVisual, pointer, window, p0, 1, downProps, KeyModifiers.None, 1));
                startVisual.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, startVisual, pointer, window, p1, 2, moveProps, KeyModifiers.None));
                startVisual.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, startVisual, pointer, window, p2, 3, moveProps, KeyModifiers.None));
                startVisual.RaiseEvent(new PointerReleasedEventArgs(startVisual, pointer, window, p2, 4, upProps, KeyModifiers.None, MouseButton.Left));

                Assert.Equal([0x1000UL, 0x2000UL, 0x3000UL], grid.SelectedItems.OfType<PlayerRecord>().Select(p => p.Address));
                Assert.True(tickChanges > 0);
                Assert.DoesNotContain(0x1000UL, excluded);
                Assert.DoesNotContain(0x2000UL, excluded);
                Assert.DoesNotContain(0x3000UL, excluded);
                Assert.Contains(0x4000UL, excluded);

                // Dragging back up to row 1 shrinks the selected span to [r0, r1].
                startVisual.RaiseEvent(new PointerPressedEventArgs(startVisual, pointer, window, p0, 5, downProps, KeyModifiers.None, 1));
                startVisual.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, startVisual, pointer, window, p2, 6, moveProps, KeyModifiers.None));
                startVisual.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, startVisual, pointer, window, p1, 7, moveProps, KeyModifiers.None));
                startVisual.RaiseEvent(new PointerReleasedEventArgs(startVisual, pointer, window, p1, 8, upProps, KeyModifiers.None, MouseButton.Left));
                Assert.Equal([0x1000UL, 0x2000UL], grid.SelectedItems.OfType<PlayerRecord>().Select(p => p.Address));

                if (!OperatingSystem.IsWindows())
                {
                    // End-to-end headless input through Avalonia's real pointer-capture pipeline.
                    window.MouseDown(p0, MouseButton.Left, RawInputModifiers.None);
                    window.MouseMove(p1, RawInputModifiers.LeftMouseButton);
                    window.MouseMove(p2, RawInputModifiers.LeftMouseButton);
                    window.MouseUp(p2, MouseButton.Left, RawInputModifiers.None);
                    Assert.Equal([0x1000UL, 0x2000UL, 0x3000UL], grid.SelectedItems.OfType<PlayerRecord>().Select(p => p.Address));
                }
            }
            finally { window.Close(); }
        });
    }
}
