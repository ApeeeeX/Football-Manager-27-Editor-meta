using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Competitions;

public sealed record LeagueFixtureOffsets(uint FixtureVtable, uint NameVtable, int Home, int Away, int Name, int Competition, int Season, int Date);

public sealed record ClubSwapOffsets(int TeamStadium, int FixtureStadium, int SecondaryStandings, int StandingTeam,
    int StandingName, int TeamSeasonState, int Competitions, int ParticipationStride, int ParticipationStats,
    uint StatsVtable, int StatsTeam);

public sealed record ClubSwapFixture(CompetitionRecord Competition, DateOnly Date, int RoundRobinCopies,
    ulong Home, ulong Away, ulong ProposedHome, ulong ProposedAway);

public sealed record ClubSwapSchedule(ClubEntry First, ClubEntry Second, CompetitionRecord FirstLeague,
    CompetitionRecord SecondLeague, IReadOnlyList<ClubSwapFixture> Fixtures)
{
    internal DateOnly ReadOn { get; init; }
    internal IReadOnlyList<ClubSwapFixtureSlot> Slots { get; init; } = [];
}

internal sealed record ClubSwapFixtureSlot(ulong Address, ulong Owner, int Season, uint RawDate, ulong Home, ulong Away);

/// <summary>Verifies the two pre-kickoff schedules and previews the fixtures affected by exchanging the senior teams.</summary>
public sealed class ClubSwapPreview(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    public ClubSwapSchedule Read(ClubEntry first, ClubEntry second)
    {
        if (profile.Competition is not { Fixtures: { } layout } competition ||
            profile.Club is not { Nation: { } nationOffset, Id: { } idOffset } club ||
            profile.Team is not { } teamLayout || profile.Contract is not { } contract)
            throw new NotSupportedException("Scheduled fixtures are unavailable on this build");
        if (first.Address == second.Address || first.Id == second.Id)
            throw new InvalidOperationException("Choose two different clubs");
        var today = GameClock.Read(memory, profile, moduleBase) ?? throw new InvalidOperationException("Game date is unavailable");
        var firstTeam = RequireTeam(first);
        var secondTeam = RequireTeam(second);
        if (firstTeam.Nation == 0 || firstTeam.Nation != secondTeam.Nation || firstTeam.Competition == secondTeam.Competition)
            throw new InvalidOperationException("Choose clubs from different divisions in the same country");

        var competitions = new CompetitionRepository(memory, profile, moduleBase);
        var firstLeague = competitions.Reload(firstTeam.Competition) ?? throw new StaleObjectException("The first division changed");
        var secondLeague = competitions.Reload(secondTeam.Competition) ?? throw new StaleObjectException("The second division changed");
        var standings = new CompetitionStandingsAccess(memory, profile, moduleBase);
        var firstMembers = RequireMembers(firstLeague, firstTeam.Team, secondTeam.Team);
        var secondMembers = RequireMembers(secondLeague, secondTeam.Team, firstTeam.Team);
        var owners = new Dictionary<ulong, ulong>();
        var fixtures = new List<(ulong Division, DateOnly Date, ClubSwapFixtureSlot Slot)>();
        var fields = new byte[new[] { layout.Home + 8, layout.Away + 8, layout.Name + 8, layout.Season + 2, layout.Date + 4 }.Max()];
        // The live heap can unmap a region mid-scan. A missing fixture still fails the complete-schedule check below.
        foreach (var hit in HeapVtableScanner.ScanReadable(memory, moduleBase + layout.FixtureVtable, CancellationToken.None))
        {
            if (!memory.TryRead(hit.Address, fields) || BitConverter.ToUInt64(fields) != hit.Vtable) continue;
            ulong name = BitConverter.ToUInt64(fields, layout.Name);
            if (name == 0) continue;
            if (!owners.TryGetValue(name, out ulong division))
            {
                division = memory.TryRead(name, new byte[8]) && memory.ReadUInt64(name) == moduleBase + layout.NameVtable
                    ? memory.ReadUInt64(name + (ulong)layout.Competition) : 0;
                owners[name] = division;
            }
            if (division != firstLeague.Address && division != secondLeague.Address) continue;
            ulong home = BitConverter.ToUInt64(fields, layout.Home), away = BitConverter.ToUInt64(fields, layout.Away);
            var members = division == firstLeague.Address ? firstMembers : secondMembers;
            if (home == away || !members.Contains(home) || !members.Contains(away)) continue;
            int season = BitConverter.ToUInt16(fields, layout.Season);
            int year = BitConverter.ToUInt16(fields, layout.Date + 2), day = BitConverter.ToUInt16(fields, layout.Date) & 0x1FF;
            if (year is < 1901 or > 2300 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365)) continue;
            var date = new DateOnly(year, 1, 1).AddDays(day - 1);
            if (date > today) fixtures.Add((division, date,
                new(hit.Address, name, season, BitConverter.ToUInt32(fields, layout.Date), home, away)));
        }

        var slots = new List<ClubSwapFixtureSlot>();
        var firstRows = RequireSchedule(firstLeague, firstMembers.Count, firstTeam.Team, secondTeam.Team);
        var secondRows = RequireSchedule(secondLeague, secondMembers.Count, secondTeam.Team, firstTeam.Team);
        if (GameClock.Read(memory, profile, moduleBase) != today || !firstMembers.SetEquals(RequireMembers(firstLeague, firstTeam.Team, secondTeam.Team))
            || !secondMembers.SetEquals(RequireMembers(secondLeague, secondTeam.Team, firstTeam.Team))
            || RequireTeam(first) != firstTeam || RequireTeam(second) != secondTeam)
            throw new StaleObjectException("The game changed while reading the fixture schedule");
        return new(first, second, firstLeague, secondLeague,
            firstRows.Concat(secondRows).OrderBy(row => row.Date).ThenBy(row => row.Competition.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray())
        { ReadOn = today, Slots = slots.ToArray() };

        (ulong Team, ulong Competition, ulong Nation) RequireTeam(ClubEntry entry)
        {
            if (entry.Id is not > 0 || entry.FirstTeam is not { } team || memory.ReadUInt64(entry.Address) != moduleBase + club.ClubVtableRva
                || BitConverter.ToInt32(memory.Read(entry.Address + (ulong)idOffset, 4)) != entry.Id
                || memory.ReadUInt64(team.Address) != moduleBase + teamLayout.VtableRva
                || memory.ReadUInt64(team.Address + (ulong)contract.TeamClub) != entry.Address)
                throw new StaleObjectException("The club or its senior team changed");
            return (team.Address, memory.ReadUInt64(team.Address + (ulong)competition.TeamCompetition),
                memory.ReadUInt64(entry.Address + (ulong)nationOffset));
        }

        HashSet<ulong> RequireMembers(CompetitionRecord league, ulong outgoing, ulong incoming)
        {
            var rows = standings.Read(league);
            if (rows is null || rows.Count < 2 || rows.Any(row => row.Played != 0))
                throw new InvalidOperationException("Both divisions must have an available table before their first match");
            var teams = rows.Select(row => row.Team).ToHashSet();
            if (teams.Count != rows.Count || !teams.Contains(outgoing) || teams.Contains(incoming))
                throw new StaleObjectException("The league table and the teams' divisions disagree");
            return teams;
        }

        IReadOnlyList<ClubSwapFixture> RequireSchedule(CompetitionRecord league, int members, ulong outgoing, ulong incoming)
        {
            var seasons = fixtures.Where(row => row.Division == league.Address)
                .GroupBy(row => row.Slot.Season)
                .Select(season => season.GroupBy(row => (row.Slot.Home, row.Slot.Away)).ToArray())
                .Where(groups => groups.Length == members * (members - 1)
                    && groups.All(group => group.Select(row => row.Date).Distinct().Count() == 1)).ToArray();
            if (seasons.Length != 1)
                throw new InvalidOperationException("The scheduled league fixtures are incomplete or ambiguous");
            var affected = seasons[0].Where(group => group.Key.Home == outgoing || group.Key.Away == outgoing).ToArray();
            slots.AddRange(affected.SelectMany(group => group).Select(row => row.Slot));
            return affected
                .Select(group => new ClubSwapFixture(league, group.First().Date, group.Count(), group.Key.Home, group.Key.Away,
                    group.Key.Home == outgoing ? incoming : group.Key.Home, group.Key.Away == outgoing ? incoming : group.Key.Away)).ToArray();
        }
    }

}
