using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

/// <summary>
/// Names of relationship reasons as the game's in-game editor shows them (Nico Paz, 23/11/2026: Sergej Jakirović is code 6,
/// Head Coach; Tenerife code 1, Supporter). Youth Staff (11) is listed by FMRTE only and kept so such rows keep their name.
/// </summary>
public static class RelationshipLabels
{
    static readonly string[] PersonReasons =
        ["None", "Parent", "Sibling", "Child", "Relation", "Teammate", "Head Coach", "Player", "Coaching Staff", "Idol", "Friend", "Youth Staff"];

    /// <summary>The in-game editor's Favorite Clubs reasons; FMRTE stored Head Coach as 3 (Kane → Tottenham, 2/11/2026).</summary>
    static readonly string[] TeamReasons = ["None", "Supporter", "Player", "Head Coach", "Coaching Staff"];

    public static IReadOnlyList<string> Reasons(RelationshipKind kind) => kind == RelationshipKind.LikesTeam ? TeamReasons : PersonReasons;

    public static string KindName(RelationshipKind kind) => Loc.S(kind switch
    {
        RelationshipKind.LikesPerson => "Likes Person",
        RelationshipKind.DislikesPerson => "Dislikes Person",
        RelationshipKind.OtherNationality => "Other Nationality",
        _ => "Likes Team",
    });

    public static string ReasonName(RelationshipKind kind, int reason) => kind switch
    {
        RelationshipKind.LikesTeam => reason >= 0 && reason < TeamReasons.Length ? Loc.S(TeamReasons[reason]) : Loc.F("Code {0}", reason),
        _ => reason >= 0 && reason < PersonReasons.Length ? Loc.S(PersonReasons[reason]) : Loc.F("Code {0}", reason),
    };
}
