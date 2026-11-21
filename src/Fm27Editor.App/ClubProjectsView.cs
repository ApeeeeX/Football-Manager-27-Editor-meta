using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.History;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    Control BuildClubProjectsCard(GameSession session, ClubEntry club)
    {
        var data = backend.Data;
        var edits = backend.Edits;
        var projects = new ClubProjects(session.Memory, session.Profile, session.ModuleBase);
        var refresh = new Button { Content = Loc.S("Refresh"), MinHeight = 24, Padding = new Thickness(9, 2) };
        var entries = new StackPanel();
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UiKit.TextSecondary, IsVisible = false };
        var body = new StackPanel { Margin = new Thickness(10, 4), Spacing = 4, Children = { entries, message } };
        var card = UiKit.Card("Club projects", body, rightHeader: refresh);
        bool busy = false;

        card.AttachedToVisualTree += async (_, _) => await RefreshAsync();
        refresh.Click += async (_, _) => await RefreshAsync();
        return card;

        bool CurrentSave() => data is not null && ReferenceEquals(backend.Data, data) &&
            ReferenceEquals(data.Session, session) && !backend.SaveChanged;

        async Task RefreshAsync()
        {
            if (busy || !CurrentSave()) return;
            SetBusy(true);
            ShowMessage(Loc.S("Loading club projects…"));
            try
            {
                var overview = await Task.Run(() => projects.ReadOverview(club));
                if (!CurrentSave()) return;

                entries.Children.Clear();
                foreach (var project in overview.Scheduled)
                {
                    bool due = project.ScheduledDate <= overview.Today;
                    var date = DateSetting.Format(project.ScheduledDate);
                    AddRow(ProjectName(project.Kind), due ? Loc.S("Due today") : Loc.F("Due {0}", date),
                        due || !session.CanWrite ? null : ActionButton("Finish today", () => FinishAsync([project], overview.Today)));
                }
                if (overview.Planning is { } plan)
                    AddRow("New stadium", plan.ExpiryDate is { } date
                        ? Loc.F("Planning · Expires {0}", DateSetting.Format(date))
                        : Loc.S("Planning · Expiry date unknown"), null);

                var pending = overview.Scheduled.Where(project => project.ScheduledDate > overview.Today).ToArray();
                if (session.CanWrite && pending.Length > 1)
                {
                    var finishAll = ActionButton("Finish all today", () => FinishAsync(pending, overview.Today));
                    finishAll.HorizontalAlignment = HorizontalAlignment.Right;
                    finishAll.Margin = new Thickness(0, 6);
                    entries.Children.Add(finishAll);
                }
                ShowMessage(entries.Children.Count == 0 ? Loc.S("No club projects") : null);
            }
            catch (Exception ex) { ShowMessage(UserMessage.For(ex)); }
            finally { SetBusy(false); }
        }

        async Task FinishAsync(IReadOnlyList<ClubProject> selected, DateOnly today)
        {
            if (busy || !CurrentSave()) return;
            SetBusy(true);
            ShowMessage(null);
            try
            {
                var byAddress = selected.ToDictionary(project => project.DateAddress);
                await Task.Run(() => edits.Execute("Club", club.DisplayName, "Finish Club Projects", () =>
                    projects.BringForward(club, selected, today),
                    (address, _) => byAddress.TryGetValue(address, out var project)
                        ? new ChangeLabel(ProjectName(project.Kind), Kind: ChangeValueKind.Date,
                            ValidateUndo: source => projects.ValidateUndo(source, club, project, today))
                        : null));
            }
            catch (Exception ex)
            {
                ShowMessage(UserMessage.For(ex));
                return;
            }
            finally { SetBusy(false); }

            await RefreshAsync();
            if (CurrentSave()) status.Text = Loc.S("Project completion dates moved to today. Continue in FM to see updates.");
        }

        void SetBusy(bool value)
        {
            busy = value;
            refresh.IsEnabled = entries.IsEnabled = !value && CurrentSave();
        }

        void ShowMessage(string? text)
        {
            message.Text = text;
            message.IsVisible = text is not null;
        }

        static Button ActionButton(string label, Func<Task> action)
        {
            var button = new Button { Content = Loc.S(label), VerticalAlignment = VerticalAlignment.Center };
            button.Click += async (_, _) => await action();
            return button;
        }

        void AddRow(string name, string detail, Button? action)
        {
            var summary = new StackPanel
            {
                Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = Loc.S(name), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = UiKit.TextSecondary },
                },
            };
            entries.Children.Add(new Border
            {
                BorderThickness = new Thickness(0, entries.Children.Count == 0 ? 0 : 1, 0, 0),
                BorderBrush = UiKit.BorderSubtle, Padding = new Thickness(0, 7), Child = UiKit.ActionRow(summary, action),
            });
        }
    }

    static string ProjectName(ClubProjectKind kind) => kind switch
    {
        ClubProjectKind.StadiumExpansion => "Stadium expansion",
        _ => "Facility upgrade",
    };
}
