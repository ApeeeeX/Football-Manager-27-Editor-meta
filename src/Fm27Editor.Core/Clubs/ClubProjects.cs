using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Clubs;

public enum ClubProjectKind { StadiumExpansion, FacilityUpgrade }

public sealed record ClubProjectQueueOffsets(int Vector, int Club, int Date, int? Stadium, uint? Vtable);
public sealed record ClubProjectOffsets(uint ManagerRoot, uint ManagerVtable, ClubProjectQueueOffsets Expansions, ClubProjectQueueOffsets Upgrades,
    int? StadiumPlans = null, int? StadiumPlanExpiry = null);

public sealed record ClubProject(ClubProjectKind Kind, ulong Address, ulong DateAddress, uint RawDate, DateOnly ScheduledDate);
public sealed record StadiumPlan(DateOnly? ExpiryDate);
public sealed record ClubProjectOverview(DateOnly Today, IReadOnlyList<ClubProject> Scheduled, StadiumPlan? Planning);

/// <summary>Projects and stadium plans owned by the club's finance manager.</summary>
public sealed class ClubProjects(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    const int DayMask = 0x1FF;
    const int MaxQueueBytes = 8 * 20000;
    const uint InactiveStadiumPlanDate = (1900u << 16) | 1u;
    public bool IsSupported => profile.Club is { Id: not null, Projects: not null } && profile.Stadium is not null;

    public IReadOnlyList<ClubProject> Read(ClubEntry club)
    {
        using var reads = new BulkReadScope(memory);
        return Read(reads.Memory, club, ReadToday(reads.Memory));
    }

    public ClubProjectOverview ReadOverview(ClubEntry club)
    {
        using var reads = new BulkReadScope(memory);
        var source = reads.Memory;
        var today = ReadToday(source);
        var (layout, manager) = ReadManager(source, club);
        var scheduled = ReadScheduled(source, club, today, layout, manager);
        return new(today, scheduled, ReadStadiumPlan(source, club, layout, manager));
    }

    static StadiumPlan? ReadStadiumPlan(IProcessMemory source, ClubEntry club, ClubProjectOffsets layout, ulong manager)
    {
        if (layout.StadiumPlans is not { } vector) return null;
        var pointers = ReadQueuePointers(source, manager, vector);
        for (int i = 0; i < pointers.Length; i += 8)
        {
            ulong row = BitConverter.ToUInt64(pointers, i);
            if (row == 0 || source.ReadUInt64(row) != club.Address) continue;
            if (layout.StadiumPlanExpiry is not { } dateField) return new(null);
            uint raw = BitConverter.ToUInt32(source.Read(row + (ulong)dateField, 4));
            // FM clears a closed plan to 1 Jan 1900 before removing its queue entry.
            if ((raw & 0xFFFF01FFu) == InactiveStadiumPlanDate) continue;
            return new(DecodeDate(raw));
        }
        return null;
    }

    public void BringForward(ClubEntry club, ClubProject project, DateOnly today) => BringForward(club, [project], today);

    public void BringForward(ClubEntry club, IReadOnlyList<ClubProject> projects, DateOnly today)
    {
        MemoryWriteBatch.Run(memory, staged =>
        {
            if (ReadToday(staged) != today) throw new StaleObjectException("The game date changed since the projects were reviewed");
            var current = Read(staged, club, today).ToHashSet();
            foreach (var project in projects)
            {
                if (project.ScheduledDate <= today) throw new StaleObjectException("This project is already due");
                if (!current.Remove(project)) throw new StaleObjectException("The club project changed since it was reviewed");
                var after = WithDate(project.RawDate, today);
                if (!staged.TryWrite(project.DateAddress, BitConverter.GetBytes(after)))
                    throw new MemoryAccessException("Could not set the project completion date");
            }
        });
    }

    public void ValidateUndo(IProcessMemory source, ClubEntry club, ClubProject project, DateOnly appliedDate)
    {
        var written = project with { RawDate = WithDate(project.RawDate, appliedDate), ScheduledDate = appliedDate };
        if (!Read(source, club, DateOnly.MinValue).Contains(written))
            throw new StaleObjectException("The club project is no longer in the queue");
    }

    static uint WithDate(uint raw, DateOnly date) => (raw & 0xFE00u) | (uint)date.DayOfYear | ((uint)date.Year << 16);

    DateOnly ReadToday(IProcessMemory source) => GameClock.Read(source, profile, moduleBase)
        ?? throw new InvalidOperationException("Game date is unavailable");

    IReadOnlyList<ClubProject> Read(IProcessMemory source, ClubEntry club, DateOnly today)
    {
        var (layout, manager) = ReadManager(source, club);
        return ReadScheduled(source, club, today, layout, manager);
    }

    (ClubProjectOffsets Layout, ulong Manager) ReadManager(IProcessMemory source, ClubEntry club)
    {
        if (profile.Club is not { Projects: { } layout, Id: { } clubId } c)
            throw new NotSupportedException("Club projects are unavailable on this game build");
        if (club.Id is not > 0 || source.ReadUInt64(club.Address) != moduleBase + c.ClubVtableRva ||
            BitConverter.ToInt32(source.Read(club.Address + (ulong)clubId, 4)) != club.Id)
            throw new StaleObjectException("The club changed since it was read");

        ulong manager = source.ReadUInt64(moduleBase + layout.ManagerRoot);
        if (manager == 0 || source.ReadUInt64(manager) != moduleBase + layout.ManagerVtable)
            throw new StaleObjectException("The finance manager is unavailable");
        return (layout, manager);
    }

    IReadOnlyList<ClubProject> ReadScheduled(IProcessMemory source, ClubEntry club, DateOnly today, ClubProjectOffsets layout, ulong manager)
    {
        var stadium = profile.Stadium ?? throw new NotSupportedException("Club projects are unavailable on this game build");
        var result = new List<ClubProject>();
        ReadQueue(layout.Expansions, ClubProjectKind.StadiumExpansion);
        ReadQueue(layout.Upgrades, ClubProjectKind.FacilityUpgrade);
        return result.OrderBy(project => project.ScheduledDate).ToArray();

        void ReadQueue(ClubProjectQueueOffsets queue, ClubProjectKind kind)
        {
            var pointers = ReadQueuePointers(source, manager, queue.Vector);
            for (int i = 0; i < pointers.Length; i += 8)
            {
                ulong address = BitConverter.ToUInt64(pointers, i);
                if (address == 0 || source.ReadUInt64(address + (ulong)queue.Club) != club.Address) continue;
                if (queue.Vtable is { } vt && source.ReadUInt64(address) != moduleBase + vt)
                    throw new StaleObjectException("The facility upgrade record is invalid");
                if (queue.Stadium is { } at)
                {
                    ulong venue = source.ReadUInt64(address + (ulong)at);
                    if (venue == 0 || source.ReadUInt64(venue) != moduleBase + stadium.VtableRva)
                        throw new StaleObjectException("The stadium expansion record is invalid");
                }
                ulong dateAddress = address + (ulong)queue.Date;
                uint raw = BitConverter.ToUInt32(source.Read(dateAddress, 4));
                if (DecodeDate(raw) is { } date && date >= today) result.Add(new(kind, address, dateAddress, raw, date));
            }
        }
    }

    static byte[] ReadQueuePointers(IProcessMemory source, ulong manager, int vectorField)
    {
        ulong vector = source.ReadUInt64(manager + (ulong)vectorField);
        var header = source.Read(vector, 24);
        ulong begin = BitConverter.ToUInt64(header), end = BitConverter.ToUInt64(header, 8), capacity = BitConverter.ToUInt64(header, 16);
        if (begin == 0 && end == 0 && capacity == 0) return [];
        if (begin == 0 || begin % 8 != 0 || end < begin || capacity < end || (capacity - begin) % 8 != 0 ||
            capacity - begin > MaxQueueBytes || (end - begin) % 8 != 0)
            throw new StaleObjectException("The club projects queue is invalid");
        return begin == end ? [] : source.Read(begin, checked((int)(end - begin)));
    }

    static DateOnly? DecodeDate(uint raw)
    {
        int year = (int)(raw >> 16), day = (int)(raw & DayMask);
        return year is >= 1901 and <= 2300 && day >= 1 && day <= (DateTime.IsLeapYear(year) ? 366 : 365)
            ? new DateOnly(year, 1, 1).AddDays(day - 1) : null;
    }
}
