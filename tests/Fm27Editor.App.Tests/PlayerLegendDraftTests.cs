using Fm27Editor.Core.Players;
using Fm27Editor.Core.Tests;

namespace Fm27Editor.App.Tests;

public class PlayerLegendDraftTests
{
    [Fact]
    public void Choosing_a_legend_previews_values_until_save_and_keeps_manual_overrides()
    {
        var world = new TestWorld();
        var repo = new PlayerRepository(world.Memory, world.Profile, TestWorld.ModuleBase);
        var player = repo.Reload(TestWorld.Mainoo)!;
        var draft = new PlayerDraft(player);
        var crossing = PlayerFields.All.Single(f => f.Id == "Crossing");
        var ca = PlayerFields.All.Single(f => f.Id == "CA");
        var messi = PlayerLegends.All.Single(p => p.Id == 19225);

        draft.Stage(crossing, 14);
        draft.SetLegend(messi);
        Assert.Equal(150, draft.Value(ca));
        Assert.Equal(14, draft.Value(crossing));
        Assert.Equal(153, repo.Reload(player.Address)!.CurrentAbility);

        // Override the preset back to the original CA, then clear and re-stage the preset.
        draft.Stage(ca, 153);
        draft.SetLegend(null);
        Assert.Equal(1, draft.Count);
        draft.SetLegend(messi);
        draft.Stage(ca, 153);

        draft.SaveLegend(new PlayerWriter(world.Memory, world.Profile, TestWorld.ModuleBase));

        var saved = repo.Reload(player.Address)!;
        Assert.Equal(153, saved.CurrentAbility);
        Assert.Equal(200, saved.PotentialAbility);
        Assert.Equal(70, saved.Raw(PlayerAttribute.Crossing));
        Assert.Equal(85, saved.Raw(PlayerAttribute.Dribbling));
        Assert.Equal(0, draft.Count);
    }
}
