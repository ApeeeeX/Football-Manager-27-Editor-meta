using Fm27Editor.Core.History;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    sealed record BackroomOption(string Label, BackroomDepartment? Department);
    sealed record BackroomTeamOption(string Label, ulong Team);

    async Task ShowBackroomTeamAsync(ulong club, IReadOnlyList<StaffRecord> staff, Action refresh)
    {
        if (backend.Data is not { } data || backend.SaveChanged) return;
        var session = data.Session;
        ulong firstTeam = loadedClubs.FirstOrDefault(c => c.Address == club)?.FirstTeam?.Address ?? 0;
        var teams = staff.Where(person => person.TeamAddress != 0 && BackroomTeam.IsEligible(person, firstTeam))
            .GroupBy(person => person.TeamAddress)
            .OrderByDescending(team => team.Key == firstTeam).ThenBy(team => team.First().TeamName, StringComparer.CurrentCultureIgnoreCase)
            .Select((team, index) => new BackroomTeamOption(team.First().TeamName ?? Loc.F("Team {0}", index + 1), team.Key)).ToArray();
        if (teams.Length == 0) { status.Text = Loc.S("Nothing would change"); return; }
        var chosenTeam = teams.Length == 1 ? teams[0] : await PickerDialog.Show(this, "Choose team", query => teams
            .Where(candidate => PickerSearch.Hit(query, candidate.Label))
            .Select(candidate => (candidate.Label, candidate)).ToArray(), showAllInitially: true);
        if (chosenTeam is null || !CurrentSave()) return;
        var available = staff.Where(person => person.TeamAddress == chosenTeam.Team && BackroomTeam.IsEligible(person, firstTeam))
            .DistinctBy(person => person.Id).Select(BackroomTeam.DepartmentOf).OfType<BackroomDepartment>()
            .GroupBy(department => department).OrderBy(group => group.Key).ToArray();
        if (available.Length == 0) return;
        var options = new[] { new BackroomOption(Counted("All departments", available.Sum(group => group.Count())), null) }
            .Concat(available.Select(group => new BackroomOption(Counted(group.Key switch
            {
                BackroomDepartment.Coaching => "Coaching Staff",
                BackroomDepartment.Recruitment => "Recruitment Staff",
                _ => "Medical Staff",
            }, group.Count()), group.Key))).ToArray();
        var option = await PickerDialog.Show(this, "Build a Backroom Team", query => options
            .Where(candidate => PickerSearch.Hit(query, candidate.Label))
            .Select(candidate => (candidate.Label, candidate)).ToArray(), showAllInitially: true);
        if (option is null || !CurrentSave()) return;

        const string title = "Build a Backroom Team";
        string clubName = staff[0].Club?.DisplayName ?? Loc.S("Club");
        try
        {
            var plan = await Task.Run(() => BackroomTeam.Plan(staff, club, option.Department, session.StaffWriter, chosenTeam.Team, firstTeam));
            if (!CurrentSave()) return;
            if (plan.Changes.Count == 0 && plan.Abilities.Count == 0 && plan.Skipped.Count == 0)
            {
                status.Text = Loc.S("Nothing would change");
                return;
            }

            var rows = plan.Changes.Select(change => MassRow.Change(change.Staff.DisplayName, StaffAttributeLabel(change.Field),
                change.Before, change.After, () =>
                {
                    BackroomTeam.Apply(change, session.Staff, session.StaffWriter);
                    if (backend.Freezer is not null && session.Staff.Reload(change.Staff.StaffAddress) is { } current)
                        backend.AcceptFrozenStaffEdit(current, change.Field);
                    return 1;
                })).ToList();
            rows.AddRange(plan.Abilities.Select(change => MassRow.Change(change.Staff.DisplayName,
                Loc.S(change.Potential ? "Potential Ability" : "Current Ability"), change.Before, change.After, () =>
                {
                    BackroomTeam.Apply(change, session.Staff, session.StaffWriter);
                    if (session.Staff.Reload(change.Staff.StaffAddress) is { } current) backend.AcceptFrozenStaffAbility(current);
                    return 1;
                })));
            rows.AddRange(plan.Skipped.Select(item => MassRow.Refused(item.Staff.DisplayName, title, Loc.S(item.Reason))));
            int targets = plan.Changes.Select(change => change.Staff.Id).Concat(plan.Abilities.Select(change => change.Staff.Id)).Concat(plan.Skipped.Select(item => item.Staff.Id)).Distinct().Count();
            var labels = plan.Changes.Select(change => change.Staff).Concat(plan.Abilities.Select(change => change.Staff)).DistinctBy(person => person.Id)
                .Select(person => StaffChangeLabels.For(session.Profile, person)).OfType<Func<ulong, int, ChangeLabel?>>().ToArray();
            await ReviewClubToolAsync(title, clubName, new MassPlan("Staff", targets, rows), CurrentSave,
                (address, length) => labels.Select(label => label(address, length)).FirstOrDefault(label => label is not null), refresh);
        }
        catch (Exception ex) { status.Text = UserMessage.For(ex); }

        bool CurrentSave() => ReferenceEquals(backend.Data, data) && !backend.SaveChanged;
        static string Counted(string label, int count) => $"{Loc.S(label)} ({count})";
    }

    static string StaffAttributeLabel(StaffAttribute attribute) => PlayerChangeLabels.Words(attribute.ToString());

    Task ReviewClubToolAsync(string title, string clubName, MassPlan plan, Func<bool> currentSave,
        Func<ulong, int, ChangeLabel?> labeler, Action? refresh = null)
    {
        return new MassEditReviewWindow([plan], WriteAsync) { Title = Loc.S(title) }.ShowDialog(this);

        async Task<int> WriteAsync(IReadOnlyList<MassPlan> plans, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
        {
            if (!currentSave()) throw new SaveChangedException();
            var selected = plans.SelectMany(page => page.Rows).Where(row => row.Include && row.CanApply).ToArray();
            int count = 0;
            try
            {
                await Task.Run(() => backend.Edits.Execute("Club", clubName, title, () =>
                {
                    if (!currentSave()) throw new SaveChangedException();
                    foreach (var row in selected)
                    {
                        if (ct.IsCancellationRequested) break;
                        count += row.Apply!();
                        row.Include = false;
                        progress?.Report((count, selected.Length));
                    }
                }, labeler));
            }
            finally
            {
                if (count > 0 && currentSave())
                {
                    refresh?.Invoke();
                    status.Text = Loc.F("{0:N0} value(s) written. Continue in FM to see updates.", count);
                }
            }
            return count;
        }
    }
}
