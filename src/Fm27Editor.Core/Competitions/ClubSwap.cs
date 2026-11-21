using Fm27Editor.Core.Game;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Presets;
using Fm27Editor.Core.Transfer;

namespace Fm27Editor.Core.Competitions;

/// <summary>Exchanges the senior teams' places in two scheduled league seasons before their first match.</summary>
public sealed class ClubSwap(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    public bool IsSupported => !profile.ReadOnly && profile.Competition is { ClubSwap: not null, Fixtures: not null }
        && profile.Stadium is not null && memory is ICodePatchHost;

    public Func<ulong, int, ChangeLabel?> Apply(ClubSwapSchedule reviewed)
    {
        if (!IsSupported || memory is not ICodePatchHost host)
            throw new NotSupportedException("Club swapping is unavailable on this game build");
        var current = new ClubSwapPreview(memory, profile, moduleBase).Read(reviewed.First, reviewed.Second);
        if (current.ReadOn != reviewed.ReadOn || current.FirstLeague != reviewed.FirstLeague
            || current.SecondLeague != reviewed.SecondLeague || !current.Fixtures.SequenceEqual(reviewed.Fixtures)
            || !current.Slots.OrderBy(slot => slot.Address).SequenceEqual(reviewed.Slots.OrderBy(slot => slot.Address)))
            throw new StaleObjectException("The reviewed league schedules changed");

        var (guards, writes) = Prepare(current);
        using (host.Pause())
        {
            if (GameClock.Read(memory, profile, moduleBase) != current.ReadOn)
                throw new StaleObjectException("The game date changed while preparing the swap");
            FutureTransferTransaction.Apply(memory, guards, writes);
        }

        var labels = writes.ToDictionary(write => write.Address, write => write.What);
        return (address, _) => labels.TryGetValue(address, out string? label)
            ? new ChangeLabel(label, ValidateUndo: ValidateUndo) // An entry can be undone on its own.
            : null;

        void ValidateUndo(IProcessMemory source)
        {
            if (GameClock.Read(source, profile, moduleBase) != current.ReadOn)
                throw new StaleObjectException("The game advanced since the club swap");
            foreach (var guard in guards)
                if (!source.Read(guard.Address, guard.Expected.Length).AsSpan().SequenceEqual(guard.Expected))
                    throw new StaleObjectException("The league structure changed since the club swap");
        }
    }

    (IReadOnlyList<PlannedWrite> Guards, IReadOnlyList<PlannedWrite> Writes) Prepare(ClubSwapSchedule plan)
    {
        var competition = profile.Competition!;
        var layout = competition.ClubSwap!;
        var fixture = competition.Fixtures!;
        var firstTeam = plan.First.FirstTeam!.Address;
        var secondTeam = plan.Second.FirstTeam!.Address;
        var guards = new Dictionary<ulong, PlannedWrite>();
        var writes = new List<PlannedWrite>();
        var first = ReadSide(plan.FirstLeague, plan.SecondLeague, plan.First, secondTeam);
        var second = ReadSide(plan.SecondLeague, plan.FirstLeague, plan.Second, firstTeam);

        Replace(firstTeam, competition.TeamCompetition, plan.FirstLeague.Address, plan.SecondLeague.Address, "Current division");
        Replace(secondTeam, competition.TeamCompetition, plan.SecondLeague.Address, plan.FirstLeague.Address, "Current division");
        Replace(first.Participation, 0, plan.FirstLeague.Address, plan.SecondLeague.Address, "Season competition");
        Replace(second.Participation, 0, plan.SecondLeague.Address, plan.FirstLeague.Address, "Season competition");
        Replace(first.Participation, layout.ParticipationStats, first.Stats, second.Stats, "Season statistics");
        Replace(second.Participation, layout.ParticipationStats, second.Stats, first.Stats, "Season statistics");
        Replace(first.Stats, layout.StatsTeam, firstTeam, secondTeam, "Season participant");
        Replace(second.Stats, layout.StatsTeam, secondTeam, firstTeam, "Season participant");
        Replace(first.PrimaryRow, layout.StandingName, first.Name, second.Name, "Standing club name");
        Replace(second.PrimaryRow, layout.StandingName, second.Name, first.Name, "Standing club name");
        Replace(first.PrimaryRow, layout.StandingTeam, firstTeam, secondTeam, "League table team");
        Replace(second.PrimaryRow, layout.StandingTeam, secondTeam, firstTeam, "League table team");
        Replace(first.MirrorRow, layout.StandingTeam, firstTeam, secondTeam, "League table team");
        Replace(second.MirrorRow, layout.StandingTeam, secondTeam, firstTeam, "League table team");

        foreach (var slot in plan.Slots)
        {
            bool firstDivision = slot.Home == firstTeam || slot.Away == firstTeam;
            var (outgoing, incoming, oldStadium, newStadium, league) = firstDivision
                ? (firstTeam, secondTeam, first.Stadium, second.Stadium, plan.FirstLeague)
                : (secondTeam, firstTeam, second.Stadium, first.Stadium, plan.SecondLeague);
            Require(slot.Address, 0, moduleBase + fixture.FixtureVtable);
            Require(slot.Address, fixture.Name, slot.Owner);
            Require(slot.Owner, 0, moduleBase + fixture.NameVtable);
            Require(slot.Owner, fixture.Competition, league.Address);
            Guard(slot.Address + (ulong)fixture.Season, BitConverter.GetBytes(checked((ushort)slot.Season)));
            Guard(slot.Address + (ulong)fixture.Date, BitConverter.GetBytes(slot.RawDate));
            if (slot.Home == outgoing)
            {
                Require(slot.Address, fixture.Away, slot.Away);
                Replace(slot.Address, fixture.Home, outgoing, incoming, "Fixture home team");
                Replace(slot.Address, layout.FixtureStadium, oldStadium, newStadium, "Fixture venue");
            }
            else
            {
                Require(slot.Address, fixture.Home, slot.Home);
                Replace(slot.Address, fixture.Away, outgoing, incoming, "Fixture away team");
            }
        }
        return (guards.Values.ToArray(), writes);

        Side ReadSide(CompetitionRecord league, CompetitionRecord destination, Clubs.ClubEntry club, ulong incoming)
        {
            ulong outgoing = club.FirstTeam!.Address;
            var stages = new CompetitionPrizeMoneyAccess(memory, profile, moduleBase);
            var stage = stages.Read(league)?.Stage ?? throw Stale();
            var rows = new CompetitionStandingsAccess(memory, profile, moduleBase).Read(league) ?? throw Stale();
            var members = rows.Select(row => row.Team).ToHashSet();
            if (members.Contains(incoming) || !members.Contains(outgoing) || rows.Any(row => row.Played != 0))
                throw Stale();
            var row = rows.Single(row => row.Team == outgoing);
            if (row.Club != club.Address || row.ClubId != club.Id) throw Stale();
            if (row.Points != 0)
                throw new InvalidOperationException("A club with a points adjustment cannot be swapped");

            Require(stage, competition.PrizeMoney!.StageOwner, league.Address);
            Require(league.Address, 0, moduleBase + competition.VtableRva);
            Guard(league.Address + (ulong)competition.Id!.Value, BitConverter.GetBytes(league.Id));
            Require(outgoing, 0, moduleBase + profile.Team!.VtableRva);
            Require(outgoing, profile.Contract!.TeamClub, row.Club);
            Require(club.Address, 0, moduleBase + profile.Club!.ClubVtableRva);
            Guard(club.Address + (ulong)profile.Club.Id!.Value, BitConverter.GetBytes(row.ClubId));
            var primary = ReadVector(stage + CompetitionStandingsAccess.TableOffset, 8, 256);
            if (primary.Count != (ulong)rows.Count) throw Stale();
            for (int i = 0; i < rows.Count; i++)
            {
                var member = rows[i];
                Require(primary.Begin + (ulong)i * 8, 0, member.Address);
                Guard(member.Address + CompetitionStandingsAccess.PlayedOffset, [0]);
                if (member.Team != outgoing) Require(member.Address, layout.StandingTeam, member.Team);
            }
            Guard(row.Address + CompetitionStandingsAccess.PointsOffset, BitConverter.GetBytes((short)0));
            var mirror = ReadVector(stage + (ulong)layout.SecondaryStandings, 8, 256);
            var mirrorMembers = new Dictionary<ulong, ulong>();
            for (ulong index = 0; index < mirror.Count; index++)
            {
                ulong address = ReadGuard(mirror.Begin + index * 8);
                ulong team = memory.ReadUInt64(address + (ulong)layout.StandingTeam);
                if (team != outgoing) Require(address, layout.StandingTeam, team);
                if (!mirrorMembers.TryAdd(team, address)) throw Stale();
                Guard(address + CompetitionStandingsAccess.PlayedOffset, [0]);
            }
            if (!members.SetEquals(mirrorMembers.Keys) || mirrorMembers[outgoing] == row.Address)
                throw Stale();

            ulong seasonState = ReadGuard(outgoing + (ulong)layout.TeamSeasonState);
            var entries = ReadVector(seasonState + (ulong)layout.Competitions, layout.ParticipationStride, 64);
            ulong entry = 0;
            for (ulong i = 0; i < entries.Count; i++)
            {
                ulong address = entries.Begin + i * (ulong)layout.ParticipationStride;
                ulong owner = memory.ReadUInt64(address);
                if (owner == destination.Address) throw Stale();
                if (owner == league.Address)
                {
                    if (entry != 0) throw Stale();
                    entry = address;
                }
                else Require(address, 0, owner);
            }
            if (entry == 0) throw Stale();
            ulong stats = memory.ReadUInt64(entry + (ulong)layout.ParticipationStats);
            Require(stats, 0, moduleBase + layout.StatsVtable);
            ulong stadium = ReadGuard(outgoing + (ulong)layout.TeamStadium);
            Require(stadium, 0, moduleBase + profile.Stadium!.VtableRva);
            ulong name = memory.ReadUInt64(row.Address + (ulong)layout.StandingName);
            if (name == 0) throw Stale();
            return new(row.Address, mirrorMembers[outgoing], name, entry, stats, stadium);
        }

        (ulong Begin, ulong Count) ReadVector(ulong header, int stride, int limit)
        {
            ulong begin = ReadGuard(header), end = ReadGuard(header + 8), capacity = ReadGuard(header + 16);
            if (begin == 0 || begin % 8 != 0 || end <= begin || capacity < end
                || (end - begin) % (ulong)stride != 0 || capacity - begin > (ulong)(stride * limit))
                throw Stale();
            return (begin, (end - begin) / (ulong)stride);
        }

        ulong ReadGuard(ulong address)
        {
            var bytes = memory.Read(address, 8);
            AddGuard(address, bytes);
            return BitConverter.ToUInt64(bytes);
        }
        void Require(ulong address, int offset, ulong expected) => Guard(address + (ulong)offset, BitConverter.GetBytes(expected));
        void Guard(ulong address, byte[] expected)
        {
            if (!memory.Read(address, expected.Length).AsSpan().SequenceEqual(expected)) throw Stale();
            AddGuard(address, expected);
        }
        void AddGuard(ulong address, byte[] expected)
        {
            if (guards.TryGetValue(address, out var guard) && !guard.Expected.AsSpan().SequenceEqual(expected)) throw Stale();
            guards[address] = new(address, expected, [], "League snapshot");
        }
        void Replace(ulong address, int offset, ulong before, ulong after, string label)
        {
            address += (ulong)offset;
            var expected = BitConverter.GetBytes(before);
            if (!memory.Read(address, expected.Length).AsSpan().SequenceEqual(expected)) throw Stale();
            if (before != after) writes.Add(new(address, expected, BitConverter.GetBytes(after), label));
        }
    }

    static StaleObjectException Stale() => new("The league membership or scheduled fixtures changed");

    sealed record Side(ulong PrimaryRow, ulong MirrorRow, ulong Name, ulong Participation, ulong Stats, ulong Stadium);
}
