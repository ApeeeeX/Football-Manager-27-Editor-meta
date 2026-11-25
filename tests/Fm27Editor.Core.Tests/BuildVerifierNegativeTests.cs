using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Stadiums;
using Fm27Editor.Core.Staff;
using Fm27Editor.Core.Verification;

namespace Fm27Editor.Core.Tests;

public class BuildVerifierNegativeTests
{
    sealed class AuditedFakeMemory : IProcessMemory, IWriteGate, IMemoryWriteObserver, IMemoryAllocationObserver, IGameHeapAllocator, IGameHeapVerifier, IMemoryRegions
    {
        readonly FakeMemory _inner = new();
        ulong _nextHeapAlloc = 0x2_0000_0000UL;
        public Action? BeforeWrite { get => _inner.BeforeWrite; set => _inner.BeforeWrite = value; }
        public Action<ulong, byte[], byte[]>? OnWrite { get; set; }
        public Action<ulong, int>? OnAllocate { get; set; }
        public Action<ulong, int, AllocationOrigin>? OnAllocateWithOrigin { get; set; }

        public AllocationOrigin NextAllocationOrigin { get; set; } = AllocationOrigin.GameHeap;
        public Func<ulong, int, HeapBlockCheck>? CustomHeapCheck { get; set; }
        public List<MemoryRegion> Regions { get; } = [];

        public byte[] Map(ulong start, int length, bool moduleData = false)
        {
            var data = _inner.Map(start, length);
            Regions.Add(new MemoryRegion(start, (ulong)length, moduleData));
            return data;
        }

        public void MapString(ulong address, string text)
        {
            _inner.MapString(address, text);
            Regions.Add(new MemoryRegion(address, (ulong)(4 + Encoding.UTF8.GetByteCount(text)), false));
        }

        public void MapNameEntry(ulong entry, ulong stringAddress, string text)
        {
            _inner.MapNameEntry(entry, stringAddress, text);
            Regions.Add(new MemoryRegion(entry, 8, false));
        }

        public bool TryRead(ulong address, Span<byte> buffer) => _inner.TryRead(address, buffer);

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            var before = new byte[data.Length];
            bool hadBefore = _inner.TryRead(address, before);
            bool ok = _inner.TryWrite(address, data);
            if (ok && hadBefore) OnWrite?.Invoke(address, before, data.ToArray());
            return ok;
        }

        public ulong AllocateFromGameHeap(int bytes)
        {
            ulong at = _nextHeapAlloc;
            _nextHeapAlloc += (ulong)((bytes + 0xFFF) & ~0xFFF);
            Map(at, Math.Max(bytes, 64));
            OnAllocateWithOrigin?.Invoke(at, bytes, NextAllocationOrigin);
            OnAllocate?.Invoke(at, bytes);
            return at;
        }

        public HeapBlockCheck VerifyGameHeapBlock(ulong address, int requestedBytes) =>
            CustomHeapCheck?.Invoke(address, requestedBytes)
            ?? new HeapBlockCheck(true, "fake-tag+malloc_size", 1, (ulong)requestedBytes, "fake valid heap block");

        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) => Regions;
    }

    [Fact]
    public void WriteAudit_catches_collateral_mutations_and_incomplete_restores()
    {
        var mem = new AuditedFakeMemory();
        var buf = mem.Map(0x1_0000_0000UL, 64);
        buf[0] = 10;
        buf[16] = 20;

        // Clean write & restore passes.
        var clean = WriteAudit.VerifyRoundTrip(mem, [new AuditWindow("obj", 0x1_0000_0000UL, 64)],
            () => mem.TryWrite(0x1_0000_0000UL, [99]),
            () => mem.TryWrite(0x1_0000_0000UL, [10]));
        Assert.True(clean.Passed);

        // Collateral byte modified directly in backing array without tracked write -> FAIL.
        var collateral = WriteAudit.VerifyRoundTrip(mem, [new AuditWindow("obj", 0x1_0000_0000UL, 64)],
            () => { mem.TryWrite(0x1_0000_0000UL, [99]); buf[16] = 77; },
            () => { mem.TryWrite(0x1_0000_0000UL, [10]); });
        Assert.False(collateral.Passed);

        // Un-restored byte -> FAIL.
        var unrestored = WriteAudit.VerifyRoundTrip(mem, [new AuditWindow("obj", 0x1_0000_0000UL, 64)],
            () => mem.TryWrite(0x1_0000_0000UL, [99]),
            () => mem.TryWrite(0x1_0000_0000UL, [55]));
        Assert.False(unrestored.Passed);
    }

    [Fact]
    public void HeapGate_rejects_raw_vm_allocations_and_unverified_blocks()
    {
        var mem = new AuditedFakeMemory { NextAllocationOrigin = AllocationOrigin.RawVm };
        using (var gate = new HeapGate(mem))
        {
            mem.AllocateFromGameHeap(64);
            var res = gate.Evaluate();
            Assert.False(res.Passed);
            Assert.Equal(1, res.RawVmCount);
        }

        mem.NextAllocationOrigin = AllocationOrigin.GameHeap;
        mem.CustomHeapCheck = (addr, _) => new HeapBlockCheck(false, "mach-tag+malloc_size", 0, 0, "user_tag=0");
        using (var gate = new HeapGate(mem))
        {
            mem.AllocateFromGameHeap(64);
            var res = gate.Evaluate();
            Assert.False(res.Passed);
            Assert.Equal(1, res.FailedVerificationCount);
        }

        mem.CustomHeapCheck = null;
        using (var gate = new HeapGate(mem))
        {
            mem.AllocateFromGameHeap(64);
            var res = gate.Evaluate();
            Assert.True(res.Passed);
        }
    }

    [Fact]
    public void BuildVerifier_passes_complete_world_and_rejects_every_deliberate_mutation()
    {
        var (mem, game, rawJson) = CreateSyntheticWorld();
        var profile = OffsetProfile.Load(rawJson);
        var gt = new GroundTruthCounts(ExpectedPlayers: 120, ExpectedStaff: 40, ExpectedClubs: 12, ExpectedHumans: 2, ToleranceFraction: 0.05);

        // Baseline passes read-only + writes + negative self-test.
        var baseline = BuildVerifier.Verify(mem, game, profile,
            new VerifyOptions(GroundTruth: gt, RunWrites: true, RunNegativeSelfTest: true, ProfileRawJson: rawJson));
        Assert.True(baseline.OverallPassed, baseline.ReceiptJson);

        // Crash gate + Continue/Load Game + release-gate passes when GAME_UI counter increments on same PID.
        string statePath = Path.Combine(Path.GetTempPath(), $"fm27-crash-test-{Guid.NewGuid():N}.json");
        try
        {
            var stage1 = BuildVerifier.Verify(mem, game, profile,
                new VerifyOptions(GroundTruth: gt, CrashGateWritePath: statePath, ProfileRawJson: rawJson));
            Assert.True(stage1.PendingCrashGate, stage1.ReceiptJson);
            Assert.True(stage1.OverallPassed, stage1.ReceiptJson);

            // Simulate user pressing Continue & Load Game (GAME_UI+0x100 increments from 1 -> 2).
            ulong uiPtr = mem.ReadUInt64(game.ModuleBase + profile.GameUiRva!.Value);
            mem.TryWrite(uiPtr + (ulong)profile.GameUiLoadsOffset!.Value, BitConverter.GetBytes(2UL));

            var release = BuildVerifier.Verify(mem, game, profile,
                new VerifyOptions(GroundTruth: gt, CrashGateVerifyPath: statePath, ReleaseGate: true, ProfileRawJson: rawJson));
            Assert.True(release.OverallPassed, release.ReceiptJson);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }

        // Release-gate WITHOUT ground truth MUST FAIL.
        var noGt = BuildVerifier.Verify(mem, game, profile,
            new VerifyOptions(ReleaseGate: true, ProfileRawJson: rawJson));
        Assert.False(noGt.OverallPassed);

        // 12 deliberate mutations against the synthetic world must EVERY ONE produce OverallPassed == false.
        var mutations = new (string Name, Action<JsonObject> Mutate, VerifyOptions? OverrideOpts)[]
        {
            ("1_stale_playerVtable", n =>
            {
                var arr = n["playerVtables"]!.AsArray();
                arr[0] = HexPlus(arr[0]!.GetValue<string>(), 8);
            }, null),
            ("2_wrong_nationAppointmentVtable", n =>
            {
                n["nationAppointmentVtable"] = HexPlus(n["nationAppointmentVtable"]!.GetValue<string>(), 8);
            }, null),
            ("3_missing_humanManagerKind", n =>
            {
                n["staff"]!["kinds"]!.AsArray().RemoveAt(2);
            }, null),
            ("4_missing_dualRolePlayerVtable", n =>
            {
                n["playerVtables"]!.AsArray().RemoveAt(1);
            }, null),
            ("5_shifted_CorporateFacilities", n =>
            {
                var cf = n["club"]!["fields"]!["CorporateFacilities"]!;
                cf["path"] = "0x158->0x8B8";
            }, null),
            ("6_corrupt_promisesGlobalRoot", n =>
            {
                var pr = n["player"]!["promises"]!;
                pr["globalRootRva"] = HexPlus(pr["globalRootRva"]!.GetValue<string>(), 0x20);
            }, null),
            ("7_bad_proposalChangedFlag", n =>
            {
                n["player"]!["proposals"]!["changedFlag"] = "0x40";
            }, null),
            ("8_bad_futureTransfersDateVector", n =>
            {
                n["player"]!["futureTransfers"]!["dateVector"] = "0x18";
            }, null),
            ("9_empty_awards_not_in_expectedEmpty", n =>
            {
                n["award"]!["listOffset"] = "0x240"; // empty vector slot on holder
            }, null),
            ("10_wrong_expectPlayers", _ => { }, new VerifyOptions(GroundTruth: gt with { ExpectedPlayers = 999 })),
            ("11_wrong_expectHumans", _ => { }, new VerifyOptions(GroundTruth: gt with { ExpectedHumans = 9 })),
            ("12_wrong_knownEntityYearFoundedOffset", n =>
            {
                n["club"]!["fields"]!["YearFounded"]!["path"] = "0x18"; // garbage int16 instead of 1878
            }, null),
        };

        var leaked = new List<string>();
        foreach (var (name, mutate, overrideOpts) in mutations)
        {
            var mutatedNode = OffsetProfile.ResolveJson(rawJson);
            mutate(mutatedNode);
            string mutatedJson = mutatedNode.ToJsonString();
            bool passed;
            try
            {
                var mp = OffsetProfile.Load(mutatedJson);
                var opts = (overrideOpts ?? new VerifyOptions(GroundTruth: gt)) with { ProfileRawJson = mutatedJson };
                var res = BuildVerifier.Verify(mem, game, mp, opts);
                passed = res.OverallPassed;
            }
            catch
            {
                passed = false;
            }
            if (passed) leaked.Add(name);
        }
        Assert.True(leaked.Count == 0, $"Mutations unexpectedly passed BuildVerifier: {string.Join(", ", leaked)}");
    }

    static string HexPlus(string s, int delta) => $"0x{Convert.ToUInt32(s[2..], 16) + (uint)delta:X}";

    static (AuditedFakeMemory Mem, GameProcess Game, string RawJson) CreateSyntheticWorld()
    {
        const ulong moduleBase = 0x100000000UL;
        const int moduleSize = 0x60000;
        var mem = new AuditedFakeMemory();
        var img = mem.Map(moduleBase, moduleSize, moduleData: true);

        // Valid Mach-O header (__TEXT [0x1000..0x2000], __DATA_CONST [0x2000..0x4000], __DATA [0x10000..0x60000]).
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0), 0xFEEDFACFu);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(20), 152 * 3);

        void WriteSeg(int off, string seg, ulong vm, ulong vmsize, uint prot, string sect, ulong saddr, ulong ssize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off), 0x19);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 4), 152);
            Encoding.ASCII.GetBytes(seg).CopyTo(img, off + 8);
            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(off + 24), vm);
            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(off + 32), vmsize);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 60), prot);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 64), 1);
            int so = off + 72;
            Encoding.ASCII.GetBytes(sect).CopyTo(img, so);
            Encoding.ASCII.GetBytes(seg).CopyTo(img, so + 16);
            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(so + 32), saddr);
            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(so + 40), ssize);
        }

        WriteSeg(32, "__TEXT", moduleBase, 0x2000, 5, "__cstring", moduleBase + 0x1000, 0x1000);
        WriteSeg(32 + 152, "__DATA_CONST", moduleBase + 0x2000, 0x2000, 1, "__const", moduleBase + 0x2000, 0x2000);
        WriteSeg(32 + 304, "__DATA", moduleBase + 0x10000, 0x50000, 3, "__data", moduleBase + 0x10000, 0x50000);

        // Build string in __TEXT.__cstring.
        const uint buildRva = 0x1040;
        Encoding.ASCII.GetBytes("27.9.0+9999999\0").CopyTo(img, (int)buildRva);

        // Load macos layout to enumerate every class & build unique RTTI vtable RVAs inside __DATA_CONST.
        var layoutObj = ProfileJsonResolver.Resolve(ProfileJsonResolver.LoadLayoutJson("layouts/macos.json")!);
        var layoutProfile = OffsetProfile.Load(layoutObj.ToJsonString());

        int strCursor = 0x1100;
        int tiCursor = 0x2000;
        int vtCursor = 0x2800;
        var tiByClass = new Dictionary<string, uint>();

        uint EmitVtable(string cls, int subOffset)
        {
            if (!tiByClass.TryGetValue(cls, out uint tiRva))
            {
                string mangled = RttiIndex.ToItanium(cls);
                Encoding.ASCII.GetBytes(mangled + "\0").CopyTo(img, strCursor);
                tiRva = (uint)tiCursor;
                BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(tiCursor), 0x7FFF11112222UL);
                BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(tiCursor + 8), moduleBase + (uint)strCursor);
                strCursor += mangled.Length + 1;
                tiCursor += 16;
                tiByClass[cls] = tiRva;
            }
            vtCursor += 16;
            uint vtRva = (uint)vtCursor;
            BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(vtCursor - 16), -subOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(vtCursor - 8), moduleBase + tiRva);
            vtCursor += 16;
            return vtRva;
        }

        // Reuse identical vtable when the same (class, offset) appears at multiple paths in macos.json!
        var vtByClassAndOff = new Dictionary<(string, int), uint>();
        uint GetOrEmit(string cls, int off) =>
            vtByClassAndOff.TryGetValue((cls, off), out uint v) ? v : vtByClassAndOff[(cls, off)] = EmitVtable(cls, off);

        foreach (var (path, spec) in layoutProfile.VtableClasses!)
        {
            uint rva = GetOrEmit(spec.Class, spec.Offset);
            ProfileJsonResolver.SetByPath(layoutObj, path, $"0x{rva:X}");
        }
        // Refresh symbolic @ references using resolved vtables.
        var compact = new JsonObject
        {
            ["extends"] = "layouts/macos.json",
            ["build"] = "27.9.0+9999999-test",
            ["matchBuild"] = "27.9.0+9999999",
            ["buildStringRva"] = $"0x{buildRva:X}",
            ["imageSize"] = $"0x{moduleSize:X}",
        };
        foreach (var (path, spec) in layoutProfile.VtableClasses!)
        {
            uint rva = vtByClassAndOff[(spec.Class, spec.Offset)];
            ProfileJsonResolver.SetByPath(compact, path, $"0x{rva:X}");
        }
        // Populate root slots cleanly starting in __DATA at 0x10000.
        uint dataSlot = 0x10000;
        uint AllocDataSlot(int bytes = 24) { uint r = dataSlot; dataSlot += (uint)bytes; return r; }

        uint playerListRva = AllocDataSlot(24);
        uint staffListRva = AllocDataSlot(24);
        uint clubListRva = AllocDataSlot(24);
        uint officialsListRva = AllocDataSlot(24);
        uint teamListRva = AllocDataSlot(24);
        uint dbRootRva = AllocDataSlot(8);
        uint citiesSlotRva = AllocDataSlot(8);
        uint gameUiRva = AllocDataSlot(8);
        uint retMgrRva = AllocDataSlot(8);
        uint youthMgrRva = AllocDataSlot(8);
        uint promisesMgrRva = AllocDataSlot(8);
        uint transferMgrRva = AllocDataSlot(8);
        uint crossCheckMgrRva = AllocDataSlot(8);

        compact["gameUiRva"] = $"0x{gameUiRva:X}";
        compact["retirementManagerRva"] = $"0x{retMgrRva:X}";
        ProfileJsonResolver.SetByPath(compact, "playerList.rootRva", $"0x{playerListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "staff.personListRootRva", $"0x{staffListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "club.listRootRva", $"0x{clubListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "globalArrays.clubsSlotRva", $"0x{clubListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "officials.listRootRva", $"0x{officialsListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "player.callUps.listRva", $"0x{teamListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "team.listRootRva", $"0x{teamListRva:X}");
        ProfileJsonResolver.SetByPath(compact, "player.cities.listRootRva", $"0x{citiesSlotRva:X}");
        ProfileJsonResolver.SetByPath(compact, "nation.listRootRva", $"0x{dbRootRva:X}");
        ProfileJsonResolver.SetByPath(compact, "nation.listOffset", "0x20");
        ProfileJsonResolver.SetByPath(compact, "competition.listRootRva", $"0x{dbRootRva:X}");
        ProfileJsonResolver.SetByPath(compact, "competition.listOffset", "0x40");
        ProfileJsonResolver.SetByPath(compact, "stadium.listRootRva", $"0x{dbRootRva:X}");
        ProfileJsonResolver.SetByPath(compact, "stadium.listOffset", "0x60");
        ProfileJsonResolver.SetByPath(compact, "award.listRootRva", $"0x{dbRootRva:X}");
        ProfileJsonResolver.SetByPath(compact, "award.listOffset", "0x80");
        ProfileJsonResolver.SetByPath(compact, "youthCandidates.managerRootRva", $"0x{youthMgrRva:X}");
        ProfileJsonResolver.SetByPath(compact, "player.promises.globalRootRva", $"0x{promisesMgrRva:X}");
        ProfileJsonResolver.SetByPath(compact, "player.proposals.managerRva", $"0x{transferMgrRva:X}");
        ProfileJsonResolver.SetByPath(compact, "player.proposals.listRootRva", $"0x{transferMgrRva:X}");
        ProfileJsonResolver.SetByPath(compact, "gameDate.primary.rootRva", $"0x{transferMgrRva:X}");
        ProfileJsonResolver.SetByPath(compact, "gameDate.crossCheck.rootRva", $"0x{crossCheckMgrRva:X}");

        // Rebuild concrete profile so we use exact layout offsets!
        // Remember DeepMerge replaces arrays wholesale, so populate concrete arrays cleanly.
        var staffKindsArr = (JsonArray)layoutObj["staff"]!["kinds"]!.DeepClone();
        staffKindsArr[0]!["staffVtable"] = $"0x{vtByClassAndOff[("db::ACTUAL_NON_PLAYER", 0)]:X}";
        staffKindsArr[0]!["personVtable"] = $"0x{vtByClassAndOff[("db::ACTUAL_NON_PLAYER", 520)]:X}";
        staffKindsArr[1]!["staffVtable"] = $"0x{vtByClassAndOff[("db::ACTUAL_PLAYER_AND_NON_PLAYER", 632)]:X}";
        staffKindsArr[1]!["personVtable"] = $"0x{vtByClassAndOff[("db::ACTUAL_PLAYER_AND_NON_PLAYER", 1152)]:X}";
        staffKindsArr[2]!["staffVtable"] = $"0x{vtByClassAndOff[("db::HUMAN_NON_PLAYER", 0)]:X}";
        staffKindsArr[2]!["personVtable"] = $"0x{vtByClassAndOff[("db::HUMAN_NON_PLAYER", 1352)]:X}";
        ProfileJsonResolver.SetByPath(compact, "staff.kinds", staffKindsArr);

        uint actualPlayerVt = vtByClassAndOff[("db::ACTUAL_PLAYER", 0)];
        uint dualVt = vtByClassAndOff[("db::ACTUAL_PLAYER_AND_NON_PLAYER", 0)];
        ProfileJsonResolver.SetByPath(compact, "playerVtables", new JsonArray($"0x{actualPlayerVt:X}", $"0x{dualVt:X}"));
        var subIns = (JsonArray)layoutObj["player"]!["subtypeInsertions"]!.DeepClone();
        subIns[0]!["vtable"] = $"0x{dualVt:X}";
        ProfileJsonResolver.SetByPath(compact, "player.subtypeInsertions", subIns);

        var cultureArr = new JsonArray(
            $"0x{vtByClassAndOff[("db::CLUB_VISION_OBJECTIVE", 0)]:X}",
            $"0x{vtByClassAndOff[("db::CLUB_COMPETITION_VISION_OBJECTIVE", 0)]:X}");
        ProfileJsonResolver.SetByPath(compact, "club.lists.cultureVtables", cultureArr);
        var visionHoldersArr = new JsonArray(
            $"0x{vtByClassAndOff[("MANAGER_DETAILS", 0)]:X}",
            $"0x{vtByClassAndOff[("CLUB_MANAGER_DETAILS", 0)]:X}",
            $"0x{vtByClassAndOff[("CLUB_HUMAN_MANAGER_DETAILS", 0)]:X}");
        ProfileJsonResolver.SetByPath(compact, "club.lists.visionHolderVtables", visionHoldersArr);
        ProfileJsonResolver.SetByPath(compact, "competition.prizeMoney.stageVtables",
            new JsonArray($"0x{vtByClassAndOff[("sicomps::LEAGUE_STAGE", 0)]:X}"));

        // The synthetic module has no inline vector allocator (the macOS layout gained one for 27.1.1), so allocating
        // writes here use the plain heap as they did before; the allocator is checked against the live game instead.
        compact["vectorAllocator"] = null;

        // Emit optional INTERACTION_MANAGER vtable into RTTI too so NewBuild/RTTI has everything clean.
        uint interactionVt = GetOrEmit("INTERACTION_MANAGER", 0);

        string compactJson = compact.ToJsonString();
        var profile = OffsetProfile.Load(compactJson);

        // Heap arena allocator for synthetic test world.
        ulong heap = 0x1_8000_0000UL;
        ulong AllocHeap(int sz) { ulong a = heap; heap += (ulong)((sz + 0xF) & ~0xF); mem.Map(a, Math.Max(sz, 16)); return a; }

        void WriteDate(ulong addr, int off, int year, int dayOfYear)
        {
            mem.TryWrite(addr + (ulong)off, BitConverter.GetBytes((ushort)dayOfYear));
            mem.TryWrite(addr + (ulong)off + 2, BitConverter.GetBytes((short)year));
        }

        // Singletons.
        ulong gameUi = AllocHeap(0x200);
        mem.TryWrite(gameUi, BitConverter.GetBytes(moduleBase + profile.GameUiVtable!.Value));
        mem.TryWrite(gameUi + (ulong)profile.GameUiLoadsOffset!.Value, BitConverter.GetBytes(1UL));
        BitConverter.GetBytes(gameUi).CopyTo(img, (int)gameUiRva);

        ulong retMgr = AllocHeap(0x40);
        mem.TryWrite(retMgr, BitConverter.GetBytes(moduleBase + profile.RetirementManagerVtable!.Value));
        BitConverter.GetBytes(retMgr).CopyTo(img, (int)retMgrRva);

        ulong youthMgr = AllocHeap(0x40);
        mem.TryWrite(youthMgr, BitConverter.GetBytes(moduleBase + vtByClassAndOff[("REGENERATION_MANAGER", 0)]));
        BitConverter.GetBytes(youthMgr).CopyTo(img, (int)youthMgrRva);

        ulong tMgr = AllocHeap(0x900);
        mem.TryWrite(tMgr, BitConverter.GetBytes(moduleBase + profile.Proposals!.ManagerVtable!.Value));
        WriteDate(tMgr, profile.GameDate!.Primary.DateOffset, 2027, 15);
        BitConverter.GetBytes(tMgr).CopyTo(img, (int)transferMgrRva);

        ulong ccMgr = AllocHeap(0x900);
        mem.TryWrite(ccMgr, BitConverter.GetBytes(moduleBase + profile.GameDate.CrossCheck!.ObjectVtableRva!.Value));
        WriteDate(ccMgr, profile.GameDate.CrossCheck.DateOffset, 2027, 15);
        BitConverter.GetBytes(ccMgr).CopyTo(img, (int)crossCheckMgrRva);

        // Helper for name entries & strings.
        ulong MakeStr(string s)
        {
            var utf8 = Encoding.UTF8.GetBytes(s);
            ulong a = AllocHeap(16 + utf8.Length);
            mem.TryWrite(a, BitConverter.GetBytes(utf8.Length));
            mem.TryWrite(a + 4, utf8);
            return a;
        }
        ulong MakeNameEntry(string s) { ulong a = AllocHeap(8); ulong st = MakeStr(s); mem.TryWrite(a, BitConverter.GetBytes(st)); return a; }

        // Nation: England (id 765, FA=1863, FIFA=1905)
        var nationAddrs = new ulong[3];
        for (int i = 0; i < 3; i++)
        {
            var nat = AllocHeap(0xA00);
            mem.TryWrite(nat, BitConverter.GetBytes(moduleBase + profile.Nation!.VtableRva));
            mem.TryWrite(nat + (ulong)profile.Nation.Id!.Value, BitConverter.GetBytes(i == 0 ? 765 : 1000 + i));
            mem.TryWrite(nat + (ulong)profile.Nation.Name, BitConverter.GetBytes(MakeStr(i == 0 ? "England" : $"Nation{i}")));
            mem.TryWrite(nat + (ulong)profile.Nation.ShortName, BitConverter.GetBytes(MakeStr(i == 0 ? "ENG" : $"N{i}")));
            if (profile.Nation.Fields is { } nf)
            {
                if (nf.TryGetValue(Nations.NationField.YearAssociationFormed, out var yaf))
                    mem.TryWrite(nat + (ulong)yaf.Path[0], BitConverter.GetBytes((short)1863));
                if (nf.TryGetValue(Nations.NationField.YearAffiliatedToFifa, out var yfifa))
                    mem.TryWrite(nat + (ulong)yfifa.Path[0], BitConverter.GetBytes((short)1905));
                if (nf.TryGetValue(Nations.NationField.EconomicFactor, out var ef))
                    mem.TryWrite(nat + (ulong)ef.Path[0], [(byte)18]);
            }
            nationAddrs[i] = nat;
        }

        // Stadiums: Old Trafford (680) + Emirates (602) so StadiumRepository sees >1 pointer entries.
        ulong MakeStadium(int id, string sName, int cap, int builtYear)
        {
            ulong s = AllocHeap(0x100);
            var so2 = profile.Stadium!;
            mem.TryWrite(s, BitConverter.GetBytes(moduleBase + so2.VtableRva));
            mem.TryWrite(s + (ulong)so2.Id, BitConverter.GetBytes(id));
            mem.TryWrite(s + (ulong)so2.Name, BitConverter.GetBytes(MakeStr(sName)));
            mem.TryWrite(s + (ulong)so2.Fields[StadiumField.Capacity].Path[0], BitConverter.GetBytes(cap));
            WriteDate(s, so2.Fields[StadiumField.BuildDate].Path[0], builtYear, 50);
            return s;
        }
        ulong stAddr = MakeStadium(680, "Old Trafford", 74000, 1909);
        ulong stAddr2 = MakeStadium(602, "Emirates Stadium", 60704, 2006);

        // Competitions & Awards & Cities & Officials.
        ulong compAddr = AllocHeap(0x200);
        mem.TryWrite(compAddr, BitConverter.GetBytes(moduleBase + profile.Competition!.VtableRva));
        mem.TryWrite(compAddr + (ulong)profile.Competition.Id!.Value, BitConverter.GetBytes(11));
        mem.TryWrite(compAddr + (ulong)profile.Competition.Name, BitConverter.GetBytes(MakeStr("Premier League")));

        ulong awAddr = AllocHeap(0x100);
        mem.TryWrite(awAddr, BitConverter.GetBytes(moduleBase + profile.Award!.VtableRva));
        mem.TryWrite(awAddr + (ulong)profile.Award.Id, BitConverter.GetBytes(1));
        mem.TryWrite(awAddr + (ulong)profile.Award.Name, BitConverter.GetBytes(MakeStr("Golden Boot")));

        ulong cityAddr = AllocHeap(0x80);
        mem.TryWrite(cityAddr, BitConverter.GetBytes(moduleBase + profile.Cities!.Vtable));
        mem.TryWrite(cityAddr + (ulong)profile.Cities.Id, BitConverter.GetBytes(1));
        mem.TryWrite(cityAddr + (ulong)profile.Cities.Name, BitConverter.GetBytes(MakeStr("Manchester")));

        ulong refAddr = AllocHeap(0x200);
        mem.TryWrite(refAddr, BitConverter.GetBytes(moduleBase + profile.Officials!.VtableRva));
        mem.TryWrite(refAddr + 0x20, BitConverter.GetBytes(MakeNameEntry("Michael")));
        mem.TryWrite(refAddr + 0x28, BitConverter.GetBytes(MakeNameEntry("Oliver")));

        // DATABASE holder object at dbRootRva.
        ulong dbObj = AllocHeap(0x200);
        BitConverter.GetBytes(dbObj).CopyTo(img, (int)dbRootRva);
        void PutVecAt(ulong addr, params ulong[] items)
        {
            ulong buf = AllocHeap(Math.Max(8, items.Length * 8));
            for (int i = 0; i < items.Length; i++) mem.TryWrite(buf + (ulong)i * 8, BitConverter.GetBytes(items[i]));
            mem.TryWrite(addr, BitConverter.GetBytes(buf));
            mem.TryWrite(addr + 8, BitConverter.GetBytes(buf + (ulong)items.Length * 8));
            mem.TryWrite(addr + 16, BitConverter.GetBytes(buf + (ulong)items.Length * 8));
        }
        PutVecAt(dbObj + 0x20, nationAddrs);
        PutVecAt(dbObj + 0x40, compAddr);
        PutVecAt(dbObj + 0x60, stAddr, stAddr2);
        PutVecAt(dbObj + 0x80, awAddr);
        ulong citiesVec = AllocHeap(24);
        PutVecAt(citiesVec, cityAddr);
        BitConverter.GetBytes(citiesVec).CopyTo(img, (int)citiesSlotRva);
        PutVecAt(moduleBase + officialsListRva, refAddr);

        // 12 Clubs + Teams (Man Utd 680/1878, Arsenal 602/1886, Real Madrid 1736/1902, Barcelona 1708/1899).
        (int Id, string Name, int Founded, byte Corp)[] clubSpecs =
        [
            (680, "Manchester United", 1878, 20), (602, "Arsenal", 1886, 18),
            (1736, "Real Madrid", 1902, 20), (1708, "Barcelona", 1899, 17),
            (603, "Club 5", 1905, 12), (604, "Club 6", 1910, 14),
            (605, "Club 7", 1912, 11), (606, "Club 8", 1920, 15),
            (607, "Club 9", 1930, 16), (608, "Club 10", 1940, 13),
            (609, "Club 11", 1950, 9), (610, "Club 12", 1960, 10),
        ];
        var clubAddrs = new ulong[clubSpecs.Length];
        var teamAddrs = new ulong[clubSpecs.Length];
        var cfField = profile.Club!.Fields![Clubs.ClubField.CorporateFacilities];
        var yfField = profile.Club.Fields![Clubs.ClubField.YearFounded];
        var balField = profile.Club.Fields![Clubs.ClubField.Balance];
        var budField = profile.Club.Fields![Clubs.ClubField.TransferBudgetSeason];
        var morField = profile.Club.Fields![Clubs.ClubField.Morale];

        for (int i = 0; i < clubSpecs.Length; i++)
        {
            ulong c = AllocHeap(0x400);
            ulong fin = AllocHeap(0xA00);
            ulong detail = AllocHeap(0x400);
            mem.TryWrite(c, BitConverter.GetBytes(moduleBase + profile.Club.ClubVtableRva));
            mem.TryWrite(c + (ulong)profile.Club.Id!.Value, BitConverter.GetBytes(clubSpecs[i].Id));
            mem.TryWrite(c + (ulong)profile.Club.Name, BitConverter.GetBytes(MakeStr(clubSpecs[i].Name)));
            mem.TryWrite(c + (ulong)profile.Club.ShortName, BitConverter.GetBytes(MakeStr(clubSpecs[i].Name)));

            void WriteClubFieldValue(Clubs.ClubFieldOffset f, byte[] val)
            {
                if (f.Path.Count == 1) mem.TryWrite(c + (ulong)f.Path[0], val);
                else if (f.Path.Count == 2)
                {
                    ulong sub = f.Path[0] == cfField.Path[0] ? fin : detail;
                    mem.TryWrite(c + (ulong)f.Path[0], BitConverter.GetBytes(sub));
                    mem.TryWrite(sub + (ulong)f.Path[1], val);
                }
            }
            WriteClubFieldValue(yfField, BitConverter.GetBytes((short)clubSpecs[i].Founded));
            WriteClubFieldValue(morField, [(byte)14]);
            mem.TryWrite(fin, BitConverter.GetBytes(moduleBase + profile.Club.Finance!.VtableRva!.Value));
            mem.TryWrite(c + (ulong)cfField.Path[0], BitConverter.GetBytes(fin));
            mem.TryWrite(fin + (ulong)cfField.Path[1], [clubSpecs[i].Corp]);
            mem.TryWrite(fin + (ulong)balField.Path[1], BitConverter.GetBytes(50_000_000));
            mem.TryWrite(fin + (ulong)budField.Path[1], BitConverter.GetBytes(20_000_000));

            ulong tm = AllocHeap(0x200);
            mem.TryWrite(tm, BitConverter.GetBytes(moduleBase + profile.Team!.VtableRva));
            mem.TryWrite(tm + (ulong)profile.Contract!.TeamClub, BitConverter.GetBytes(c));
            clubAddrs[i] = c;
            teamAddrs[i] = tm;
        }
        PutVecAt(moduleBase + clubListRva, clubAddrs);
        PutVecAt(moduleBase + teamListRva, teamAddrs);

        // Language catalogue objects.
        ulong[] langObjs =
        [
            MakeLangObj(1, "English"), MakeLangObj(2, "Spanish"), MakeLangObj(3, "French"), MakeLangObj(4, "German")
        ];
        ulong MakeLangObj(int id, string nm)
        {
            ulong l = AllocHeap(0x40);
            mem.TryWrite(l, BitConverter.GetBytes(moduleBase + profile.Languages!.LanguageVtable));
            mem.TryWrite(l + (ulong)profile.Languages.LanguageId, BitConverter.GetBytes(id));
            mem.TryWrite(l + (ulong)profile.Languages.LanguageName, BitConverter.GetBytes(MakeStr(nm)));
            return l;
        }

        // 120 Players (100 ACTUAL_PLAYER + 20 ACTUAL_PLAYER_AND_NON_PLAYER with 0xF8 insertion; Mainoo 2000123586 first).
        ulong promiseRec = AllocHeap(profile.Promises!.RecordSize);
        var playerAddrs = new ulong[120];
        var squadSlots = new List<ulong>[clubSpecs.Length];
        for (int s = 0; s < clubSpecs.Length; s++) squadSlots[s] = [];

        for (int i = 0; i < 120; i++)
        {
            bool dual = i >= 100;
            uint vt = dual ? dualVt : actualPlayerVt;
            ulong p = AllocHeap(0x600);
            playerAddrs[i] = p;
            mem.TryWrite(p, BitConverter.GetBytes(moduleBase + vt));
            int Off(int canonical) => profile.PlayerOffset(vt, canonical);

            bool isGk = i % 10 == 1;
            bool isSt = !isGk && i % 10 == 2;
            mem.TryWrite(p + (ulong)profile.Positions[PlayerPosition.Goalkeeper], [(byte)(isGk ? 20 : 1)]);
            mem.TryWrite(p + (ulong)profile.Positions[PlayerPosition.Striker], [(byte)(isSt ? 20 : 1)]);
            mem.TryWrite(p + (ulong)profile.Positions[PlayerPosition.MidfielderCentral], [(byte)(!isGk && !isSt ? 20 : 1)]);
            foreach (var (attr, off) in profile.Attributes)
            {
                byte val = attr == PlayerAttribute.Handling
                    ? (byte)(isGk ? 85 : isSt ? 10 : 15)
                    : (byte)(50 + (i % 4) * 5);
                mem.TryWrite(p + (ulong)off, [val]);
            }
            mem.TryWrite(p + (ulong)profile.Fields.Height, BitConverter.GetBytes((short)180));
            mem.TryWrite(p + (ulong)profile.Fields.CurrentAbility, BitConverter.GetBytes((short)140));
            mem.TryWrite(p + (ulong)profile.Fields.PotentialAbility, BitConverter.GetBytes((short)160));

            int pid = i == 0 ? 2000123586 : 2000000 + i;
            mem.TryWrite(p + (ulong)Off(profile.Fields.Id!.Value), BitConverter.GetBytes(pid));
            if (profile.Fields.PersonIndex is { } pixOff)
                mem.TryWrite(p + (ulong)Off(pixOff), BitConverter.GetBytes(500 + i));
            if (i == 0) WriteDate(p, Off(profile.Fields.DateOfBirth), 2005, 109); // 2005-04-19
            else WriteDate(p, Off(profile.Fields.DateOfBirth), 1998, 100);

            mem.TryWrite(p + (ulong)Off(profile.Fields.FirstName), BitConverter.GetBytes(MakeNameEntry(i == 0 ? "Kobbie" : $"First{i}")));
            mem.TryWrite(p + (ulong)Off(profile.Fields.LastName), BitConverter.GetBytes(MakeNameEntry(i == 0 ? "Mainoo" : $"Last{i}")));
            mem.TryWrite(p + (ulong)Off(profile.Fields.FullName), BitConverter.GetBytes(MakeStr(i == 0 ? "Kobbie Mainoo" : $"Player {i}")));
            mem.TryWrite(p + (ulong)Off(profile.Nation!.PlayerNation), BitConverter.GetBytes(nationAddrs[0]));

            // Contract at team i % 12
            int clubIdx = i % clubSpecs.Length;
            ulong ctr = AllocHeap(0x100);
            mem.TryWrite(ctr, BitConverter.GetBytes(moduleBase + profile.Contract!.ContractVtableRva));
            mem.TryWrite(ctr + (ulong)profile.Contract.Team, BitConverter.GetBytes(teamAddrs[clubIdx]));
            mem.TryWrite(ctr + (ulong)profile.Contract.Wage, BitConverter.GetBytes(50000));
            WriteDate(ctr, profile.Contract.ExpiryDate, 2029, 180);
            WriteDate(ctr, profile.Contract.JoinedDate, 2024, 180);
            mem.TryWrite(p + (ulong)Off(profile.Contract.PlayerContract), BitConverter.GetBytes(ctr));
            mem.TryWrite(p + (ulong)Off(profile.Team!.PlayerTeam), BitConverter.GetBytes(teamAddrs[clubIdx]));
            squadSlots[clubIdx].Add(p);

            if (i < langObjs.Length)
            {
                ulong lRec = AllocHeap(profile.Languages!.RecordSize);
                mem.TryWrite(lRec + (ulong)profile.Languages.Language, BitConverter.GetBytes(langObjs[i]));
                mem.TryWrite(lRec + (ulong)profile.Languages.Value, [(byte)10]);
                PutVecAt(p + (ulong)Off(profile.Languages.Vector), lRec);
            }
            if (i == 0)
            {
                ulong pHolder = AllocHeap(24);
                PutVecAt(pHolder, promiseRec);
                mem.TryWrite(p + (ulong)Off(profile.Promises.Holder), BitConverter.GetBytes(pHolder));

                ulong idBuf = AllocHeap(8);
                mem.TryWrite(idBuf, BitConverter.GetBytes(0)); // offer id 0
                ulong idHolder = AllocHeap(24);
                mem.TryWrite(idHolder, BitConverter.GetBytes(idBuf));
                mem.TryWrite(idHolder + 8, BitConverter.GetBytes(idBuf + 4));
                mem.TryWrite(idHolder + 16, BitConverter.GetBytes(idBuf + 4));
                int pIdsOff = Off(profile.Proposals!.PlayerIds);
                mem.TryWrite(p + (ulong)pIdsOff, BitConverter.GetBytes(idHolder));
            }
        }
        PutVecAt(moduleBase + playerListRva, playerAddrs);
        for (int s = 0; s < clubSpecs.Length; s++)
        {
            PutVecAt(teamAddrs[s] + (ulong)profile.Team!.Squad, squadSlots[s].ToArray());
            mem.TryWrite(clubAddrs[s] + (ulong)profile.Contract!.TeamClub, BitConverter.GetBytes(clubAddrs[s])); // unused
        }

        // Promises manager (INTERACTION_MANAGER) sharing promiseRec.
        ulong pMgr = AllocHeap(0x200);
        mem.TryWrite(pMgr, BitConverter.GetBytes(moduleBase + interactionVt));
        PutVecAt(pMgr + (ulong)profile.Promises.GlobalVector, promiseRec);
        BitConverter.GetBytes(pMgr).CopyTo(img, (int)promisesMgrRva);

        // Proposals + FutureTransfers DateVector.
        ulong propObj = AllocHeap(0xA0);
        mem.TryWrite(propObj, BitConverter.GetBytes(moduleBase + profile.Proposals!.TransferVtable));
        mem.TryWrite(propObj + (ulong)profile.Proposals.Person, BitConverter.GetBytes(playerAddrs[0] + (ulong)profile.Relations!.PlayerPerson));
        mem.TryWrite(propObj + (ulong)profile.Proposals.Buyer, BitConverter.GetBytes(teamAddrs[1]));
        mem.TryWrite(propObj + (ulong)profile.Proposals.Seller, BitConverter.GetBytes(teamAddrs[0]));
        mem.TryWrite(propObj + (ulong)profile.Proposals.Id, BitConverter.GetBytes(0));
        mem.TryWrite(tMgr + 0x40, [(byte)0x42]); // non-bool poison byte for shifted changedFlag mutation test
        mem.TryWrite(tMgr + (ulong)profile.Proposals.ChangedFlag!.Value, [(byte)0]);
        PutVecAt(tMgr + (ulong)profile.Proposals.ListOffset, propObj);
        if (profile.Proposals.OfferVector != profile.Proposals.ListOffset)
            PutVecAt(tMgr + (ulong)profile.Proposals.OfferVector, propObj);

        ulong ftRec = AllocHeap(0x40);
        WriteDate(ftRec, profile.FutureTransfers!.Date, 2027, 200);
        PutVecAt(tMgr + (ulong)profile.FutureTransfers.DateVector, ftRec);

        // 40 Staff (30 normal, 8 dual, 2 human managers).
        var so = profile.Staff!;
        var staffEntries = new ulong[40];
        for (int i = 0; i < 40; i++)
        {
            var kind = i < 30 ? so.Kinds[0] : i < 38 ? so.Kinds[1] : so.Kinds[2];
            ulong entry = AllocHeap(0x800);
            ulong person = entry + (ulong)kind.DirectPersonDelta;
            staffEntries[i] = entry;
            mem.TryWrite(entry, BitConverter.GetBytes(moduleBase + kind.StaffVtableRva));
            mem.TryWrite(person + (ulong)kind.StaffPart, BitConverter.GetBytes(moduleBase + kind.PersonVtableRva));

            mem.TryWrite(person + (ulong)so.Id, BitConverter.GetBytes(3000 + i));
            mem.TryWrite(person + (ulong)so.FirstName, BitConverter.GetBytes(MakeNameEntry($"SFirst{i}")));
            mem.TryWrite(person + (ulong)so.LastName, BitConverter.GetBytes(MakeNameEntry($"SLast{i}")));
            mem.TryWrite(person + (ulong)so.FullName, BitConverter.GetBytes(MakeStr($"Staff {i}")));
            WriteDate(person, so.DateOfBirth, 1980, 100);

            bool gkCoach = i is >= 5 and < 10;
            short jc = kind.Manager ? (short)16 : (gkCoach ? (short)34 : (short)2);
            foreach (var (sa, (off, scaled)) in so.Attributes)
            {
                byte v = sa == StaffAttribute.CoachingGoalkeeping
                    ? (byte)(gkCoach ? 90 : 15)
                    : (byte)50;
                mem.TryWrite(entry + (ulong)off, [v]);
            }
            ulong job = AllocHeap(0x40);
            mem.TryWrite(job + (ulong)so.JobPerson, BitConverter.GetBytes(person + (ulong)so.JobPersonAnchor));
            mem.TryWrite(job + (ulong)so.JobTeam, BitConverter.GetBytes(teamAddrs[0]));
            mem.TryWrite(job + (ulong)so.JobCode, BitConverter.GetBytes(jc));
            mem.TryWrite(person + (ulong)so.Job, BitConverter.GetBytes(job));
        }
        PutVecAt(moduleBase + staffListRva, staffEntries);

        // Work permit global table vector (110 sorted 0x20-byte entries inside TRANSFER_MANAGER so Add/RemoveWorkPermit works).
        var wpRecs = new ulong[110];
        for (int i = 0; i < wpRecs.Length; i++)
        {
            ulong rec = AllocHeap(0x28);
            mem.TryWrite(rec, BitConverter.GetBytes(nationAddrs[1]));
            mem.TryWrite(rec + 8, BitConverter.GetBytes(1000 + i));
            mem.TryWrite(rec + 12, BitConverter.GetBytes(2030));
            wpRecs[i] = rec;
        }
        PutVecAt(tMgr + 0x100, wpRecs);

        var game = new GameProcess(424242, moduleBase, moduleSize, null, "/Applications/Football Manager 27.app");
        return (mem, game, compactJson);
    }
}
