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

    const string Launcher = "/Users/me/.fm27editor/fm27-launch";

    // Steam on macOS runs launch options without a shell and takes the first word as the program, so
    // DYLD_INSERT_LIBRARIES="…" %command% fails with "OS error 260" (reported by players with SIP on, 25-11-2026).
    // The launcher's path comes first; arguments Steam passes stay in order behind it.
    [Theory]
    [InlineData("", $"{Launcher} %command%")]
    [InlineData("-windowed", $"{Launcher} %command% -windowed")]
    [InlineData("SOME=1 %command% -x", $"{Launcher} SOME=1 %command% -x")]
    // The 1.5.3-1.5.5 setup's own assignment is dropped: the launcher adds the plugin.
    [InlineData($"DYLD_INSERT_LIBRARIES=\"{Plugin}\" %command%", $"{Launcher} %command%")]
    [InlineData("DYLD_INSERT_LIBRARIES=\"/old/libfm27editor_plugin.dylib\" %command%", $"{Launcher} %command%")]
    // Another tool's library stays, unquoted, as an argument the launcher exports.
    [InlineData($"DYLD_INSERT_LIBRARIES=\"/a/libfmrte_plugin.dylib:{Plugin}\" %command%", $"{Launcher} DYLD_INSERT_LIBRARIES=/a/libfmrte_plugin.dylib %command%")]
    [InlineData($"{Launcher} %command%", $"{Launcher} %command%")]
    public void The_launcher_comes_first_once(string before, string after) =>
        Assert.Equal(after, MacPluginSetup.WithLauncher(before, Launcher));

    [Theory]
    [InlineData("/Users/me", "me", "/Users/me/.fm27editor/fm27-launch")]
    // Steam splits on spaces, so a home folder with one moves the launcher to /Users/Shared.
    [InlineData("/Users/John Smith", "John Smith", "/Users/Shared/Fm27Editor-John_Smith/fm27-launch")]
    public void The_launcher_path_has_no_spaces(string home, string user, string expected) =>
        Assert.Equal(expected, MacPluginSetup.LauncherPathFor(home, user));

    [Fact]
    public void The_launcher_exports_leading_variables_adds_the_plugin_and_runs_the_game()
    {
        if (OperatingSystem.IsWindows()) return; // the launcher is a macOS shell script
        string folder = Path.Combine(Path.GetTempPath(), $"fm27-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string plugin = Path.Combine(folder, "it's here", "libfm27editor_plugin.dylib"), log = Path.Combine(folder, "last-launch.txt");
            string script = Path.Combine(folder, "fm27-launch"), output = Path.Combine(folder, "game.txt");
            File.WriteAllText(script, MacPluginSetup.LauncherScript(plugin, log));

            // No plugin file and no DYLD argument: nothing is injected, so the "game" runs on any Mac. It writes a variable
            // the launcher exported and the argument after its own name.
            Run(script, "FOO=bar", "/bin/sh", "-c", $"printf '%s|%s' \"$FOO\" \"$1\" > '{output}'", "game", "-windowed");
            Assert.Equal("bar|-windowed", File.ReadAllText(output));

            // With the plugin and another tool's library the launcher joins both. The log is written before the game
            // starts; whether dyld can load these stand-in files depends on the Mac's SIP setting, so only the log counts.
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            File.WriteAllText(plugin, "");
            Run(script, "DYLD_INSERT_LIBRARIES=/a/other.dylib", "/usr/bin/true");
            string[] lines = File.ReadAllLines(log);
            Assert.Equal($"DYLD_INSERT_LIBRARIES={plugin}:/a/other.dylib", lines[1]);
            Assert.Equal("/usr/bin/true", lines[2]);
        }
        finally { Directory.Delete(folder, recursive: true); }

        static void Run(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit(10000);
        }
    }

    /// <summary>
    /// Steam on macOS gives %command% as the app bundle, not its executable (Steam's gameprocess_log.txt, 25-11-2026:
    /// "fm27-launch '…/Football Manager 27/fm.app'", exit code 126 when the bundle itself was run).
    /// </summary>
    [Fact]
    public void The_launcher_runs_the_executable_inside_an_app_bundle()
    {
        if (OperatingSystem.IsWindows()) return; // the launcher is a macOS shell script
        string folder = Path.Combine(Path.GetTempPath(), $"fm27-launcher-{Guid.NewGuid():N}");
        string bundle = Path.Combine(folder, "Football Manager 27", "fm.app"), output = Path.Combine(folder, "game.txt");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        try
        {
            File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>Football Manager 27</string></dict></plist>\n");
            string executable = Path.Combine(bundle, "Contents", "MacOS", "Football Manager 27");
            File.WriteAllText(executable, $"#!/bin/sh\nprintf '%s|%s' \"$0\" \"$1\" > '{output}'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string script = Path.Combine(folder, "fm27-launch");
            File.WriteAllText(script, MacPluginSetup.LauncherScript(Path.Combine(folder, "missing.dylib"), Path.Combine(folder, "log.txt")));
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { ArgumentList = { script, bundle, "-windowed" } };
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                process.WaitForExit(10000);
                Assert.Equal(0, process.ExitCode);
            }

            Assert.Equal($"{executable}|-windowed", File.ReadAllText(output));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>
    /// Steam started the launcher under Rosetta on 25-11-2026, so the game ran as x86_64 and dyld aborted it on the arm64
    /// plugin ("incompatible architecture (have 'arm64', need 'x86_64')"). The launcher must hand the game a native start.
    /// </summary>
    [Fact]
    public void The_launcher_starts_the_game_natively_when_started_under_rosetta()
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
            return; // Rosetta exists only on Apple silicon
        string folder = Path.Combine(Path.GetTempPath(), $"fm27-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string script = Path.Combine(folder, "fm27-launch");
            File.WriteAllText(script, MacPluginSetup.LauncherScript(Path.Combine(folder, "missing.dylib"), Path.Combine(folder, "log.txt")));
            // uname is universal: it reports the architecture its own process runs as.
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/arch")
            {
                ArgumentList = { "-x86_64", "/bin/sh", script, "/usr/bin/uname", "-m" },
                RedirectStandardOutput = true,
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            string machine = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);

            Assert.Equal("arm64", machine);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

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
