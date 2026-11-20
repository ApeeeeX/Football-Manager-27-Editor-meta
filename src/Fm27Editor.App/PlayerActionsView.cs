using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    readonly Dictionary<ulong, Dictionary<PlayerField, (long Expected, long Value)>> playerScalarDrafts = [];

    void HookPlayerActions(PlayerRecord player)
    {
        var tab = playerTabs.FirstOrDefault(t => ReferenceEquals(t.Panel, editor));
        if (tab is not null) tab.Sticky.Content = CreatePlayerScalarsPanel(player);

        // 2. The actions panel is docked beside the section host by RenderEditor.

        UpdateScalarDraftState(player);
    }

    Control? CreatePlayerScalarsPanel(PlayerRecord player)
    {
        if (backend.State.Status != ConnectionStatus.Connected) return null;
        var fields = backend.Session.PlayerFields.ReadAll(player);
        if (fields.Count == 0) return null;

        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
        };

        bool hasAny = false;
        var existingDraft = playerScalarDrafts.GetValueOrDefault(player.Address);

        foreach (var d in SquadScalarDefinitions.Descriptors)
        {
            if (d.Field is not (PlayerField.Condition or PlayerField.MatchSharpness or PlayerField.Jadedness)) continue;
            if (!fields.TryGetValue(d.Field, out long snapshotVal))
                continue;

            hasAny = true;
            long currentVal = existingDraft?.TryGetValue(d.Field, out var staged) == true
                ? staged.Value
                : snapshotVal;

            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 20, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var lbl = new TextBlock
            {
                Text = Loc.S(d.Label),
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(lbl, $"{Loc.S(d.Label)} ({d.Min} .. {d.Max})");

            var num = new NumericUpDown
            {
                Minimum = d.Min,
                Maximum = d.Max,
                Value = (decimal)currentVal,
                Width = 68,
                MinWidth = 0,
                Height = 22,
                MinHeight = 22,
                ShowButtonSpinner = false,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Right,
            };

            num.ValueChanged += (_, _) =>
            {
                if (updating) return;
                if (num.Value is { } nv)
                {
                    long newL = (long)nv;
                    StageScalarChange(player, d.Field, snapshotVal, newL);
                }
            };

            item.Children.Add(lbl);
            item.Children.Add(num);
            panel.Children.Add(item);
        }

        return hasAny
            ? new Border
            {
                Background = UiKit.SurfaceAlt,
                BorderBrush = UiKit.BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 5),
                Child = panel,
            }
            : null;
    }

    void StageScalarChange(PlayerRecord player, PlayerField field, long snapshotVal, long newVal)
    {
        if (newVal == snapshotVal)
        {
            if (playerScalarDrafts.TryGetValue(player.Address, out var d))
            {
                d.Remove(field);
                if (d.Count == 0) playerScalarDrafts.Remove(player.Address);
            }
        }
        else
        {
            if (!playerScalarDrafts.TryGetValue(player.Address, out var d))
                playerScalarDrafts[player.Address] = d = [];
            d[field] = (snapshotVal, newVal);
        }

        UpdateScalarDraftState(player);
    }

    void UpdateScalarDraftState(PlayerRecord player)
    {
        int scalarCount = playerScalarDrafts.TryGetValue(player.Address, out var d) ? d.Count : 0;
        int stdCount = drafts.TryGetValue(player.Address, out var sd) ? sd.Count : 0;
        int total = scalarCount + stdCount + (informationDirty?.Invoke() == true ? 1 : 0);

        if (saveButton is not null) saveButton.IsEnabled = total > 0;
        status.Text = total > 0 ? Loc.F("{0} unsaved change(s)", total) : "";
        if (total > 0) SetActiveTabDirty(true);
        MarkEditedSection(player, total > 0);
    }

    /// <summary>
    /// The page's own edits (Information fields, positions, scalars) star the child tab they were made in; when none are
    /// left, those stars go. Child tabs with their own Save are starred by <see cref="SectionHost"/> itself.
    /// </summary>
    /// <summary>Child tabs starred by the page's own edits, per player (Information fields, positions, scalars).</summary>
    readonly Dictionary<ulong, HashSet<string>> playerPageSections = [];

    /// <summary>Stars <paramref name="key"/> while it holds edits saved by the shell's Save (the Contract tab).</summary>
    void MarkSection(PlayerRecord player, string key, bool dirty)
    {
        if (!playerPageSections.TryGetValue(player.Address, out var page)) playerPageSections[player.Address] = page = [];
        if (dirty) page.Add(key); else page.Remove(key);
        PublishSections(player);
    }

    /// <summary>
    /// The page's own edits star the child tab they were made in; when none are left those stars go (the Contract star
    /// stays while its draft has changes). Child tabs with their own Save are starred by <see cref="SectionHost"/>.
    /// </summary>
    void MarkEditedSection(PlayerRecord player, bool any)
    {
        if (!playerPageSections.TryGetValue(player.Address, out var page)) playerPageSections[player.Address] = page = [];
        if (!any) page.RemoveWhere(k => k != "Contract");
        else if (SectionHost.Selected("player") is { } key && key != "Contract") page.Add(key);
        PublishSections(player);
    }

    void PublishSections(PlayerRecord player)
    {
        if (!playerDirtySections.TryGetValue(player.Address, out var sections)) playerDirtySections[player.Address] = sections = [];
        sections.Clear();
        if (playerPageSections.TryGetValue(player.Address, out var page)) sections.UnionWith(page);
        SectionHost.MarkDirty("player", sections);
    }

    void HookScalarDraftButtons(PlayerRecord player)
    {
        UpdateScalarDraftState(player);

        if (saveButton is not null)
        {
            saveButton.Click += (_, _) =>
            {
                if (playerScalarDrafts.TryGetValue(player.Address, out var d) && d.Count > 0)
                {
                    int saved = 0;
                    foreach (var (f, (expected, val)) in d.ToArray())
                    {
                        try
                        {
                            backend.Edits.Execute("Player", player.DisplayName, "Page save", () => backend.Session.PlayerFields.Set(player, f, expected, val));
                            d.Remove(f);
                            saved++;
                        }
                        catch (Exception ex)
                        {
                            status.Text = Loc.F("Failed to save {0}: {1}", f, UserMessage.For(ex));
                            return;
                        }
                    }
                    if (d.Count == 0)
                        playerScalarDrafts.Remove(player.Address);
                    status.Text = Loc.F("Saved {0} change(s). Continue in FM to see updates.", saved);
                    RenderEditor();
                }
            };
        }

    }

    void RegisterPlayerLabels(PlayerRecord p) =>
        backend.Edits.RegisterLabels("Player", p.DisplayName, Fm27Editor.Core.History.PlayerChangeLabels.For(backend.Session.Profile, p.Address));

    Control CreatePlayerActionsPanel(
        PlayerRecord? player,
        bool isSquadMode,
        Func<IReadOnlyList<PlayerRecord>>? getSquadPlayers,
        Action<string>? onStatus,
        Func<IReadOnlyList<PlayerRecord>>? getSelectedPlayers = null,
        Action? onApplied = null,
        Action<Action>? registerSelectionUpdater = null,
        Func<IReadOnlyList<PlayerRecord>>? getYouthPlayers = null,
        ulong? squadClub = null)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(8, 8),
        };

        static Grid Pair(Control left, Control right)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            g.Children.Add(left);
            g.Children.Add(right);
            return g;
        }

        static TextBlock Subhead(string text, bool first = false) => new()
        {
            Text = Loc.S(text),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = UiKit.Brush("#7E8BA3"),
            LetterSpacing = 0.4,
            Margin = new Thickness(1, first ? 2 : 6, 0, 1),
        };

        if (!isSquadMode && player is not null)
        {
            if (backend.Freezer is not null)
            {
                var freeze = new Button
                {
                    Content = Loc.S("Freeze"),
                    FontSize = 12.5,
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(3, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                freeze.Click += (_, _) =>
                {
                    try
                    {
                        backend.Edits.Execute(() => backend.Freezer.SetPlayer(player, FrozenPlayer.AllAttributes(player)));
                        onStatus?.Invoke(Loc.F("Frozen: {0}", player.DisplayName));
                    }
                    catch (Exception ex) { onStatus?.Invoke(Loc.F("Freezer: {0}", UserMessage.For(ex))); }
                };

                var unfreeze = new Button
                {
                    Content = Loc.S("Unfreeze"),
                    FontSize = 12.5,
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(3, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                unfreeze.Click += (_, _) =>
                {
                    try
                    {
                        if (player.Id is { } pid)
                        {
                            backend.Edits.Execute(() => backend.Freezer.RemovePlayer(pid));
                            onStatus?.Invoke(Loc.F("Unfrozen: {0}", player.DisplayName));
                        }
                    }
                    catch (Exception ex) { onStatus?.Invoke(Loc.F("Freezer: {0}", UserMessage.For(ex))); }
                };

                panel.Children.Add(Pair(freeze, unfreeze));
            }
            var export = new Button { Content = Loc.S("Export"), FontSize = 12.5, Height = 24, MinHeight = 24, Padding = new Thickness(3, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(export, Loc.S("Export attributes"));
            export.Click += async (_, _) => await ExportPlayerAttributesAsync(player);
            var import = new Button { Content = Loc.S("Import"), FontSize = 12.5, Height = 24, MinHeight = 24, Padding = new Thickness(3, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(import, Loc.S("Import attributes"));
            import.Click += async (_, _) => await ImportPlayerAttributesAsync(player);
            panel.Children.Add(Pair(export, import));
        }

        TextBlock? selectedCountText = null;
        if (isSquadMode)
        {
            // Every player in the table is included unless his box is unticked.
            selectedCountText = new TextBlock { FontSize = 12, Foreground = UiKit.Brush("#94A3B8"), Margin = new Thickness(1, -2, 0, 4) };
            void UpdateCount()
            {
                int included = getSelectedPlayers?.Invoke()?.Count ?? 0;
                selectedCountText.Text = included == 1 ? Loc.S("( 1 Player )") : Loc.F("( {0} Players )", included);
            }
            UpdateCount();
            registerSelectionUpdater?.Invoke(UpdateCount);
            panel.Children.Add(selectedCountText);
        }
        panel.Children.Add(Subhead("CONTRACT", first: false));

        Button ActionBtn(string text, Action onClick, bool enabled = true)
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = Loc.S(text), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                FontSize = 12.5,
                MinHeight = 24,
                Padding = new Thickness(3, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsEnabled = enabled,
            };
            if (ToolTips.TryGetValue(text, out var tip)) ToolTip.SetTip(btn, Loc.S(tip));
            btn.Click += (_, _) => onClick();
            return btn;
        }

        void RunAction(string actionName, Action<PlayerRecord> action)
        {
            var selectedPlayers = isSquadMode ? getSelectedPlayers?.Invoke() ?? [] : [];
            bool batch = isSquadMode;
            if (batch)
            {
                var list = selectedPlayers;
                if (list.Count == 0)
                {
                    onStatus?.Invoke(Loc.S("No players selected"));
                    return;
                }

                var report = RunBulkVoid(backend.Edits, list, actionName, action, RegisterPlayerLabels);
                onStatus?.Invoke(report.FormatStatus(actionName));

                if (report.Changed > 0)
                    onApplied?.Invoke();
            }
            else
            {
                var target = selectedPlayers.Count == 1 ? selectedPlayers[0] : (player ?? getSelectedPlayers?.Invoke()?.FirstOrDefault());
                if (target is null)
                {
                    onStatus?.Invoke(Loc.S("Select a player first"));
                    return;
                }

                try
                {
                    long before = backend.Edits.WriteCount;
                    { RegisterPlayerLabels(target); backend.Edits.Execute("Player", target.DisplayName, actionName, () => action(target)); }
                    onStatus?.Invoke(before >= 0 && backend.Edits.WriteCount == before
                        ? Loc.F("{0}: {1} already has these values, nothing was changed.", Loc.S(actionName), target.DisplayName)
                        : Loc.F("{0} applied to {1}. Continue in FM to see updates.", Loc.S(actionName), target.DisplayName));
                    onApplied?.Invoke();
                }
                catch (Exception ex)
                {
                    onStatus?.Invoke(Loc.F("Failed {0}: {1}", Loc.S(actionName), UserMessage.For(ex)));
                }
            }
        }

        panel.Children.Add(ActionBtn("Renew for 4 Years", () =>
            RunAction("Renew for 4 Years", p => SquadAdapter.RenewForYears(backend.Session, p, 4))));

        panel.Children.Add(ActionBtn("Make it Expire", () =>
            RunAction("Make it Expire", p => SquadAdapter.MakeExpire(backend.Session, p))));

        panel.Children.Add(Pair(
            ActionBtn("−1 Year", () => RunAction("−1 Year", p => SquadAdapter.ShiftExpiry(backend.Session, p, -1))),
            ActionBtn("+1 Year", () => RunAction("+1 Year", p => SquadAdapter.ShiftExpiry(backend.Session, p, 1)))));

        if (isSquadMode && squadClub is { } clubAddress)
            panel.Children.Add(ActionBtn("Grant work permit", () => GrantClubNationPermits(clubAddress, getSelectedPlayers, onStatus, onApplied)));

        if (!isSquadMode && player is not null)
        {
            panel.Children.Add(ActionBtn("Release Player", () => _ = ShowReleasePlayerAsync(player), enabled: player.Contract is not null));
            if (releaseUndo.Count > 0)
                panel.Children.Add(ActionBtn("Undo last release", () => _ = UndoReleaseAsync(), enabled: true));
        }

        if (!isSquadMode && player is not null)
        {
            panel.Children.Add(Subhead("TEAM"));
            bool captain = backend.Session.Profile.Team?.Captain is not null;
            bool vice = backend.Session.Profile.Team?.ViceCaptain is not null;
            panel.Children.Add(ActionBtn("Make Captain", () => _ = MakeCaptainAsync(player, CaptainRole.Captain), enabled: captain));
            panel.Children.Add(ActionBtn("Make Vice Captain", () => _ = MakeCaptainAsync(player, CaptainRole.ViceCaptain), enabled: vice));
            if (captainUndo.Count > 0)
                panel.Children.Add(ActionBtn("Undo captain change", () => _ = UndoCaptainAsync(), enabled: true));
        }

        panel.Children.Add(Subhead("STATE"));

        panel.Children.Add(Pair(
            ActionBtn("Heal", () => RunAction("Heal", p => backend.Session.Heal(p))),
            ActionBtn("Inspire", () => RunAction("Inspire", p => backend.Session.Inspire(p)))));

        panel.Children.Add(Pair(
            ActionBtn("Destroy", () => RunAction("Destroy", p => backend.Session.Destroy(p))),
            ActionBtn("No Bans", () => RunAction("Remove bans", p => backend.Session.RemoveMatchBans(p)))));

        if (isSquadMode)
        {
            panel.Children.Add(Pair(
                ActionBtn("Freeze", () => RunAction("Freeze", p =>
                {
                    if (backend.Freezer is { } freezer)
                        freezer.SetPlayer(p, FrozenPlayer.AllAttributes(p));
                })),
                ActionBtn("Unfreeze", () => RunAction("Unfreeze", p =>
                {
                    if (backend.Freezer is { } freezer && p.Id is > 0)
                        freezer.RemovePlayer(p.Id.Value);
                }))));
        }

        // FMRTE's Information-page presets; each returns false when the player already has what it adds.
        void RunPreset(string actionName, Func<PlayerRecord, bool> preset)
        {
            if (isSquadMode)
            {
                var list = getSelectedPlayers?.Invoke() ?? [];
                if (list.Count == 0) { onStatus?.Invoke(Loc.S("No players selected")); return; }
                var report = RunBulk(backend.Edits, list, actionName, preset, RegisterPlayerLabels);
                onStatus?.Invoke(report.FormatStatus(actionName));
                if (report.Changed > 0) onApplied?.Invoke();
                return;
            }
            int unchanged = 0, failed = 0;
            RunAction(actionName, p => { try { if (!preset(p)) unchanged++; } catch { failed++; throw; } });
            if (unchanged > 0 && failed == 0)
                onStatus?.Invoke(Loc.F("{0}: {1} player(s) already had it or have nothing to change.", Loc.S(actionName), unchanged));
        }
        panel.Children.Add(ActionBtn("Remove Int. Withdrawal", () => RunPreset("Remove Int. Withdrawal", p => backend.Session.RemoveInternationalWithdrawal(p) > 0)));
        panel.Children.Add(ActionBtn("Register for UEFA", () => RunPreset("Register for UEFA", p => backend.Session.RegisterForUefa(p))));
        panel.Children.Add(ActionBtn("Register for Domestic", () => RunPreset("Register for Domestic", p => backend.Session.RegisterForDomestic(p))));
        panel.Children.Add(ActionBtn("Trained at club", () => RunPreset("Trained at club", p => backend.Session.TrainedAtCurrentClub(p))));
        panel.Children.Add(ActionBtn("Trained at nation", () => RunPreset("Trained at nation", p => backend.Session.TrainedInCurrentNation(p))));
        panel.Children.Add(ActionBtn("Team Cohesion", () => RunPreset("Improve Team Cohesion", p => backend.Session.SetTeamCohesion(p, TeamCohesionWriter.Improve))));
        panel.Children.Add(ActionBtn("Tactical Understanding", () => RunAction("Tactical Understanding", p => backend.Session.TacticalUnderstanding(p))));
        panel.Children.Add(ActionBtn("All-in-one", () => RunAction("All-in-one", p => backend.Session.AllInOne(p))));
        if (getYouthPlayers is not null)
        {
            // Plain number boxes with their names beside them; the range is checked when the button is pressed.
            TextBox PaBox(int value) => new() { Text = value.ToString(), Width = 52, Height = 24, MinHeight = 24, FontSize = 12.5, HorizontalContentAlignment = HorizontalAlignment.Right };
            var minPa = PaBox(Fm27Editor.Core.Players.GoldenGenerationWriter.DefaultMinPa);
            var maxPa = PaBox(Fm27Editor.Core.Players.GoldenGenerationWriter.DefaultMaxPa);
            Control Labelled(string label, TextBox box)
            {
                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(box, Dock.Right);
                row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = Loc.S(label), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = UiKit.TextSecondary });
                return row;
            }
            panel.Children.Add(Subhead("YOUTH"));
            panel.Children.Add(Labelled("Lowest PA", minPa));
            panel.Children.Add(Labelled("Highest PA", maxPa));
            panel.Children.Add(ActionBtn("Golden Generation", () =>
            {
                if (!int.TryParse(minPa.Text, out int lo) || !int.TryParse(maxPa.Text, out int hi) || lo is < 1 or > 200 || hi is < 1 or > 200 || lo > hi)
                { onStatus?.Invoke(Loc.S("Golden Generation: enter PA from 1 to 200, lowest first")); return; }
                var youth = getYouthPlayers();
                if (youth.Count == 0) { onStatus?.Invoke(Loc.S("Golden Generation: the club has no youth or reserve players")); return; }
                int done = 0;
                var failed = new List<string>();
                foreach (var p in youth)
                {
                    try { RegisterPlayerLabels(p); backend.Edits.Execute("Player", p.DisplayName, "Golden Generation", () => backend.Session.GoldenGeneration(p, lo, hi)); done++; }
                    catch (Exception ex) { failed.Add($"{p.DisplayName}: {UserMessage.For(ex)}"); }
                }
                onApplied?.Invoke();
                onStatus?.Invoke(failed.Count == 0
                    ? Loc.F("Golden Generation: {0} youth and reserve player(s) updated. Continue in FM to see updates.", done)
                    : Loc.F("Golden Generation: {0} updated, {1} failed ({2}).", done, failed.Count, string.Join(", ", failed.Take(2))));
            }));
        }
        if (!isSquadMode && player is not null)
        {
            var retirement = new PlannedRetirements(backend.Session.Memory, backend.Session.Profile, backend.Session.ModuleBase);
            if (retirement.IsSupported)
                panel.Children.Add(ActionBtn("Prevent retirement", () => PreventRetirementNow(player)));
            if (backend.Session.CanWrite && drafts.TryGetValue(player.Address, out var legendDraft))
                AddLegendPreset(panel, player, legendDraft, ActionBtn);
        }
        if (isSquadMode && squadClub is { } matchClub && backend.Session.InMatch is not null)
        {
            var match = ActionBtn("In-Match Ability Modifier", () => _ = ShowInMatchAbilityAsync(matchClub), true);
            ToolTip.SetTip(match, Loc.S("Edit attributes and condition for players in an active match."));
            panel.Children.Add(match);
        }
        AddDevelopmentSpeed(panel, player, isSquadMode, getSelectedPlayers, onStatus, onApplied, ActionBtn);

        var card = UiKit.Card(isSquadMode ? "Presets" : "Tools", panel);
        card.Width = isSquadMode ? 196 : 168;
        card.VerticalAlignment = VerticalAlignment.Top;
        return card;
    }

    /// <summary>Removes the player's planned retirement and sets the keep-playing traits at once; no preview. The History window can undo it.</summary>
    void PreventRetirementNow(PlayerRecord player)
    {
        try
        {
            var session = backend.Session;
            var writer = new KeepPlayingWriter(session.Memory, session.Profile, session.ModuleBase);
            var plan = writer.TryPlan(player);
            if (plan is null) { status.Text = Loc.F("{0}: already set to keep playing", player.DisplayName); return; }
            backend.Edits.Execute("Player", player.DisplayName, "Prevent retirement", () => writer.Apply(plan));
            ReloadCurrentScreen();
            status.Text = plan.RetirementDate is { } was
                ? Loc.F("{0} will not retire on {1}. Continue in FM to see updates.", player.DisplayName, DateSetting.Format(was))
                : Loc.F("{0} will keep playing. Continue in FM to see updates.", player.DisplayName);
        }
        catch (Exception ex) { status.Text = Loc.F("Prevent retirement: {0}", UserMessage.For(ex)); }
    }
}
