using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fm27Editor.App;

/// <summary>A newer release on GitHub: its tag, the file this platform installs, and the checksum file.</summary>
public sealed record ReleaseInfo(string Tag, string AssetName, string AssetUrl, string ChecksumsUrl);

/// <summary>
/// Asks GitHub's public API once at start for the latest release. Nothing is sent but the request itself; any failure
/// (offline, rate limit, no release yet) is silent. Builds without a real version (local builds, tests) never ask.
/// </summary>
public static class UpdateCheck
{
    const string Repository = "Jackhamerp24/Football-Manager-27-Editor";
    public const string ExeName = "Fm27Editor.exe", MacDiskImageName = "Fm27Editor-mac-arm64.dmg", ChecksumsName = "SHA256SUMS.txt";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";
    static string DownloadPrefix => $"https://github.com/{Repository}/releases/download/";

    /// <summary>Version of this build from the release tag; null for a local build (0.0.0).</summary>
    public static Version? CurrentVersion()
    {
        var text = typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(text) is { } v && v != new Version(0, 0, 0) ? v : null;
    }

    /// <summary>Reads "v1.2.3", "1.2.3-beta" or "1.2.3+sha" as 1.2.3; null when it is not a version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var v) ? v : null;
    }

    public static bool IsNewer(string? latestTag, Version current) => Parse(latestTag) is { } latest && latest > current;

    /// <summary>
    /// The release in a GitHub "latest release" answer when it is newer than <paramref name="current"/> and carries
    /// <paramref name="asset"/> and the checksum file, both from this repository's release downloads; otherwise null.
    /// </summary>
    public static ReleaseInfo? FromJson(JsonElement root, Version current, string asset = ExeName)
    {
        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!IsNewer(tag, current) || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        string? Asset(string name) => assets.EnumerateArray()
            .Where(a => a.TryGetProperty("name", out var n) && n.GetString() == name)
            .Select(a => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null)
            .FirstOrDefault(u => u is not null && u.StartsWith(DownloadPrefix, StringComparison.Ordinal));
        return Asset(asset) is { } url && Asset(ChecksumsName) is { } sums ? new(tag!, asset, url, sums) : null;
    }

    /// <summary>The SHA-256 listed for <paramref name="file"/> in a "hash  name" checksum file, lower case; null when absent.</summary>
    public static string? ChecksumFor(string checksums, string file)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == file && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Fm27Editor");
        return http;
    }

    /// <summary>The newest release when it is newer than this build, otherwise null.</summary>
    public static async Task<ReleaseInfo?> NewerReleaseAsync(string asset = ExeName, CancellationToken ct = default)
    {
        if (CurrentVersion() is not { } current) return null;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(6));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return FromJson(json.RootElement, current, asset);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Downloads the release file to <paramref name="target"/> and checks it against the release's checksum file. Returns the
    /// downloaded file; throws when the download or the check fails (the partial file is removed).
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string target, CancellationToken ct = default,
        IProgress<(long Done, long? Total)>? progress = null)
    {
        using var http = Client(TimeSpan.FromMinutes(5));
        string sums = await http.GetStringAsync(release.ChecksumsUrl, ct);
        string expected = ChecksumFor(sums, release.AssetName) ?? throw new InvalidDataException(Loc.S("The release has no checksum for the editor"));
        try
        {
            using (var response = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(target);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                progress?.Report((0, total));
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((done, total));
                }
            }
            await using var check = File.OpenRead(target);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
            if (actual != expected) throw new InvalidDataException(Loc.S("The downloaded file does not match the release checksum"));
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Puts <paramref name="downloaded"/> in place of the running <paramref name="exePath"/>: Windows lets a running exe be
    /// renamed, so it becomes "<c>.old</c>" (removed at the next start) and the new file takes its name. If the second step
    /// fails the old exe is put back.
    /// </summary>
    public static void Swap(string exePath, string downloaded)
    {
        string old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try { File.Move(downloaded, exePath); }
        catch { File.Move(old, exePath); throw; }
    }

    /// <summary>
    /// The .app bundle the running editor belongs to, when it can replace itself: an Apple silicon build whose executable is
    /// <c>Fm27Editor.app/Contents/MacOS/Fm27Editor</c>, not run from the mounted disk image or from macOS's translocated copy.
    /// </summary>
    public static string? MacBundleOf(string? processPath, bool arm64 = true)
    {
        // macOS paths are split on "/" here, not with Path, so the rule reads the same on every platform.
        const string Tail = "/Contents/MacOS/Fm27Editor";
        if (!arm64 || string.IsNullOrEmpty(processPath) || !processPath.StartsWith('/') || !processPath.EndsWith(Tail, StringComparison.Ordinal))
            return null;
        string bundle = processPath[..^Tail.Length];
        if (!bundle.EndsWith(".app", StringComparison.Ordinal) || bundle.LastIndexOf('/') < 0) return null;
        if (bundle.StartsWith("/Volumes/", StringComparison.Ordinal) || bundle.Contains("/AppTranslocation/", StringComparison.Ordinal)) return null;
        return bundle;
    }

    static string Sibling(string bundle, string suffix) =>
        Path.Combine(Path.GetDirectoryName(bundle)!, "." + Path.GetFileName(bundle) + suffix);

    /// <summary>
    /// Copies the editor out of a downloaded disk image next to <paramref name="bundle"/> and swaps it in. The running copy keeps
    /// working from its moved files; the old bundle is removed at the next start. If the swap fails the old bundle is put back.
    /// The file came through the editor rather than a browser, so it carries no quarantine mark and opens without a prompt.
    /// </summary>
    public static void InstallMacBundle(string diskImage, string bundle)
    {
        string mount = Directory.CreateTempSubdirectory("fm27-update-mount").FullName;
        string staged = Sibling(bundle, ".new"), old = Sibling(bundle, ".old");
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Run("hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, diskImage);
        try
        {
            string source = Directory.GetDirectories(mount, "*.app")
                .SingleOrDefault(app => File.Exists(Path.Combine(app, "Contents", "MacOS", "Fm27Editor")))
                ?? throw new InvalidDataException(Loc.S("The disk image has no editor"));
            Run("ditto", source, staged);
        }
        finally
        {
            try { Run("hdiutil", "detach", mount, "-force"); } catch (IOException) { }
            try { Directory.Delete(mount); } catch (IOException) { }
        }
        if (Directory.Exists(old)) Directory.Delete(old, true);
        Directory.Move(bundle, old);
        try { Directory.Move(staged, bundle); }
        catch { Directory.Move(old, bundle); throw; }
    }

    /// <summary>Removes the old and half-copied bundles an earlier macOS update left behind; best effort.</summary>
    public static void CleanUpMac(string bundle)
    {
        foreach (var leftover in new[] { Sibling(bundle, ".old"), Sibling(bundle, ".new") })
            try { if (Directory.Exists(leftover)) Directory.Delete(leftover, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"{file} did not start");
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120_000)) { try { process.Kill(); } catch (InvalidOperationException) { } throw new IOException($"{file} timed out"); }
        if (process.ExitCode != 0) throw new IOException($"{file} failed: {errors.Result.Trim()}");
    }

    /// <summary>Removes what an earlier update left behind; best effort.</summary>
    public static void CleanUp(string exePath)
    {
        foreach (var leftover in new[] { exePath + ".old", exePath + ".new" })
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
