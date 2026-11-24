using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Clubs;

public enum ClubProjectKind { StadiumExpansion, FacilityUpgrade }

public sealed record ClubProjectQueueOffsets(int Vector, int Club, int Date, int? Stadium, uint? Vtable)
{
    /// <summary>How to add an expansion row of the game's own shape (FM Touch 27.1.0, measured 24/11/2026); null where unmeasured.</summary>
    public ClubProjectRowOffsets? NewRow { get; init; }
}

/// <summary>Fields of a stadium expansion row besides club, stadium and completion date: the row size, the start date, the
/// seats added and the stadium's capacity and seating capacity when the works began.</summary>
public sealed record ClubProjectRowOffsets(int Size, int StartDate, int AddedSeats, int Capacity, int SeatingCapacity);
/// <summary>
/// The game's table of pending stadium changes (<c>db::DATABASE_TABLE&lt;db::STADIUM_CHANGE&gt;</c>), the record that
/// actually finishes a capacity change. Windows Steam 27.1.0, 18/12/2026: moving Wrexham's completion date (+0x34)
/// to the game date removed the row on the next day and STōK Cae Ras went from 10,859 to its +0x3C target of 18,500;
/// moving only the finance manager's expansion row, or +0x30, did not. The CE preset "Complete Active Board
/// Requests" writes this table as well as the finance manager queues.
/// </summary>
public sealed record StadiumChangeOffsets(uint TableRoot, uint TableVtable, int Vector, uint Vtable, int Stadium, int Date);

public sealed record StadiumBoardRequestOffsets(int HolderType, int Requests, int Vector, int RequestType, int Flags,
    int TargetDate, int CompletedDate);

public sealed record ClubProjectOffsets(uint ManagerRoot, uint ManagerVtable, ClubProjectQueueOffsets Expansions, ClubProjectQueueOffsets Upgrades,
    int? StadiumPlans = null, int? StadiumPlanExpiry = null, StadiumBoardRequestOffsets? BoardRequests = null)
{
    public StadiumChangeOffsets? StadiumChanges { get; init; }
}

/// <summary>A scheduled project. <paramref name="ChangeDateAddress"/> is the stadium change that completes an
/// expansion (0 when the build has no stadium change table or the expansion has none).</summary>
public sealed record ClubProject(ClubProjectKind Kind, ulong Address, ulong DateAddress, uint RawDate, DateOnly ScheduledDate,
    ulong ChangeDateAddress = 0, uint ChangeRawDate = 0);
public sealed record StadiumPlan(DateOnly? ExpiryDate, DateOnly? ReviewDate = null);
public sealed record ClubProjectOverview(DateOnly Today, IReadOnlyList<ClubProject> Scheduled, StadiumPlan? Planning,
    DateOnly? AnnouncedExpansionDate = null);

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
        var planning = ReadStadiumPlan(source, club, layout, manager);
        var (expansionDate, reviewDate) = layout.BoardRequests is { } requests
            ? new ClubListReader(source, profile, moduleBase).ReadStadiumBoardRequests(club.Address, requests, today)
            : (null, null);
        if (reviewDate is not null) planning = new(planning?.ExpiryDate, reviewDate);
        if (scheduled.Any(project => project.Kind == ClubProjectKind.StadiumExpansion)) expansionDate = null;
        return new(today, scheduled, planning, expansionDate);
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
                if (project.DateAddress != 0 &&
                    !staged.TryWrite(project.DateAddress, BitConverter.GetBytes(WithDate(project.RawDate, today))))
                    throw new MemoryAccessException("Could not set the project completion date");
                if (project.ChangeDateAddress != 0 &&
                    !staged.TryWrite(project.ChangeDateAddress, BitConverter.GetBytes(WithDate(project.ChangeRawDate, today))))
                    throw new MemoryAccessException("Could not set the stadium change completion date");
            }
        });
    }

    public bool CanFinishAnnounced => profile.Club?.Projects?.Expansions is { NewRow: not null, Stadium: not null }
        && profile.Competition?.ClubSwap is not null && profile.Stadium is not null;

    /// <summary>
    /// Finishes an approved stadium expansion that has not started (shown as "Announced"): adds an expansion row due today,
    /// copied from a row the game made and pointed at this club's home ground, so the game completes the works on its next
    /// day as it does for its own rows. Rows are only added into the queue's spare capacity.
    /// </summary>
    public void FinishAnnouncedExpansion(ClubEntry club, DateOnly today, Func<int, ulong> allocate)
    {
        if (!CanFinishAnnounced) throw new NotSupportedException("Finishing announced projects is unavailable on this game build");
        var stadiumLayout = profile.Stadium!;
        var (layout, manager) = ReadManager(memory, club);
        var queue = layout.Expansions;
        var shape = queue.NewRow!;
        if (ReadToday(memory) != today) throw new StaleObjectException("The game date changed since the projects were reviewed");
        var overview = ReadOverview(club);
        if (overview.AnnouncedExpansionDate is null) throw new StaleObjectException("The club has no announced stadium expansion");
        if (club.FirstTeam is not { } team) throw new StaleObjectException("The club has no senior team");
        ulong home = memory.ReadUInt64(team.Address + (ulong)profile.Competition!.ClubSwap!.TeamStadium);
        if (home == 0 || memory.ReadUInt64(home) != moduleBase + stadiumLayout.VtableRva) throw new StaleObjectException("The home ground is unavailable");

        ulong header = memory.ReadUInt64(manager + (ulong)queue.Vector);
        var vector = memory.Read(header, 24);
        ulong begin = BitConverter.ToUInt64(vector), end = BitConverter.ToUInt64(vector, 8), capacity = BitConverter.ToUInt64(vector, 16);
        if (begin == 0 || end < begin || capacity < end || (end - begin) % 8 != 0) throw new StaleObjectException("The club projects queue is invalid");
        if (end + 8 > capacity) throw new InvalidOperationException("The game's project queue is full; continue a few days and try again");

        // A row the game made for another club supplies the fields whose meaning is unknown.
        byte[]? template = null;
        for (ulong at = begin; at < end && template is null; at += 8)
        {
            ulong row = memory.ReadUInt64(at);
            if (row == 0) continue;
            ulong rowClub = memory.ReadUInt64(row + (ulong)queue.Club), venue = memory.ReadUInt64(row + (ulong)queue.Stadium!.Value);
            if (rowClub != 0 && venue != 0 && memory.ReadUInt64(venue) == moduleBase + stadiumLayout.VtableRva)
                template = memory.Read(row, shape.Size);
        }
        if (template is null) throw new InvalidOperationException("The game has no expansion row to copy yet");

        var fields = new Stadiums.StadiumWriter(memory, profile, moduleBase).ReadFields(home);
        int seats = (int)fields.GetValueOrDefault(Stadiums.StadiumField.Capacity);
        int seating = (int)fields.GetValueOrDefault(Stadiums.StadiumField.SeatingCapacity, seats);
        int added = (int)Math.Max(1000, fields.GetValueOrDefault(Stadiums.StadiumField.ExpansionCapacity) - seats);
        uint flags = BitConverter.ToUInt32(template, queue.Date) & 0xFE00u;
        uint date = flags | (uint)today.DayOfYear | ((uint)today.Year << 16);
        BitConverter.GetBytes(club.Address).CopyTo(template, queue.Club);
        BitConverter.GetBytes(home).CopyTo(template, queue.Stadium!.Value);
        BitConverter.GetBytes(date).CopyTo(template, queue.Date);
        BitConverter.GetBytes(date).CopyTo(template, shape.StartDate);
        BitConverter.GetBytes(added).CopyTo(template, shape.AddedSeats);
        BitConverter.GetBytes(seats).CopyTo(template, shape.Capacity);
        BitConverter.GetBytes(seating).CopyTo(template, shape.SeatingCapacity);

        ulong block = allocate(shape.Size);
        if (!memory.TryWrite(block, template)) throw new MemoryAccessException("Could not write the expansion row");
        MemoryWriteBatch.Run(memory, staged =>
        {
            if (staged.ReadUInt64(header + 8) != end) throw new StaleObjectException("The club projects queue changed");
            if (!staged.TryWrite(end, BitConverter.GetBytes(block)) || !staged.TryWrite(header + 8, BitConverter.GetBytes(end + 8)))
                throw new MemoryAccessException("Could not add the expansion row");
        });
    }

    public void ValidateUndo(IProcessMemory source, ClubEntry club, ClubProject project, DateOnly appliedDate)
    {
        var written = project with
        {
            RawDate = project.DateAddress == 0 ? project.RawDate : WithDate(project.RawDate, appliedDate),
            ChangeRawDate = project.ChangeDateAddress == 0 ? 0 : WithDate(project.ChangeRawDate, appliedDate),
            ScheduledDate = appliedDate,
        };
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
        var changes = ReadStadiumChanges(source, club, today, layout, stadium);
        ReadQueue(layout.Expansions, ClubProjectKind.StadiumExpansion);
        ReadQueue(layout.Upgrades, ClubProjectKind.FacilityUpgrade);
        // A change left without its finance queue row (FM drops the row once it is due) still finishes the works.
        foreach (var change in changes.Where(c => c.Owned))
            result.Add(new(ClubProjectKind.StadiumExpansion, change.Row, 0, 0, change.Date, change.DateAddress, change.Raw));
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
                ulong venue = 0;
                if (queue.Stadium is { } at)
                {
                    venue = source.ReadUInt64(address + (ulong)at);
                    if (venue == 0 || source.ReadUInt64(venue) != moduleBase + stadium.VtableRva)
                        throw new StaleObjectException("The stadium expansion record is invalid");
                }
                ulong dateAddress = address + (ulong)queue.Date;
                uint raw = BitConverter.ToUInt32(source.Read(dateAddress, 4));
                if (DecodeDate(raw) is not { } date || date < today) continue;
                // The change that finishes this expansion is the one for the same stadium on the same day.
                int match = venue == 0 ? -1 : changes.FindIndex(c => c.Venue == venue && c.Date == date);
                if (match >= 0)
                {
                    var change = changes[match];
                    changes.RemoveAt(match);
                    result.Add(new(kind, address, dateAddress, raw, date, change.DateAddress, change.Raw));
                }
                else
                    result.Add(new(kind, address, dateAddress, raw, date));
            }
        }
    }

    /// <summary>Pending stadium changes; <c>Owned</c> marks stadiums the club owns.</summary>
    List<(ulong Venue, bool Owned, ulong Row, ulong DateAddress, uint Raw, DateOnly Date)> ReadStadiumChanges(
        IProcessMemory source, ClubEntry club, DateOnly today, ClubProjectOffsets layout, Stadiums.StadiumOffsets stadium)
    {
        var found = new List<(ulong, bool, ulong, ulong, uint, DateOnly)>();
        if (layout.StadiumChanges is not { } t) return found;
        ulong table = source.ReadUInt64(moduleBase + t.TableRoot);
        if (table == 0 || source.ReadUInt64(table) != moduleBase + t.TableVtable)
            throw new StaleObjectException("The stadium change table is unavailable");
        var pointers = ReadQueuePointers(source, table, t.Vector);
        for (int i = 0; i < pointers.Length; i += 8)
        {
            ulong row = BitConverter.ToUInt64(pointers, i);
            if (row == 0) continue;
            if (source.ReadUInt64(row) != moduleBase + t.Vtable) throw new StaleObjectException("The stadium change record is invalid");
            ulong venue = source.ReadUInt64(row + (ulong)t.Stadium);
            if (venue == 0 || source.ReadUInt64(venue) != moduleBase + stadium.VtableRva) continue;
            bool owned = source.ReadUInt64(venue + (ulong)stadium.Owner) == club.Address;
            ulong dateAddress = row + (ulong)t.Date;
            uint raw = BitConverter.ToUInt32(source.Read(dateAddress, 4));
            if (DecodeDate(raw) is { } date && date >= today) found.Add((venue, owned, row, dateAddress, raw, date));
        }
        return found;
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
