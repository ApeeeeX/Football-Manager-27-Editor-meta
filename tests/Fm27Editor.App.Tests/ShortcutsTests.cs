using Avalonia.Input;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public sealed class ShortcutsTests
{
    [Fact]
    public void Shortcuts_use_command_on_mac_and_ctrl_elsewhere()
    {
        Assert.Equal(KeyModifiers.Meta, Shortcuts.CommandFor(mac: true));
        Assert.Equal(KeyModifiers.Control, Shortcuts.CommandFor(mac: false));
        Assert.Equal("Mass Edit (⌘M)", Shortcuts.LabelFor("Mass Edit (Ctrl+M)", mac: true));
        Assert.Equal("Mass Edit (Ctrl+M)", Shortcuts.LabelFor("Mass Edit (Ctrl+M)", mac: false));
        Assert.Equal("⌘K", Shortcuts.LabelFor("Ctrl+K", mac: true));
    }

    [Fact]
    public void The_helper_version_is_read_from_its_marker()
    {
        Assert.Equal(2, MacElevation.HelperVersion("\0\0FM27_HELPER_VERSION=2;\0"u8));
        Assert.Equal(17, MacElevation.HelperVersion("xFM27_HELPER_VERSION=17;y"u8));
        Assert.Null(MacElevation.HelperVersion("no marker here"u8));
        Assert.Null(MacElevation.HelperVersion("FM27_HELPER_VERSION=;"u8));
    }

    [Fact]
    public void A_refused_club_swap_says_why_instead_of_game_data_changed()
    {
        var started = new Fm27Editor.Core.Competitions.ClubSwapRefusedException(Fm27Editor.Core.Competitions.ClubSwapRefusal.SeasonStarted, "Premier League");
        Assert.Equal("Premier League has already played its first round, so its clubs cannot be swapped this season.", UserMessage.For(started));
        var country = new Fm27Editor.Core.Competitions.ClubSwapRefusedException(Fm27Editor.Core.Competitions.ClubSwapRefusal.DifferentCountry);
        Assert.Equal("Choose a club from the same country.", UserMessage.For(country));
    }
}
