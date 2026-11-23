using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    sealed record RelationTarget(string Label, ulong Address)
    {
        public override string ToString() => Label;
    }

    const byte PermanentFlag = 0x4F, CapsType = 0x68, YouthCapsType = 0x6B, OtherNationalityType = 0x09;
    static readonly string[] PersonRelationGroups = ["Favorite People", "Disliked People", "Favorite Clubs", "Disliked Clubs", RelationshipSplitView.OtherGroup];

    static bool Matches(string? text, string query) => text is not null && Fold(text).Contains(Fold(query), StringComparison.Ordinal);
    static RelationshipKind KindFor(byte type) => type switch { 0x01 => RelationshipKind.LikesPerson, 0x02 => RelationshipKind.DislikesPerson, _ => RelationshipKind.LikesTeam };

    static string RelationGroup(byte type) => type switch
    {
        0x01 => "Favorite People", 0x02 => "Disliked People", 0x03 => "Favorite Clubs", 0x04 => "Disliked Clubs", _ => RelationshipSplitView.OtherGroup,
    };

    static byte? RelationTypeOf(string group) => group switch
    {
        "Favorite People" => 0x01, "Disliked People" => 0x02, "Favorite Clubs" => 0x03, "Disliked Clubs" => 0x04, _ => null,
    };

    /// <summary>The list shows the target alone inside a named group; the Other group also needs the row's type.</summary>
    static string RelationLabel(byte type, string? target)
    {
        string typeName = RelationCatalog.Find(type)?.Name is { } name ? Loc.S(name) : Loc.F("Unknown ({0})", type);
        if (RelationGroup(type) != RelationshipSplitView.OtherGroup) return target ?? Loc.S("Choose…");
        return string.IsNullOrEmpty(target) ? typeName : $"{typeName}: {target}";
    }

    Control RelationshipsPanel(PlayerRecord player) => RelationshipsPanel(backend.Session.Owner(player), RenderEditor);

    /// <summary>
    /// Relationships of a player or a staff member: every relation type in RelationCatalog, in the shared list-and-form layout.
    /// Other Nation Caps / Youth Caps rows keep Caps at +0xC and Goals at +0xD (Frida trace on Haaland, 10/11/2026; FMRTE clamps both to 127).
    /// </summary>
    Control RelationshipsPanel(PersonOwner player, Action rerender)
    {
        var panel = new StackPanel { Spacing = 8 };
        var statuses = backend.Session.ReadStatuses(player);
        var edits = new List<EntryEdit>();
        var save = new Button { IsVisible = false, Content = Loc.S("Save changes"), IsEnabled = false, Classes = { "accent" } };
        var infoCodes = RelationshipWriter.OtherNationalityInfos;
        var removed = new List<RelationEntry>();
        var drafts = new List<DraftRow>();
        var entryOf = new Dictionary<RelationshipRow, RelationEntry>();
        var draftOf = new Dictionary<RelationshipRow, DraftRow>();
        RelationshipSplitView? view = null;

        bool AnyChanged() => removed.Count > 0 || drafts.Count > 0 || edits.Any(e => !removed.Contains(e.Entry) && e.Changed());
        void Refresh() => save.IsEnabled = AnyChanged();

        RelationshipRow EntryRow(RelationEntry entry)
        {
            var edit = new EntryEdit(entry);
            var lines = new List<Control>();
            RelationshipRow? row = null;
            TextBox Editable(string text)
            {
                var box = RelationshipSplitView.Number(text);
                box.TextChanged += (_, _) => Refresh();
                return box;
            }
            if (entry.Type is CapsType or YouthCapsType)
            {
                edit.Caps = Editable(entry.Value.ToString(Inv));
                edit.Goals = Editable(entry.Flags.ToString(Inv));
                lines.Add(RelationshipSplitView.FormRow("Caps", edit.Caps));
                lines.Add(RelationshipSplitView.FormRow("Goals", edit.Goals));
            }
            else if (entry.Type == OtherNationalityType)
            {
                var names = infoCodes.Select(c => InformationLabels.Name(InformationLabels.NationalityInfos, c)).ToList();
                int at = infoCodes.ToList().IndexOf(entry.Flags);
                if (at < 0) { names.Add(Loc.F("Code {0}", entry.Flags)); at = names.Count - 1; }
                edit.Info = RelationshipSplitView.Choice(names, at);
                edit.Info.SelectionChanged += (_, _) => Refresh();
                lines.Add(RelationshipSplitView.FormRow("Info", edit.Info));
            }
            else if (RelationCatalog.HasLikeValue(entry.Type))
            {
                edit.Caps = Editable(entry.Value.ToString(Inv));
                lines.Add(RelationshipSplitView.FormRow("Value", edit.Caps));
                if (RelationCatalog.HasReason(entry.Type))
                {
                    var reasons = RelationshipLabels.Reasons(KindFor(entry.Type)).Select(r => Loc.S(r)).ToList();
                    int at = entry.Plus8;
                    if (at >= reasons.Count) reasons.AddRange(Enumerable.Range(reasons.Count, at - reasons.Count + 1).Select(c => Loc.F("Code {0}", c)));
                    edit.Reason = RelationshipSplitView.Choice(reasons, Math.Clamp(at, 0, reasons.Count - 1));
                    edit.Reason.SelectionChanged += (_, _) => Refresh();
                    lines.Add(RelationshipSplitView.FormRow("Reason", edit.Reason));
                }
                if (RelationCatalog.HasPermanent(entry.Type))
                {
                    edit.Permanent = new CheckBox { IsChecked = entry.Flags == PermanentFlag };
                    edit.Permanent.IsCheckedChanged += (_, _) => Refresh();
                    lines.Add(RelationshipSplitView.FormRow("Permanent", edit.Permanent));
                }
            }
            else lines.Add(RelationshipSplitView.FormRow("Value", new TextBlock { Text = entry.Value.ToString(Inv) }));

            row = new RelationshipRow
            {
                Group = RelationGroup(entry.Type),
                Name = RelationLabel(entry.Type, entry.TargetName ?? (entry.Target == 0 ? null : Loc.S("Unknown"))),
                Value = entry.Value.ToString(Inv),
                Detail = RelationshipSplitView.Form([.. lines]),
            };
            if (edit.Caps is { } valueBox) valueBox.TextChanged += (_, _) => { row.Value = valueBox.Text ?? ""; view?.Refresh(); };
            edits.Add(edit);
            entryOf[row] = entry;
            return row;
        }

        // What a new row can point at, by the kind its type needs; built the first time a draft asks.
        var choices = new Dictionary<RelationTargetKind, IReadOnlyList<RelationTarget>>();
        IReadOnlyList<RelationTarget> TargetChoices(RelationTargetKind kind)
        {
            if (choices.TryGetValue(kind, out var known)) return known;
            var session = backend.Session;
            IEnumerable<RelationTarget> found = kind switch
            {
                RelationTargetKind.Nation => (nationCatalogue ??= session.Nations.LoadAll().Where(n => n.DisplayName.Length > 0).OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList())
                    .Select(n => new RelationTarget(n.Id is { } id ? $"{n.DisplayName} ({id})" : n.DisplayName, n.Address)),
                RelationTargetKind.Club => (clubCache ??= session.LoadClubs(Players())).Where(c => c.Gender != Fm27Editor.Core.Offsets.Gender.Women).Select(c => new RelationTarget(c.DisplayName, c.Address)),
                RelationTargetKind.Team => (clubCache ??= session.LoadClubs(Players())).Where(c => c.FirstTeam is not null && c.Gender != Fm27Editor.Core.Offsets.Gender.Women).Select(c => new RelationTarget(c.DisplayName, c.FirstTeam!.Address)),
                RelationTargetKind.Person => Players().Where(p => p.Address != player.Address && p.Gender != Fm27Editor.Core.Offsets.Gender.Women && p.Club?.Gender != Fm27Editor.Core.Offsets.Gender.Women).Select(p => new RelationTarget($"{p.DisplayName} ({p.ClubName})", session.PersonOf(p))),
                _ => [],
            };
            return choices[kind] = found.ToList();
        }

        // Add puts a draft row in the list; the type decides which lines of its form show. Nothing is written until Save.
        RelationshipRow DraftRelationRow(string group)
        {
            var d = new DraftRow();
            var typeBox = RelationshipSplitView.Choice(RelationCatalog.All.Select(t => Loc.S(t.Name)), -1);
            typeBox.PlaceholderText = Loc.S("Select type…");
            var targetButton = new Button { Width = 240, HorizontalContentAlignment = HorizontalAlignment.Left, Content = Loc.S("Choose…") };
            d.Value = RelationshipSplitView.Number("");
            d.Goals = RelationshipSplitView.Number("0");
            d.Info = RelationshipSplitView.Choice(infoCodes.Select(c => InformationLabels.Name(InformationLabels.NationalityInfos, c)), infoCodes.ToList().IndexOf(80));
            d.Reason = RelationshipSplitView.Choice(null, -1);
            d.Permanent = new CheckBox();
            Grid targetLine = RelationshipSplitView.FormRow("Name", targetButton), valueLine = RelationshipSplitView.FormRow("Value", d.Value),
                goalsLine = RelationshipSplitView.FormRow("Goals", d.Goals), infoLine = RelationshipSplitView.FormRow("Info", d.Info),
                reasonLine = RelationshipSplitView.FormRow("Reason", d.Reason), permanentLine = RelationshipSplitView.FormRow("Permanent", d.Permanent);
            foreach (var line in new[] { targetLine, valueLine, goalsLine, infoLine, reasonLine, permanentLine }) line.IsVisible = false;
            var row = new RelationshipRow
            {
                Group = group,
                Name = Loc.S("Select type…"),
                Value = "",
                Detail = RelationshipSplitView.Form(RelationshipSplitView.FormRow("Type", typeBox), targetLine, valueLine, goalsLine, infoLine, reasonLine, permanentLine),
            };
            void Relabel()
            {
                row.Name = d.Type is { } t ? RelationLabel(t.Code, d.Target?.Label ?? (t.Target == RelationTargetKind.None ? null : Loc.S("Choose…"))) : Loc.S("Select type…");
                row.Value = d.Value!.Text ?? "";
                view?.Refresh();
            }
            typeBox.SelectionChanged += (_, _) =>
            {
                d.Type = typeBox.SelectedIndex < 0 ? null : RelationCatalog.All[typeBox.SelectedIndex];
                d.Target = null;
                targetButton.Content = Loc.S("Choose…");
                targetLine.IsVisible = d.Type is { Target: not RelationTargetKind.None };
                valueLine.IsVisible = d.Type is not null && d.Type.Code != OtherNationalityType;
                goalsLine.IsVisible = d.Type?.Code is CapsType or YouthCapsType;
                infoLine.IsVisible = d.Type?.Code == OtherNationalityType;
                bool hasReason = d.Type is { } picked && RelationCatalog.HasReason(picked.Code);
                d.Reason.ItemsSource = hasReason ? RelationshipLabels.Reasons(KindFor(d.Type!.Code)).Select(r => Loc.S(r)).ToArray() : null;
                d.Reason.SelectedIndex = hasReason ? 0 : -1;
                reasonLine.IsVisible = hasReason;
                permanentLine.IsVisible = d.Type is { } withFlag && RelationCatalog.HasPermanent(withFlag.Code);
                d.Permanent.IsChecked = d.Type?.Flags == PermanentFlag;
                d.Value.Text = d.Type?.Value.ToString(Inv) ?? "";
                d.Goals.Text = "0";
                if (d.Type is { } type) row.Group = RelationGroup(type.Code);
                Relabel();
                if (view is not null) view.Select(row);
                Refresh();
            };
            d.Value.TextChanged += (_, _) => Relabel();
            targetButton.Click += async (_, _) =>
            {
                if (d.Type is not { Target: not RelationTargetKind.None } type) return;
                var options = TargetChoices(type.Target);
                var chosen = await PickerDialog.Show<RelationTarget>(this, Loc.S(type.Name),
                    q => options.Where(t => Matches(t.Label, q)).Take(200).Select(t => (t.Label, t)).ToList());
                if (chosen is null) return;
                d.Target = chosen;
                targetButton.Content = chosen.Label;
                ToolTip.SetTip(targetButton, chosen.Label);
                Relabel();
            };
            // The group the user is on already says the type; the Other group leaves it to them.
            if (RelationTypeOf(group) is { } implied) typeBox.SelectedIndex = RelationCatalog.All.ToList().FindIndex(t => t.Code == implied);
            drafts.Add(d);
            draftOf[row] = d;
            Refresh();
            return row;
        }

        if (statuses.Holder != 0)
        {
            view = new RelationshipSplitView(PersonRelationGroups, statuses.Entries.Select(EntryRow).ToList(), row =>
            {
                if (draftOf.Remove(row, out var draft)) drafts.Remove(draft);
                else if (entryOf.TryGetValue(row, out var entry)) removed.Add(entry);
                Refresh();
            }, DraftRelationRow);
            panel.Children.Add(view.View);
        }

        RelationDraft ToDraft(DraftRow d)
        {
            var type = d.Type ?? throw new InvalidOperationException(Loc.S("Relationships: choose a type for every new row"));
            ulong target = type.Target == RelationTargetKind.None ? 0
                : d.Target?.Address ?? throw new InvalidOperationException(Loc.F("{0}: choose a target", Loc.S(type.Name)));
            byte value = type.Value, flags = type.Flags;
            if (type.Code is CapsType or YouthCapsType)
            {
                if (!byte.TryParse(d.Value!.Text, out value) || value > 127 || !byte.TryParse(d.Goals!.Text, out flags) || flags > 127)
                    throw new InvalidOperationException(Loc.S("Other Nation Caps: Caps and Goals are 0–127"));
            }
            else if (type.Code == OtherNationalityType) flags = (byte)infoCodes[d.Info!.SelectedIndex];
            else if (!byte.TryParse(d.Value!.Text, out value)) throw new InvalidOperationException(Loc.F("{0}: enter a value", Loc.S(type.Name)));
            if (RelationCatalog.HasPermanent(type.Code)) flags = d.Permanent!.IsChecked == true ? PermanentFlag : (byte)0;
            int plus8 = RelationCatalog.HasReason(type.Code) ? Math.Max(d.Reason!.SelectedIndex, 0) : type.Plus8;
            return new RelationDraft(type, target, plus8, value, flags);
        }
        save.Click += (_, _) =>
        {
            try
            {
                var session = backend.Session;
                var toAdd = drafts.Select(ToDraft).ToList();
                backend.Edits.Execute("Player", player.Name, "Page save", () =>
                {
                    var writer = new RelationshipWriter(session.Memory, session.Profile, session.Game.ModuleBase);
                    foreach (var e in edits.Where(x => !removed.Contains(x.Entry) && x.Changed()))
                    {
                        var entry = e.Entry;
                        if (entry.Type is CapsType or YouthCapsType)
                        {
                            if (!byte.TryParse(e.Caps!.Text, out var caps) || caps > 127 || !byte.TryParse(e.Goals!.Text, out var goals) || goals > 127)
                                throw new InvalidOperationException(Loc.S("Other Nation Caps: Caps and Goals are 0–127"));
                            writer.UpdateEntry(player, entry, entry.Plus8, caps, goals);
                        }
                        else if (entry.Type == OtherNationalityType)
                        {
                            writer.UpdateEntry(player, entry, entry.Plus8, entry.Value, (byte)infoCodes[e.Info!.SelectedIndex]);
                        }
                        else
                        {
                            if (!byte.TryParse(e.Caps!.Text, out var value) || value is < 1 or > 100)
                                throw new InvalidOperationException(Loc.S("Relationships: enter a value 1–100"));
                            // An untouched box keeps the game's byte (0x4E exists); only a real change writes 0x4F / 0.
                            byte flags = e.PermanentChanged() ? (e.Permanent!.IsChecked == true ? PermanentFlag : (byte)0) : entry.Flags;
                            writer.UpdateEntry(player, entry, e.Reason?.SelectedIndex ?? entry.Plus8, value, flags);
                        }
                    }
                    // Removing and adding move the list, so it is read again before each one.
                    foreach (var gone in removed)
                    {
                        var now = session.ReadStatuses(player).Entries.FirstOrDefault(x => x.Type == gone.Type && x.Target == gone.Target && x.Snapshot == gone.Snapshot)
                            ?? throw new StaleObjectException("The relationship is no longer in the list");
                        writer.RemoveEntry(player, now);
                    }
                    foreach (var draft in toAdd) writer.AddEntry(player, session.ReadStatuses(player), draft, session.AllocateGameBlock);
                });
                rerender();
                status.Text = Loc.S("Saved relationships. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        panel.Children.Add(save);
        return panel;
    }

    /// <summary>A row added with Add and not yet saved; its controls live in the row's form.</summary>
    sealed class DraftRow
    {
        public RelationType? Type;
        public RelationTarget? Target;
        public TextBox? Value, Goals;
        public ComboBox? Info, Reason;
        public CheckBox? Permanent;
    }

    sealed class EntryEdit(RelationEntry entry)
    {
        public RelationEntry Entry { get; } = entry;
        public TextBox? Caps { get; set; }
        public TextBox? Goals { get; set; }
        public ComboBox? Info { get; set; }
        public ComboBox? Reason { get; set; }
        public CheckBox? Permanent { get; set; }
        public bool PermanentChanged() => Permanent is not null && (Permanent.IsChecked == true) != (Entry.Flags == PermanentFlag);
        public bool Changed()
        {
            if (Entry.Type is 0x68 or 0x6B)
                return Caps is not null && (Caps.Text != Entry.Value.ToString(CultureInfo.InvariantCulture) || Goals?.Text != Entry.Flags.ToString(CultureInfo.InvariantCulture));
            if (Entry.Type == 0x09)
                return Info?.SelectedIndex is { } i && (byte)RelationshipWriter.OtherNationalityInfos[i] != Entry.Flags;
            if (Caps is null) return false;   // read-only row (no editable control)
            return Caps.Text != Entry.Value.ToString(CultureInfo.InvariantCulture) || (Reason is not null && Reason.SelectedIndex != Entry.Plus8) || PermanentChanged();
        }
    }
}
