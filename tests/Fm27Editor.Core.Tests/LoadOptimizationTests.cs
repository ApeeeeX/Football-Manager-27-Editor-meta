using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Stadiums;

namespace Fm27Editor.Core.Tests;

public sealed class LoadOptimizationTests
{
    [Fact]
    public void TransferClauseReader_PreloadScanned_skips_region_enumeration()
    {
        var baseProfile = GameSession.BuiltInProfiles().First(p => p.Build == "27.1.0+2505117-mac");
        const ulong module = 0x1000_0000;
        var memory = new RegionTrapMemory(new FakeMemory());
        ulong clauseAddr = 0x2000_1000;
        ulong clubAddr = 0x2000_2000;
        var t = baseProfile.Club!.TransferClauses!;
        var clauseBytes = memory.Inner.Map(clauseAddr, 0x38);
        BitConverter.GetBytes(module + t.SellOnVtableRva).CopyTo(clauseBytes, 0);
        BitConverter.GetBytes(12345).CopyTo(clauseBytes, 8);
        BitConverter.GetBytes(clubAddr).CopyTo(clauseBytes, 0x10);
        BitConverter.GetBytes(500_000).CopyTo(clauseBytes, 0x18);
        BitConverter.GetBytes((short)25).CopyTo(clauseBytes, 0x22);

        var clubBytes = memory.Inner.Map(clubAddr, 0xD0);
        BitConverter.GetBytes(module + baseProfile.Club.ClubVtableRva).CopyTo(clubBytes, 0);
        memory.Inner.MapString(0x2000_3000, "Test FC");
        BitConverter.GetBytes(0x2000_3000UL).CopyTo(clubBytes, baseProfile.Club.Name);
        BitConverter.GetBytes(0x2000_3000UL).CopyTo(clubBytes, baseProfile.Club.ShortName);

        var reader = new TransferClauseReader(memory, baseProfile, module);
        Assert.NotEmpty(reader.ScanVtableAddresses);
        reader.PreloadScanned([clauseAddr]);
        reader.EnsureLoaded();

        var clauses = reader.ReadAll();
        Assert.Equal(0, memory.Enumerations);
        var single = Assert.Single(clauses);
        Assert.Equal(12345, single.PlayerId);
        Assert.Equal((short)25, single.Percentage);
    }

    [Fact]
    public void HeapVtableScanner_uses_native_scanner_when_available_and_falls_back_on_null()
    {
        var inner = new FakeMemory();
        var region = inner.Map(0x1_0000, 4096);
        const ulong vt = 0x9000_1234;
        BitConverter.GetBytes(vt).CopyTo(region, 64);

        var fast = new NativeScannerMemory(inner, [new(0x1_0040, vt)]);
        var fastResult = HeapVtableScanner.Scan(fast, vt);
        Assert.Equal(1, fast.NativeCalls);
        Assert.Equal(0, fast.ReadCalls);
        Assert.Equal([new HeapVtableMatch(0x1_0040, vt)], fastResult);

        var fallback = new NativeScannerMemory(inner, null);
        var fallbackResult = HeapVtableScanner.Scan(fallback, vt);
        Assert.Equal(1, fallback.NativeCalls);
        Assert.True(fallback.ReadCalls > 0);
        Assert.Equal([new HeapVtableMatch(0x1_0040, vt)], fallbackResult);
    }

    [Fact]
    public void ClubDirectory_FromPlayers_uses_cached_team_addresses_without_rereading_player_or_contract_teams()
    {
        var profile = GameSession.BuiltInProfiles().First(p => p.Build == "27.0.0+2493426 Game Pass");
        var memory = new FakeMemory();
        const ulong module = 0x7000_0000_0000;
        ulong player = 0x1000_0000, contract = 0x1000_1000, team = 0x1000_2000, club = 0x1000_3000, squad = 0x1000_4000;
        ulong player2 = 0x1000_8000, contract2 = 0x1000_9000;
        var pBytes = memory.Map(player, 0x3C8);
        BitConverter.GetBytes(module + profile.PlayerVtableRvas[0]).CopyTo(pBytes, 0);
        BitConverter.GetBytes(19334410).CopyTo(pBytes, profile.Fields.Id!.Value);
        BitConverter.GetBytes(team).CopyTo(pBytes, profile.Team!.PlayerTeam);
        BitConverter.GetBytes(contract).CopyTo(pBytes, profile.Contract!.PlayerContract);
        var cBytes = memory.Map(contract, 0x60);
        BitConverter.GetBytes(module + profile.Contract.ContractVtableRva).CopyTo(cBytes, 0);
        BitConverter.GetBytes(team).CopyTo(cBytes, profile.Contract.Team);

        var p2Bytes = memory.Map(player2, 0x3C8);
        BitConverter.GetBytes(module + profile.PlayerVtableRvas[0]).CopyTo(p2Bytes, 0);
        BitConverter.GetBytes(19334411).CopyTo(p2Bytes, profile.Fields.Id!.Value);
        BitConverter.GetBytes(team).CopyTo(p2Bytes, profile.Team.PlayerTeam);
        BitConverter.GetBytes(contract2).CopyTo(p2Bytes, profile.Contract.PlayerContract);
        var c2Bytes = memory.Map(contract2, 0x60);
        BitConverter.GetBytes(module + profile.Contract.ContractVtableRva).CopyTo(c2Bytes, 0);
        BitConverter.GetBytes(team).CopyTo(c2Bytes, profile.Contract.Team);

        var tBytes = memory.Map(team, 0x70);
        BitConverter.GetBytes(module + profile.Team.VtableRva).CopyTo(tBytes, 0);
        BitConverter.GetBytes(club).CopyTo(tBytes, profile.Contract.TeamClub);
        BitConverter.GetBytes(squad).CopyTo(tBytes, profile.Team.Squad);
        BitConverter.GetBytes(squad + 16).CopyTo(tBytes, profile.Team.Squad + 8);
        BitConverter.GetBytes(squad + 16).CopyTo(tBytes, profile.Team.Squad + 16);
        var squadBytes = memory.Map(squad, 16);
        BitConverter.GetBytes(player).CopyTo(squadBytes, 0);
        BitConverter.GetBytes(player2).CopyTo(squadBytes, 8);

        var cb = memory.Map(club, 0xD0);
        BitConverter.GetBytes(module + profile.Club!.ClubVtableRva).CopyTo(cb, 0);
        BitConverter.GetBytes(680).CopyTo(cb, profile.Club.Id!.Value);
        memory.MapString(0x1000_5000, "Manchester United");
        memory.MapString(0x1000_6000, "Man Utd");
        BitConverter.GetBytes(0x1000_5000UL).CopyTo(cb, profile.Club.Name);
        BitConverter.GetBytes(0x1000_6000UL).CopyTo(cb, profile.Club.ShortName);

        var repo = new PlayerRepository(memory, profile, module);
        var loaded = new[] { repo.Reload(player)!, repo.Reload(player2)! };
        var probe = new CountingMemory(memory);

        var clubs = ClubDirectory.FromPlayers(probe, loaded, profile, module);
        var united = Assert.Single(clubs);
        Assert.Equal("Man Utd", united.DisplayName);

        ulong playerTeamSlot = profile.PlayerField(loaded[0], profile.Team.PlayerTeam);
        ulong player2TeamSlot = profile.PlayerField(loaded[1], profile.Team.PlayerTeam);
        ulong contract2TeamSlot = loaded[1].Contract!.Address + (ulong)profile.Contract.Team;
        Assert.DoesNotContain(probe.ReadAddresses, addr => addr == playerTeamSlot);
        Assert.DoesNotContain(probe.ReadAddresses, addr => addr == player2TeamSlot);
        Assert.DoesNotContain(probe.ReadAddresses, addr => addr == contract2TeamSlot);
    }

    [Fact]
    public void LanguageReader_Catalogue_reads_shared_language_object_once()
    {
        var world = new TestWorld();
        var o = world.Profile.Languages!;
        const ulong vec1 = 0x6000_0000, rec1 = 0x6000_1000;
        const ulong p2Addr = 0x6001_0000, vec2 = 0x6001_2000, rec2 = 0x6001_3000;
        const ulong langObj = 0x6002_0000, langName = 0x6002_1000;

        var lang = world.Memory.Map(langObj, 0x30);
        TestWorld.Set64(lang, 0, TestWorld.ModuleBase + o.LanguageVtable);
        BitConverter.GetBytes(23).CopyTo(lang, o.LanguageId);
        TestWorld.Set64(lang, o.LanguageName, langName);
        world.Memory.MapString(langName, "Spanish");

        var r1 = world.Memory.Map(rec1, o.RecordSize);
        TestWorld.Set64(r1, o.Language, langObj);
        r1[o.Value] = 8;
        var v1 = world.Memory.Map(vec1, 8);
        TestWorld.Set64(v1, 0, rec1);
        world.Memory.TryWrite(TestWorld.Mainoo + (ulong)o.Vector,
            [.. BitConverter.GetBytes(vec1), .. BitConverter.GetBytes(vec1 + 8), .. BitConverter.GetBytes(vec1 + 8)]);

        var p2Bytes = world.Memory.Map(p2Addr, world.Profile.PlayerObjectSize);
        TestWorld.Set64(p2Bytes, 0, TestWorld.ModuleBase + world.Profile.PlayerVtableRvas[0]);
        BitConverter.GetBytes(123456).CopyTo(p2Bytes, world.Profile.Fields.Id!.Value);
        var r2 = world.Memory.Map(rec2, o.RecordSize);
        TestWorld.Set64(r2, o.Language, langObj);
        r2[o.Value] = 9;
        var v2 = world.Memory.Map(vec2, 8);
        TestWorld.Set64(v2, 0, rec2);
        BitConverter.GetBytes(vec2).CopyTo(p2Bytes, o.Vector);
        BitConverter.GetBytes(vec2 + 8).CopyTo(p2Bytes, o.Vector + 8);
        BitConverter.GetBytes(vec2 + 8).CopyTo(p2Bytes, o.Vector + 16);

        var repo = new PlayerRepository(world.Memory, world.Profile, TestWorld.ModuleBase);
        var players = new[] { repo.Reload(TestWorld.Mainoo)!, repo.Reload(p2Addr)! };
        var probe = new CountingMemory(world.Memory);

        var entries = new LanguageReader(probe, world.Profile, TestWorld.ModuleBase).Catalogue(players);
        var spanish = Assert.Single(entries);
        Assert.Equal("Spanish", spanish.Name);
        Assert.Equal(23, spanish.Id);
        Assert.Equal(1, probe.ReadAddresses.Count(a => a == langName));
        Assert.Equal(2, probe.ReadAddresses.Count(a => a == langObj));
    }

    [Fact]
    public void StadiumRepository_populates_capacity_matching_StadiumWriter()
    {
        const ulong holder = 0x7000_0000, vector = 0x7001_0000, nation = 0x7100_0000, stadiums = 0x7500_0000;
        var world = new TestWorld();
        var nOffsets = world.Profile.Nation!;
        var sOffsets = world.Profile.Stadium!;
        TestWorld.Set64(world.Memory.Map(TestWorld.ModuleBase + nOffsets.ListRootRva!.Value, 8), 0, holder);
        var head = world.Memory.Map(holder, 0x580);
        TestWorld.Set64(head, nOffsets.ListOffset!.Value, vector);
        TestWorld.Set64(head, nOffsets.ListOffset.Value + 8, vector + 8);
        TestWorld.Set64(world.Memory.Map(vector, 8), 0, nation);

        var nationBytes = world.Memory.Map(nation, 0x120);
        TestWorld.Set64(nationBytes, 0, TestWorld.ModuleBase + nOffsets.VtableRva);
        TestWorld.Set64(nationBytes, nOffsets.Stadium!.Value, stadiums);

        var capField = sOffsets.Fields[StadiumField.Capacity];
        int[] capacities = [74310, 53400];
        int stride = sOffsets.RecordStride > 0 ? sOffsets.RecordStride : 0xC0;
        for (int i = 0; i < 2; i++)
        {
            ulong addr = stadiums + (ulong)(i * stride);
            var sb = world.Memory.Map(addr, 0xC0);
            TestWorld.Set64(sb, 0, TestWorld.ModuleBase + sOffsets.VtableRva);
            BitConverter.GetBytes(100 + i).CopyTo(sb, sOffsets.Id);
            var encodedCap = ClubFieldAccess.Encode(capacities[i], capField);
            encodedCap.CopyTo(sb, capField.Path[^1]);
        }

        var loaded = new StadiumRepository(world.Memory, world.Profile, TestWorld.ModuleBase).LoadAll();
        var writer = new StadiumWriter(world.Memory, world.Profile, TestWorld.ModuleBase);
        Assert.Equal(2, loaded.Count);
        for (int i = 0; i < loaded.Count; i++)
        {
            Assert.NotNull(loaded[i].Capacity);
            Assert.Equal((int)writer.ReadFields(loaded[i].Address)[StadiumField.Capacity], loaded[i].Capacity!.Value);
        }
    }

    sealed class RegionTrapMemory(FakeMemory inner) : IProcessMemory, IMemoryRegions
    {
        public FakeMemory Inner { get; } = inner;
        public int Enumerations { get; private set; }
        public bool TryRead(ulong address, Span<byte> buffer) => Inner.TryRead(address, buffer);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => Inner.TryWrite(address, data);
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
        {
            Enumerations++;
            throw new InvalidOperationException("Region enumeration should not run when candidates are preloaded");
        }
    }

    sealed class NativeScannerMemory(FakeMemory inner, IReadOnlyList<HeapVtableMatch>? response) : IProcessMemory, IMemoryRegions, IVtableHeapScanner
    {
        public int NativeCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public bool TryRead(ulong address, Span<byte> buffer) { ReadCalls++; return inner.TryRead(address, buffer); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => inner.TryWrite(address, data);
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) =>
            [new MemoryRegion(0x1_0000, 4096, false)];
        public IReadOnlyList<HeapVtableMatch>? TryScanVtables(IReadOnlyList<MemoryRegion> regions, ReadOnlySpan<ulong> vtables, bool tolerateUnreadable, bool unreadablePagesAreNotHeap, CancellationToken ct)
        {
            NativeCalls++;
            return response;
        }
    }

    sealed class CountingMemory(IProcessMemory inner) : IProcessMemory
    {
        public List<ulong> ReadAddresses { get; } = [];
        public bool TryRead(ulong address, Span<byte> buffer) { ReadAddresses.Add(address); return inner.TryRead(address, buffer); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => inner.TryWrite(address, data);
    }
}
