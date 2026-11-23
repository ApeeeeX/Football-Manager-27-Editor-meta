using Fm27Editor.Core.Game;

namespace Fm27Editor.Core.Tests;

public class MacPluginSetupTests
{
    const string Plugin = "/Users/me/Library/Application Support/Fm27Editor/libfm27editor_plugin.dylib";

    // Shape copied from a real localconfig.vdf (Steam on macOS, 22-11-2026); other apps and keys trimmed.
    const string Config = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"7\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-other\"\n\t\t\t\t\t}\n" +
        "\t\t\t\t\t\"4530370\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1795300810\"\n\t\t\t\t\t\t\"autocloud\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"lastlaunch\"\t\t\"1795300510\"\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n" +
        "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    [Fact]
    public void A_new_launch_option_goes_inside_the_game_block_only()
    {
        string updated = MacPluginSetup.WriteLaunchOptions(Config, MacPluginSetup.SteamAppId, $"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%");

        Assert.Equal($"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%", MacPluginSetup.ReadLaunchOptions(updated, MacPluginSetup.SteamAppId));
        Assert.Equal("-other", MacPluginSetup.ReadLaunchOptions(updated, "7"));
        // Steam's own escaping: quotes inside a value are backslashed, and the line sits at the block's key indent.
        Assert.Contains($"\t\t\t\t\t\t\"LaunchOptions\"\t\t\"DYLD_INSERT_LIBRARIES=\\\"{Plugin}\\\" %command%\"\n\t\t\t\t\t}}", updated);
        Assert.Equal(Config, MacPluginSetup.WriteLaunchOptions(updated, MacPluginSetup.SteamAppId, "").Replace("\t\t\t\t\t\t\"LaunchOptions\"\t\t\"\"\n", ""));
    }

    [Fact]
    public void An_existing_launch_option_is_replaced_in_place()
    {
        string withOption = MacPluginSetup.WriteLaunchOptions(Config, MacPluginSetup.SteamAppId, "-windowed");
        string updated = MacPluginSetup.WriteLaunchOptions(withOption, MacPluginSetup.SteamAppId, "x");

        Assert.Equal("x", MacPluginSetup.ReadLaunchOptions(updated, MacPluginSetup.SteamAppId));
        Assert.Equal(withOption.Length - "-windowed".Length + 1, updated.Length);
    }

    [Theory]
    [InlineData("", $"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%")]
    [InlineData("-windowed", $"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command% -windowed")]
    [InlineData("SOME=1 %command% -x", $"DYLD_INSERT_LIBRARIES=\"{Plugin}\" SOME=1 %command% -x")]
    // Another tool's plugin stays loaded; ours joins the colon-separated list.
    [InlineData("DYLD_INSERT_LIBRARIES=\"/a/libfmrte_plugin.dylib\" %command%", $"DYLD_INSERT_LIBRARIES=\"/a/libfmrte_plugin.dylib:{Plugin}\" %command%")]
    // An older copy of our plugin at another path is replaced, not duplicated.
    [InlineData("DYLD_INSERT_LIBRARIES=\"/old/libfm27editor_plugin.dylib\" %command%", $"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%")]
    [InlineData($"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%", $"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%")]
    public void The_plugin_is_added_once(string before, string after) =>
        Assert.Equal(after, MacPluginSetup.WithPlugin(before, Plugin));

    [Theory]
    // `csrutil status` on a Mac with debugging restrictions off (custom configuration) and on a default Mac.
    [InlineData("System Integrity Protection status: unknown (Custom Configuration).\n\tDebugging Restrictions: disabled\n", false)]
    [InlineData("System Integrity Protection status: enabled.\n", true)]
    [InlineData("System Integrity Protection status: disabled.\n", false)]
    public void Debugging_restrictions_are_read_from_csrutil(string output, bool restricted) =>
        Assert.Equal(restricted, MacPluginSetup.DebuggingRestricted(output));

    [Theory]
    // Names as Steam's app manifests give them; the plugin setup covers every main-game release.
    [InlineData("Football Manager 27", true)]
    [InlineData("Football Manager 2024", true)]
    [InlineData("Football Manager 26", true)]
    [InlineData("Football Manager 27 Touch", false)]
    [InlineData("Football Manager 2024 Editor", false)]
    [InlineData("Balatro", false)]
    public void Every_main_football_manager_release_is_covered(string name, bool covered) =>
        Assert.Equal(covered, MacPluginSetup.IsFootballManager(name));
}
