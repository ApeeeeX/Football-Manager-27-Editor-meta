using Fm27Editor.Core.History;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    sealed record BackroomOption(string Label, BackroomDepartment? Department);

    async Task ShowBackroomTeamAsync(ulong club, IReadOnlyList<StaffRecord> staff, Action refresh)
    {
        if (backend.Data is not { } data || backend.SaveChanged) return;
        var session = data.Session;
        var available = staff.Select(BackroomTeam.DepartmentOf).OfType<BackroomDepartment>().Distinct().Order().ToArray();
        if (available.Length == 0) return;
        var options = new[] { new BackroomOption("All departments", null) }
            .Concat(available.Select(department => new BackroomOption(department.ToString(), department))).ToArray();
        var option = await PickerDialog.Show(this, "Build a Backroom Team", query => options
            .Where(candidate => PickerSearch.Hit(query, Loc.S(candidate.Label)))
            .Select(candidate => (Loc.S(candidate.Label), candidate)).ToArray(), showAllInitially: true);
        if (option is null || !CurrentSave()) return;

        const string title = "Build a Backroom Team";
        string clubName = staff[0].Club?.DisplayName ?? Loc.S("Club");
        try
        {
            var plan = await Task.Run(() => BackroomTeam.Plan(staff, club, option.Department, session.StaffWriter));
            if (!CurrentSave()) return;
            if (plan.Changes.Count == 0 && plan.Skipped.Count == 0)
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
            rows.AddRange(plan.Skipped.Select(item => MassRow.Refused(item.Staff.DisplayName, title, Loc.S(item.Reason))));
            int targets = plan.Changes.Select(change => change.Staff.Id).Concat(plan.Skipped.Select(item => item.Staff.Id)).Distinct().Count();
            var labels = plan.Changes.Select(change => change.Staff).DistinctBy(person => person.Id)
                .Select(person => StaffChangeLabels.For(session.Profile, person)).OfType<Func<ulong, int, ChangeLabel?>>().ToArray();
            await ReviewClubToolAsync(title, clubName, new MassPlan("Staff", targets, rows), CurrentSave,
                (address, length) => labels.Select(label => label(address, length)).FirstOrDefault(label => label is not null), refresh);
        }
        catch (Exception ex) { status.Text = UserMessage.For(ex); }

        bool CurrentSave() => ReferenceEquals(backend.Data, data) && !backend.SaveChanged;
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
