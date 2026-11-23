using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    const string RivalClubsGroup = "Rival Clubs";
    static readonly string[] ClubRelationGroups = [RivalClubsGroup, "Favorite People", "Disliked People", RelationshipSplitView.OtherGroup];

    static string ClubRelationGroup(ClubRelationship row) => row.Kind switch
    {
        ClubRelationshipKind.Rival => RivalClubsGroup,
        ClubRelationshipKind.LikesPerson => "Favorite People",
        ClubRelationshipKind.DislikesPerson => "Disliked People",
        _ => RelationshipSplitView.OtherGroup,
    };

    /// <summary>A saved row's controls; only fields the user really changed are written.</summary>
    sealed class ClubRelationEdit(ClubRelationship row)
    {
        public ClubRelationship Row { get; } = row;
        public TextBox? Value;
        public ComboBox? Reason;
        public CheckBox? Permanent, NameStadium, NoTransfers;
        public int? ChangedReason => Reason is { SelectedIndex: >= 0 } r && r.SelectedIndex != Row.Reason ? r.SelectedIndex : null;
        public bool? ChangedPermanent => Permanent?.IsChecked is { } p && p != Row.Permanent ? p : null;
        public bool? ChangedNameStadium => NameStadium?.IsChecked is { } n && n != Row.CanNameStadium ? n : null;
        public bool? ChangedNoTransfers => NoTransfers?.IsChecked is { } t && t != Row.NoTransfers ? t : null;
        public bool ValueChanged => Value is not null && Value.Text != Row.Value.ToString(Inv);
        public bool Changed => ValueChanged || ChangedReason is not null || ChangedPermanent is not null || ChangedNameStadium is not null || ChangedNoTransfers is not null;
    }

    /// <summary>A rival added with Add and not yet saved.</summary>
    sealed class ClubRivalDraft
    {
        public ClubEntry? Club;
        public required TextBox Value;
        public required ComboBox Reason;
        public CheckBox? NoTransfers;
    }

    void RenderClubRelationships(GameSession session, ClubEntry club)
    {
        IReadOnlyList<ClubRelationship> ordered;
        try { ordered = ClubRelationshipLabels.Ordered(session.ReadClubRelationships(club.Address)); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return; }

        var save = new Button { IsVisible = false, Content = Loc.S("Save changes"), IsEnabled = false };
        var edits = new List<ClubRelationEdit>();
        var removed = new List<ClubRelationship>();
        var drafts = new List<ClubRivalDraft>();
        var editOf = new Dictionary<RelationshipRow, ClubRelationEdit>();
        var draftOf = new Dictionary<RelationshipRow, ClubRivalDraft>();
        RelationshipSplitView? view = null;
        void Refresh() => save.IsEnabled = removed.Count > 0 || drafts.Count > 0 || edits.Any(e => !removed.Contains(e.Row) && e.Changed);

        RelationshipRow SavedRow(ClubRelationship row)
        {
            var edit = new ClubRelationEdit(row);
            var lines = new List<Control>();
            if (row.Kind == ClubRelationshipKind.Other)
            {
                lines.Add(RelationshipSplitView.FormRow("Value", new TextBlock { Text = row.Value.ToString(Inv) }));
            }
            else
            {
                edit.Value = RelationshipSplitView.Number(row.Value.ToString(Inv));
                edit.Value.TextChanged += (_, _) => Refresh();
                lines.Add(RelationshipSplitView.FormRow("Value", edit.Value));
                if (row.Kind != ClubRelationshipKind.DislikesPerson)
                {
                    var reasons = ClubRelationshipLabels.Reasons(row.Kind).Select(r => Loc.S(r)).ToList();
                    edit.Reason = RelationshipSplitView.Choice(reasons, row.Reason < reasons.Count ? row.Reason : -1);
                    edit.Reason.SelectionChanged += (_, _) => Refresh();
                    lines.Add(RelationshipSplitView.FormRow("Reason", edit.Reason));
                }
                CheckBox Flag(string label, bool value)
                {
                    var box = new CheckBox { IsChecked = value };
                    box.IsCheckedChanged += (_, _) => Refresh();
                    lines.Add(RelationshipSplitView.FormRow(label, box));
                    return box;
                }
                if (row.Kind != ClubRelationshipKind.Rival) edit.Permanent = Flag("Permanent", row.Permanent);
                if (row.CanNameStadium is { } named) edit.NameStadium = Flag("Can Name Stadium After Person", named);
                if (row.NoTransfers is { } blocked) edit.NoTransfers = Flag("Clubs Don't Transfer Players", blocked);
            }
            string name = row.Name ?? Loc.S("Unknown");
            var result = new RelationshipRow
            {
                Group = ClubRelationGroup(row),
                Name = row.Kind == ClubRelationshipKind.Other ? $"{ClubRelationshipLabels.TypeName(row)}: {name}" : name,
                Value = row.Value.ToString(Inv),
                Detail = RelationshipSplitView.Form([.. lines]),
                CanRemove = row.Kind != ClubRelationshipKind.Other,
            };
            if (edit.Value is { } box) box.TextChanged += (_, _) => { result.Value = box.Text ?? ""; view?.Refresh(); };
            edits.Add(edit);
            editOf[result] = edit;
            return result;
        }

        RelationshipRow DraftRival(string _)
        {
            IReadOnlyList<ClubEntry> clubs;
            try { clubs = clubCache ?? session.LoadClubs([]); } catch { clubs = []; }
            var draft = new ClubRivalDraft
            {
                Value = RelationshipSplitView.Number(""),
                Reason = RelationshipSplitView.Choice(ClubRelationshipLabels.Reasons(ClubRelationshipKind.Rival).Select(r => Loc.S(r)), -1),
                NoTransfers = session.Profile.Club?.Relationships?.NoTransfers is null ? null : new CheckBox(),
            };
            var pick = new Button { Width = 240, HorizontalContentAlignment = HorizontalAlignment.Left, Content = Loc.S("Choose club…") };
            var lines = new List<Control>
            {
                RelationshipSplitView.FormRow("Club", pick),
                RelationshipSplitView.FormRow("Value", draft.Value),
                RelationshipSplitView.FormRow("Reason", draft.Reason),
            };
            if (draft.NoTransfers is { } noTransfers) lines.Add(RelationshipSplitView.FormRow("Clubs Don't Transfer Players", noTransfers));
            var row = new RelationshipRow { Group = RivalClubsGroup, Name = Loc.S("Choose club…"), Value = "", Detail = RelationshipSplitView.Form([.. lines]) };
            draft.Value.TextChanged += (_, _) => { row.Value = draft.Value.Text ?? ""; view?.Refresh(); };
            pick.Click += async (_, _) =>
            {
                var choices = clubs.Where(c => c.Address != club.Address && c.Gender != Fm27Editor.Core.Offsets.Gender.Women)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var chosen = await PickerDialog.Show<ClubEntry>(this, Loc.S("Choose rival club"), q => choices.Where(c => PickerSearch.Hit(q, c.DisplayName, c.ShortName, c.Id)).Select(c => ($"{c.Name} ({c.Id})", c)).ToList());
                if (chosen is null) return;
                draft.Club = chosen;
                pick.Content = $"{chosen.Name} ({chosen.Id})";
                row.Name = chosen.DisplayName;
                view?.Refresh();
            };
            drafts.Add(draft);
            draftOf[row] = draft;
            Refresh();
            return row;
        }

        view = new RelationshipSplitView(ClubRelationGroups, ordered.Select(SavedRow).ToList(), row =>
        {
            if (draftOf.Remove(row, out var draft)) drafts.Remove(draft);
            else if (editOf.TryGetValue(row, out var edit)) removed.Add(edit.Row);
            Refresh();
        }, DraftRival);

        save.Click += (_, _) =>
        {
            var changes = edits.Where(e => !removed.Contains(e.Row) && e.Changed).ToList();
            static bool ValidValue(string? text, out int value) => int.TryParse(text, out value) && value is >= 1 and <= 100;
            if (changes.Any(e => e.ValueChanged && !ValidValue(e.Value!.Text, out _)) || drafts.Any(d => !ValidValue(d.Value.Text, out _)))
            {
                status.Text = Loc.S("Relationships: enter a value 1–100");
                return;
            }
            if (drafts.Any(d => d.Club is null)) { status.Text = Loc.S("Pick another club"); return; }
            try
            {
                var writer = new ClubRelationshipWriter(session.Memory, session.Profile, session.Game.ModuleBase);
                backend.Edits.Execute("Club", club.DisplayName, "Relationships", () =>
                {
                    foreach (var e in changes)
                        session.SetClubRelationship(club.Address, e.Row, e.ChangedReason, e.ChangedPermanent,
                            e.ValueChanged ? int.Parse(e.Value!.Text!) : null, e.ChangedNameStadium, e.ChangedNoTransfers);
                    // Removing and adding move the list, so it is read again before each one.
                    foreach (var gone in removed)
                    {
                        var now = session.ReadClubRelationships(club.Address).FirstOrDefault(x => x.TypeCode == gone.TypeCode && x.Target == gone.Target && x.Snapshot == gone.Snapshot)
                            ?? throw new StaleObjectException("The relationship is no longer in the list");
                        writer.Remove(club.Address, now);
                    }
                    foreach (var d in drafts)
                    {
                        var added = writer.AddRival(club.Address, d.Club!.Address, int.Parse(d.Value.Text!), Math.Max(d.Reason.SelectedIndex, 0), session.AllocateGameBlock);
                        if (d.NoTransfers?.IsChecked == true && added is not null)
                            session.SetClubRelationship(club.Address, added, null, null, null, null, true);
                    }
                });
                RenderClubEditor();
                status.Text = Loc.S("Saved relationships. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        clubEditorPanel.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 0, 0), Children = { view.View, save } });
    }
}
