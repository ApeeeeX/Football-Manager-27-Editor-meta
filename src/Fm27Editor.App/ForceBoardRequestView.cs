using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.ForceAccept;
using Fm27Editor.Core.Game;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    event Action? forceBoardRequestChanged;

    Control BuildBoardRequestsCard(GameSession session, ulong? clubAddress = null)
    {
        var planner = session.ForceAccept;
        var data = backend.Data;
        var edits = backend.Edits;
        var state = new TextBlock
        {
            Text = Loc.S("Checking…"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.TextSecondary,
        };
        var toggle = new Button
        {
            Content = Loc.S("Turn on"), IsEnabled = false, MinWidth = 86,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, Loc.S("The board accepts every board request and Club Vision change you submit while this is turned on."));
        var row = UiKit.ActionRow(state, toggle);
        var message = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = UiKit.Brush("#E06C75") };
        var card = UiKit.Card("Force Board Request", new StackPanel
        {
            Spacing = 6, Margin = new Thickness(10, 6), Children = { row, message },
        });
        ForceAcceptPlanState? displayed = null;
        bool busy = false;

        bool CurrentSave() => data is not null && ReferenceEquals(backend.Data, data) &&
            ReferenceEquals(data.Session, session) && !backend.SaveChanged;

        void Render(ForceAcceptPlanState next)
        {
            displayed = next;
            state.Text = Loc.S(next switch
            {
                ForceAcceptPlanState.Active => "On",
                ForceAcceptPlanState.Appliable => "Off",
                ForceAcceptPlanState.Blocked => "Unavailable · unexpected code bytes",
                _ => "Unavailable on this build",
            });
            toggle.Content = Loc.S(next == ForceAcceptPlanState.Active ? "Turn off" : "Turn on");
            toggle.IsVisible = next is ForceAcceptPlanState.Appliable or ForceAcceptPlanState.Active;
            toggle.IsEnabled = toggle.IsVisible && session.CanWrite && CurrentSave();
        }

        bool CheckClubAccess()
        {
            card.IsVisible = clubAddress is null || FindHumanClubManager(session, clubAddress.Value) is not null;
            return card.IsVisible;
        }

        async Task RefreshAsync()
        {
            if (busy || !CurrentSave() || !CheckClubAccess()) return;
            busy = true;
            toggle.IsEnabled = false;
            try
            {
                var plan = await Task.Run(() => planner.Plan(ForceAcceptPreset.BoardRequests));
                if (CurrentSave()) Render(plan.State);
            }
            catch (Exception ex)
            {
                Render(ForceAcceptPlanState.Unavailable);
                ShowError(ex);
            }
            finally { busy = false; }
        }

        void ShowError(Exception ex)
        {
            message.Text = UserMessage.For(ex);
            message.IsVisible = true;
        }

        toggle.Click += async (_, _) =>
        {
            if (busy || !CurrentSave() || !CheckClubAccess() || displayed is not { } expected ||
                expected is not (ForceAcceptPlanState.Active or ForceAcceptPlanState.Appliable)) return;
            bool enable = expected == ForceAcceptPlanState.Appliable;
            busy = true;
            toggle.IsEnabled = false;
            message.IsVisible = false;
            try
            {
                await Task.Run(() => edits.Execute("Hook", "Force Board Request", enable ? "Turn on" : "Turn off", () =>
                {
                    var plan = planner.Plan(ForceAcceptPreset.BoardRequests);
                    if (plan.State != expected) throw new InvalidOperationException("The board request hook changed. Try again.");
                    if (enable) planner.Apply(plan, gamePaused: true);
                    else planner.Restore(plan, gamePaused: true);
                }));
                forceBoardRequestChanged?.Invoke();
            }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; }
            await RefreshAsync();
        };

        void OnExternalChange()
        {
            if (CurrentSave()) message.IsVisible = false;
            _ = RefreshAsync();
        }

        card.AttachedToVisualTree += (_, _) =>
        {
            forceBoardRequestChanged += OnExternalChange;
            _ = RefreshAsync();
        };
        card.DetachedFromVisualTree += (_, _) => forceBoardRequestChanged -= OnExternalChange;
        return card;
    }
}
