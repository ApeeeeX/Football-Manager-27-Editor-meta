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
        var open = new Button { Content = Loc.S("Preview Club Swap"), HorizontalAlignment = HorizontalAlignment.Left };
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
        var clubs = loadedClubs.ToArray();
        var second = await PickerDialog.Show(this, "Choose the other club", query =>
            clubs.Where(club => club.Id is > 0 && club.FirstTeam is not null && club.Address != first.Address)
                .Select(club => (Club: club, Score: PickerSearch.Score(query, club.Name, club.ShortName, club.Id)))
                .Where(item => item.Score >= 0).OrderByDescending(item => item.Score)
                .ThenBy(item => item.Club.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(150).Select(item => (item.Club.DisplayName, item.Club)).ToArray());
        if (second is null || !CurrentSave()) return;
        try
        {
            status.Text = Loc.S("Checking both league schedules…");
            var preview = await Task.Run(() => new ClubSwapPreview(session.Memory, session.Profile, session.ModuleBase).Read(first, second));
            if (!CurrentSave()) return;
            var names = clubs.SelectMany(club => club.Teams.Select(team => (team.Address, club.DisplayName)))
                .GroupBy(team => team.Address).ToDictionary(team => team.Key, team => team.First().DisplayName);
            string Match(ulong home, ulong away) => $"{names.GetValueOrDefault(home, Loc.S("Unknown"))} – {names.GetValueOrDefault(away, Loc.S("Unknown"))}";
            var rows = ToolGrid.Create();
            rows.ItemsSource = preview.Fixtures;
            rows.Columns.Add(ToolGrid.Text<ClubSwapFixture>("Date", row => DateSetting.Format(row.Date), 105, "Date"));
            rows.Columns.Add(ToolGrid.Text<ClubSwapFixture>("Competition", row => row.Competition.DisplayName, 168, "Competition"));
            rows.Columns.Add(ToolGrid.Text<ClubSwapFixture>("Before", row => Match(row.Home, row.Away), 0, "Before", minWidth: 184));
            rows.Columns.Add(ToolGrid.Text<ClubSwapFixture>("After", row => Match(row.ProposedHome, row.ProposedAway), 0, "After", minWidth: 184));
            ToolGrid.EnableSort(rows, new Dictionary<string, Func<ClubSwapFixture, IComparable?>>
            {
                ["Date"] = row => row.Date,
                ["Competition"] = row => row.Competition.DisplayName,
                ["Before"] = row => Match(row.Home, row.Away),
                ["After"] = row => Match(row.ProposedHome, row.ProposedAway),
            });
            var title = new TextBlock
            {
                Text = $"{first.DisplayName} → {preview.SecondLeague.DisplayName}\n{second.DisplayName} → {preview.FirstLeague.DisplayName}",
                FontWeight = FontWeight.SemiBold,
            };
            var editor = new ClubSwap(session.Memory, session.Profile, session.ModuleBase);
            var description = new TextBlock
            {
                Text = Loc.F(editor.IsSupported ? "{0} affected league matches" : "{0} affected matches · Read-only preview", preview.Fixtures.Count),
                Foreground = UiKit.TextSecondary,
            };
            var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8), Children = { title, description } };
            DockPanel.SetDock(header, Dock.Top);
            var close = new Button { Content = Loc.S("Close"), IsCancel = true };
            var apply = new Button { Content = Loc.S("Swap Clubs"), IsVisible = editor.IsSupported };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
                Children = { close, apply },
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            var window = new Window
            {
                Title = Loc.S("Preview Club Swap"), Width = 920, Height = 610, MinWidth = 720, MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new DockPanel { Margin = new Thickness(12), Children = { header, actions, rows } },
            };
            close.Click += (_, _) => window.Close();
            apply.Click += async (_, _) =>
            {
                apply.IsEnabled = close.IsEnabled = false;
                try
                {
                    await Task.Run(() =>
                    {
                        Func<ulong, int, ChangeLabel?>? labels = null;
                        backend.Edits.Execute("Club", $"{first.DisplayName} ↔ {second.DisplayName}", "Swap Clubs", () =>
                        {
                            if (!CurrentSave()) throw new SaveChangedException();
                            labels = editor.Apply(preview);
                        }, (address, length) => labels?.Invoke(address, length));
                    });
                    status.Text = Loc.F("Clubs swapped across {0} league matches", preview.Fixtures.Count);
                    window.Close();
                }
                catch (Exception ex)
                {
                    description.Text = UserMessage.For(ex);
                    apply.IsEnabled = true;
                }
                finally { close.IsEnabled = true; }
            };
            status.Text = description.Text;
            await window.ShowDialog(this);
        }
        catch (Exception ex) { status.Text = UserMessage.For(ex); }

        bool CurrentSave() => ReferenceEquals(backend.Data, data) && !backend.SaveChanged;
    }
}
