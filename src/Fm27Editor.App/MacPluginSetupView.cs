using Fm27Editor.Core.Game;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// macOS, Steam copy, System Integrity Protection on: the editor reads the game through its plugin, which the game
    /// loads at start from its Steam launch options. Offered at start and when the game refuses a connection; a Mac with
    /// debugging restrictions off never sees it.
    /// </summary>
    async Task OfferPluginSetupAsync(bool refused)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var state = await Task.Run(() =>
        {
            // An editor update reaches the game at its next start; the launch options already name the fixed copy.
            try { MacPluginSetup.RefreshPlugin(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return MacPluginSetup.State();
        });
        if (state == MacPluginState.Missing)
            ShowNotice("Set up the editor plugin",
                "With System Integrity Protection on, the editor reads Football Manager 27 from Steam through its plugin. " +
                "Set up adds the plugin to the game's Steam launch options; the game's files are not changed. " +
                "Quit Football Manager 27 and Steam before you select Set up.",
                ("Later", false, null), ("Set up", true, InstallPluginAsync));
        else if (state == MacPluginState.Installed && refused)
            ShowNotice("Restart Football Manager 27",
                "The plugin is set up, but this game was started without it. Quit Football Manager 27, start it again from Steam, load your save, then select Load Game.",
                ("OK", true, null));
    }

    async Task InstallPluginAsync()
    {
        // The player quits them: the editor never closes Steam or the game.
        if (MacPluginSetup.SteamRunning() || MacPluginSetup.GameRunning())
        {
            ShowNotice("Quit Steam first",
                "Steam rewrites its settings when it quits, which would undo the setup. Quit Football Manager 27 and Steam, then select Set up.",
                ("Later", false, null), ("Set up", true, InstallPluginAsync));
            return;
        }
        try
        {
            int accounts = await Task.Run(MacPluginSetup.Install);
            FlightRecorder.Current?.Log("plugin", $"set up for {accounts} Steam account(s)");
            ShowNotice("Plugin set up",
                "Start Steam and Football Manager 27, load your save, then select Load Game. If the game was open, quit it and start it again from Steam.",
                ("OK", true, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            ShowNotice("Plugin not set up", UserMessage.For(ex), ("OK", true, null));
        }
    }
}
