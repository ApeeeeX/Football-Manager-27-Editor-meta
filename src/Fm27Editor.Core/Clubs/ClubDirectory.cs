using System.Text;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Clubs;

/// <summary>A team of a club (first team, reserves, U21, U18...) and its squad vector.</summary>
public sealed record TeamEntry(ulong Address, ulong Club, ulong SquadBegin, int SquadCount, int SquadCapacity, bool IsFirstTeam)
{
    public bool HasRoom => SquadCount < SquadCapacity;
}

public sealed record ClubEntry(ulong Address, int? Id, string? Name, string? ShortName, ulong Finance, IReadOnlyList<TeamEntry> Teams, Gender Gender = Gender.Unknown)
{
    public string DisplayName => ShortName ?? Name ?? $"club {Id}";
    public TeamEntry? FirstTeam => Teams.FirstOrDefault(t => t.IsFirstTeam);
}

/// <summary>
/// Every club and team in memory, from one pass over the writable heap: objects with the club vtable, and
/// objects with the team vtable whose club pointer leads to one of them. The first team is the team most of
/// whose squad is contracted to that team itself; reserve and youth players are contracted to the first team
/// (Lacey at Paris SG 2 is contracted to PSG's first team).
/// </summary>
public static class ClubDirectory
{
    const int MaxSquad = 200, MaxName = 200;

    /// <summary>
    /// Clubs and teams reachable from the loaded players (their playing team and contract team), so no heap
    /// scan: well under a second instead of ~15 s. Clubs without a single player are not listed.
    /// </summary>
    public static IReadOnlyList<ClubEntry> FromPlayers(IProcessMemory memory, IEnumerable<Players.PlayerRecord> players, OffsetProfile profile, ulong moduleBase)
    {
        using var reads = new BulkReadScope(memory);
        return FromPlayersCore(reads.Memory, players, profile, moduleBase);
    }

    static IReadOnlyList<ClubEntry> FromPlayersCore(IProcessMemory memory, IEnumerable<Players.PlayerRecord> players, OffsetProfile profile, ulong moduleBase)
    {
        var t = profile.Team ?? throw new InvalidOperationException("Profile has no team offsets");
        var c = profile.Contract ?? throw new InvalidOperationException("Profile has no contract offsets");
        var teams = new HashSet<ulong>();
        foreach (var p in players)
        {
            if (p.PlayingTeamAddress is { } pt)
            {
                if (pt != 0) teams.Add(pt);
            }
            else if (TryQ(memory, profile.PlayerField(p, t.PlayerTeam), out ulong readPt) && readPt != 0)
            {
                teams.Add(readPt);
            }

            if (p.ContractTeamAddress is { } ct)
            {
                if (ct != 0) teams.Add(ct);
            }
            else if (p.Contract is { } k && TryQ(memory, k.Address + (ulong)c.Team, out ulong readCt) && readCt != 0)
            {
                teams.Add(readCt);
            }
        }
        return Build(memory, profile, moduleBase, teams, clubs: null);
    }

    public static IReadOnlyList<ClubEntry> Load(IProcessMemory memory, IEnumerable<MemoryRegion> regions, OffsetProfile profile, ulong moduleBase)
    {
        var k = profile.Club ?? throw new InvalidOperationException("Profile has no club offsets");
        var t = profile.Team ?? throw new InvalidOperationException("Profile has no team offsets");
        var c = profile.Contract ?? throw new InvalidOperationException("Profile has no contract offsets");
        ulong clubVt = moduleBase + k.ClubVtableRva, teamVt = moduleBase + t.VtableRva;
        var clubs = new List<ulong>();
        var teams = new List<ulong>();
        foreach (var r in regions.Where(r => !r.IsModuleData && r.Size <= 0x4000_0000))
        {
            var d = new byte[r.Size];
            if (!memory.TryRead(r.Start, d)) continue;
            for (int o = 0; o + 8 <= d.Length; o += 8)
            {
                ulong q = BitConverter.ToUInt64(d, o);
                if (q == clubVt) clubs.Add(r.Start + (ulong)o);
                else if (q == teamVt) teams.Add(r.Start + (ulong)o);
            }
        }
        return Build(memory, profile, moduleBase, teams, clubs);
    }

    /// <param name="clubs">Known club objects; null to take them from the teams' club pointers (checked by vtable).</param>
    static IReadOnlyList<ClubEntry> Build(IProcessMemory memory, OffsetProfile profile, ulong moduleBase, IEnumerable<ulong> teams, IEnumerable<ulong>? clubs)
    {
        var k = profile.Club!;
        var t = profile.Team!;
        var c = profile.Contract!;
        ulong clubVt = moduleBase + k.ClubVtableRva, teamVt = moduleBase + t.VtableRva;
        var clubSet = clubs?.ToHashSet() ?? [];
        var teamsByClub = new Dictionary<ulong, List<TeamEntry>>();
        var teamArray = teams as ulong[] ?? teams.ToArray();
        int teamReadSize = Math.Max(c.TeamClub + 8, t.Squad + 24);
        if (memory is ReadAheadMemory ram) ram.Prefetch(teamArray, teamReadSize);
        var teamBuf = new byte[teamReadSize];
        foreach (ulong team in teamArray)
        {
            if (!memory.TryRead(team, teamBuf) || BitConverter.ToUInt64(teamBuf, 0) != teamVt) continue;
            ulong club = BitConverter.ToUInt64(teamBuf, c.TeamClub);
            if (clubs is null && !clubSet.Contains(club) && TryQ(memory, club, out ulong cv) && cv == clubVt) clubSet.Add(club);
            if (!clubSet.Contains(club)) continue;
            ulong begin = BitConverter.ToUInt64(teamBuf, t.Squad);
            ulong end = BitConverter.ToUInt64(teamBuf, t.Squad + 8);
            ulong cap = BitConverter.ToUInt64(teamBuf, t.Squad + 16);
            if (begin == 0 || end < begin || cap < end || (cap - begin) / 8 > MaxSquad) continue;
            int count = (int)((end - begin) / 8);
            // First team: its first squad player is contracted to the team itself (one check per team keeps the scan fast).
            bool first = count > 0 && TryQ(memory, begin, out ulong player)
                         && TryQ(memory, profile.PlayerField(memory, moduleBase, player, c.PlayerContract), out ulong contract) && contract != 0
                         && TryQ(memory, contract + (ulong)c.Team, out ulong contractTeam) && contractTeam == team;
            if (!teamsByClub.TryGetValue(club, out var list)) teamsByClub[club] = list = [];
            list.Add(new TeamEntry(team, club, begin, count, (int)((cap - begin) / 8), first));
        }
        int clubReadSize = Math.Max(0xD0, k.Finance is { } fin ? fin.Pointer + 8 : 0xD0);
        if (memory is ReadAheadMemory clubRam) clubRam.Prefetch(clubSet.ToArray(), clubReadSize);
        var clubBuf = new byte[0xD0];
        return clubSet.Select(club =>
        {
            Array.Clear(clubBuf);
            memory.TryRead(club, clubBuf);
            ulong finance = k.Finance is { } f && TryQ(memory, club + (ulong)f.Pointer, out ulong fp) ? fp : 0;
            return new ClubEntry(club, k.Id is { } id ? BitConverter.ToInt32(clubBuf, id) : null,
                StringAt(memory, BitConverter.ToUInt64(clubBuf, k.Name)), StringAt(memory, BitConverter.ToUInt64(clubBuf, k.ShortName)),
                finance, teamsByClub.TryGetValue(club, out var ts) ? ts : [], GenderReader.FromClubBytes(clubBuf, k.Gender));
        }).ToList();
    }

    static bool TryQ(IProcessMemory memory, ulong address, out ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        bool ok = memory.TryRead(address, b);
        value = ok ? BitConverter.ToUInt64(b) : 0;
        return ok;
    }

    /// <summary>Length-prefixed UTF-8, as the player names.</summary>
    static string? StringAt(IProcessMemory memory, ulong p)
    {
        if (p == 0) return null;
        Span<byte> len = stackalloc byte[4];
        if (!memory.TryRead(p, len)) return null;
        int n = BitConverter.ToInt32(len);
        if (n < 0 || n > MaxName) return null;
        var bytes = new byte[n];
        return memory.TryRead(p + 4, bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }
}
