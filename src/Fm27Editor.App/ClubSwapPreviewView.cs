using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Competitions;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.History;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    Control BuildClubSwapPreviewCard(GameSession session, ClubEntry club)
    {
        var open = new Button
        {
            Content = Loc.S("Swap Clubs"), HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = new ClubSwap(session.Memory, session.Profile, session.ModuleBase).IsSupported,
        };
        open.Click += async (_, _) =>
        {
            open.IsEnabled = false;
            try { await ShowClubSwapPreviewAsync(session, club); }
            finally { open.IsEnabled = true; }
        };
        return UiKit.Card("Swap Clubs", new Border { Padding = new Thickness(8), Child = open });
    }

    async Task ShowClubSwapPreviewAsync(GameSession session, ClubEntry first)
    {
        if (backend.Data is not { } data || !ReferenceEquals(data.Session, session) || backend.SaveChanged) return;
        var preview = new ClubSwapPreview(session.Memory, session.Profile, session.ModuleBase);
        // Refuse at once when this club cannot take part (season started, no table), before asking for the other club.
        if (!await CheckAsync(() => preview.CheckClub(first))) return;
        var clubs = loadedClubs.ToArray();
        var second = await PickerDialog.Show(this, "Choose the other club", query =>
            clubs.Where(club => club.Id is > 0 && club.FirstTeam is not null && club.Address != first.Address)
                .Select(club => (Club: club, Score: PickerSearch.Score(query, club.Name, club.ShortName, club.Id)))
                .Where(item => item.Score >= 0).OrderByDescending(item => item.Score)
                .ThenBy(item => item.Club.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(150).Select(item => (item.Club.DisplayName, item.Club)).ToArray());
        if (second is null || !CurrentSave()) return;
        if (!await CheckAsync(() => preview.Read(first, second))) return;

        var editor = new ClubSwap(session.Memory, session.Profile, session.ModuleBase);
        var message = new TextBlock { Text = Loc.F("Swap {0} and {1}?", first.DisplayName, second.DisplayName), TextWrapping = TextWrapping.Wrap };
        var close = new Button { Content = Loc.S("Close"), IsCancel = true };
        var apply = new Button { Content = Loc.S("Swap Clubs"), Classes = { "accent" } };
        var window = new Window
        {
            Title = Loc.S("Swap Clubs"), Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(14), Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { close, apply } },
                },
            },
        };
        close.Click += (_, _) => window.Close();
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = close.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    var schedule = preview.Read(first, second);
                    Func<ulong, int, ChangeLabel?>? labels = null;
                    backend.Edits.Execute("Club", $"{first.DisplayName} ↔ {second.DisplayName}", "Swap Clubs", () =>
                    {
                        if (!CurrentSave()) throw new SaveChangedException();
                        labels = editor.Apply(schedule);
                    }, (address, length) => labels?.Invoke(address, length));
                });
                status.Text = Loc.F("Swapped {0} and {1}. Continue in FM to see updates.", first.DisplayName, second.DisplayName);
                window.Close();
            }
            catch (Exception ex)
            {
                // Nothing was written; trying again would fail the same way, so only Close is left.
                window.Title = Loc.S("Cannot swap clubs");
                message.Text = UserMessage.For(ex);
                apply.IsVisible = false;
            }
            finally { close.IsEnabled = true; }
        };
        try { await window.ShowDialog(this); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); }

        bool CurrentSave() => ReferenceEquals(backend.Data, data) && !backend.SaveChanged;

        async Task<bool> CheckAsync(Action check)
        {
            try
            {
                await Task.Run(check);
                return CurrentSave();
            }
            catch (Exception ex)
            {
                await CreateNotice("Cannot swap clubs", UserMessage.For(ex), ("Close", true, null)).ShowDialog(this);
                return false;
            }
        }
    }
}
