using Fm27Editor.App;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App.Tests;

public class RelationshipLabelsTests
{
    /// <summary>Codes from RAM; names from the in-game editor's dropdowns (Nico Paz, 23/11/2026), which call FMRTE's Manager "Head Coach" and Backroom Staff "Coaching Staff".</summary>
    [Theory]
    [InlineData(RelationshipKind.LikesPerson, 2, "Sibling")]   // Ethan Mbappé
    [InlineData(RelationshipKind.LikesPerson, 4, "Relation")]  // fourth entry of the in-game dropdown
    [InlineData(RelationshipKind.LikesPerson, 5, "Teammate")]  // Michael Olise, Neymar
    [InlineData(RelationshipKind.LikesPerson, 6, "Head Coach")] // Sergej Jakirović on Nico Paz
    [InlineData(RelationshipKind.LikesPerson, 9, "Idol")]      // Thierry Henry
    [InlineData(RelationshipKind.LikesTeam, 1, "Supporter")]   // Tenerife on Nico Paz
    [InlineData(RelationshipKind.LikesTeam, 2, "Player")]      // Como on Nico Paz
    [InlineData(RelationshipKind.LikesPerson, 8, "Coaching Staff")] // FMRTE wrote 8 for this entry (Kane → Saka)
    [InlineData(RelationshipKind.LikesTeam, 3, "Head Coach")]        // FMRTE wrote 3 for this entry (Kane → Tottenham)
    [InlineData(RelationshipKind.LikesTeam, 4, "Coaching Staff")]    // last entry of the in-game dropdown
    [InlineData(RelationshipKind.LikesTeam, 5, "Code 5")]
    public void Reason_names_match_the_game(RelationshipKind kind, int code, string name) => Assert.Equal(name, RelationshipLabels.ReasonName(kind, code));

    [Theory]
    [InlineData(0x01, true, true)]   // Favorite People: Reason and Permanent
    [InlineData(0x02, false, true)]  // Disliked People: Level and Permanent only
    [InlineData(0x03, true, false)]  // Favorite Clubs: Reason, no Permanent
    [InlineData(0x04, false, true)]  // Disliked Clubs: no Reason
    public void Only_liked_rows_have_a_reason(int code, bool reason, bool permanent)
    {
        Assert.Equal(reason, RelationCatalog.HasReason(code));
        Assert.Equal(permanent, RelationCatalog.HasPermanent(code));
    }
}
