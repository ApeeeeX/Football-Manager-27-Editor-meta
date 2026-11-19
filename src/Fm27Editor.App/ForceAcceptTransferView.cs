using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.ForceAccept;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    event Action? forceBoardRequestChanged;

    Control BuildClubBoardRequestsCard(GameSession session) =>
        BuildForceBoardRequestCard(session, includeRefreshButton: false);

    Control BuildForceBoardRequestSection(GameSession session) =>
        BuildForceBoardRequestCard(session, includeRefreshButton: true);

    Control BuildForceBoardRequestCard(GameSession session, bool includeRefreshButton)
    {
        var heading = new TextBlock
        {
            Text = Loc.S("Force Board Request (Experimental)"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleControl = UiKit.TitleWithInfo(
            heading,
            Loc.S("The board accepts every board request and Club Vision change you submit while this is turned on."));

        // A build whose profile has no measured board gates (or an architecture without a patch) cannot offer it.
        if (session.ForceAccept.Plan(ForceAcceptPreset.BoardRequests).State == ForceAcceptPlanState.Unavailable)
        {
            return UiKit.Card(
                Loc.S("Force Board Request (Experimental)"),
                new TextBlock
                {
                    Text = Loc.S("Unavailable on this game build."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = UiKit.TextMuted,
                },
                customTitle: titleControl);
        }

        var planner = session.ForceAccept;
        var edits = backend.Edits;

        var on = new CheckBox
        {
            Content = Loc.S("On"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stateText = new TextBlock
        {
            Text = Loc.S("Checking…"),
            FontSize = 12.5,
            Foreground = UiKit.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var statusText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = UiKit.Brush("#E06C75"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        bool syncing = false;
        bool busy = false;

        void RenderPlan(ForceAcceptPlan plan)
        {
            syncing = true;
            try
            {
                on.IsEnabled = plan.State is ForceAcceptPlanState.Appliable or ForceAcceptPlanState.Active;
                on.IsChecked = plan.State == ForceAcceptPlanState.Active;
                stateText.Text = plan.State switch
                {
                    ForceAcceptPlanState.Active => Loc.S("On"),
                    ForceAcceptPlanState.Appliable => Loc.S("Off"),
                    ForceAcceptPlanState.Blocked => Loc.S("Unavailable · unexpected code bytes"),
                    _ => Loc.S("Unavailable on this build"),
                };
            }
            finally { syncing = false; }
        }

        async Task RefreshAsync()
        {
            if (busy) return;
            busy = true;
            try
            {
                var plan = await Task.Run(() => planner.Plan(ForceAcceptPreset.BoardRequests));
                RenderPlan(plan);
            }
            catch (Exception ex)
            {
                statusText.Text = UserMessage.For(ex);
                statusText.IsVisible = true;
            }
            finally { busy = false; }
        }

        var row = new DockPanel { LastChildFill = true };
        if (includeRefreshButton)
        {
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Children = { on },
            };
            var refreshBtn = new Button
            {
                Content = Loc.S("Refresh"),
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            controls.Children.Add(refreshBtn);
            DockPanel.SetDock(controls, Dock.Right);
            row.Children.Add(controls);

            refreshBtn.Click += async (_, _) =>
            {
                statusText.IsVisible = false;
                await RefreshAsync();
            };
        }
        else
        {
            on.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(on, Dock.Right);
            row.Children.Add(on);
        }
        row.Children.Add(stateText);

        var body = new StackPanel
        {
            Spacing = 6,
            Children = { row, statusText },
        };
        var card = UiKit.Card(
            Loc.S("Force Board Request (Experimental)"),
            body,
            customTitle: titleControl);

        on.IsCheckedChanged += async (_, _) =>
        {
            if (syncing || busy) return;
            bool enable = on.IsChecked == true;
            busy = true;
            statusText.IsVisible = false;
            try
            {
                var updated = await Task.Run(() =>
                {
                    var current = planner.Plan(ForceAcceptPreset.BoardRequests);
                    if (enable)
                    {
                        if (current.State != ForceAcceptPlanState.Appliable)
                            throw new InvalidOperationException("Force Board Request cannot be enabled in its current state.");
                        edits.Execute("Force Accept", "Board requests", "Force accept", () => planner.Apply(current, gamePaused: true));
                    }
                    else if (current.State == ForceAcceptPlanState.Active)
                    {
                        edits.Execute("Force Accept", "Board requests", "Restore", () => planner.Restore(current, gamePaused: true));
                    }
                    return planner.Plan(ForceAcceptPreset.BoardRequests);
                });
                RenderPlan(updated);
                forceBoardRequestChanged?.Invoke();
            }
            catch (Exception ex)
            {
                syncing = true;
                on.IsChecked = !enable;
                syncing = false;
                statusText.Text = UserMessage.For(ex);
                statusText.IsVisible = true;
            }
            finally { busy = false; }
        };

        void OnExternalChange()
        {
            if (!busy)
                _ = RefreshAsync();
        }

        forceBoardRequestChanged += OnExternalChange;
        card.DetachedFromVisualTree += (_, _) => forceBoardRequestChanged -= OnExternalChange;

        try
        {
            RenderPlan(planner.Plan(ForceAcceptPreset.BoardRequests));
        }
        catch (Exception ex)
        {
            on.IsEnabled = false;
            stateText.Text = Loc.S("Unavailable on this build");
            statusText.Text = UserMessage.For(ex);
            statusText.IsVisible = true;
        }

        return card;
    }

    Control BuildForceAcceptTransferSection(GameSession session)
    {
        var hook = session.ForceTransferOffersHook;
        var edits = backend.Edits;
        var panel = new StackPanel { Spacing = 6 };
        if (!hook.IsArchitectureSupported)
        {
            panel.Children.Add(UiKit.Card(Loc.S("Force Accept Transfer"), new TextBlock
            {
                Text = Loc.S("This hook is verified only for x64 Windows game builds; it is disabled on this architecture."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = UiKit.TextMuted,
            }));
            return panel;
        }

        var heading = new TextBlock
        {
            Text = Loc.S("Force Accept Transfer (Experimental)"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(UiKit.TitleWithInfo(
            heading,
            "Clubs accept transfer offers made by the club you manage. Works while active without affecting offers made by AI clubs."));

        var on = new CheckBox { Content = Loc.S("On"), VerticalAlignment = VerticalAlignment.Center };
        var refreshBtn = new Button
        {
            Content = Loc.S("Refresh"),
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var removeBtn = new Button
        {
            Content = Loc.S("Remove"),
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = false,
        };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { on, refreshBtn, removeBtn },
        });

        var stateText = new TextBlock
        {
            Text = Loc.S("Checking…"),
            FontSize = 12.5,
            Foreground = UiKit.Brush("#8FA3B8"),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(stateText);

        var status = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = UiKit.Brush("#8FA3B8"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        panel.Children.Add(status);

        bool syncing = false;
        bool busy = false;

        IReadOnlyList<(int Id, string Name)> ReadManagedClubs()
        {
            if (session.Profile.Club?.Id is not { } idOff) return [];
            var result = new List<(int Id, string Name)>();
            var seen = new HashSet<int>();
            foreach (var staff in session.Staff.LoadAll())
            {
                if (!staff.Kind.Manager || staff.ManagerAttributes is null || staff.Club is not { } club) continue;
                var raw = new byte[4];
                if (!session.Memory.TryRead(club.Address + (ulong)idOff, raw)) continue;
                int id = BitConverter.ToInt32(raw, 0);
                if (id == 0 || id == -1 || !seen.Add(id)) continue;
                result.Add((id, string.IsNullOrWhiteSpace(club.Name) ? $"club {id}" : club.Name));
            }
            return result;
        }

        void RenderState(ForceTransferOffersState? state, IReadOnlyList<(int Id, string Name)> clubs)
        {
            syncing = true;
            try
            {
                on.IsChecked = state?.Enabled == true;
                removeBtn.IsEnabled = state is not null;
                if (state is null)
                {
                    stateText.Text = clubs.Count == 0
                        ? "Not installed · no managed club found"
                        : $"Not installed · managed clubs: {string.Join(", ", clubs.Select(c => $"{c.Name} ({c.Id})"))}";
                }
                else
                {
                    var nameMap = clubs.ToDictionary(c => c.Id, c => c.Name);
                    string clubLabel = state.ClubIds.Count == 0
                        ? "none"
                        : string.Join(", ", state.ClubIds.Select(id => nameMap.TryGetValue(id, out var n) ? $"{n} ({id})" : $"club {id}"));
                    stateText.Text = $"{(state.Enabled ? "On" : "Off")} · offers seen {state.Calls} · accepted {state.Forced} · clubs: {clubLabel}";
                }
            }
            finally { syncing = false; }
        }

        async Task RefreshSectionAsync()
        {
            if (busy) return;
            busy = true;
            try
            {
                var (state, clubs) = await Task.Run(() => (hook.Read(), ReadManagedClubs()));
                RenderState(state, clubs);
            }
            catch (Exception ex)
            {
                status.Text = UserMessage.For(ex);
                status.IsVisible = true;
            }
            finally { busy = false; }
        }

        on.IsCheckedChanged += async (_, _) =>
        {
            if (syncing || busy) return;
            bool enable = on.IsChecked == true;
            busy = true;
            status.IsVisible = false;
            try
            {
                var result = await Task.Run(() =>
                {
                    var clubs = ReadManagedClubs();
                    if (enable)
                    {
                        if (clubs.Count == 0)
                            throw new InvalidOperationException("No managed club found in the loaded save.");
                        ForceTransferOffersState? s = null;
                        edits.Execute("Hook", "Force Accept Transfer", "Configure", () => s = hook.Configure(true, clubs.Select(c => c.Id)));
                        return (s, clubs);
                    }
                    else
                    {
                        var existing = hook.Read();
                        var ids = existing?.ClubIds ?? clubs.Select(c => c.Id).ToList();
                        ForceTransferOffersState? s = null;
                        if (existing is not null) edits.Execute("Hook", "Force Accept Transfer", "Configure", () => s = hook.Configure(false, ids));
                        return (s, clubs);
                    }
                });
                RenderState(result.s, result.clubs);
                status.Text = enable ? "Force Accept Transfer enabled." : "Force Accept Transfer turned off.";
                status.IsVisible = true;
            }
            catch (Exception ex)
            {
                syncing = true;
                on.IsChecked = !enable;
                syncing = false;
                status.Text = UserMessage.For(ex);
                status.IsVisible = true;
            }
            finally { busy = false; }
        };

        removeBtn.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            status.IsVisible = false;
            try
            {
                var clubs = await Task.Run(() =>
                {
                    edits.Execute("Hook", "Force Accept Transfer", "Remove", hook.Uninstall);
                    return ReadManagedClubs();
                });
                RenderState(null, clubs);
                status.Text = Loc.S("Force Accept Transfer removed.");
                status.IsVisible = true;
            }
            catch (Exception ex)
            {
                status.Text = UserMessage.For(ex);
                status.IsVisible = true;
            }
            finally { busy = false; }
        };

        refreshBtn.Click += async (_, _) =>
        {
            status.IsVisible = false;
            await RefreshSectionAsync();
        };

        _ = RefreshSectionAsync();
        return panel;
    }
}
