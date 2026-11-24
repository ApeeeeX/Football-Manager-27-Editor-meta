using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Tests;

namespace Fm27Editor.App.Tests;

/// <summary>
/// Independent oracle: clamp ranges from FM27 Steam/Game Pass allowance clamps (0..10 Coaches/Physios,
/// 0..5 Sports Scientists/Performance Analysts/Recruitment Analysts, 0..15 Scouts) and captured
/// 21-byte Mscc+0x116C..0x1180 bytes (<c>030000040000000000000000000000FFFFFF000500</c>) from Man Utd.
/// </summary>
public class ClubAdditionalStaffCardTests
{
    const ulong Base = TestWorld.ModuleBase;
    const ulong ClubAddr = 0x2765_1736_CD00;
    const ulong TeamsVecAddr = 0x2765_1737_0000;
    const ulong FirstTeamAddr = 0x2765_706D_5C00;
    const ulong HumanStaffAddr = 0x2766_4AB7_3700;
    const ulong MsccAddr = 0x2767_0847_F600;

    static readonly string Steam2710Path = Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json");
    static readonly byte[] CapturedPatchedBlock = Convert.FromHexString("030000040000000000000000000000FFFFFF000500");

    static StaffAllowanceSnapshot ReadCapturedSnapshot()
    {
        var profile = OffsetProfile.LoadFile(Steam2710Path);
        var memory = new FakeMemory();
        uint clubVt = profile.Club!.ClubVtableRva;
        uint humanStaffVt = profile.Staff!.Kinds.First(k => k.Manager).StaffVtableRva;

        var club = memory.Map(ClubAddr, 0x40);
        TestWorld.Set64(club, 0x00, Base + clubVt);
        TestWorld.Set64(club, 0x18, TeamsVecAddr);
        TestWorld.Set64(club, 0x20, TeamsVecAddr + 8);

        var teamsVec = memory.Map(TeamsVecAddr, 8);
        TestWorld.Set64(teamsVec, 0x00, FirstTeamAddr);

        var team = memory.Map(FirstTeamAddr, 0x100);
        TestWorld.Set64(team, 0x80, HumanStaffAddr);

        var human = memory.Map(HumanStaffAddr, 0x440);
        TestWorld.Set64(human, 0x00, Base + humanStaffVt);
        TestWorld.Set64(human, 0x388, MsccAddr);

        var mscc = memory.Map(MsccAddr, 0x1200);
        CapturedPatchedBlock.CopyTo(mscc, 0x116C);

        return new StaffAllowanceAccess(memory, profile, Base).ReadForClub(ClubAddr)!;
    }

    [Theory]
    [InlineData(-5, 10, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(3.4, 10, 3)]
    [InlineData(3.6, 10, 4)]
    [InlineData(10, 10, 10)]
    [InlineData(99, 10, 10)]
    [InlineData(6, 5, 5)]
    [InlineData(999, 15, 15)]
    public void ClampValue_enforces_valid_inclusive_bounds(double raw, int max, int expected)
    {
        Assert.Equal(expected, ClubAdditionalStaffCard.ClampValue((decimal)raw, max));
    }

    [Fact]
    public void Build_without_human_snapshot_renders_unavailable_card()
    {
        AvaloniaTestThread.Run(() =>
        {
            var card = ClubAdditionalStaffCard.Build(
                null,
                _ => null,
                (_, _) => { },
                (_, _) => { },
                () => false);

            Assert.Empty(card.GetLogicalDescendants().OfType<NumericUpDown>());
            var msg = card.GetLogicalDescendants().OfType<TextBlock>().Last();
            Assert.False(string.IsNullOrWhiteSpace(msg.Text));
        });
    }

    [Fact]
    public void Build_clamps_live_out_of_range_edits_and_updates_staged_values()
    {
        var snapshot = ReadCapturedSnapshot();

        AvaloniaTestThread.Run(() =>
        {
            var staged = new Dictionary<StaffAllowanceField, int>();
            var invalid = new HashSet<StaffAllowanceField>();

            var card = ClubAdditionalStaffCard.Build(
                snapshot,
                f => staged.TryGetValue(f, out int v) ? v : null,
                (f, v) =>
                {
                    if (v is { } val) staged[f] = val;
                    else staged.Remove(f);
                },
                (f, isInvalid) =>
                {
                    if (isInvalid) invalid.Add(f);
                    else invalid.Remove(f);
                },
                () => false);

            var inputs = card.GetLogicalDescendants()
                .OfType<NumericUpDown>()
                .ToDictionary(n => Assert.IsType<StaffAllowanceField>(n.Tag!));

            Assert.Equal(14, inputs.Count);
            Assert.Equal(3m, inputs[StaffAllowanceField.CoachesFirstTeam].Value);
            Assert.Equal(4m, inputs[StaffAllowanceField.PhysiosFirstTeam].Value);
            Assert.Equal(5m, inputs[StaffAllowanceField.Scouts].Value);

            // Exceeding max via Value is clamped to 10 (Coaches max) and staged.
            var coaches = inputs[StaffAllowanceField.CoachesFirstTeam];
            coaches.Value = 99m;
            Assert.Equal(10m, coaches.Value);
            Assert.Equal(10, staged[StaffAllowanceField.CoachesFirstTeam]);
            Assert.Empty(invalid);

            // Resetting back to snapshot baseline (3) unstages the pending change.
            coaches.Value = 3m;
            Assert.False(staged.ContainsKey(StaffAllowanceField.CoachesFirstTeam));

            // Out-of-range text on LostFocus for Sports Scientists (max 5) clamps to 5.
            var sci = inputs[StaffAllowanceField.SportsScientistsU21];
            sci.Text = "999";
            sci.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Assert.Equal(5m, sci.Value);
            Assert.Equal("5", sci.Text);
            Assert.Equal(5, staged[StaffAllowanceField.SportsScientistsU21]);
            Assert.Empty(invalid);

            // Negative value clamps to 0; since Scouts baseline is 5, 0 stays staged.
            var scouts = inputs[StaffAllowanceField.Scouts];
            scouts.Value = -4m;
            Assert.Equal(0m, scouts.Value);
            Assert.Equal(0, staged[StaffAllowanceField.Scouts]);
        });
    }

    [Fact]
    public void Build_stages_typed_text_immediately_before_lost_focus_and_marks_empty_text_invalid()
    {
        var snapshot = ReadCapturedSnapshot();

        AvaloniaTestThread.Run(() =>
        {
            var staged = new Dictionary<StaffAllowanceField, int>();
            var invalid = new HashSet<StaffAllowanceField>();

            var card = ClubAdditionalStaffCard.Build(
                snapshot,
                f => staged.TryGetValue(f, out int v) ? v : null,
                (f, v) =>
                {
                    if (v is { } val) staged[f] = val;
                    else staged.Remove(f);
                },
                (f, isInvalid) =>
                {
                    if (isInvalid) invalid.Add(f);
                    else invalid.Remove(f);
                },
                () => false);

            var inputs = card.GetLogicalDescendants()
                .OfType<NumericUpDown>()
                .ToDictionary(n => Assert.IsType<StaffAllowanceField>(n.Tag!));

            var coaches = inputs[StaffAllowanceField.CoachesFirstTeam];
            coaches.Text = "7";
            Assert.Equal(7, staged[StaffAllowanceField.CoachesFirstTeam]);
            Assert.Empty(invalid);

            coaches.Text = "";
            Assert.Contains(StaffAllowanceField.CoachesFirstTeam, invalid);

            coaches.Text = "3";
            Assert.Empty(invalid);
            Assert.False(staged.ContainsKey(StaffAllowanceField.CoachesFirstTeam));
        });
    }

    [Theory]
    [InlineData(333)]
    [InlineData(433)]
    [InlineData(843)]
    public void Card_fits_cleanly_across_small_default_and_fullscreen_column_widths(double columnWidth)
    {
        var snapshot = ReadCapturedSnapshot();

        AvaloniaTestThread.Run(() =>
        {
            var card = ClubAdditionalStaffCard.Build(
                snapshot,
                _ => null,
                (_, _) => { },
                (_, _) => { },
                () => false);

            var host = new Border
            {
                Width = columnWidth,
                Child = card,
            };

            host.Measure(new Size(columnWidth, 2000));
            host.Arrange(new Rect(0, 0, columnWidth, host.DesiredSize.Height));

            Assert.True(card.DesiredSize.Width <= columnWidth + 0.5,
                $"Card desired width {card.DesiredSize.Width} exceeded column {columnWidth}");
            Assert.True(card.Bounds.Width <= columnWidth + 0.5,
                $"Card arranged width {card.Bounds.Width} exceeded column {columnWidth}");

            var rowGrids = card.GetLogicalDescendants()
                .OfType<Grid>()
                .Where(g => g.Children.OfType<NumericUpDown>().Any())
                .ToList();
            Assert.Equal(ClubAdditionalStaffCard.Rows.Count, rowGrids.Count);

            foreach (var rowGrid in rowGrids)
            {
                var label = Assert.Single(rowGrid.Children.OfType<TextBlock>());
                var numerics = rowGrid.Children.OfType<NumericUpDown>().ToList();
                Assert.NotEmpty(numerics);

                Assert.True(label.Bounds.Width >= 80,
                    $"Label '{label.Text}' width {label.Bounds.Width} too narrow at {columnWidth}px");
                Assert.True(label.Bounds.Height <= rowGrid.Bounds.Height + 0.5,
                    $"Label '{label.Text}' height {label.Bounds.Height} clipped in row {rowGrid.Bounds.Height}");

                double leftmostNumeric = numerics.Min(n => n.Bounds.Left);
                Assert.True(label.Bounds.Right <= leftmostNumeric + 0.5,
                    $"Label '{label.Text}' right {label.Bounds.Right} overlaps numeric column at {leftmostNumeric}");

                foreach (var nud in numerics)
                {
                    Assert.True(nud.Bounds.Width >= 50,
                        $"NumericUpDown {nud.Tag} shrunk to {nud.Bounds.Width}px at {columnWidth}px");
                    Assert.True(nud.Bounds.Right <= rowGrid.Bounds.Width + 0.5,
                        $"NumericUpDown {nud.Tag} overflowed row width {rowGrid.Bounds.Width}px");
                }
            }
        });
    }
}
