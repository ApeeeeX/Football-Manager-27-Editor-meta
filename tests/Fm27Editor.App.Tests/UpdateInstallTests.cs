using System.Text.Json;
using Avalonia.LogicalTree;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

/// <summary>
/// The in-app update. The release answer and checksum file are the real ones GitHub served for v1.3.0 (14/11/2026):
/// three assets, and SHA256SUMS.txt written by the release workflow with CRLF line ends.
/// </summary>
public sealed class UpdateInstallTests
{
    const string Release130 = """
        {"tag_name":"v1.3.0","assets":[
          {"name":"Fm27Editor.exe","browser_download_url":"https://github.com/Jackhamerp24/Football-Manager-27-Editor/releases/download/v1.3.0/Fm27Editor.exe"},
          {"name":"Fm27Editor.Tools.exe","browser_download_url":"https://github.com/Jackhamerp24/Football-Manager-27-Editor/releases/download/v1.3.0/Fm27Editor.Tools.exe"},
          {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/Jackhamerp24/Football-Manager-27-Editor/releases/download/v1.3.0/SHA256SUMS.txt"}]}
        """;

    const string Sums130 =
        "b6e51b4a381ea8b32ef311b7937c57d6f76138d5094b267c273a08c465ca457c  Fm27Editor.exe\r\n" +
        "ed431b4ed216c8176f3c9ede5968a629e17ecd45335beedcb75e4599e2b1ce0d  Fm27Editor.Tools.exe\r\n";

    static ReleaseInfo? Read(string json, string current) => UpdateCheck.FromJson(JsonDocument.Parse(json).RootElement, Version.Parse(current));

    [Fact]
    public void A_newer_release_gives_the_editor_exe_and_checksum_urls()
    {
        var r = Read(Release130, "1.2.0");
        Assert.NotNull(r);
        Assert.Equal("v1.3.0", r.Tag);
        Assert.Equal("https://github.com/Jackhamerp24/Football-Manager-27-Editor/releases/download/v1.3.0/Fm27Editor.exe", r.AssetUrl);
        Assert.EndsWith("/v1.3.0/SHA256SUMS.txt", r.ChecksumsUrl);
    }

    [Fact]
    public void The_same_or_an_older_release_offers_nothing()
    {
        Assert.Null(Read(Release130, "1.3.0"));
        Assert.Null(Read(Release130, "1.4.0"));
    }

    [Fact]
    public void A_release_without_the_checksum_file_or_with_a_foreign_download_is_not_offered()
    {
        Assert.Null(Read(Release130.Replace("\"SHA256SUMS.txt\"", "\"notes.txt\""), "1.2.0"));
        Assert.Null(Read(Release130.Replace("https://github.com/Jackhamerp24/Football-Manager-27-Editor/releases/download/v1.3.0/Fm27Editor.exe",
            "https://example.com/Fm27Editor.exe"), "1.2.0"));
    }

    [Fact]
    public void The_checksum_is_read_for_the_editor_and_not_for_the_tools()
    {
        Assert.Equal("b6e51b4a381ea8b32ef311b7937c57d6f76138d5094b267c273a08c465ca457c", UpdateCheck.ChecksumFor(Sums130, "Fm27Editor.exe"));
        Assert.Equal("ed431b4ed216c8176f3c9ede5968a629e17ecd45335beedcb75e4599e2b1ce0d", UpdateCheck.ChecksumFor(Sums130, "Fm27Editor.Tools.exe"));
        Assert.Null(UpdateCheck.ChecksumFor(Sums130, "Other.exe"));
        Assert.Null(UpdateCheck.ChecksumFor("nothex  Fm27Editor.exe", "Fm27Editor.exe"));
    }

    [Fact]
    public void Swap_puts_the_new_exe_in_place_and_cleanup_removes_the_old_one()
    {
        string dir = Directory.CreateTempSubdirectory("fm27update").FullName;
        try
        {
            string exe = Path.Combine(dir, "Fm27Editor.exe");
            File.WriteAllText(exe, "old");
            File.WriteAllText(exe + ".new", "new");
            UpdateCheck.Swap(exe, exe + ".new");
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".old"));
            Assert.False(File.Exists(exe + ".new"));
            UpdateCheck.CleanUp(exe);
            Assert.False(File.Exists(exe + ".old"));
            Assert.Equal("new", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_failed_swap_puts_the_old_exe_back()
    {
        string dir = Directory.CreateTempSubdirectory("fm27update").FullName;
        try
        {
            string exe = Path.Combine(dir, "Fm27Editor.exe");
            File.WriteAllText(exe, "old");
            Assert.ThrowsAny<IOException>(() => UpdateCheck.Swap(exe, Path.Combine(dir, "missing.new")));
            Assert.Equal("old", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_mac_build_asks_for_the_disk_image_and_its_checksum()
    {
        string json = Release130.Replace("Fm27Editor.exe\"", "Fm27Editor-mac-arm64.dmg\"").Replace("/Fm27Editor.exe", "/Fm27Editor-mac-arm64.dmg");
        var r = UpdateCheck.FromJson(JsonDocument.Parse(json).RootElement, Version.Parse("1.2.0"), UpdateCheck.MacDiskImageName);
        Assert.NotNull(r);
        Assert.Equal(UpdateCheck.MacDiskImageName, r.AssetName);
        Assert.EndsWith("/v1.3.0/Fm27Editor-mac-arm64.dmg", r.AssetUrl);
        Assert.Null(UpdateCheck.FromJson(JsonDocument.Parse(Release130).RootElement, Version.Parse("1.2.0"), UpdateCheck.MacDiskImageName));
    }

    [Theory]
    [InlineData("/Applications/Fm27Editor.app/Contents/MacOS/Fm27Editor", true, "/Applications/Fm27Editor.app")]
    [InlineData("/Users/me/Apps/FM27 Editor.app/Contents/MacOS/Fm27Editor", true, "/Users/me/Apps/FM27 Editor.app")]
    [InlineData("/Volumes/FM27 Editor/Fm27Editor.app/Contents/MacOS/Fm27Editor", true, null)]
    [InlineData("/private/var/folders/x/AppTranslocation/ABC/d/Fm27Editor.app/Contents/MacOS/Fm27Editor", true, null)]
    [InlineData("/Applications/Fm27Editor.app/Contents/MacOS/Fm27Editor", false, null)]
    [InlineData("/Users/me/src/bin/Release/net10.0/Fm27Editor", true, null)]
    [InlineData("C:\\Tools\\Fm27Editor.exe", true, null)]
    public void Only_an_installed_apple_silicon_bundle_updates_itself(string path, bool arm64, string? bundle) =>
        Assert.Equal(bundle, UpdateCheck.MacBundleOf(path, arm64));

    [Fact]
    public void A_downloaded_disk_image_replaces_the_bundle_and_cleanup_removes_the_old_one()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string dir = Directory.CreateTempSubdirectory("fm27macupdate").FullName;
        try
        {
            string MakeBundle(string root, string text)
            {
                string bundle = Path.Combine(root, "Fm27Editor.app");
                Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
                File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "Fm27Editor"), text);
                return bundle;
            }
            string installed = MakeBundle(Path.Combine(dir, "Applications"), "old");
            string source = Path.Combine(dir, "image");
            MakeBundle(source, "new");
            Directory.CreateSymbolicLink(Path.Combine(source, "Applications"), "/Applications");
            string image = Path.Combine(dir, "update.dmg");
            var create = System.Diagnostics.Process.Start("hdiutil", ["create", "-quiet", "-volname", "FM27 Editor", "-srcfolder", source, "-format", "UDZO", image])!;
            create.WaitForExit();
            Assert.Equal(0, create.ExitCode);

            UpdateCheck.InstallMacBundle(image, installed);
            Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Contents", "MacOS", "Fm27Editor")));
            string old = Path.Combine(dir, "Applications", ".Fm27Editor.app.old");
            Assert.Equal("old", File.ReadAllText(Path.Combine(old, "Contents", "MacOS", "Fm27Editor")));
            UpdateCheck.CleanUpMac(installed);
            Assert.False(Directory.Exists(old));
            Assert.Equal(["Fm27Editor.app"], Directory.GetFileSystemEntries(Path.Combine(dir, "Applications")).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_update_window_shows_download_progress_and_blocks_cancel_while_installing()
    {
        AvaloniaTestThread.Run(() =>
        {
            using var cancel = new CancellationTokenSource();
            var release = new ReleaseInfo("v9.9.9", UpdateCheck.MacDiskImageName, "https://x/a.dmg", "https://x/SHA256SUMS.txt");
            var (window, show, finish) = MainWindow.UpdateProgressWindow(release, cancel);
            var bar = window.GetLogicalDescendants().OfType<Avalonia.Controls.ProgressBar>().Single();
            var text = window.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().First();
            var cancelButton = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().Single();

            show(16 * 1024 * 1024, 32 * 1024 * 1024, null);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(50, bar.Value);
            Assert.Contains("16.0 / 32.0 MB", text.Text);

            show(0, null, "Installing the new version…");
            Assert.True(bar.IsIndeterminate);
            Assert.False(cancelButton.IsEnabled);
            Assert.False(cancel.IsCancellationRequested);
            finish();
        });
    }
}
