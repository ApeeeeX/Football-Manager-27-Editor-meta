using System.Globalization;
using Fm27Editor.Core.Clubs;

namespace Fm27Editor.App;

/// <summary>
/// Club → Relationships names as the game's in-game editor shows them (Man Utd, 23/11/2026). Rival codes 1, 2, 3 and person
/// codes 0, 1, 2, 4, 8 are confirmed in RAM (Alex Ferguson is 2, Head Coach); 3, 5, 6, 7 follow the dropdown's order.
/// Founder (8) is not in the in-game dropdown but exists in game rows.
/// </summary>
public static class ClubRelationshipLabels
{
    static readonly string[] RivalReasons = ["No Reason", "Local", "Competitive", "Historic"];

    static readonly string[] PersonReasons =
        ["No Reason", "Player", "Head Coach", "Coaching Staff", "Chairperson", "Player And Head Coach", "Owner", "Club Staff", "Founder"];

    public static IReadOnlyList<string> Reasons(ClubRelationshipKind kind) => kind == ClubRelationshipKind.Rival ? RivalReasons : PersonReasons;

    public static string ReasonName(ClubRelationshipKind kind, int reason) =>
        reason >= 0 && reason < Reasons(kind).Count ? Loc.S(Reasons(kind)[reason]) : reason.ToString(CultureInfo.InvariantCulture);

    public static string TypeName(ClubRelationship row) => row.Kind switch
    {
        ClubRelationshipKind.Rival => Loc.S("Rival"),
        ClubRelationshipKind.LikesPerson => Loc.S("Likes Person"),
        ClubRelationshipKind.DislikesPerson => Loc.S("Dislikes Person"),
        _ => Loc.F("Unknown ({0})", row.TypeCode.ToString(CultureInfo.InvariantCulture)),
    };

    /// <summary>FMRTE lists rows by descending type code (Unknown 68, Unknown 29, Rival, Dislikes, Likes); equal types keep RAM order.</summary>
    public static IReadOnlyList<ClubRelationship> Ordered(IEnumerable<ClubRelationship> rows) =>
        rows.Select((r, i) => (r, i)).OrderByDescending(x => x.r.TypeCode).ThenBy(x => x.i).Select(x => x.r).ToArray();

    public static ClubListTableModel BuildTable(IReadOnlyList<ClubRelationship> ordered) => new(
        ClubSection.Relationships,
        ClubFieldInfo.SectionTitle(ClubSection.Relationships),
        HasReader: true,
        UsesTeamRoot: false,
        [new(Loc.S("Type"), 120), new(Loc.S("Name"), 1, IsStar: true), new(Loc.S("Value"), 60, RightAlign: true)],
        ordered.Select(r => new ClubListRowModel(
            [new(TypeName(r)), new(r.Name ?? Loc.S("Unknown")), new(r.Value.ToString(CultureInfo.InvariantCulture), RightAlign: true)],
            [])).ToArray());
}
