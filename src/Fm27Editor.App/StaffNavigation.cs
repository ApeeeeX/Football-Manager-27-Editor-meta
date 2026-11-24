using System.Globalization;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

/// <summary>Match both the current-session address and the unique ID, never a possibly duplicated name.</summary>
public static class StaffNavigation
{
    public static StaffRow? Find(IEnumerable<StaffRow> rows, ulong address, int id) =>
        rows.FirstOrDefault(row => row.Staff.Address == address && row.Staff.Id == id);
}

public static class StaffFilter
{
    internal static bool MatchesGender(StaffRecord staff, Gender? gender, bool excludeWomenClubs)
    {
        // The ordinary staff person flag can misclassify human managers. Filter them by the club they manage.
        var category = staff.Kind.Manager ? staff.Club?.Gender : staff.Gender;
        return (gender is null || category == gender) &&
            (!excludeWomenClubs || staff.Club?.Gender != Gender.Women);
    }

    internal static bool IsInUniversalSearch(StaffRecord staff) =>
        staff.Kind.Manager || (staff.Gender != Gender.Women && staff.Club?.Gender != Gender.Women);

    public static IReadOnlyList<StaffRow> Apply(IEnumerable<StaffRow> rows, string foldedName, string? club, string? job,
        Gender? gender = null, bool excludeWomenClubs = false) =>
        rows.Where(row =>
            MatchesGender(row.Staff, gender, excludeWomenClubs) &&
            (foldedName.Length == 0
                || row.SearchName.Contains(foldedName, StringComparison.Ordinal)
                || row.Staff.Id.ToString(CultureInfo.InvariantCulture).Contains(foldedName, StringComparison.Ordinal)) &&
            (club is null || string.Equals(row.Club, club, StringComparison.OrdinalIgnoreCase)) &&
            (job is null || string.Equals(row.Job, job, StringComparison.OrdinalIgnoreCase))).ToArray();
}
