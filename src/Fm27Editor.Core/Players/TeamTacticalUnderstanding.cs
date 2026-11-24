using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

/// <summary>Where a human-managed team's tactics are kept (Mac libc++ tree or Windows MSVC std::map tree, driven by profile offsets).</summary>
public sealed record TeamTacticOffsets(uint ManagersRva, int Entries, int TeamTree, int TreeKey, int TreeValue, int Tactics, int FlagsA, int FlagsB,
    int TreeRight = 8, int? TreeHeadParent = null, int? TreeIsNil = null);

/// <summary>
/// Tactical Understanding of a team the way the game's Maximise action sees it. Each tactic slot maps to one option per area;
/// every squad player holds a list per area of (option, value), and an option the player has never learned has no row, so
/// raising only existing rows has no effect on the current tactic. A missing row is added, as the game does.
/// </summary>
public sealed class TeamTacticalUnderstanding(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    public const int Areas = 6;

    /// <summary>The bars in the game's order and the list each one reads. Measured on Steam 27.1.0 macOS, 23/11/2026: lists 0-5
    /// were written 11, 22, 33, 44, 55, 66 and FMRTE showed Line of Engagement 11, Pressing Intensity 22, Pressing Trap 33,
    /// Passing Style 44, Build Up 55 and Chance Creation 66.</summary>
    public static readonly (string Group, string Name, int List)[] Bars =
    [
        ("In Possession", "Passing Style", 3), ("In Possession", "Build Up", 4), ("In Possession", "Chance Creation", 5),
        ("Out of Possession", "Line of Engagement", 0), ("Out of Possession", "Pressing Intensity", 1), ("Out of Possession", "Pressing Trap", 2),
    ];

    const int MaxRows = 20, MaxOption = 20, ValueMask = 0x3FFF, OptionShift = 14, OptionMask = 0x3F, MaxManagers = 64, MaxTactics = 3, MaxDepth = 64;

    public bool IsSupported => profile.TeamTactics is not null && profile.TacticalUnderstanding is not null;

    /// <summary>The option of each area for one tactic's flag words; the mapping of the game's function at 0x4919368.</summary>
    public static int[] OptionsOf(ulong a, ulong b)
    {
        static bool Bit(ulong q, int bit) => (q >> bit & 1) != 0;
        static int Clamp(int v) => (v & 0xFE) != 0 ? v & 0xFF : 1;
        int passing = Bit(a, 61) ? 17 : a >> 63 == 0 ? 10 : 3;
        passing += Bit(a, 56) ? 3 : Bit(a, 57) ? 2 : Bit(a, 58) ? -2 : Bit(a, 59) ? -3 : 0;
        int buildUp = Bit(b, 1) ? 17 : Bit(b, 2) ? 14 : Bit(b, 3) ? 6 : Bit(b, 4) ? 3 : 10;
        buildUp += Bit(b, 6) ? 3 : Bit(b, 7) ? -3 : 0;
        int chance = Bit(b, 16) ? 20 : Bit(b, 17) ? 1 : 10;
        int shape = Bit(a, 1) ? 17 : Bit(a, 2) ? 14 : Bit(a, 3) ? 6 : Bit(a, 4) ? 3 : 10;
        shape += Bit(a, 46) ? 3 : Bit(a, 47) ? 2 : Bit(a, 48) ? -2 : Bit(a, 49) ? -3 : 0;
        int line = Bit(a, 7) ? 17 : Bit(a, 8) ? 3 : 10;
        line += Bit(a, 44) ? 3 : Bit(a, 45) ? -3 : 0;
        int press = Bit(a, 9) ? 13 : Bit(a, 17) ? 7 : 10;
        press += Bit(a, 15) ? 2 : Bit(a, 16) ? -2 : 0;
        press += Bit(a, 5) ? 2 : Bit(a, 6) ? -2 : 0;
        press += Bit(a, 54) ? 3 : Bit(a, 55) ? -3 : 0;
        return [Math.Max(passing, 1), Clamp(buildUp), chance, Math.Max(shape, 1), Clamp(line), Clamp(press)];
    }

    /// <summary>One option list per tactic slot; null when the team has no human manager's tactics (unknown, not empty).</summary>
    public IReadOnlyList<int[]>? TacticOptions(ulong team)
    {
        if (team == 0 || profile.TeamTactics is not { } o) return null;
        Span<byte> b = stackalloc byte[8];
        if (!memory.TryRead(team + 8, b[..4])) return null;
        int id = BitConverter.ToInt32(b[..4]);
        if (!memory.TryRead(moduleBase + o.ManagersRva, b)) return null;
        ulong root = BitConverter.ToUInt64(b);
        if (root == 0) return null;
        ulong begin = memory.ReadUInt64(root + (ulong)o.Entries), end = memory.ReadUInt64(root + (ulong)o.Entries + 8);
        if (begin == 0 || end < begin || (end - begin) % 8 != 0 || (end - begin) / 8 > MaxManagers) return null;
        for (ulong at = begin; at < end; at += 8)
        {
            ulong entry = memory.ReadUInt64(at);
            if (entry == 0) continue;
            ulong node = memory.ReadUInt64(entry + (ulong)o.TeamTree);
            if (o.TreeHeadParent is { } headParent && node != 0)
                node = memory.ReadUInt64(node + (ulong)headParent);
            for (int depth = 0; node != 0 && depth < MaxDepth; depth++)
            {
                if (o.TreeIsNil is { } isNil && memory.Read(node + (ulong)isNil, 1)[0] != 0) break;
                int key = BitConverter.ToInt32(memory.Read(node + (ulong)o.TreeKey, 4));
                if (key == id) return Options(node + (ulong)o.TreeValue, o);
                node = memory.ReadUInt64(node + (key < id ? (ulong)o.TreeRight : 0UL));
            }
        }
        return null;
    }

    IReadOnlyList<int[]>? Options(ulong holder, TeamTacticOffsets o)
    {
        ulong begin = memory.ReadUInt64(holder + (ulong)o.Tactics), end = memory.ReadUInt64(holder + (ulong)o.Tactics + 8);
        if (begin == 0 || end <= begin || (end - begin) % 8 != 0) return null;
        var result = new List<int[]>();
        for (ulong at = begin; at < end && result.Count < MaxTactics; at += 8)
        {
            ulong tactic = memory.ReadUInt64(at);
            if (tactic != 0) result.Add(OptionsOf(memory.ReadUInt64(tactic + (ulong)o.FlagsA), memory.ReadUInt64(tactic + (ulong)o.FlagsB)));
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Squad average (0-100) of each area: over the team's tactics when human-managed, or each player's highest learned option for AI teams.</summary>
    public int[]? Read(ulong team, IReadOnlyList<PlayerRecord> squad)
    {
        if (squad.Count == 0) return null;
        var tactics = TacticOptions(team);
        var result = new int[Areas];
        for (int area = 0; area < Areas; area++)
        {
            long total = 0; int count = 0;
            if (tactics is not null)
            {
                var options = tactics.Select(t => t[area]).Distinct().ToArray();
                foreach (var player in squad)
                {
                    var rows = Rows(Holder(player), area);
                    foreach (int option in options)
                    {
                        count++;
                        foreach (uint row in rows) if ((row >> OptionShift & OptionMask) == option) { total += row & ValueMask; break; }
                    }
                }
            }
            else
            {
                foreach (var player in squad)
                {
                    count++;
                    int max = 0;
                    foreach (uint row in Rows(Holder(player), area))
                    {
                        int v = (int)(row & ValueMask);
                        if (v > max) max = v;
                    }
                    total += max;
                }
            }
            result[area] = count == 0 ? 0 : (int)Math.Round(total / (double)count / 100);
        }
        return result;
    }

    /// <summary>Sets one area (0-100) for the whole squad on the options of the team's tactics (or existing rows for AI teams). Returns how many rows changed.</summary>
    public int Set(ulong team, IReadOnlyList<PlayerRecord> squad, int area, int percent, Func<int, ulong> allocate)
    {
        if (area is < 0 or >= Areas) throw new ArgumentOutOfRangeException(nameof(area));
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        var tactics = TacticOptions(team);
        int changed = 0;
        int scaled = percent * 100;
        foreach (var player in squad)
        {
            ulong holder = Holder(player);
            if (holder == 0) continue;
            ulong vector = holder + (ulong)(area * Stride);
            if (tactics is not null)
            {
                foreach (int option in tactics.Select(t => t[area]).Distinct())
                    if (SetRow(vector, option, scaled, allocate)) changed++;
            }
            else
            {
                changed += SetExistingRows(vector, scaled);
            }
        }
        return changed;
    }

    /// <summary>The game's Maximise for the six areas: every tactic option at 100%, adding missing rows.</summary>
    public int Maximise(ulong team, IReadOnlyList<PlayerRecord> squad, Func<int, ulong> allocate)
    {
        int changed = 0;
        for (int area = 0; area < Areas; area++) changed += Set(team, squad, area, 100, allocate);
        return changed;
    }

    /// <summary>Squad average (0-100) of the Position/Role rows the players already hold; null when none has any.</summary>
    public int? ReadRoles(IReadOnlyList<PlayerRecord> squad, ulong team = 0)
    {
        bool humanTeam = team != 0 && TacticOptions(team) is not null;
        double total = 0; int count = 0;
        foreach (var player in squad)
        {
            long playerTotal = 0; int playerMax = 0, playerCount = 0;
            foreach (var (at, _) in RoleRows(Holder(player)))
            {
                int v = BitConverter.ToUInt16(memory.Read(at, 2));
                playerTotal += v;
                if (v > playerMax) playerMax = v;
                playerCount++;
            }
            if (playerCount == 0) continue;
            total += humanTeam ? playerMax : playerTotal / (double)playerCount;
            count++;
        }
        return count == 0 ? null : (int)Math.Round(total / count / 100);
    }

    /// <summary>Sets the Position/Role rows the players already hold. A role a player has no row for is not added.</summary>
    public int SetRoles(IReadOnlyList<PlayerRecord> squad, int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        var want = BitConverter.GetBytes((ushort)(percent * 100));
        int changed = 0;
        foreach (var player in squad)
            foreach (var (at, _) in RoleRows(Holder(player)))
            {
                if (memory.Read(at, 2).AsSpan().SequenceEqual(want)) continue;
                Write(at, want);
                changed++;
            }
        return changed;
    }

    /// <summary>One player's share of the game's Maximise: the team's tactic options at 100%, adding missing rows (or existing rows for AI teams).</summary>
    public int MaximisePlayer(ulong team, PlayerRecord player, Func<int, ulong> allocate)
    {
        ulong holder = Holder(player);
        if (holder == 0) return 0;
        var tactics = TacticOptions(team);
        int changed = 0;
        for (int area = 0; area < Areas; area++)
        {
            ulong vector = holder + (ulong)(area * Stride);
            if (tactics is not null)
            {
                foreach (int option in tactics.Select(t => t[area]).Distinct())
                    if (SetRow(vector, option, 10_000, allocate)) changed++;
            }
            else
            {
                changed += SetExistingRows(vector, 10_000);
            }
        }
        return changed;
    }

    int SetExistingRows(ulong vector, int value)
    {
        ulong begin = memory.ReadUInt64(vector), end = memory.ReadUInt64(vector + 8), capacity = memory.ReadUInt64(vector + 16);
        if (begin == 0 || end <= begin || capacity < end || (end - begin) % 4 != 0 || (capacity - begin) % 4 != 0 || (end - begin) / 4 > MaxRows)
            return 0;
        int count = (int)((end - begin) / 4), changed = 0;
        var old = memory.Read(begin, count * 4);
        for (int i = 0; i < count; i++)
        {
            uint row = BitConverter.ToUInt32(old, i * 4);
            if ((row & ValueMask) == value) continue;
            Write(begin + (ulong)(i * 4), BitConverter.GetBytes(row & ~(uint)ValueMask | (uint)value));
            changed++;
        }
        return changed;
    }

    IEnumerable<(ulong At, int Index)> RoleRows(ulong holder)
    {
        if (holder == 0) yield break;
        var o = profile.TacticalUnderstanding!;
        ulong begin = memory.ReadUInt64(holder + (ulong)o.RoleVector), end = memory.ReadUInt64(holder + (ulong)o.RoleVector + 8);
        if (begin == 0 || end <= begin || (end - begin) % (ulong)o.RoleEntry != 0 || (end - begin) / (ulong)o.RoleEntry > PlayerTacticalUnderstanding.MaxItems) yield break;
        int count = (int)((end - begin) / (ulong)o.RoleEntry);
        for (int i = 0; i < count; i++) yield return (begin + (ulong)(i * o.RoleEntry + o.RoleValue), i);
    }

    int Stride => profile.TacticalUnderstanding!.Stride;

    ulong Holder(PlayerRecord player) => memory.ReadUInt64(profile.PlayerField(player, profile.TacticalUnderstanding!.Holder));

    uint[] Rows(ulong holder, int area)
    {
        if (holder == 0) return [];
        ulong vector = holder + (ulong)(area * Stride), begin = memory.ReadUInt64(vector), end = memory.ReadUInt64(vector + 8);
        if (begin == 0 || end <= begin || (end - begin) % 4 != 0 || (end - begin) / 4 > MaxRows) return [];
        var bytes = memory.Read(begin, (int)(end - begin));
        var rows = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, rows, 0, bytes.Length);
        return rows;
    }

    bool SetRow(ulong vector, int option, int value, Func<int, ulong> allocate)
    {
        ulong begin = memory.ReadUInt64(vector), end = memory.ReadUInt64(vector + 8), capacity = memory.ReadUInt64(vector + 16);
        if (end < begin || capacity < end || (end - begin) % 4 != 0 || (capacity - begin) % 4 != 0 || (end - begin) / 4 > MaxRows)
            throw new MemoryAccessException($"Tactical understanding list at 0x{vector:X} is not valid");
        int count = (int)((end - begin) / 4);
        var old = count == 0 ? [] : memory.Read(begin, count * 4);
        for (int i = 0; i < count; i++)
        {
            uint row = BitConverter.ToUInt32(old, i * 4);
            if ((row >> OptionShift & OptionMask) != option) continue;
            if ((row & ValueMask) == value) return false;
            Write(begin + (ulong)(i * 4), BitConverter.GetBytes(row & ~(uint)ValueMask | (uint)value));
            return true;
        }
        // The game refuses an option above 20 or a list that already has 20 rows.
        if (option > MaxOption || count >= MaxRows) return false;
        var added = BitConverter.GetBytes((uint)option << OptionShift | (uint)value);
        if (end < capacity)
        {
            Write(end, added);
            Write(vector + 8, BitConverter.GetBytes(end + 4));
            return true;
        }
        int rows = Math.Max((int)((capacity - begin) / 4) * 2, count + 1);
        ulong block = allocate(rows * 4);
        var fresh = new byte[rows * 4];
        old.CopyTo(fresh, 0);
        added.CopyTo(fresh, count * 4);
        Write(block, fresh);
        var header = new byte[24];
        BitConverter.GetBytes(block).CopyTo(header, 0);
        BitConverter.GetBytes(block + (ulong)((count + 1) * 4)).CopyTo(header, 8);
        BitConverter.GetBytes(block + (ulong)(rows * 4)).CopyTo(header, 16);
        Write(vector, header);
        return true;
    }

    void Write(ulong at, byte[] bytes)
    {
        if (!memory.TryWrite(at, bytes) || !memory.Read(at, bytes.Length).AsSpan().SequenceEqual(bytes))
            throw new MemoryAccessException($"Could not write tactical understanding at 0x{at:X}");
    }
}
