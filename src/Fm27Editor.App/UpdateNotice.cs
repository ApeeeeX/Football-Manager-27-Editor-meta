using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// At start: if GitHub has a newer release, offer to install it. Update now downloads the new exe, checks it against the
    /// release checksum, puts it in place of this one and restarts. Anything that goes wrong leaves this version as it was
    /// and offers the download page instead.
    /// </summary>
    static bool IsSelfUpdatable(string? exe) =>
        exe is not null && Path.GetFileName(exe).Equals(UpdateCheck.ExeName, StringComparison.OrdinalIgnoreCase);

    static string? MacBundle() => OperatingSystem.IsMacOS()
        ? UpdateCheck.MacBundleOf(Environment.ProcessPath, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        : null;

    async Task NotifyIfUpdateAsync()
    {
        string? exe = Environment.ProcessPath;
        string? bundle = MacBundle();
        if (IsSelfUpdatable(exe))
            await Task.Run(() => UpdateCheck.CleanUp(exe!));
        if (bundle is not null)
            await Task.Run(() => UpdateCheck.CleanUpMac(bundle));
        var release = await UpdateCheck.NewerReleaseAsync(bundle is not null ? UpdateCheck.MacDiskImageName : UpdateCheck.ExeName);
        if (release is null) return;
        bool selfUpdatable = IsSelfUpdatable(exe) || bundle is not null;
        await Dispatcher.UIThread.InvokeAsync(() =>
            ShowNotice(Loc.S("Update available"),
                selfUpdatable
                    ? Loc.F("Version {0} is out. Update now downloads it, closes the editor and opens the new version. Changes you have not saved yet are lost; anything running in the game (Development Speed, Wonderkid Academy) keeps running.", release.Tag)
                    : Loc.F("Version {0} is out. This copy cannot replace itself, so Update now opens the download page for it.", release.Tag),
                (Loc.S("Later"), false, null),
                (Loc.S("Update now"), true, () => UpdateAsync(release))));
    }

    async Task UpdateAsync(ReleaseInfo release)
    {
        string? exe = Environment.ProcessPath;
        string? bundle = MacBundle();
        // Only the single-file release exe and the macOS app bundle can replace themselves; anything else goes to the download page.
        if (!IsSelfUpdatable(exe) && bundle is null)
        {
            OpenReleasesPage();
            return;
        }
        using var cancel = new CancellationTokenSource();
        var (window, show, finish) = UpdateProgressWindow(release, cancel);
        window.Show(this);
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p => show(p.Done, p.Total, null));
            if (bundle is not null)
            {
                string image = Path.Combine(Path.GetTempPath(), $"fm27editor-{release.Tag}.dmg");
                try
                {
                    await UpdateCheck.DownloadAsync(release, image, cancel.Token, progress);
                    show(0, null, Loc.S("Installing the new version…"));
                    await Task.Run(() => UpdateCheck.InstallMacBundle(image, bundle));
                }
                finally { try { File.Delete(image); } catch (IOException) { } }
                show(0, null, Loc.S("Opening the new version…"));
                // Open the new bundle once this process has gone, so macOS starts it instead of focusing this one.
                var relaunch = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                foreach (var arg in new[] { "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; open \"$2\"", "relaunch", Environment.ProcessId.ToString(), bundle })
                    relaunch.ArgumentList.Add(arg);
                System.Diagnostics.Process.Start(relaunch);
            }
            else
            {
                string downloaded = await UpdateCheck.DownloadAsync(release, exe! + ".new", cancel.Token, progress);
                show(0, null, Loc.S("Installing the new version…"));
                UpdateCheck.Swap(exe!, downloaded);
                show(0, null, Loc.S("Opening the new version…"));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            finish();
            status.Text = Loc.S("Update cancelled. This version is unchanged.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            finish();
            status.Text = Loc.F("Update failed: {0}", ex.Message);
            ShowNotice(Loc.S("Update failed"),
                Loc.F("The update could not be installed ({0}). This version is unchanged. You can download {1} yourself instead.", ex.Message, release.Tag),
                (Loc.S("Close"), false, null),
                (Loc.S("Download manually"), true, () => { OpenReleasesPage(); return Task.CompletedTask; }));
            return;
        }
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Close();
    }

    /// <summary>A small window with the download's progress bar, what is happening, and Cancel while the download runs.</summary>
    internal static (Window Window, Action<long, long?, string?> Show, Action Finish) UpdateProgressWindow(ReleaseInfo release, CancellationTokenSource cancel)
    {
        var text = new TextBlock { Text = Loc.F("Downloading {0}…", release.Tag), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, MinHeight = 8, IsIndeterminate = true };
        var cancelButton = new Button { Content = Loc.S("Cancel"), MinWidth = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var window = new Window
        {
            Title = Loc.F("Updating to {0}", release.Tag), Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 14, Children = { text, bar, cancelButton } },
        };
        cancelButton.Click += (_, _) => cancel.Cancel();
        bool finished = false;
        // Closing the window while it downloads means Cancel; once installing, it stays until the editor restarts.
        window.Closing += (_, e) =>
        {
            if (finished) return;
            e.Cancel = true;
            if (cancelButton.IsEnabled) cancel.Cancel();
        };
        void Show(long done, long? total, string? stage)
        {
            if (stage is not null)
            {
                // Past the download there is nothing safe to stop: the swap either completes or puts the old version back.
                text.Text = stage;
                bar.IsIndeterminate = true;
                cancelButton.IsEnabled = false;
                return;
            }
            const double MB = 1024 * 1024;
            if (total is > 0)
            {
                bar.IsIndeterminate = false;
                bar.Value = Math.Min(100, done * 100.0 / total.Value);
                text.Text = Loc.F("Downloading {0}… {1:0.0} / {2:0.0} MB", release.Tag, done / MB, total.Value / MB);
            }
            else text.Text = Loc.F("Downloading {0}… {1:0.0} MB", release.Tag, done / MB);
        }
        return (window, Show, () => { finished = true; window.Close(); });
    }

    void OpenReleasesPage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateCheck.ReleasesPage) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { status.Text = Loc.F("Could not open the browser: {0}", UpdateCheck.ReleasesPage); }
    }
}
