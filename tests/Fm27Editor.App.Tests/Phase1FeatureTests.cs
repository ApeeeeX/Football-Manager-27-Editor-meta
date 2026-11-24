using System.Text;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Fm27Editor.App;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App.Tests;

public sealed class Phase1FeatureTests : IDisposable
{
    readonly int? savedFloor = RegenIds.Current;
    public Phase1FeatureTests() => RegenIds.Current = 1_000_000;
    public void Dispose() => RegenIds.Current = savedFloor;

    [Fact]
    public void Rca_preview_uses_the_valid_attribute_being_typed_before_focus_leaves_the_box()
    {
        AvaloniaTestThread.Run(() =>
        {
            var player = MakePlayer(1, 100, "Forward", new DateOnly(2000, 1, 1), 120, 160, uniformAttr20: 10);
            player.RawAttributes[(int)PlayerAttribute.LeftFoot] = 50;
            var draft = new PlayerDraft(player);
            var finishing = PlayerFields.All.Single(field => field.Id == nameof(PlayerAttribute.Finishing));
            var input = MainWindow.RatingBox(10, 1, 20, value => draft.Stage(finishing, value), updateAsTyped: true);
            Assert.Equal(79, draft.RecommendedAbility());
            input.Text = "20";
            AvaloniaTestThread.RunJobs();
            Assert.Equal(99, draft.RecommendedAbility());
            Assert.Equal(50, player.Raw(PlayerAttribute.Finishing));
            input.Text = "201";
            AvaloniaTestThread.RunJobs();
            Assert.Equal(99, draft.RecommendedAbility());
            input.Text = "10";
            AvaloniaTestThread.RunJobs();
            Assert.Equal(79, draft.RecommendedAbility());
            Assert.Equal(0, draft.Count);
        });
    }

    [Fact]
    public void Role_details_follow_selection_and_show_the_players_key_attribute_values()
    {
        AvaloniaTestThread.Run(() =>
        {
            using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
                {
                  "SettingsVersion":"test", "KeyAttributesWeight":16, "PreferableAttributesWeight":4,
                  "AvoidAttributesWeight":3, "PositionRatingPenalties":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],
                  "GeneralPositionToRoleAndDutiesDefinitionsMap":{"Striker":[
                    {"Role":"Finisher","DutiesAttributes":{"InPossession":[{"Name":"Finishing","KeyAttribute":true}]}},
                    {"Role":"Creator","DutiesAttributes":{"InPossession":[{"Name":"Passing","KeyAttribute":true},{"Name":"Vision","KeyAttribute":false}]}}
                  ]}
                }
                """));
            var attributes = new byte[64];
            attributes[(int)PlayerAttribute.Finishing] = 90;
            attributes[(int)PlayerAttribute.Passing] = 50;
            attributes[(int)PlayerAttribute.Vision] = 60;
            var view = MainWindow.BuildPhaseRoleTables(PhaseRoleRatings.Load(json), PlayerPosition.Striker, attributes, 5, 20);
            var window = new Window { Content = view, Width = 540, Height = 440 };
            window.Show();
            try
            {
                AvaloniaTestThread.RunJobs();
                var table = view.GetLogicalDescendants().OfType<DataGrid>().Single();
                Assert.Equal(2, table.Columns.Count);
                string?[] Labels() => view.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                Assert.Contains(ScoutingLabels.Attribute(PlayerAttribute.Finishing), Labels());
                Assert.Contains("18", Labels());
                table.SelectedItem = table.ItemsSource!.Cast<object>().Last();
                AvaloniaTestThread.RunJobs();
                Assert.Contains(ScoutingLabels.Attribute(PlayerAttribute.Passing), Labels());
                Assert.Contains("10", Labels());
                Assert.DoesNotContain(ScoutingLabels.Attribute(PlayerAttribute.Finishing), Labels());
                Assert.False(view.GetLogicalDescendants().OfType<Expander>().Single().IsExpanded);
            }
            finally { window.Close(); }
        });
    }
    static PlayerRecord MakePlayer(
        ulong addr,
        int id,
        string name,
        DateOnly dob,
        short ca,
        short pa,
        PlayerPosition naturalPos = PlayerPosition.Striker,
        byte uniformAttr20 = 12,
        Action<byte[], byte[]>? customize = null)
    {
        var raw = new byte[64];
        Array.Fill(raw, (byte)(uniformAttr20 * 5));
        raw[(int)PlayerAttribute.RightFoot] = 100;
        raw[(int)PlayerAttribute.LeftFoot] = 60;
        var pers = new byte[8];
        Array.Fill(pers, (byte)10);
        var positions = new byte[14];
        positions[(int)naturalPos] = 20;
        customize?.Invoke(raw, pers);
        return new PlayerRecord
        {
            Address = addr,
            VtableRva = 1,
            Id = id,
            CommonName = name,
            FullName = name,
            DateOfBirth = dob,
            GameDate = new DateOnly(2026, 7, 1),
            CurrentAbility = ca,
            PotentialAbility = pa,
            HomeReputation = 100,
            CurrentReputation = 100,
            WorldReputation = 100,
            Height = 180,
            RawAttributes = raw,
            AttributeScale = 5,
            Positions = positions,
            Personality = pers,
        };
    }

    [Fact]
    public void PlayersModuleSortsByRcaBestRoleSpecificRoleAndPersonality()
    {
        var mod = new PlayersModule();
        var highRca = MakePlayer(1, 100, "High RCA", new DateOnly(2002, 1, 1), 160, 170, PlayerPosition.Striker, 16);
        var midRca = MakePlayer(2, 101, "Mid RCA", new DateOnly(2002, 1, 1), 140, 160, PlayerPosition.MidfielderCentral, 12);
        var lowRca = MakePlayer(3, 102, "Low RCA", new DateOnly(2002, 1, 1), 110, 150, PlayerPosition.DefenderCentral, 9,
            (raw, pers) =>
            {
                // Make Ambitious (id=0) instead of Driven/Resolute so personality name sorts alphabetically before Balanced/Resolute
                pers[(int)PersonalityAttribute.Ambition] = 17;
                pers[(int)PersonalityAttribute.Loyalty] = 5;
                raw[(int)PlayerAttribute.Determination] = 5 * 5;
            });

        var all = new[] { midRca, lowRca, highRca };

        var byRca = mod.FilterAndSort(all, new PlayerFilter(), new PlayerSortSpec(PlayerSortKey.Rca, Descending: true));
        Assert.Equal(["High RCA", "Mid RCA", "Low RCA"], byRca.Select(p => p.DisplayName));

        var byBestRole = mod.FilterAndSort(all, new PlayerFilter(), new PlayerSortSpec(PlayerSortKey.BestRoleScore, Descending: true));
        Assert.Equal(["High RCA", "Mid RCA", "Low RCA"], byBestRole.Select(p => p.DisplayName));

        var byGkRole = mod.FilterAndSort(all, new PlayerFilter(), new PlayerSortSpec(PlayerSortKey.RoleScore, Descending: true, RoleCode: "SK (D)"));
        Assert.Equal(["High RCA", "Mid RCA", "Low RCA"], byGkRole.Select(p => p.DisplayName));

        var byPersAsc = mod.FilterAndSort(all, new PlayerFilter(), new PlayerSortSpec(PlayerSortKey.Personality, Descending: false));
        Assert.Equal("Ambitious", PersonalityClassifier.Classify(byPersAsc[0], isRegenOverride: false).Name);
    }

    [Fact]
    public void PlayersModuleFiltersByRcaRangeRoleScoreAndPersonalityPolarity()
    {
        int? previousFloor = RegenIds.Current;
        RegenIds.Current = 1_000_000;
        try
        {
            var mod = new PlayersModule();
            var gem = MakePlayer(1, 1_000_100, "Model Pro", new DateOnly(2006, 5, 1), 150, 190, PlayerPosition.Striker, 15,
                (raw, pers) =>
                {
                    pers[(int)PersonalityAttribute.Ambition] = 14;
                    pers[(int)PersonalityAttribute.Loyalty] = 14;
                    pers[(int)PersonalityAttribute.Pressure] = 14;
                    pers[(int)PersonalityAttribute.Professionalism] = 20;
                    pers[(int)PersonalityAttribute.Sportsmanship] = 14;
                    pers[(int)PersonalityAttribute.Temperament] = 14;
                    raw[(int)PlayerAttribute.Determination] = 14 * 5;
                });

            var slackRegen = MakePlayer(2, 1_000_200, "Slack Regen", new DateOnly(2008, 3, 10), 120, 175, PlayerPosition.MidfielderCentral, 11,
                (raw, pers) =>
                {
                    pers[(int)PersonalityAttribute.Professionalism] = 1;
                    pers[(int)PersonalityAttribute.Temperament] = 10;
                    raw[(int)PlayerAttribute.Determination] = 5 * 5;
                });

            Assert.False(PersonalityClassifier.Classify(gem).IsNegative);
            Assert.True(PersonalityClassifier.Classify(slackRegen).IsNegative);
            Assert.Equal("Slack", PersonalityClassifier.Classify(slackRegen).Name);

            var all = new[] { gem, slackRegen };

            var noNegative = mod.FilterAndSort(all, new PlayerFilter
            {
                NumericRanges = new Dictionary<string, NumericRange> { ["Personality"] = new(-2, -2) }
            }, new PlayerSortSpec());
            Assert.Equal(["Model Pro"], noNegative.Select(p => p.DisplayName));

            var onlyNegative = mod.FilterAndSort(all, new PlayerFilter
            {
                NumericRanges = new Dictionary<string, NumericRange> { ["Personality"] = new(-3, -3) }
            }, new PlayerSortSpec());
            Assert.Equal(["Slack Regen"], onlyNegative.Select(p => p.DisplayName));

            var rcaFiltered = mod.FilterAndSort(all, new PlayerFilter
            {
                NumericRanges = new Dictionary<string, NumericRange> { ["RCA"] = new(RcaCalculator.Calculate(gem) - 1, null) }
            }, new PlayerSortSpec());
            Assert.Equal(["Model Pro"], rcaFiltered.Select(p => p.DisplayName));

            var roleFiltered = mod.FilterAndSort(all, new PlayerFilter
            {
                RoleCode = "AF (A)",
                MinRoleScore = 70.0,
            }, new PlayerSortSpec());
            Assert.Equal(["Model Pro"], roleFiltered.Select(p => p.DisplayName));
        }
        finally
        {
            RegenIds.Current = previousFloor;
        }
    }

    [Fact]
    public void MiniScoutingCardRendersOnAvaloniaUiThread()
    {
        AvaloniaTestThread.Run(() =>
        {
            var player = MakePlayer(1, 2_000_001, "Wonder Regen", new DateOnly(2007, 4, 1), 130, 185, PlayerPosition.Striker, 14);
            Assert.NotNull(MainWindow.MiniScoutingCard(player));
        });
    }

    [Fact]
    public void StaffRcaIsUnknownWithoutTheStoredAttributeBlock()
    {
        var staff = new StaffRecord
        {
            Address = 0x100,
            StaffAddress = 0x200,
            Attributes = new byte[Enum.GetValues<StaffAttribute>().Length],
            Personality = new byte[8],
            Kind = new StaffKind(0, 0, 0, null, false),
        };
        var result = StaffRcaCalculator.Calculate(staff);
        Assert.Null(result.Score);
        Assert.NotNull(result.UnavailableReason);
    }
}
