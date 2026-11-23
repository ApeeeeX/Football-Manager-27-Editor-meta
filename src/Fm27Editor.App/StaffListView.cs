using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

/// <summary>
/// Which of a staff member's contracts a list acts on. A person can work for a club and for a national team at once; the Staff
/// list of a club works on the club contract only and the Staff list of a nation on the national contract only.
/// </summary>
internal interface IStaffContractScope
{
    string Name { get; }
    /// <summary>"" when the person has no such contract, "No limit" when it has no expiry, otherwise the date.</summary>
    string ExpiryText(StaffRecord staff);
    void Renew(StaffRecord staff, int years, DateOnly today);
    void Expire(StaffRecord staff, DateOnly today);
    void Shift(StaffRecord staff, int years);
}

internal sealed class ClubStaffScope(GameSession session) : IStaffContractScope
{
    public string Name => "Club";
    public string ExpiryText(StaffRecord staff) => session.StaffContracts.Read(staff) is { } c ? c.Expires is { } ends ? DateSetting.Format(ends) : Loc.S("No limit") : "";
    public void Renew(StaffRecord staff, int years, DateOnly today) => session.StaffContracts.Renew(staff, Need(staff), years, today);
    public void Expire(StaffRecord staff, DateOnly today) => session.StaffContracts.Expire(staff, Need(staff), today);
    public void Shift(StaffRecord staff, int years) => session.StaffContracts.ShiftExpiry(staff, Need(staff), years);
    Fm27Editor.Core.Players.OfferContract Need(StaffRecord staff) =>
        session.StaffContracts.Read(staff) ?? throw new InvalidOperationException(Loc.F("{0} has no club contract", staff.DisplayName));
}

internal sealed class NationStaffScope(GameSession session) : IStaffContractScope
{
    public string Name => "National";
    public string ExpiryText(StaffRecord staff) => session.NationStaffContracts.Read(staff) is { } c ? c.Expires is { } ends ? DateSetting.Format(ends) : Loc.S("No limit") : "";
    public void Renew(StaffRecord staff, int years, DateOnly today) => session.NationStaffContracts.Renew(staff, Need(staff), years, today);
    public void Expire(StaffRecord staff, DateOnly today) => session.NationStaffContracts.Expire(staff, Need(staff), today);
    public void Shift(StaffRecord staff, int years) => session.NationStaffContracts.ShiftExpiry(staff, Need(staff), years);
    Fm27Editor.Core.Players.OfferContract Need(StaffRecord staff) =>
        session.NationStaffContracts.Read(staff) ?? throw new InvalidOperationException(Loc.F("{0} has no national contract", staff.DisplayName));
}

/// <summary>One line of a Staff list (club or nation).</summary>
internal sealed record StaffListRow(StaffRecord Staff, string Job, string Team, string Nation, int? Age, int CA, int PA, string Expires)
{
    public string Name => Staff.DisplayName;
}

public sealed partial class MainWindow
{
    readonly Dictionary<string, HashSet<ulong>> staffTicks = [];
    /// <summary>
    /// The Staff table shared by clubs and nations: the squad table's look (tick column, header sorting) and a Tools card whose
    /// Renew / Make it expire / −1 / +1 Year act on the ticked rows and only on the contract <paramref name="scope"/> names.
    /// </summary>
    Control BuildStaffTable(IReadOnlyList<StaffListRow> source, IStaffContractScope scope, Action refresh, ulong? club = null)
    {
        var rows = source.ToList();
        // The ticks survive a Renew/Expire (which redraws the table), so a chosen row stays the chosen row.
        string tickKey = scope.Name + ":" + (source.Count > 0 ? source[0].Staff.Address : 0UL);
        if (!staffTicks.TryGetValue(tickKey, out var excluded)) staffTicks[tickKey] = excluded = [];
        Action? updateCount = null;
        var grid = ToolGrid.Create();
        grid.ItemsSource = rows;
        ToolGrid.AddTick<StaffListRow>(grid, excluded, r => r.Staff.Address, () => rows, () => updateCount?.Invoke());
        grid.Columns.Add(ToolGrid.StarText<StaffListRow>("Job", r => r.Job, 1.1, 100, "Job"));
        grid.Columns.Add(ToolGrid.StarText<StaffListRow>("Name", r => r.Name, 1.4, 120, "Staff name", "#F1F5F9", bold: true));
        grid.Columns.Add(ToolGrid.Number<StaffListRow>("Age", r => r.Age?.ToString(CultureInfo.InvariantCulture) ?? "", 36, "Age"));
        grid.Columns.Add(ToolGrid.StarText<StaffListRow>("Team", r => r.Team, 0.8, 76, "Team"));
        grid.Columns.Add(ToolGrid.StarText<StaffListRow>("Nat", r => r.Nation, 0.85, 80, "Nationality"));
        grid.Columns.Add(ToolGrid.Number<StaffListRow>("CA", r => r.CA.ToString(CultureInfo.InvariantCulture), 36, "Current Ability"));
        grid.Columns.Add(ToolGrid.Number<StaffListRow>("PA", r => r.PA.ToString(CultureInfo.InvariantCulture), 36, "Potential Ability"));
        grid.Columns.Add(ToolGrid.Number<StaffListRow>("Expires", r => r.Expires, 84, scope.Name == "Club" ? "Club contract expiry" : "National contract expiry"));
        var keys = new Dictionary<string, Func<StaffListRow, IComparable?>>
        {
            ["Job"] = r => r.Job.ToLowerInvariant(),
            ["Name"] = r => r.Name.ToLowerInvariant(),
            ["Age"] = r => r.Age,
            ["Team"] = r => r.Team.ToLowerInvariant(),
            ["Nat"] = r => r.Nation.ToLowerInvariant(),
            ["CA"] = r => r.CA,
            ["PA"] = r => r.PA,
            ["Expires"] = r => r.Expires.Length > 0 && r.Expires != Loc.S("No limit") ? r.Expires : null,
        };
        ToolGrid.EnableSort(grid, keys);
        grid.DoubleTapped += (_, e) =>
        {
            if (UiKit.OnRow(e) && grid.SelectedItem is StaffListRow r) OpenClubStaff(r.Staff);
        };

        var count = new TextBlock { FontSize = 12, Foreground = UiKit.Brush("#94A3B8"), Margin = new Thickness(1, -2, 0, 4) };
        StaffRecord[] Chosen() => rows.Where(r => !excluded.Contains(r.Staff.Address)).Select(r => r.Staff).ToArray();
        Button? backroom = club is { } clubAddress
            ? ToolGrid.ToolButton("Build a Backroom Team", () => _ = ShowBackroomTeamAsync(clubAddress, Chosen(), refresh))
            : null;
        updateCount = () =>
        {
            int included = rows.Count(r => !excluded.Contains(r.Staff.Address));
            count.Text = included == 1 ? Loc.S("( 1 Staff )") : Loc.F("( {0} Staff )", included);
            if (backroom is not null) backroom.IsEnabled = Chosen().Any(staff => BackroomTeam.DepartmentOf(staff).HasValue);
        };
        updateCount();

        void Run(string action, Action<StaffRecord, DateOnly> operation)
        {
            var chosen = rows.Where(r => !excluded.Contains(r.Staff.Address)).Select(r => r.Staff).ToList();
            if (chosen.Count == 0) { status.Text = Loc.S("No staff ticked"); return; }
            DateOnly today;
            try { today = backend.Session.ReadGameDate() ?? throw new InvalidOperationException(Loc.S("The in-game date could not be read")); }
            catch (Exception ex) { status.Text = UserMessage.For(ex); return; }
            int done = 0;
            var failed = new List<string>();
            foreach (var s in chosen)
            {
                try { backend.Edits.Execute("Staff", s.DisplayName, $"{scope.Name} contract: {action}", () => operation(s, today)); done++; }
                catch (Exception ex) { failed.Add($"{s.DisplayName}: {UserMessage.For(ex)}"); }
            }
            string scopeVi = Loc.S(scope.Name);
            string actionVi = Loc.S(action);
            status.Text = failed.Count == 0
                ? Loc.F("{0} contract, {1}: {2} staff updated. Continue in FM to see updates.", scopeVi, actionVi, done)
                : Loc.F("{0} contract, {1}: {2} updated, {3} not changed ({4}).", scopeVi, actionVi, done, failed.Count, string.Join("; ", failed.Take(2)));
            if (done > 0) refresh();
        }

        void RunFreeze(string label, Action<StaffRecord> operation)
        {
            var chosen = Chosen();
            if (chosen.Length == 0) { status.Text = Loc.S("No staff ticked"); return; }
            int done = 0;
            var failed = new List<string>();
            using (backend.Edits.Group("Staff", Loc.S("Bulk Edit"), Loc.S(label)))
                foreach (var s in chosen)
                {
                    try { backend.Edits.Execute(() => operation(s)); done++; }
                    catch (Exception ex) { failed.Add($"{s.DisplayName}: {UserMessage.For(ex)}"); }
                }
            status.Text = failed.Count == 0
                ? Loc.F("{0}: applied to {1:N0} staff", Loc.S(label), done)
                : Loc.F("{0}: {1:N0} ok, {2:N0} failed ({3})", Loc.S(label), done, failed.Count, string.Join("; ", failed.Take(3)));
        }

        var pair = ToolGrid.ToolPair(
            ToolGrid.ToolButton("−1 Year", () => Run("−1 Year", (s, _) => scope.Shift(s, -1))),
            ToolGrid.ToolButton("+1 Year", () => Run("+1 Year", (s, _) => scope.Shift(s, 1))));
        var tools = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(7, 7),
            Children =
            {
                count,
                ToolGrid.ToolSectionHeader("CONTRACT"),
                ToolGrid.ToolButton("Renew for 4 Years", () => Run("Renew for 4 Years", (s, today) => scope.Renew(s, 4, today))),
                ToolGrid.ToolButton("Make it expire", () => Run("Make it expire", (s, today) => scope.Expire(s, today))),
                pair,
            },
        };
        if (backroom is not null)
            tools.Children.Insert(1, backroom);
        if (club.HasValue && backend.Freezer is { } freezer)
            tools.Children.Insert(backroom is null ? 1 : 2, ToolGrid.ToolPair(
                ToolGrid.ToolButton("Freeze", () => RunFreeze("Freeze", s => freezer.SetStaff(s, FrozenStaff.AllAttributes(s))),
                    "Keeps the ticked staff's attributes and Current Ability from changing while you play."),
                ToolGrid.ToolButton("Unfreeze", () => RunFreeze("Unfreeze", s => freezer.RemoveStaff(s.Id)),
                    "Lets the ticked staff's attributes and Current Ability change again.")));
        var card = UiKit.Card("Tools", tools);
        return ToolGrid.WithTools(grid, card);
    }

    Control IScreenHost.BuildStaffTable(IReadOnlyList<StaffListRow> rows, IStaffContractScope scope, Action refresh) => BuildStaffTable(rows, scope, refresh);
}
