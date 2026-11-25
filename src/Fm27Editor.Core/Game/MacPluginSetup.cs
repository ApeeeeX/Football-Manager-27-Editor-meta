using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Fm27Editor.Core.Game;

/// <summary>What the plugin setup found on this Mac.</summary>
public enum MacPluginState
{
    /// <summary>Not macOS, no Steam copy of Football Manager 27, or the editor can already reach the game without the plugin.</summary>
    NotNeeded,
    /// <summary>The Steam copy needs the plugin and its launch options do not load it yet.</summary>
    Missing,
    /// <summary>Every Steam account that has the game loads the plugin at start.</summary>
    Installed,
}

/// <summary>
/// Sets up libfm27editor_plugin.dylib for every Steam copy of Football Manager: the plugin is copied to a fixed folder,
/// and each game's Steam launch options start the game through a small launcher script that puts the plugin in
/// DYLD_INSERT_LIBRARIES. Steam on macOS runs launch options without a shell and treats the first word as the program,
/// so the usual <c>DYLD_INSERT_LIBRARIES="…" %command%</c> fails with "OS error 260"; the launcher's path has no spaces.
/// The fixed path and the plugin's own check for any "Football Manager" executable keep one setup working across
/// editor updates and new game releases; a newly installed release is found at the next start. The game's own files
/// are never changed. Needed only when System Integrity Protection's debugging restrictions are on, because they refuse
/// task_for_pid on the game's Hardened Runtime even for the root helper (kern_return_t 5).
/// </summary>
public static partial class MacPluginSetup
{
    /// <summary>Football Manager 27 on Steam.</summary>
    public const string SteamAppId = "4530370";
    public const string PluginFileName = "libfm27editor_plugin.dylib";
    const string BackupSuffix = ".fm27editor-backup";

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string SteamRoot => Path.Combine(Home, "Library", "Application Support", "Steam");

    /// <summary>The fixed copy the launch options name, so moving or updating the editor keeps them valid.</summary>
    public static string InstalledPluginPath =>
        Path.Combine(Home, "Library", "Application Support", "Fm27Editor", PluginFileName);

    /// <summary>The plugin shipped next to the editor's executable (Contents/MacOS in the app bundle).</summary>
    public static string BundledPluginPath => Path.Combine(AppContext.BaseDirectory, PluginFileName);

    /// <summary>
    /// The launcher the launch options name: an absolute path without whitespace, because Steam splits the options on
    /// spaces and expands no <c>~</c>. A home folder with whitespace in its path uses /Users/Shared instead.
    /// </summary>
    public static string LauncherPath => LauncherPathFor(Home, Environment.UserName);

    public static string LauncherPathFor(string home, string userName) =>
        home.Any(char.IsWhiteSpace)
            ? $"/Users/Shared/Fm27Editor-{Regex.Replace(userName, @"[^A-Za-z0-9._-]", "_")}/fm27-launch"
            : $"{home.TrimEnd('/')}/.fm27editor/fm27-launch"; // a macOS path, built the same on every OS

    /// <summary>
    /// The launcher's text. Leading NAME=value arguments (a player's own variables, or another tool's
    /// DYLD_INSERT_LIBRARIES) are exported, the plugin joins DYLD_INSERT_LIBRARIES, and the game replaces the script.
    /// The last command line is kept in the editor's logs for support.
    /// </summary>
    public static string LauncherScript(string pluginPath, string logPath) => $$"""
        #!/bin/sh
        # Football Manager 27 Editor: Steam runs this in front of the game ("<this file> %command%").
        # Steam can start this under Rosetta, which would run the game as x86_64; run natively first, as Steam's own
        # launch would (before any DYLD variable is set, since /usr/bin/arch drops them).
        if [ "$(/usr/sbin/sysctl -n sysctl.proc_translated 2>/dev/null)" = 1 ]; then
            exec /usr/bin/arch -arm64 /bin/sh "$0" "$@"
        fi
        plugin={{ShellQuote(pluginPath)}}
        libraries=
        while [ $# -gt 0 ]; do
            case "$1" in
                DYLD_INSERT_LIBRARIES=*) libraries="${1#DYLD_INSERT_LIBRARIES=}"; shift ;;
                [A-Za-z_]*=*) export "$1"; shift ;;
                *) break ;;
            esac
        done
        # Steam passes the app bundle (…/fm.app), which cannot be run directly: run the executable its Info.plist names.
        if [ -d "$1/Contents/MacOS" ]; then
            bundle="$1"; shift
            executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$bundle/Contents/Info.plist" 2>/dev/null)
            set -- "$bundle/Contents/MacOS/$executable" "$@"
        fi
        if [ -f "$plugin" ]; then libraries="$plugin${libraries:+:$libraries}"; fi
        # Logged before DYLD_INSERT_LIBRARIES is set, so the libraries are never loaded into the logging commands.
        { date; echo "DYLD_INSERT_LIBRARIES=$libraries"; printf '%s\n' "$@"; } > {{ShellQuote(logPath)}} 2>/dev/null
        if [ -n "$libraries" ]; then DYLD_INSERT_LIBRARIES="$libraries"; export DYLD_INSERT_LIBRARIES; fi
        exec "$@"

        """;

    static string LauncherLogPath => Path.Combine(Home, "Library", "Application Support", "Fm27Editor", "logs", "last-launch.txt");

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static MacPluginState State()
    {
        if (!OperatingSystem.IsMacOS()) return MacPluginState.NotNeeded;
        var games = SteamGames();
        if (games.Count == 0 || !DebuggingRestricted()) return MacPluginState.NotNeeded;
        if (!File.Exists(InstalledPluginPath) || !File.Exists(LauncherPath)) return MacPluginState.Missing;
        foreach (string path in AccountConfigs())
        {
            string text = File.ReadAllText(path);
            foreach (string app in games.Keys)
                if (FindAppBlock(text, app) is not null &&
                    !(ReadLaunchOptions(text, app) ?? "").TrimStart().StartsWith(LauncherPath + " ", StringComparison.Ordinal))
                    return MacPluginState.Missing;
        }
        return MacPluginState.Installed;
    }

    /// <summary>Every Football Manager release installed through Steam, app ID to name, from the libraries' app manifests.</summary>
    public static IReadOnlyDictionary<string, string> SteamGames()
    {
        var games = new Dictionary<string, string>();
        foreach (string library in SteamLibraries())
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                var id = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"");
                var name = Regex.Match(text, "\"name\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (id.Success && name.Success && IsFootballManager(Unescape(name.Groups[1].Value)))
                    games[id.Groups[1].Value] = Unescape(name.Groups[1].Value);
            }
        }
        return games;
    }

    /// <summary>"Football Manager 27", "Football Manager 2026"…; the editions without the main game (Touch, Console) are excluded.</summary>
    public static bool IsFootballManager(string name) =>
        Regex.IsMatch(name, @"^Football Manager \d+$");

    /// <summary>True while Steam runs; Steam rewrites its config when it quits, which would undo the setup.</summary>
    public static bool SteamRunning() => Process.GetProcessesByName("steam_osx").Length > 0;

    /// <summary>True while a Football Manager game runs; it must start again to load the plugin. The editor never quits it, or Steam.</summary>
    public static bool GameRunning() =>
        Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("Football Manager", StringComparison.Ordinal); } catch (InvalidOperationException) { return false; } finally { p.Dispose(); } });

    /// <summary>
    /// Replaces the fixed copy with the plugin shipped in this editor when they differ, so an editor update reaches the
    /// game at its next start without touching the launch options. Does nothing before the first setup.
    /// </summary>
    public static bool RefreshPlugin()
    {
        if (!File.Exists(InstalledPluginPath) || !File.Exists(BundledPluginPath)) return false;
        bool changed = WriteLauncher(onlyIfPresent: true);
        if (File.ReadAllBytes(InstalledPluginPath).AsSpan().SequenceEqual(File.ReadAllBytes(BundledPluginPath))) return changed;
        CopyPlugin();
        return true;
    }

    /// <summary>Writes the launcher when its text differs; owner read/write/execute, everyone read/execute.</summary>
    static bool WriteLauncher(bool onlyIfPresent)
    {
        string path = LauncherPath;
        if (onlyIfPresent && !File.Exists(path)) return false;
        string text = LauncherScript(InstalledPluginPath, LauncherLogPath);
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".new", text, new UTF8Encoding(false));
        File.SetUnixFileMode(path + ".new", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(path + ".new", path, overwrite: true);
        return true;
    }

    // A new file renamed over the old one, so a game still running the old plugin keeps its mapped copy intact.
    static void CopyPlugin()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledPluginPath)!);
        string staged = InstalledPluginPath + ".new";
        File.Copy(BundledPluginPath, staged, overwrite: true);
        File.Move(staged, InstalledPluginPath, overwrite: true);
    }

    /// <summary>
    /// Copies the plugin into place and adds it to the launch options of every Football Manager game in every Steam
    /// account that has played it. Each config file is backed up once (".fm27editor-backup") before its first change.
    /// Steam must not be running. Returns how many game entries load the plugin afterwards.
    /// </summary>
    public static int Install()
    {
        if (SteamRunning()) throw new InvalidOperationException("Steam is still running: it rewrites its settings when it quits");
        if (!File.Exists(BundledPluginPath)) throw new FileNotFoundException("The editor's plugin is missing from the app", BundledPluginPath);
        CopyPlugin();
        WriteLauncher(onlyIfPresent: false);

        var games = SteamGames();
        int entries = 0;
        foreach (string path in AccountConfigs())
        {
            string original = File.ReadAllText(path), text = original;
            foreach (string app in games.Keys)
            {
                if (FindAppBlock(text, app) is null) continue;
                string current = ReadLaunchOptions(text, app) ?? "";
                text = WriteLaunchOptions(text, app, WithLauncher(current, LauncherPath));
                entries++;
            }
            if (text == original) continue;
            if (!File.Exists(path + BackupSuffix)) File.Copy(path, path + BackupSuffix);
            File.WriteAllText(path + ".new", text, new UTF8Encoding(false));
            File.Move(path + ".new", path, overwrite: true);
        }
        if (entries == 0) throw new InvalidOperationException("Start Football Manager from Steam once, quit it, then set up again");
        return entries;
    }

    /// <summary>
    /// The launch options starting with the launcher, once. Our own DYLD_INSERT_LIBRARIES entry from earlier setups is
    /// dropped (the launcher adds the plugin); another tool's libraries stay as an argument the launcher exports.
    /// Options without %command% are game arguments and stay after it.
    /// </summary>
    public static string WithLauncher(string options, string launcherPath)
    {
        options = options.Trim();
        var assignment = DyldAssignment().Match(options);
        if (assignment.Success)
        {
            var libraries = assignment.Groups["value"].Value.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(library => !library.EndsWith("/" + PluginFileName, StringComparison.Ordinal)).ToList();
            string kept = libraries.Count == 0 ? "" : $"DYLD_INSERT_LIBRARIES={string.Join(':', libraries)}";
            options = Regex.Replace(options[..assignment.Index] + kept + options[(assignment.Index + assignment.Length)..], @"\s+", " ").Trim();
        }
        if (options.StartsWith(launcherPath + " ", StringComparison.Ordinal)) return options;
        if (options.Contains("%command%", StringComparison.Ordinal)) return $"{launcherPath} {options}";
        return options.Length == 0 ? $"{launcherPath} %command%" : $"{launcherPath} %command% {options}";
    }

    [GeneratedRegex("DYLD_INSERT_LIBRARIES=(?:\"(?<value>[^\"]*)\"|(?<value>[^\\s\"]+))")]
    private static partial Regex DyldAssignment();

    /// <summary>
    /// Debugging restrictions on, as `csrutil status` reports them. When they are off the editor reaches the game directly
    /// and the plugin would only change the player's launch options for nothing. Unknown output counts as restricted.
    /// </summary>
    public static bool DebuggingRestricted()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/csrutil", "status") { RedirectStandardOutput = true, UseShellExecute = false });
            if (process is null) return true;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return DebuggingRestricted(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
    }

    public static bool DebuggingRestricted(string csrutilStatus) =>
        !csrutilStatus.Contains("Protection status: disabled", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(csrutilStatus, @"Debugging Restrictions:\s*disabled", RegexOptions.IgnoreCase);

    static IEnumerable<string> SteamLibraries()
    {
        var libraries = new List<string> { SteamRoot };
        string folders = Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
            foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
                libraries.Add(Unescape(match.Groups[1].Value));
        return libraries.Distinct();
    }

    static IEnumerable<string> AccountConfigs()
    {
        string userdata = Path.Combine(SteamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        return Directory.EnumerateDirectories(userdata)
            .Select(account => Path.Combine(account, "config", "localconfig.vdf"))
            .Where(File.Exists);
    }

    // ---- localconfig.vdf: only the game's LaunchOptions line changes; every other byte is kept.

    static readonly string[] AppsPath = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps"];

    public static string? ReadLaunchOptions(string vdf, string appId)
    {
        if (FindAppBlock(vdf, appId) is not { } block) return null;
        return FindValue(vdf, block, "LaunchOptions") is { } value ? Unescape(vdf.Substring(value.Start, value.Length)) : null;
    }

    public static string WriteLaunchOptions(string vdf, string appId, string options)
    {
        var block = FindAppBlock(vdf, appId) ?? throw new InvalidOperationException("Football Manager 27 is not in this Steam account's settings");
        string escaped = Escape(options);
        if (FindValue(vdf, block, "LaunchOptions") is { } value)
            return vdf[..value.Start] + escaped + vdf[(value.Start + value.Length)..];
        // New key: indent like the block's own keys, just before its closing brace.
        int lineStart = vdf.LastIndexOf('\n', block.Close - 1) + 1;
        string indent = new(vdf[lineStart..block.Close].TakeWhile(c => c == '\t').ToArray());
        string newline = vdf.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return vdf[..lineStart] + indent + "\t\"LaunchOptions\"\t\t\"" + escaped + "\"" + newline + vdf[lineStart..];
    }

    readonly record struct Block(int Open, int Close);
    readonly record struct Span(int Start, int Length);
    readonly record struct Token(char Kind, int Start, int Length); // '"' string (content span), '{', '}'

    static List<Token> Tokenize(string vdf)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < vdf.Length; i++)
        {
            char c = vdf[i];
            if (c == '{' || c == '}') tokens.Add(new(c, i, 1));
            else if (c == '"')
            {
                int start = ++i;
                while (i < vdf.Length && vdf[i] != '"') i += vdf[i] == '\\' ? 2 : 1;
                if (i >= vdf.Length) throw new InvalidDataException("Steam's settings file ends inside a string");
                tokens.Add(new('"', start, i - start));
            }
            else if (c == '/' && i + 1 < vdf.Length && vdf[i + 1] == '/')
                while (i < vdf.Length && vdf[i] != '\n') i++;
        }
        return tokens;
    }

    static Block? FindAppBlock(string vdf, string appId)
    {
        var tokens = Tokenize(vdf);
        int from = 0, to = tokens.Count;
        foreach (string key in AppsPath.Append(appId))
        {
            if (FindChildBlock(vdf, tokens, from, to, key) is not { } span) return null;
            (from, to) = span;
        }
        return new(tokens[from - 1].Start, tokens[to].Start);
    }

    /// <summary>Token range inside the child block named <paramref name="key"/> among the direct children of [from, to).</summary>
    static (int From, int To)? FindChildBlock(string vdf, List<Token> tokens, int from, int to, string key)
    {
        for (int i = from; i < to; i++)
        {
            if (tokens[i].Kind != '"') { if (tokens[i].Kind == '{') i = Skip(tokens, i); continue; }
            bool named = string.Equals(vdf.Substring(tokens[i].Start, tokens[i].Length), key, StringComparison.OrdinalIgnoreCase);
            if (i + 1 < to && tokens[i + 1].Kind == '{')
            {
                int close = Skip(tokens, i + 1);
                if (named) return (i + 2, close);
                i = close;
            }
            else i++; // a key/value pair
        }
        return null;
    }

    static int Skip(List<Token> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == '{') depth++;
            else if (tokens[i].Kind == '}' && --depth == 0) return i;
        }
        throw new InvalidDataException("Steam's settings file has an unclosed block");
    }

    static Span? FindValue(string vdf, Block block, string key)
    {
        var tokens = Tokenize(vdf);
        int from = tokens.FindIndex(t => t.Start == block.Open) + 1, to = tokens.FindIndex(t => t.Start == block.Close);
        for (int i = from; i < to; i++)
        {
            if (tokens[i].Kind == '{') { i = Skip(tokens, i); continue; }
            if (tokens[i].Kind != '"' || i + 1 >= to) continue;
            if (tokens[i + 1].Kind == '{') { i = Skip(tokens, i + 1); continue; }
            if (string.Equals(vdf.Substring(tokens[i].Start, tokens[i].Length), key, StringComparison.OrdinalIgnoreCase))
                return new(tokens[i + 1].Start, tokens[i + 1].Length);
            i++;
        }
        return null;
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    static string Unescape(string value) => Regex.Replace(value, @"\\(.)", "$1");
}
