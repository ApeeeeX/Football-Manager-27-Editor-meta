using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.CodeGen;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Offsets;

public enum DiscoveryStatus { Pass, Warn, Manual }

public sealed record DiscoveryItem(string Key, DiscoveryStatus Status, string Value, string Detail, string? ManualHint = null);

public sealed record DiscoveryResult(
    string LayoutName,
    string Build,
    string CompactJson,
    string FullJson,
    OffsetProfile ResolvedProfile,
    IReadOnlyList<DiscoveryItem> Items)
{
    public bool HasManualItems => Items.Any(i => i.Status == DiscoveryStatus.Manual);
}

/// <summary>
/// Discovers build-specific vtables, static roots, and drift-prone structural offsets for an attached FM27 build
/// using a base layout (<c>layouts/macos.json</c>, <c>layouts/touch-macos.json</c>, or <c>layouts/windows.json</c>).
/// Emits both a compact <c>"extends"</c> profile and a full flattened profile (AOT-safe).
/// </summary>
public static class NewBuildDiscoverer
{
    static readonly JsonWriterOptions WriteOptions = new() { Indented = true };

    public static string DetectDefaultLayout(GameProcess game)
    {
        if (!OperatingSystem.IsMacOS()) return "windows";
        return game.ExePath?.Contains("Touch", StringComparison.OrdinalIgnoreCase) == true
            ? "touch-macos"
            : "macos";
    }

    public static DiscoveryResult Discover(
        IProcessMemory memory,
        GameProcess game,
        string? layoutName = null,
        Func<string, string?>? resolveLayout = null,
        Action<string>? log = null)
    {
        string layout = NormalizeLayoutShortName(string.IsNullOrWhiteSpace(layoutName) ? DetectDefaultLayout(game) : layoutName);
        string layoutRef = $"layouts/{layout}.json";
        string baseJson = ProfileJsonResolver.LoadLayoutJson(layoutRef, resolveLayout)
            ?? throw new InvalidOperationException($"Base layout '{layoutRef}' could not be loaded");

        var rawBase = (JsonNode.Parse(baseJson, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject)
            ?? throw new FormatException($"Layout '{layoutRef}' must be a JSON object");
        var working = ProfileJsonResolver.Resolve(baseJson, resolveLayout);
        var items = new List<DiscoveryItem>();
        var discoveredVtablePaths = new List<string>();
        var discoveredRootPaths = new List<string>();
        var structuralOverrides = new HashSet<string>(StringComparer.Ordinal);
        var touchedArrayRoots = new HashSet<string>(StringComparer.Ordinal);

        log?.Invoke($"reading module discovery slices (0x{game.ModuleSize:X} bytes)...");
        var image = ModuleImage.ReadForDiscovery(memory, game.ModuleBase, game.ModuleSize);
        log?.Invoke("indexing RTTI...");
        var rtti = RttiIndex.Build(image);

        uint buildRva = RootFinder.FindBuildString(image)
            ?? throw new FormatException("Could not locate FM 27 build string in module strings");
        string rawBuild = RootFinder.ReadAsciiZ(image, buildRva)
            ?? throw new FormatException($"Build string at 0x{buildRva:X} was unreadable");

        bool isGamePass = game.ExePath?.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) == true;
        string buildName = layout switch
        {
            "macos" => $"{rawBuild}-mac",
            "touch-macos" => $"{rawBuild}-touch-mac",
            _ => isGamePass ? $"{rawBuild} Game Pass" : rawBuild,
        };
        string moduleName = working["module"]?.GetValue<string>() ?? (OperatingSystem.IsMacOS() ? "game_plugin" : "game_plugin.dll");

        working["build"] = buildName;
        working["buildStringRva"] = Hex(buildRva);
        working["imageSize"] = Hex((uint)game.ModuleSize);
        if (layout is "macos" or "touch-macos")
        {
            working["matchBuild"] = rawBuild;
        }
        else if (isGamePass)
        {
            working["matchBuild"] = rawBuild;
            working["variant"] = "gamepass";
        }
        if (image.PeTimestamp is { } peTs)
            working["peTimestamp"] = Hex(peTs);

        // A discovered profile never enables writes by itself: verify-build --release-gate lifts this after the live gate.
        working["readOnly"] = true;

        items.Add(new("build", DiscoveryStatus.Pass, buildName, $"buildStringRva={Hex(buildRva)}, imageSize={Hex((uint)game.ModuleSize)}"
            + (image.PeTimestamp is { } ts ? $", peTimestamp={Hex(ts)}" : "")));

        var resolvedProfileForMeta = OffsetProfile.Load(working.ToJsonString(), resolveLayout);
        var vtableSpecs = resolvedProfileForMeta.VtableClasses ?? new Dictionary<string, VtableClassSpec>();
        var rootSpecs = resolvedProfileForMeta.RootSpecs ?? new Dictionary<string, RootDiscoverySpec>();

        var secondaryVtablePaths = vtableSpecs.Where(kv => kv.Value.Offset > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        // Phase 1: Primary (offset == 0) vtables via RTTI + live static disambiguation.
        log?.Invoke($"resolving {vtableSpecs.Count} RTTI vtable paths...");
        foreach (var (path, spec) in vtableSpecs.Where(kv => kv.Value.Offset == 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var resolved = ResolveVtableSpec(rtti, image, memory, spec, parentSamples: null, out string detail, out DiscoveryStatus status, out string? hint);
            if (resolved is { } vtRva)
            {
                SetWorkingPath(working, path, Hex(vtRva), discoveredVtablePaths, touchedArrayRoots);
                items.Add(new(path, status, Hex(vtRva), detail, hint));
            }
            else if (!spec.Optional)
            {
                items.Add(new(path, DiscoveryStatus.Manual, "MISSING", detail, hint ?? $"dotnet run --project src/Fm27Editor.Tools -c Release -- slotsbyname \"{spec.Class}\""));
            }
            else
            {
                items.Add(new(path, DiscoveryStatus.Warn, "ABSENT (optional)", detail, hint));
            }
        }

        // Phase 2: Primary roots needed to sample parent objects for secondary subobject vtables + completeness checks.
        // We resolve direct vectors / objects / indirect vectors first, then promises & aliases.
        ResolveRootPass(working, rootSpecs, image, memory, items, discoveredRootPaths, touchedArrayRoots, log, includeDependent: false, secondaryVtablePaths);

        // Phase 3: Secondary (offset > 0) subobject vtables (for example db::ACTUAL_PLAYER+904 or db::ACTUAL_NON_PLAYER+520).
        foreach (var (path, spec) in vtableSpecs.Where(kv => kv.Value.Offset > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var samples = CollectSamplesForClass(working, memory, game.ModuleBase, spec.Class);
            var resolved = ResolveVtableSpec(rtti, image, memory, spec, samples, out string detail, out DiscoveryStatus status, out string? hint);
            if (resolved is { } vtRva)
            {
                SetWorkingPath(working, path, Hex(vtRva), discoveredVtablePaths, touchedArrayRoots);
                items.Add(new(path, status, Hex(vtRva), detail, hint));
            }
            else if (!spec.Optional)
            {
                items.Add(new(path, DiscoveryStatus.Manual, "MISSING", detail, hint ?? $"dotnet run --project src/Fm27Editor.Tools -c Release -- slotsbyname \"{spec.Class}\""));
            }
            else
            {
                items.Add(new(path, DiscoveryStatus.Warn, "ABSENT (optional)", detail, hint));
            }
        }

        // Phase 4: Dependent roots (promises requiring INTERACTION_MANAGER overlap check, and aliases).
        ResolveRootPass(working, rootSpecs, image, memory, items, discoveredRootPaths, touchedArrayRoots, log, includeDependent: true, secondaryVtablePaths);

        // Refresh @ references cleanly on working after vtables changed.
        working = ReapplyBaseRefsAndKeepResolved(rawBase, working, discoveredVtablePaths, discoveredRootPaths, touchedArrayRoots, resolveLayout);
        NormalizeGameUiTuple(working, rawBase, discoveredVtablePaths, discoveredRootPaths, structuralOverrides, items);
        bool seededCrossCheckDate = NormalizeOptionalSections(working, discoveredVtablePaths, discoveredRootPaths);
        bool seededCallUpNation = SeedCallUpContainerNationIfMissing(working);

        // Phase 4b: staff kinds of players who are also staff (spec offset -1) come from the staff list itself.
        ResolvePlayerStaffKinds(rtti, memory, game.ModuleBase, OffsetProfile.Load(working.ToJsonString(), resolveLayout), vtableSpecs,
            working, items, discoveredVtablePaths, touchedArrayRoots);

        // Phase 5: Live structural & subtype completeness verification on the provisionally resolved profile.
        var provisional = OffsetProfile.Load(working.ToJsonString(), resolveLayout);
        CheckPlayerAndStaffCompleteness(memory, game.ModuleBase, provisional, items);
        var witnesses = CollectDateWitnesses(memory, game.ModuleBase, provisional);
        ProbeGameDates(memory, game.ModuleBase, provisional, working, rawBase, witnesses, structuralOverrides, items);
        if (seededCrossCheckDate)
        {
            if (items.Any(i => i.Key == "gameDate.crossCheck.dateOffset" && i.Status == DiscoveryStatus.Pass)
                && working["gameDate"]?["crossCheck"]?["dateOffset"] is JsonValue ccDv)
            {
                RecordStructuralIfDifferent(rawBase, "gameDate.crossCheck.dateOffset", ccDv.GetValue<string>(), structuralOverrides);
            }
            else if (working["gameDate"] is JsonObject gdObj)
            {
                gdObj.Remove("crossCheck");
                discoveredVtablePaths.Remove("gameDate.crossCheck.objectVtable");
                discoveredRootPaths.Remove("gameDate.crossCheck.rootRva");
            }
        }
        ProbeCorporateFacilities(memory, game.ModuleBase, provisional, working, rawBase, structuralOverrides, items);
        ProbeFutureTransferDateVector(memory, game.ModuleBase, provisional, working, rawBase, structuralOverrides, items);
        ProbeProposalChangedFlag(memory, game.ModuleBase, provisional, working, rawBase, structuralOverrides, items);
        ProbeStaffJobPersonAnchor(memory, game.ModuleBase, provisional, working, rawBase, structuralOverrides, items);
        ProbeCallUpContainerNation(memory, game.ModuleBase, provisional, working, rawBase, structuralOverrides, items);
        if (seededCallUpNation && !structuralOverrides.Contains("player.callUps.containerNation") && working["player"]?["callUps"] is JsonObject cuObj)
        {
            cuObj.Remove("containerNation");
            cuObj.Remove("containerVtable");
            discoveredVtablePaths.Remove("player.callUps.containerVtable");
        }
        ProbePeCodeSignatures(image, memory, game.ModuleBase, provisional, working, rawBase, discoveredRootPaths, structuralOverrides, items);
        ProbeArm64CodeSignatures(memory, game.ModuleBase, (ulong)game.ModuleSize, provisional, working, discoveredRootPaths, items);
        ProbeVectorAllocator(image, memory, provisional, working, discoveredRootPaths, items);

        // Build final resolved profile + compact "extends" output.
        var finalProfile = OffsetProfile.Load(working.ToJsonString(), resolveLayout);
        var compact = BuildCompactProfile(
            layoutRef,
            working,
            rawBuild,
            buildName,
            buildRva,
            moduleName,
            isGamePass,
            layout,
            game,
            image,
            discoveredVtablePaths,
            discoveredRootPaths,
            structuralOverrides,
            touchedArrayRoots);

        return new DiscoveryResult(
            layout,
            buildName,
            ToIndentedJson(compact),
            ToIndentedJson(working),
            finalProfile,
            items);
    }

    static string NormalizeLayoutShortName(string name)
    {
        string s = Path.GetFileNameWithoutExtension(name.Trim().Replace('\\', '/'));
        return s.ToLowerInvariant() switch
        {
            "mac" or "macos" or "steam-mac" => "macos",
            "touch" or "touch-mac" or "touch-macos" => "touch-macos",
            "win" or "windows" => "windows",
            _ => s,
        };
    }

    /// <summary>
    /// MSVC objects with virtual bases begin with a pointer to a virtual-base table, which has no RTTI; their vtable sits
    /// further in. Steam 27.1.0+2505117 (19/11/2026): db::ACTUAL_NON_PLAYER and db::HUMAN_NON_PLAYER have RTTI vtables
    /// only at +256/+296 and +1088/+1128, while the staff list's objects start with 0x4EECF08 and 0x4E2170C, the values the
    /// calibrated profile holds. Finds the live objects by the vtable at the smallest offset and takes the qword at +0 that
    /// at least 95% of them share, when it points into the module.
    /// </summary>
    internal static bool DeriveFromLiveObjects(RttiIndex rtti, ModuleImage image, IProcessMemory memory, string className,
        IReadOnlyList<uint> vtables, out uint derived, out string detail)
    {
        derived = 0; detail = "";
        if (memory is not IMemoryRegions) return false;
        var inner = vtables.Select(v => (Vtable: v, Offset: rtti.EntryOf(v)?.SubobjectOffset ?? -1))
            .Where(x => x.Offset > 0).OrderBy(x => x.Offset).FirstOrDefault();
        if (inner.Offset <= 0) return false;
        var hits = Memory.HeapVtableScanner.Scan(memory, image.ModuleBase + inner.Vtable);
        var counts = new Dictionary<ulong, int>();
        Span<byte> head = stackalloc byte[8];
        foreach (var hit in hits)
        {
            if (hit.Address < (ulong)inner.Offset || !memory.TryRead(hit.Address - (ulong)inner.Offset, head)) continue;
            ulong value = BitConverter.ToUInt64(head);
            counts[value] = counts.GetValueOrDefault(value) + 1;
        }
        int total = counts.Values.Sum();
        if (total == 0) return false;
        var (top, n) = counts.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();
        ulong moduleEnd = image.ModuleBase + (ulong)image.Bytes.Length;
        if (top < image.ModuleBase || top >= moduleEnd || n * 100 < total * 95) return false;
        derived = (uint)(top - image.ModuleBase);
        detail = $"{className}+0 has no RTTI vtable (virtual-base table); {n}/{total} live objects found by its +{inner.Offset} vtable {Hex(inner.Vtable)} start with {Hex(derived)}";
        return true;
    }

    static uint? ResolveVtableSpec(
        RttiIndex rtti,
        ModuleImage image,
        IProcessMemory memory,
        VtableClassSpec spec,
        IReadOnlyList<ulong>? parentSamples,
        out string detail,
        out DiscoveryStatus status,
        out string? manualHint)
    {
        manualHint = null;
        string usedClass = spec.Class;
        var candidates = rtti.Vtables(spec.Class, spec.Offset);
        if (candidates.Count == 0 && spec.FallbackClasses is { Length: > 0 })
        {
            foreach (var fb in spec.FallbackClasses)
            {
                candidates = rtti.Vtables(fb, spec.Offset);
                if (candidates.Count > 0) { usedClass = fb; break; }
            }
        }

        if (candidates.Count == 0)
        {
            var anyOffset = rtti.Vtables(spec.Class);
            if (anyOffset.Count > 0)
            {
                var foundOffsets = anyOffset.Select(v => rtti.EntryOf(v)?.SubobjectOffset ?? -1).Distinct().OrderBy(x => x);
                detail = $"No {spec.Class} vtable at subobject +{spec.Offset}; RTTI has offsets [{string.Join(", ", foundOffsets)}]";
                if (spec.Offset == 0 && DeriveFromLiveObjects(rtti, image, memory, spec.Class, anyOffset, out uint derived, out string why))
                {
                    status = DiscoveryStatus.Pass;
                    detail = why;
                    return derived;
                }
            }
            else
            {
                detail = $"RTTI class '{spec.Class}' not found";
            }
            status = spec.Optional ? DiscoveryStatus.Warn : DiscoveryStatus.Manual;
            return null;
        }

        if (candidates.Count == 1)
        {
            status = DiscoveryStatus.Pass;
            detail = $"unique RTTI match {usedClass}+{spec.Offset}";
            return candidates[0];
        }

        IReadOnlyList<uint> narrowed = spec.Offset == 0
            ? RootFinder.DisambiguateVtables(image, memory, candidates)
            : RootFinder.DisambiguateSubobjectVtable(memory, image.ModuleBase, parentSamples ?? [], spec.Offset, candidates);

        if (narrowed.Count == 1)
        {
            status = DiscoveryStatus.Pass;
            detail = $"disambiguated {usedClass}+{spec.Offset} from {candidates.Count} candidates via live heap";
            return narrowed[0];
        }

        if (narrowed.Count > 1 && narrowed.Count < candidates.Count)
        {
            status = DiscoveryStatus.Warn;
            detail = $"narrowed {usedClass}+{spec.Offset} to [{string.Join(", ", narrowed.Select(Hex))}], picked first";
            manualHint = $"dotnet run --project src/Fm27Editor.Tools -c Release -- slotsbyname \"{usedClass}\"";
            return narrowed[0];
        }

        status = DiscoveryStatus.Warn;
        detail = $"{candidates.Count} RTTI candidates for {usedClass}+{spec.Offset} with 0 live samples ({string.Join(", ", candidates.Select(Hex))}), picked first";
        manualHint = $"dotnet run --project src/Fm27Editor.Tools -c Release -- slotsbyname \"{usedClass}\"";
        return candidates[0];
    }

    static void ResolveRootPass(
        JsonObject working,
        IReadOnlyDictionary<string, RootDiscoverySpec> rootSpecs,
        ModuleImage image,
        IProcessMemory memory,
        List<DiscoveryItem> items,
        List<string> discoveredRootPaths,
        HashSet<string> touchedArrayRoots,
        Action<string>? log,
        bool includeDependent,
        ISet<string>? secondaryVtablePaths = null)
    {
        uint? sharedDbRoot = null;
        RttiIndex? holderRtti = null;
        // A root found through a secondary (offset > 0) subobject vtable waits for that vtable (phase 3), and an alias for its target.
        foreach (var (path, spec) in rootSpecs.OrderBy(kv => kv.Value.Kind == "alias").ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            bool isDependent = spec.Kind is "promises" or "alias" ||
                (spec.VtablePath is { } vp && secondaryVtablePaths?.Contains(vp) == true);
            if (isDependent != includeDependent) continue;

            switch (spec.Kind)
            {
                case "vector":
                {
                    var vts = ResolveSpecVtables(working, spec);
                    if (vts.Count == 0)
                    {
                        items.Add(new(path, spec.Optional ? DiscoveryStatus.Warn : DiscoveryStatus.Manual, "SKIPPED",
                            $"Missing target vtable '{spec.VtablePath ?? spec.VtablesPath}'", null));
                        continue;
                    }
                    bool pointerVector = path == "player.cities.listRootRva";
                    var hits = RootFinder.FindVectors(image, memory, vts, spec.Min, spec.Max, allowPointerToVector: pointerVector);
                    var chosen = pointerVector
                        ? hits.FirstOrDefault(h => h.IsPointerToVector)
                        : hits.FirstOrDefault(h => !h.IsPointerToVector);
                    if (chosen == default && hits.Count > 0) chosen = hits[0];
                    if (chosen == default && IsExpectedEmpty(working, path))
                    {
                        items.Add(new(path, DiscoveryStatus.Pass, "EXPECTED_EMPTY", $"'{path.Split('.')[0]}' is listed in expectedEmpty for this layout"));
                        continue;
                    }
                    if (chosen == default)
                    {
                        items.Add(new(path, spec.Optional ? DiscoveryStatus.Warn : DiscoveryStatus.Manual, "NOT_FOUND",
                            $"No vector of {string.Join("/", vts.Select(Hex))} in [{spec.Min}..{spec.Max}]",
                            spec.Optional ? null : $"dotnet run --project src/Fm27Editor.Tools -c Release -- vectorroot {Hex(vts.First())} 0"));
                        continue;
                    }
                    SetWorkingPath(working, path, Hex(chosen.Rva), discoveredRootPaths, touchedArrayRoots);
                    var status = hits.Count > 1 && hits[0].Count == hits[1].Count ? DiscoveryStatus.Warn : DiscoveryStatus.Pass;
                    items.Add(new(path, status, Hex(chosen.Rva),
                        $"vector count={chosen.Count} ({chosen.MatchedVtables} vtable(s), {hits.Count} candidate(s))"));
                    break;
                }
                case "indirectVector":
                {
                    var vts = ResolveSpecVtables(working, spec);
                    if (vts.Count == 0 || string.IsNullOrEmpty(spec.OffsetPath))
                    {
                        items.Add(new(path, DiscoveryStatus.Manual, "SKIPPED", "Missing vtable or offsetPath", null));
                        continue;
                    }
                    var hits = RootFinder.FindIndirectVectors(image, memory, vts, spec.MaxOffset, spec.Min, spec.Max);
                    if (hits.Count == 0)
                    {
                        items.Add(new(path, spec.Optional ? DiscoveryStatus.Warn : DiscoveryStatus.Manual, "NOT_FOUND",
                            $"No indirect vector of {string.Join("/", vts.Select(Hex))} within +0x{spec.MaxOffset:X}",
                            $"dotnet run --project src/Fm27Editor.Tools -c Release -- ptrpath {Hex(vts.First())}"));
                        continue;
                    }
                    if (!string.IsNullOrEmpty(spec.HolderClass))
                    {
                        holderRtti ??= RttiIndex.Build(image);
                        var holderVts = holderRtti.Vtables(spec.HolderClass, 0).Select(vt => image.ModuleBase + vt).ToHashSet();
                        var owned = hits.Where(h => holderVts.Contains(ReadU64Safe(memory, ReadU64Safe(memory, image.ModuleBase + h.RootRva)))).ToList();
                        if (owned.Count == 0)
                        {
                            items.Add(new(path, DiscoveryStatus.Manual, "NOT_FOUND",
                                $"{hits.Count} vector(s) of {string.Join("/", vts.Select(Hex))}, none inside a {spec.HolderClass} object",
                                $"dotnet run --project src/Fm27Editor.Tools -c Release -- slotsbyname \"{spec.HolderClass}\""));
                            continue;
                        }
                        var own = owned[0];
                        SetWorkingPath(working, path, Hex(own.RootRva), discoveredRootPaths, touchedArrayRoots);
                        SetWorkingPath(working, spec.OffsetPath, Hex((uint)own.Offset), discoveredRootPaths, touchedArrayRoots);
                        items.Add(new(path, owned.Count == 1 ? DiscoveryStatus.Pass : DiscoveryStatus.Warn, $"{Hex(own.RootRva)} + {Hex((uint)own.Offset)}",
                            $"{spec.HolderClass} vector count={own.Count} ({owned.Count} owned of {hits.Count} candidate(s))"));
                        continue;
                    }
                    var chosen = sharedDbRoot is { } db
                        ? (hits.FirstOrDefault(h => h.RootRva == db) is var match && match != default ? match : hits[0])
                        : hits[0];
                    sharedDbRoot ??= chosen.RootRva;
                    SetWorkingPath(working, path, Hex(chosen.RootRva), discoveredRootPaths, touchedArrayRoots);
                    SetWorkingPath(working, spec.OffsetPath, Hex((uint)chosen.Offset), discoveredRootPaths, touchedArrayRoots);
                    items.Add(new(path, DiscoveryStatus.Pass, $"{Hex(chosen.RootRva)} + {Hex((uint)chosen.Offset)}",
                        $"indirect vector count={chosen.Count} ({hits.Count} candidate(s))"));
                    break;
                }
                case "object":
                {
                    var vts = ResolveSpecVtables(working, spec);
                    if (vts.Count == 0 && !string.IsNullOrEmpty(spec.HolderClass))
                    {
                        holderRtti ??= RttiIndex.Build(image);
                        foreach (uint vt in holderRtti.Vtables(spec.HolderClass, 0)) vts.Add(vt);
                    }
                    string label = spec.VtablePath ?? spec.HolderClass ?? "";
                    if (vts.Count == 0)
                    {
                        if (!spec.Optional)
                            items.Add(new(path, DiscoveryStatus.Manual, "SKIPPED", $"Missing vtable '{label}'", null));
                        continue;
                    }
                    var hits = new List<uint>();
                    foreach (uint vt in vts) hits.AddRange(RootFinder.FindObjects(image, memory, vt));
                    if (hits.Count == 0)
                    {
                        items.Add(new(path, spec.Optional ? DiscoveryStatus.Warn : DiscoveryStatus.Manual, "NOT_FOUND",
                            $"No static singleton for vtable {string.Join("/", vts.Select(Hex))}",
                            spec.Optional ? null : $"dotnet run --project src/Fm27Editor.Tools -c Release -- slotsbyname \"{label}\""));
                        continue;
                    }
                    uint chosen = hits[0];
                    SetWorkingPath(working, path, Hex(chosen), discoveredRootPaths, touchedArrayRoots);
                    items.Add(new(path, hits.Count == 1 ? DiscoveryStatus.Pass : DiscoveryStatus.Warn, Hex(chosen),
                        $"singleton object ({hits.Count} slot(s))"));
                    break;
                }
                case "promises":
                {
                    var className = spec.VtablePath ?? "INTERACTION_MANAGER";
                    var rtti = RttiIndex.Build(image);
                    var mgrVts = RootFinder.DisambiguateVtables(image, memory, rtti.Vtables(className, 0));
                    uint? chosen = null;
                    int chosenVector = ProfileJsonResolver.GetByPath(working, "player.promises.globalVector") is JsonValue gv && TryParseHexUInt(gv.GetValue<string>(), out uint defGv)
                        ? (int)defGv : 0xF8;
                    int overlapCount = 0;
                    var samplePromiseAddrs = CollectPlayerPromiseAddresses(working, memory, image.ModuleBase);
                    foreach (uint vt in mgrVts)
                    {
                        foreach (uint root in RootFinder.FindObjects(image, memory, vt))
                        {
                            ulong obj = ReadU64Safe(memory, image.ModuleBase + root);
                            if (obj == 0) continue;
                            var candidates = new List<int> { chosenVector };
                            for (int off = 0; off <= 0x300; off += 8) if (off != chosenVector) candidates.Add(off);
                            foreach (int off in candidates)
                            {
                                if (!LooksLikePromiseVector(memory, obj + (ulong)off, samplePromiseAddrs, out int hits)) continue;
                                chosen = root;
                                chosenVector = off;
                                overlapCount = hits;
                                break;
                            }
                            if (chosen is not null && overlapCount > 0) break;
                        }
                        if (chosen is not null && overlapCount > 0) break;
                    }
                    bool emptyGlobalVector = false;
                    if (chosen is null && samplePromiseAddrs.Count == 0)
                    {
                        uint? singleRoot = null;
                        int matchingRoots = 0;
                        foreach (uint vt in mgrVts)
                        {
                            foreach (uint root in RootFinder.FindObjects(image, memory, vt))
                            {
                                ulong obj = ReadU64Safe(memory, image.ModuleBase + root);
                                if (obj == 0) continue;
                                if (ReadU64Safe(memory, obj + (ulong)chosenVector) == 0 &&
                                    ReadU64Safe(memory, obj + (ulong)chosenVector + 8) == 0 &&
                                    ReadU64Safe(memory, obj + (ulong)chosenVector + 16) == 0)
                                {
                                    singleRoot = root;
                                    matchingRoots++;
                                }
                            }
                        }
                        if (matchingRoots == 1)
                        {
                            chosen = singleRoot;
                            emptyGlobalVector = true;
                        }
                    }
                    if (chosen is { } pRoot)
                    {
                        SetWorkingPath(working, path, Hex(pRoot), discoveredRootPaths, touchedArrayRoots);
                        SetWorkingPath(working, "player.promises.globalVector", Hex((uint)chosenVector), discoveredRootPaths, touchedArrayRoots);
                        items.Add(new(path, overlapCount > 0 ? DiscoveryStatus.Pass : DiscoveryStatus.Warn,
                            $"{Hex(pRoot)} + {Hex((uint)chosenVector)}",
                            overlapCount > 0
                                ? $"confirmed {overlapCount} shared promise record(s)"
                                : emptyGlobalVector
                                    ? "singleton found (0 live player promises; empty global vector)"
                                    : "singleton found (0 live player promises to cross-check)"));
                    }
                    else
                    {
                        items.Add(new(path, DiscoveryStatus.Manual, "NOT_FOUND", $"Could not resolve {className} promise list",
                            $"dotnet run --project src/Fm27Editor.Tools -c Release -- promisescan"));
                    }
                    break;
                }
                case "alias":
                {
                    if (ResolveAliasTarget(working, rootSpecs, spec.VtablePath) is { } targetVal)
                    {
                        SetWorkingPath(working, path, targetVal, discoveredRootPaths, touchedArrayRoots);
                        items.Add(new(path, DiscoveryStatus.Pass, targetVal, $"alias -> {spec.VtablePath}"));
                    }
                    else if (!spec.Optional)
                    {
                        items.Add(new(path, DiscoveryStatus.Warn, "UNRESOLVED_ALIAS", $"Target '{spec.VtablePath}' missing", null));
                    }
                    break;
                }
            }
        }
        log?.Invoke(includeDependent ? "finished promise/alias root pass" : "finished primary root pass");
    }

    static bool IsExpectedEmpty(JsonObject working, string path) =>
        working["expectedEmpty"] is JsonArray arr &&
        arr.Any(e => e is JsonValue v && string.Equals(v.GetValue<string>(), path.Split('.')[0], StringComparison.OrdinalIgnoreCase));

    static string? ResolveAliasTarget(JsonObject working, IReadOnlyDictionary<string, RootDiscoverySpec> rootSpecs, string? targetPath, int depth = 0)
    {
        if (string.IsNullOrEmpty(targetPath) || depth > 5) return null;
        if (ProfileJsonResolver.GetByPath(working, targetPath) is JsonValue val)
            return val.GetValue<string>();
        if (rootSpecs.TryGetValue(targetPath, out var next) && next.Kind == "alias")
            return ResolveAliasTarget(working, rootSpecs, next.VtablePath, depth + 1);
        return null;
    }

    static HashSet<uint> ResolveSpecVtables(JsonObject working, RootDiscoverySpec spec)
    {
        var set = new HashSet<uint>();
        foreach (string single in (spec.VtablePath ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ProfileJsonResolver.GetByPath(working, single) is JsonValue v && TryParseHexUInt(v.GetValue<string>(), out uint rva))
                set.Add(rva);
        if (!string.IsNullOrEmpty(spec.VtablesPath) &&
            ProfileJsonResolver.GetByPath(working, spec.VtablesPath) is JsonArray arr)
        {
            foreach (var elem in arr)
                if (elem is JsonValue sv && TryParseHexUInt(sv.GetValue<string>(), out uint vt)) set.Add(vt);
        }
        return set;
    }

    static IReadOnlyList<ulong> CollectSamplesForClass(JsonObject working, IProcessMemory memory, ulong moduleBase, string className)
    {
        if (className.Contains("ACTUAL_PLAYER", StringComparison.Ordinal))
        {
            var pts = ReadVectorSamplePointers(working, memory, moduleBase, "playerList.rootRva", "playerList.beginOffset", "playerList.endOffset", 64);
            if (pts.Count > 0) return pts;
        }
        if (className.Contains("NON_PLAYER", StringComparison.Ordinal))
        {
            if (ProfileJsonResolver.GetByPath(working, "staff.personListRootRva") is JsonValue rootVal &&
                TryParseHexUInt(rootVal.GetValue<string>(), out uint rva))
            {
                bool direct = ProfileJsonResolver.GetByPath(working, "staff.directStaffVector")?.GetValue<bool>() == true;
                ulong listAddr = direct ? moduleBase + rva : ReadU64Safe(memory, moduleBase + rva);
                int off = ProfileJsonResolver.GetByPath(working, "staff.personListOffset") is JsonValue ov && TryParseHexUInt(ov.GetValue<string>(), out uint lo) ? (int)lo : 0;
                if (listAddr != 0)
                    return ReadVectorPointersFromAddr(memory, listAddr + (ulong)off, 256);
            }
        }
        return [];
    }

    static List<ulong> ReadVectorSamplePointers(JsonObject working, IProcessMemory memory, ulong moduleBase, string rootPath, string beginPath, string endPath, int limit)
    {
        if (ProfileJsonResolver.GetByPath(working, rootPath) is not JsonValue rv || !TryParseHexUInt(rv.GetValue<string>(), out uint rva))
            return [];
        int bOff = ProfileJsonResolver.GetByPath(working, beginPath) is JsonValue bv && TryParseHexUInt(bv.GetValue<string>(), out uint b) ? (int)b : 0;
        int eOff = ProfileJsonResolver.GetByPath(working, endPath) is JsonValue ev && TryParseHexUInt(ev.GetValue<string>(), out uint e) ? (int)e : 8;
        ulong begin = ReadU64Safe(memory, moduleBase + rva + (ulong)bOff);
        ulong end = ReadU64Safe(memory, moduleBase + rva + (ulong)eOff);
        if (begin < 0x1_0000_0000UL || end <= begin || (end - begin) % 8 != 0) return [];
        int count = (int)Math.Min((ulong)limit, (end - begin) / 8);
        var buf = new byte[count * 8];
        if (!memory.TryRead(begin, buf)) return [];
        var list = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            ulong p = BitConverter.ToUInt64(buf, i * 8);
            if (p >= 0x1_0000_0000UL) list.Add(p);
        }
        return list;
    }

    static List<ulong> ReadVectorPointersFromAddr(IProcessMemory memory, ulong vectorAddr, int limit)
    {
        ulong begin = ReadU64Safe(memory, vectorAddr);
        ulong end = ReadU64Safe(memory, vectorAddr + 8);
        if (begin < 0x1_0000_0000UL || end <= begin || (end - begin) % 8 != 0 || (end - begin) / 8 > 500_000) return [];
        int total = (int)((end - begin) / 8);
        int step = Math.Max(1, total / limit);
        int take = Math.Min(limit, total);
        var list = new List<ulong>(take);
        for (int i = 0; i < total && list.Count < limit; i += step)
        {
            ulong p = ReadU64Safe(memory, begin + (ulong)i * 8);
            if (p >= 0x1_0000_0000UL) list.Add(p);
        }
        return list;
    }

    static HashSet<ulong> CollectPlayerPromiseAddresses(JsonObject working, IProcessMemory memory, ulong moduleBase)
    {
        var set = new HashSet<ulong>();
        if (ProfileJsonResolver.GetByPath(working, "player.promises.holder") is not JsonValue hv ||
            !TryParseHexUInt(hv.GetValue<string>(), out uint holderOffInt))
            return set;
        int holderOff = (int)holderOffInt;
        int recPersonOff = ProfileJsonResolver.GetByPath(working, "player.promises.person") is JsonValue rpv && TryParseHexUInt(rpv.GetValue<string>(), out uint rp)
            ? (int)rp : 0x20;
        int playerPersonOff = ProfileJsonResolver.GetByPath(working, "player.relations.playerPerson") is JsonValue ppv && TryParseHexUInt(ppv.GetValue<string>(), out uint pp)
            ? (int)pp : 0;

        var insertions = new Dictionary<ulong, (int At, int Length)>();
        if (ProfileJsonResolver.GetByPath(working, "player.subtypeInsertions") is JsonArray insArr)
        {
            foreach (var item in insArr)
            {
                if (item is not JsonObject obj) continue;
                if (obj["vtable"] is JsonValue vtv && TryParseHexUInt(vtv.GetValue<string>(), out uint vtRva) &&
                    obj["at"] is JsonValue atv && TryParseHexUInt(atv.GetValue<string>(), out uint atVal) &&
                    obj["length"] is JsonValue lenv && TryParseHexUInt(lenv.GetValue<string>(), out uint lenVal))
                {
                    insertions[moduleBase + vtRva] = ((int)atVal, (int)lenVal);
                }
            }
        }

        int TranslateOffset(int baseOff, ulong vtable) =>
            insertions.TryGetValue(vtable, out var ins) && baseOff >= ins.At ? baseOff + ins.Length : baseOff;

        var players = ReadVectorSamplePointers(working, memory, moduleBase, "playerList.rootRva", "playerList.beginOffset", "playerList.endOffset", 100_000);
        if (players.Count == 0) return set;

        var playerAddrs = players.ToArray();
        var vtables = ReadU64Array(memory, playerAddrs);
        var holderSlotAddrs = new ulong[players.Count];
        for (int i = 0; i < players.Count; i++)
            holderSlotAddrs[i] = players[i] + (ulong)TranslateOffset(holderOff, vtables[i]);
        var holders = ReadU64Array(memory, holderSlotAddrs);

        Span<byte> recHead = stackalloc byte[4];
        for (int i = 0; i < players.Count; i++)
        {
            ulong holder = holders[i];
            if (holder < 0x1_0000_0000UL) continue;
            ulong b = ReadU64Safe(memory, holder), e = ReadU64Safe(memory, holder + 8);
            if (b < 0x1_0000_0000UL || e <= b || (e - b) % 8 != 0 || (e - b) / 8 > 64) continue;
            ulong expectedPerson = playerPersonOff > 0
                ? players[i] + (ulong)TranslateOffset(playerPersonOff, vtables[i])
                : 0;
            for (ulong p = b; p < e; p += 8)
            {
                ulong rec = ReadU64Safe(memory, p);
                if (rec < 0x1_0000_0000UL) continue;
                if (!memory.TryRead(rec, recHead)) continue;
                uint refCount = BitConverter.ToUInt32(recHead);
                if (refCount is < 1 or > 10) continue;
                if (playerPersonOff > 0 && ReadU64Safe(memory, rec + (ulong)recPersonOff) != expectedPerson) continue;
                set.Add(rec);
            }
        }
        return set;
    }

    static ulong[] ReadU64Array(IProcessMemory memory, ulong[] addresses)
    {
        var values = new ulong[addresses.Length];
        if (addresses.Length == 0) return values;
        if (memory is IScatterReadMemory scatter)
        {
            const int batchSize = 512;
            var ok = new bool[batchSize];
            var bufs = new byte[batchSize][];
            for (int b = 0; b < batchSize; b++) bufs[b] = new byte[8];
            for (int i = 0; i < addresses.Length; i += batchSize)
            {
                int n = Math.Min(batchSize, addresses.Length - i);
                var reqs = new MemoryReadRequest[n];
                for (int j = 0; j < n; j++) reqs[j] = new(addresses[i + j], bufs[j]);
                scatter.ReadMany(reqs, ok.AsSpan(0, n));
                for (int j = 0; j < n; j++)
                    values[i + j] = ok[j] ? BitConverter.ToUInt64(bufs[j]) : 0;
            }
            return values;
        }

        for (int i = 0; i < addresses.Length; i++)
            values[i] = ReadU64Safe(memory, addresses[i]);
        return values;
    }

    static bool LooksLikePromiseVector(IProcessMemory memory, ulong vectorAddr, HashSet<ulong> samplePromises, out int overlapHits)
    {
        overlapHits = 0;
        ulong begin = ReadU64Safe(memory, vectorAddr);
        ulong end = ReadU64Safe(memory, vectorAddr + 8);
        ulong cap = ReadU64Safe(memory, vectorAddr + 16);
        if (begin < 0x1_0000_0000UL || end < begin || cap < end || (end - begin) % 8 != 0) return false;
        ulong count = (end - begin) / 8;
        if (count > 200_000) return false;
        if (count == 0) return samplePromises.Count == 0;
        int toRead = (int)Math.Min(count, 4096);
        var buf = new byte[toRead * 8];
        if (!memory.TryRead(begin, buf)) return false;
        if (samplePromises.Count > 0)
        {
            for (int i = 0; i < toRead; i++)
                if (samplePromises.Contains(BitConverter.ToUInt64(buf, i * 8))) overlapHits++;
            return overlapHits > 0;
        }
        ulong first = BitConverter.ToUInt64(buf, 0);
        if (first < 0x1_0000_0000UL) return false;
        Span<byte> recHead = stackalloc byte[4];
        return memory.TryRead(first, recHead) && BitConverter.ToUInt32(recHead) is >= 1 and <= 10;
    }

    static JsonObject ReapplyBaseRefsAndKeepResolved(
        JsonObject rawBase,
        JsonObject resolvedSoFar,
        List<string> vtablePaths,
        List<string> rootPaths,
        HashSet<string> touchedArrays,
        Func<string, string?>? resolveLayout)
    {
        var overlay = new JsonObject();
        foreach (var k in new[] { "build", "matchBuild", "variant", "buildStringRva", "imageSize", "peTimestamp" })
            if (resolvedSoFar[k] is { } v) overlay[k] = v.DeepClone();
        foreach (var p in vtablePaths.Concat(rootPaths))
        {
            if (p.Contains('[') || ProfileJsonResolver.GetByPath(resolvedSoFar, p) is not { } node) continue;
            ProfileJsonResolver.SetByPath(overlay, p, node.DeepClone());
        }
        foreach (var arrRoot in touchedArrays)
            if (ProfileJsonResolver.GetByPath(resolvedSoFar, arrRoot) is { } arrNode)
                ProfileJsonResolver.SetByPath(overlay, arrRoot, arrNode.DeepClone());

        var merged = (JsonObject)rawBase.DeepClone();
        // Merge newly resolved vtables and roots into the unresolved base layout BEFORE expanding @ references,
        // so symbolic references like @club.finance.vtable propagate the new build's vtable RVAs everywhere.
        var overlayNode = (JsonObject)JsonNode.Parse(overlay.ToJsonString())!;
        MergeInto(merged, overlayNode);
        return ProfileJsonResolver.Resolve(merged.ToJsonString(), resolveLayout);
    }

    static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (var kv in overlay.ToList())
        {
            if (kv.Value is null) { target.Remove(kv.Key); continue; }
            if (kv.Value is JsonObject child && target[kv.Key] is JsonObject existing)
            {
                MergeInto(existing, child);
                continue;
            }
            target[kv.Key] = kv.Value.DeepClone();
        }
    }

    internal sealed record PlayerStaffKindMeasurement(bool Ok, uint EntryRva, uint PersonRva, string Detail);

    /// <summary>Measures staff kind <paramref name="kindIndex"/> from the staff-list entries of <paramref name="className"/> objects.</summary>
    internal static PlayerStaffKindMeasurement MeasurePlayerStaffKind(RttiIndex rtti, IProcessMemory memory, ulong moduleBase,
        OffsetProfile profile, int kindIndex, string className)
    {
        if (profile.Staff is not { } so || kindIndex >= so.Kinds.Count) return new(false, 0, 0, "profile has no such staff kind");
        var kind = so.Kinds[kindIndex];
        var insertion = profile.PlayerInsertions.Where(kv => rtti.EntryOf(kv.Key)?.CanonicalName == className).ToList();
        if (insertion.Count != 1)
            return new(false, 0, 0, $"needs exactly one player.subtypeInsertions entry of class {className}, found {insertion.Count}");
        var (objectVt, ins) = (insertion[0].Key, insertion[0].Value);
        var entryVts = new Dictionary<ulong, int>();
        var personVts = new Dictionary<ulong, int>();
        int matches = 0;
        ulong root = so.DirectStaffVector ? moduleBase + so.PersonListRootRva : ReadU64Safe(memory, moduleBase + so.PersonListRootRva);
        ulong begin = ReadU64Safe(memory, root + (ulong)so.PersonListOffset);
        ulong end = ReadU64Safe(memory, root + (ulong)so.PersonListOffset + 8);
        if (begin != 0 && end > begin && (end - begin) % 8 == 0 && (end - begin) / 8 <= 1_000_000)
        {
            var raw = memory.Read(begin, (int)(end - begin));
            for (int i = 0; i < raw.Length; i += 8)
            {
                ulong entry = BitConverter.ToUInt64(raw, i);
                if (entry <= (ulong)ins.At || ReadU64Safe(memory, entry - (ulong)ins.At) != moduleBase + objectVt) continue;
                matches++;
                ulong ev = ReadU64Safe(memory, entry), pv = ReadU64Safe(memory, entry + (ulong)kind.StaffPart);
                entryVts[ev] = entryVts.GetValueOrDefault(ev) + 1;
                personVts[pv] = personVts.GetValueOrDefault(pv) + 1;
            }
        }
        if (matches == 0) return new(false, 0, 0, $"no staff-list entry at +{Hex((uint)ins.At)} of a {className} object");
        var (entryVt, entryCount) = entryVts.MaxBy(kv => kv.Value);
        var (personVt, personCount) = personVts.MaxBy(kv => kv.Value);
        uint entryRva = (uint)(entryVt - moduleBase), personRva = (uint)(personVt - moduleBase);
        var personEntry = rtti.EntryOf(personRva);
        int expectedPerson = ins.At + kind.StaffPart;
        bool personOk = personEntry is { } pe && pe.CanonicalName == className && pe.SubobjectOffset == expectedPerson
            && profile.Relations is { } rel && pe.SubobjectOffset == rel.PlayerPerson + ins.Length;
        bool dominant = entryCount * 100 >= matches * 95 && personCount * 100 >= matches * 95;
        string detail = $"{matches} entries at +{Hex((uint)ins.At)} of {className}; entry {Hex(entryRva)}:{entryCount}, " +
            $"person +{Hex((uint)kind.StaffPart)} {Hex(personRva)}:{personCount} = {personEntry?.CanonicalName ?? "no RTTI"}+{Hex((uint)(personEntry?.SubobjectOffset ?? 0))}" +
            $" (expected +{Hex((uint)expectedPerson)} = plain person +{Hex((uint)(profile.Relations?.PlayerPerson ?? 0))} + insertion {Hex((uint)ins.Length)})";
        return new(dominant && personOk, entryRva, personRva, detail);
    }

    /// <summary>
    /// A player who is also staff is one object: player part, staff part at <c>player.subtypeInsertions.at</c> (where the
    /// staff list points, a vbtable on Windows, so no RTTI), then the person part. A staff kind whose vtable specs have
    /// offset -1 is measured from those staff-list entries: the entry's own vtable and the one <c>staffPart</c> further on.
    /// The person part's RTTI offset must equal the plain player's person part plus the insertion length, which checks
    /// the insertion too (Windows 27.1.0: person at +0x380 = 0x288 + 0xF8; Steam's hand-made profile had kept the
    /// standalone db::NON_PLAYING_DATA vtable here and matched none of the 139 entries).
    /// </summary>
    internal static void ResolvePlayerStaffKinds(RttiIndex rtti, IProcessMemory memory, ulong moduleBase, OffsetProfile profile,
        IReadOnlyDictionary<string, VtableClassSpec> specs, JsonObject working, List<DiscoveryItem> items,
        List<string> discoveredVtablePaths, HashSet<string> touchedArrayRoots)
    {
        if (profile.Staff is not { } so) return;
        for (int k = 0; k < so.Kinds.Count; k++)
        {
            string staffPath = $"staff.kinds[{k}].staffVtable", personPath = $"staff.kinds[{k}].personVtable";
            if (!specs.TryGetValue(staffPath, out var spec) || spec.Offset != -1) continue;
            PlayerStaffKindMeasurement m;
            try { m = MeasurePlayerStaffKind(rtti, memory, moduleBase, profile, k, spec.Class); }
            catch (Exception ex) { m = new(false, 0, 0, ex.Message); }
            if (m.EntryRva == 0)
            {
                items.Add(new(staffPath, DiscoveryStatus.Manual, "MISSING", m.Detail, "Resolve player.subtypeInsertions[0] first, then measure with hybridprobe"));
                continue;
            }
            var status = m.Ok ? DiscoveryStatus.Pass : DiscoveryStatus.Manual;
            string? hint = m.Ok ? null : "Measure the object with hybridprobe; fix player.subtypeInsertions or staff.kinds staffPart";
            SetWorkingPath(working, staffPath, Hex(m.EntryRva), discoveredVtablePaths, touchedArrayRoots);
            SetWorkingPath(working, personPath, Hex(m.PersonRva), discoveredVtablePaths, touchedArrayRoots);
            items.Add(new(staffPath, status, Hex(m.EntryRva), m.Detail, hint));
            items.Add(new(personPath, status, Hex(m.PersonRva), m.Detail, hint));
        }
    }

    static void CheckPlayerAndStaffCompleteness(IProcessMemory memory, ulong moduleBase, OffsetProfile profile, List<DiscoveryItem> items)
    {
        try
        {
            ulong pRoot = moduleBase + profile.PlayerListRootRva;
            ulong begin = ReadU64Safe(memory, pRoot + (ulong)profile.PlayerListBeginOffset);
            ulong end = ReadU64Safe(memory, pRoot + (ulong)profile.PlayerListEndOffset);
            if (begin != 0 && end > begin && (end - begin) % 8 == 0 && (end - begin) / 8 <= 1_000_000)
            {
                var raw = memory.Read(begin, (int)(end - begin));
                var knownP = profile.PlayerVtableRvas.Select(v => moduleBase + v).ToHashSet();
                var counts = new Dictionary<ulong, int>();
                int unmapped = 0;
                for (int i = 0; i < raw.Length; i += 8)
                {
                    ulong p = BitConverter.ToUInt64(raw, i);
                    if (p == 0) continue;
                    ulong vt = ReadU64Safe(memory, p);
                    if (vt == 0) continue;
                    counts[vt] = counts.GetValueOrDefault(vt) + 1;
                    if (!knownP.Contains(vt)) unmapped++;
                }
                items.Add(new("completeness.playerList", unmapped == 0 ? DiscoveryStatus.Pass : DiscoveryStatus.Manual,
                    $"{raw.Length / 8} entries ({unmapped} unmapped)",
                    string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{Hex((uint)(kv.Key - moduleBase))}:{kv.Value}")),
                    unmapped > 0 ? "Unmapped subtype vtable in playerList — inspect with slotsbyname/vtsurvey" : null));
            }
        }
        catch (Exception ex)
        {
            items.Add(new("completeness.playerList", DiscoveryStatus.Warn, "ERROR", ex.Message));
        }

        if (profile.Staff is { } so)
        {
            try
            {
                ulong root = so.DirectStaffVector ? moduleBase + so.PersonListRootRva : ReadU64Safe(memory, moduleBase + so.PersonListRootRva);
                ulong begin = ReadU64Safe(memory, root + (ulong)so.PersonListOffset);
                ulong end = ReadU64Safe(memory, root + (ulong)so.PersonListOffset + 8);
                if (begin != 0 && end > begin && (end - begin) % 8 == 0 && (end - begin) / 8 <= 1_000_000)
                {
                    var raw = memory.Read(begin, (int)(end - begin));
                    var knownS = so.Kinds.Select(k => moduleBase + (so.DirectStaffVector ? k.StaffVtableRva : k.PersonVtableRva)).ToHashSet();
                    var counts = new Dictionary<ulong, int>();
                    int unmapped = 0;
                    for (int i = 0; i < raw.Length; i += 8)
                    {
                        ulong p = BitConverter.ToUInt64(raw, i);
                        if (p == 0) continue;
                        ulong vt = ReadU64Safe(memory, p);
                        if (vt == 0) continue;
                        counts[vt] = counts.GetValueOrDefault(vt) + 1;
                        if (!knownS.Contains(vt)) unmapped++;
                    }
                    items.Add(new("completeness.staffList", unmapped == 0 ? DiscoveryStatus.Pass : DiscoveryStatus.Manual,
                        $"{raw.Length / 8} entries ({unmapped} unmapped)",
                        string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{Hex((uint)(kv.Key - moduleBase))}:{kv.Value}")),
                        unmapped > 0 ? "Unmapped subtype vtable in staffList — inspect with slotsbyname/vtsurvey" : null));
                }
            }
            catch (Exception ex)
            {
                items.Add(new("completeness.staffList", DiscoveryStatus.Warn, "ERROR", ex.Message));
            }
        }
    }

    static IReadOnlyList<DateWitness> CollectDateWitnesses(IProcessMemory memory, ulong moduleBase, OffsetProfile profile)
    {
        var witnesses = new List<DateWitness>();
        try
        {
            var players = new PlayerRepository(memory, profile, moduleBase).LoadAll().Take(3000).ToList();
            foreach (var p in players)
            {
                if (p.Contract?.Joined is { } signed && signed.Year is >= 1995 and <= 2100)
                    witnesses.Add(new(signed, null));
            }
            if (profile.Injuries is not null)
            {
                var injuryReader = new InjuryReader(memory, profile, moduleBase);
                foreach (var p in players)
                {
                    foreach (var inj in injuryReader.Read(p))
                    {
                        if (inj.Start is { } st && st.Year is >= 2020 and <= 2100)
                        {
                            int totalDays = Math.Clamp(inj.MinimumDays + inj.ExtraDays + 30, 1, 730);
                            witnesses.Add(new(st, st.AddDays(totalDays)));
                        }
                    }
                }
            }
        }
        catch { }
        return witnesses;
    }

    static void ProbeGameDates(
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        IReadOnlyList<DateWitness> witnesses,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.GameDate?.Primary is not { } primary) return;
        ulong obj = ReadU64Safe(memory, moduleBase + primary.RootRva);
        if (obj == 0)
        {
            items.Add(new("gameDate.primary", DiscoveryStatus.Warn, "UNREADABLE", "Primary manager pointer is 0 (save not loaded?)"));
            return;
        }
        var cands = RootFinder.FindGameDateCandidates(memory, obj, 0x400, 0xB00, witnesses);
        int injuryWitnesses = witnesses.Count(w => w.MaxDateInclusive is not null);
        // Contracts alone only bound the date from below; a later date the game keeps for something else also fits them.
        DiscoveryStatus dateStatus = injuryWitnesses >= 10 ? DiscoveryStatus.Pass : DiscoveryStatus.Warn;
        string injuryNote = injuryWitnesses >= 10 ? "" : $"; only {injuryWitnesses} injury witnesses (need 10): confirm the date against the game UI";
        var current = cands.FirstOrDefault(c => c.Offset == primary.DateOffset);
        var best = cands.FirstOrDefault();
        DateOnly? primaryDate = GameClock.Read(memory, profile, moduleBase);

        if (best != default && (current == default || best.WitnessMatches > current.WitnessMatches))
        {
            string val = Hex((uint)best.Offset);
            ProfileJsonResolver.SetByPath(working, "gameDate.primary.dateOffset", val);
            RecordStructuralIfDifferent(rawBase, "gameDate.primary.dateOffset", val, structuralOverrides);
            primaryDate = best.Date;
            items.Add(new("gameDate.primary.dateOffset", dateStatus, $"{val} ({best.Date:yyyy-MM-dd})",
                $"witness match {best.WitnessMatches}/{best.WitnessTotal}, adjusted from {Hex((uint)primary.DateOffset)}{injuryNote}"));
        }
        else if (current != default)
        {
            primaryDate = current.Date;
            items.Add(new("gameDate.primary.dateOffset", dateStatus, $"{Hex((uint)current.Offset)} ({current.Date:yyyy-MM-dd})",
                $"witness match {current.WitnessMatches}/{current.WitnessTotal}{injuryNote}"));
        }
        else
        {
            items.Add(new("gameDate.primary.dateOffset", DiscoveryStatus.Manual, "NOT_FOUND",
                "No valid 2020..2100 date found in TRANSFER_MANAGER"));
        }

        if (profile.GameDate?.CrossCheck is { } cc && primaryDate is { } expectedDate)
        {
            ulong ccObj = ReadU64Safe(memory, moduleBase + cc.RootRva);
            if (ccObj == 0) return;
            var ccCands = RootFinder.FindGameDateCandidates(memory, ccObj, 0x400, 0xB00, [new(expectedDate, expectedDate)]);
            var ccCur = ccCands.FirstOrDefault(c => c.Offset == cc.DateOffset);
            var ccBest = ccCands.FirstOrDefault(c => c.Date == expectedDate);
            if (ccBest != default && (ccCur == default || ccCur.Date != expectedDate))
            {
                string val = Hex((uint)ccBest.Offset);
                ProfileJsonResolver.SetByPath(working, "gameDate.crossCheck.dateOffset", val);
                RecordStructuralIfDifferent(rawBase, "gameDate.crossCheck.dateOffset", val, structuralOverrides);
                items.Add(new("gameDate.crossCheck.dateOffset", DiscoveryStatus.Pass, $"{val} ({ccBest.Date:yyyy-MM-dd})",
                    $"matched primary date; adjusted from {Hex((uint)cc.DateOffset)}"));
            }
            else if (ccCur != default && ccCur.Date == expectedDate)
            {
                items.Add(new("gameDate.crossCheck.dateOffset", DiscoveryStatus.Pass, $"{Hex((uint)ccCur.Offset)} ({ccCur.Date:yyyy-MM-dd})",
                    "matches primary date"));
            }
            else
            {
                items.Add(new("gameDate.crossCheck.dateOffset", DiscoveryStatus.Warn, "MISMATCH",
                    $"Could not locate {expectedDate:yyyy-MM-dd} in crossCheck object"));
            }
        }
    }

    static void ProbeCorporateFacilities(
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.Club?.Finance?.VtableRva is not { } fullFinanceVt || profile.Club.Fields is null ||
            !profile.Club.Fields.TryGetValue(ClubField.CorporateFacilities, out var cf) || cf.Path.Count != 2)
            return;

        int finPtrOff = cf.Path[0];
        int currentFieldOff = cf.Path[1];

        var players = new PlayerRepository(memory, profile, moduleBase).LoadAll();
        var clubs = ClubDirectory.FromPlayers(memory, players, profile, moduleBase).Take(400).ToList();
        var clubFinPairs = new List<(ulong Club, ulong Finance)>();
        foreach (var club in clubs)
        {
            ulong fin = ReadU64Safe(memory, club.Address + (ulong)finPtrOff);
            if (fin >= 0x1_0000_0000UL && ReadU64Safe(memory, fin) == moduleBase + fullFinanceVt)
                clubFinPairs.Add((club.Address, fin));
        }
        if (clubFinPairs.Count < 10)
        {
            items.Add(new("club.fields.CorporateFacilities.path", DiscoveryStatus.Warn, cf.PathText,
                $"only {clubFinPairs.Count} full finance objects sampled"));
            return;
        }

        var clubFields = new ClubFieldAccess(memory, profile, moduleBase);
        ClubField[] compareFields =
        [
            ClubField.Training,
            ClubField.YouthImportance,
            ClubField.YouthFacilities,
            ClubField.YouthCoaching,
            ClubField.YouthRecruitment,
        ];
        var knownValues = new List<long?[]>();
        foreach (var field in compareFields)
        {
            if (!clubFields.IsMapped(field) || clubFields.Root(field) != ClubFieldRoot.Club) continue;
            var vals = new long?[clubFinPairs.Count];
            for (int i = 0; i < clubFinPairs.Count; i++)
                vals[i] = clubFields.Read(clubFinPairs[i].Club, field);
            knownValues.Add(vals);
        }

        var excludedFinanceOffsets = new HashSet<int>();
        foreach (var (field, fieldOff) in profile.Club.Fields)
        {
            if (field != ClubField.CorporateFacilities && fieldOff.Root == ClubFieldRoot.Club &&
                fieldOff.Path.Count == 2 && fieldOff.Path[0] == finPtrOff)
            {
                excludedFinanceOffsets.Add(fieldOff.Path[1]);
            }
        }

        var probes = new List<int> { currentFieldOff };
        for (int off = Math.Max(0, currentFieldOff - 0x40); off <= currentFieldOff + 0x40; off++) if (!probes.Contains(off)) probes.Add(off);

        (int Offset, double Ratio, int Distinct, int Min, int Max)? best = null;
        Span<byte> b = stackalloc byte[1];
        var candVals = new byte[clubFinPairs.Count];
        foreach (int off in probes)
        {
            if (excludedFinanceOffsets.Contains(off)) continue;
            int valid = 0;
            var distinct = new HashSet<byte>();
            int min = int.MaxValue, max = int.MinValue;
            Array.Clear(candVals);
            for (int i = 0; i < clubFinPairs.Count; i++)
            {
                if (!memory.TryRead(clubFinPairs[i].Finance + (ulong)off, b)) continue;
                byte val = b[0];
                if (val is >= 1 and <= 20)
                {
                    candVals[i] = val;
                    valid++;
                    distinct.Add(val);
                    if (val < min) min = val;
                    if (val > max) max = val;
                }
            }
            double ratio = (double)valid / clubFinPairs.Count;
            if (ratio >= 0.80 && distinct.Count > 1)
            {
                if (DuplicatesKnownField(candVals, knownValues)) continue;
                if (best is null || IsBetterCorporateCandidate(off, ratio, distinct.Count, best.Value, currentFieldOff))
                    best = (off, ratio, distinct.Count, min, max);
                if (off == currentFieldOff && ratio >= 0.90 && distinct.Count >= 3) break;
            }
        }

        if (best is { } win)
        {
            string newPath = $"0x{finPtrOff:X}->0x{win.Offset:X}";
            ProfileJsonResolver.SetByPath(working, "club.fields.CorporateFacilities.path", newPath);
            RecordStructuralIfDifferent(rawBase, "club.fields.CorporateFacilities.path", newPath, structuralOverrides);
            items.Add(new("club.fields.CorporateFacilities.path", DiscoveryStatus.Pass, newPath,
                $"valid={win.Ratio:P0}, distinct={win.Distinct}, range=[{win.Min}..{win.Max}] across {clubFinPairs.Count} clubs"));
        }
        else
        {
            items.Add(new("club.fields.CorporateFacilities.path", DiscoveryStatus.Manual, cf.PathText,
                $"No offset within ±0x40 of 0x{currentFieldOff:X} had >=80% in [1..20] with variance across {clubFinPairs.Count} clubs",
                "dump a full CLUB_FINANCE record (memdump) and compare with the previous build's values for the same club"));
        }
    }

    static bool DuplicatesKnownField(byte[] candVals, List<long?[]> knownValues)
    {
        foreach (var known in knownValues)
        {
            int compared = 0, equal = 0;
            for (int i = 0; i < candVals.Length; i++)
            {
                byte cv = candVals[i];
                if (cv == 0 || known[i] is not { } kv || kv < 1 || kv > 20) continue;
                compared++;
                if (cv == kv) equal++;
            }
            if (compared >= 5 && equal * 10 >= compared * 8) return true;
        }
        return false;
    }

    static bool IsBetterCorporateCandidate(
        int off,
        double ratio,
        int distinctCount,
        (int Offset, double Ratio, int Distinct, int Min, int Max) best,
        int anchorOff)
    {
        if (distinctCount != best.Distinct) return distinctCount > best.Distinct;
        if (Math.Abs(ratio - best.Ratio) > 1e-9) return ratio > best.Ratio;
        int dist = Math.Abs(off - anchorOff), bestDist = Math.Abs(best.Offset - anchorOff);
        if (dist != bestDist) return dist < bestDist;
        return off >= anchorOff && best.Offset < anchorOff;
    }

    static void ProbeFutureTransferDateVector(
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.FutureTransfers is not { } ft || profile.Proposals is not { } pr) return;
        ulong root = ReadU64Safe(memory, moduleBase + pr.ListRootRva);
        if (root == 0) return;

        var personVtables = new HashSet<ulong>();
        if (profile.Relations?.PersonVtable is { } pVt and not 0) personVtables.Add(moduleBase + pVt);
        if (profile.Relations?.StaffPersonVtable is { } spVt and not 0) personVtables.Add(moduleBase + spVt);
        if (profile.Staff?.Kinds is { } kinds)
            foreach (var k in kinds) personVtables.Add(moduleBase + k.PersonVtableRva);

        var candidateList = new List<int> { ft.DateVector };
        for (int off = 0x8; off <= 0x200; off += 8) if (!candidateList.Contains(off)) candidateList.Add(off);
        int[] candidates = candidateList.ToArray();
        (int Offset, int ValidDates, int Total)? winner = null;
        int recSize = Math.Max(ft.DateOfferId + 4, ft.DatePerson + 8);
        var rec = new byte[recSize];
        foreach (int off in candidates)
        {
            ulong begin = ReadU64Safe(memory, root + (ulong)off);
            ulong end = ReadU64Safe(memory, root + (ulong)off + 8);
            ulong cap = ReadU64Safe(memory, root + (ulong)off + 16);
            if (begin < 0x1_0000_0000UL || end <= begin || cap < end ||
                (end - begin) % 8 != 0 || (cap - begin) % 8 != 0 ||
                (end - begin) / 8 > 100_000 || (cap - begin) / 8 > 200_000)
            {
                continue;
            }
            int total = (int)((end - begin) / 8);
            int sample = Math.Min(total, 32);
            int valid = 0;
            for (int i = 0; i < sample; i++)
            {
                ulong ptr = ReadU64Safe(memory, begin + (ulong)i * 8);
                if (ptr == 0 || !memory.TryRead(ptr, rec)) continue;
                int year = BitConverter.ToInt16(rec, ft.Date + 2);
                int day = BitConverter.ToInt16(rec, ft.Date) & 0x1FF;
                if (year is < 2020 or > 2045 || day is < 1 or > 366) continue;
                ulong personPtr = BitConverter.ToUInt64(rec, ft.DatePerson);
                if (personPtr < 0x1_0000_0000UL) continue;
                if (personVtables.Count > 0 && !personVtables.Contains(ReadU64Safe(memory, personPtr))) continue;
                valid++;
            }
            if (valid * 2 >= sample && (winner is null || valid > winner.Value.ValidDates))
            {
                winner = (off, valid, total);
                if (off == ft.DateVector) break;
            }
        }

        if (winner is { } win)
        {
            string hex = Hex((uint)win.Offset);
            ProfileJsonResolver.SetByPath(working, "player.futureTransfers.dateVector", hex);
            RecordStructuralIfDifferent(rawBase, "player.futureTransfers.dateVector", hex, structuralOverrides);
            items.Add(new("player.futureTransfers.dateVector", DiscoveryStatus.Pass, hex,
                $"vector entries={win.Total}, valid dates sample={win.ValidDates}"));
        }
        else
        {
            items.Add(new("player.futureTransfers.dateVector", DiscoveryStatus.Manual, Hex((uint)ft.DateVector),
                "No vector of future-transfer date records in TRANSFER_MANAGER+0x8..0x200",
                "find a player with a known arranged transfer (game UI) and search TRANSFER_MANAGER vectors for a record holding his person pointer"));
        }
    }

    static void ProbeProposalChangedFlag(
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.Proposals is not { } pr) return;
        ulong root = ReadU64Safe(memory, moduleBase + pr.ListRootRva);
        if (root == 0)
        {
            items.Add(new("player.proposals.changedFlag", DiscoveryStatus.Warn, "null", "TRANSFER_MANAGER root pointer is null"));
            return;
        }
        // The manager's vtable identifies the list root only where they are one object (macOS). Windows keeps the offer list
        // and TRANSFER_MANAGER apart (Steam 27.1.0: 0x586CA20 and 0x586B520).
        bool sameObject = pr.ManagerRva is { } managerRva && managerRva == pr.ListRootRva;
        if (sameObject && pr.ManagerVtable is { } mv && ReadU64Safe(memory, root) != moduleBase + mv)
        {
            items.Add(new("player.proposals.changedFlag", DiscoveryStatus.Warn, "null", "TRANSFER_MANAGER vtable mismatch"));
            return;
        }

        int dateVector = working["player"]?["futureTransfers"]?["dateVector"] is JsonValue dv && TryParseHexUInt(dv.GetValue<string>(), out uint dvu)
            ? (int)dvu
            : profile.FutureTransfers?.DateVector ?? 0x38;
        int expected = dateVector + 0x18;
        bool overlapsList = expected >= pr.ListOffset && expected < pr.ListOffset + 24;
        bool overlapsDate = expected >= dateVector && expected < dateVector + 24;
        bool isVectorHeader = LooksLikeVectorHeader(memory, root + (ulong)expected);
        Span<byte> b = stackalloc byte[1];
        if (!overlapsList && !overlapsDate && !isVectorHeader && memory.TryRead(root + (ulong)expected, b) && b[0] <= 1)
        {
            string hex = Hex((uint)expected);
            ProfileJsonResolver.SetByPath(working, "player.proposals.changedFlag", hex);
            RecordStructuralIfDifferent(rawBase, "player.proposals.changedFlag", hex, structuralOverrides);
            items.Add(new("player.proposals.changedFlag", DiscoveryStatus.Warn, hex,
                $"guessed from layout: bool={b[0]} at TRANSFER_MANAGER+0x{expected:X} (dateVector+0x18)",
                $"confirm: write 1 at TRANSFER_MANAGER+0x{expected:X}, press Continue once; the game must reset it to 0"));
            return;
        }
        items.Add(new("player.proposals.changedFlag", DiscoveryStatus.Warn, "null",
            isVectorHeader
                ? $"TRANSFER_MANAGER+0x{expected:X} looks like a std::vector header, not a bool"
                : $"TRANSFER_MANAGER+0x{expected:X} unreadable, overlaps vector, or not bool (0x{b[0]:X2})"));
    }

    /// <summary>Returns true when <paramref name="addr"/> holds an aligned heap <c>{begin, end, cap}</c> std::vector header.</summary>
    internal static bool LooksLikeVectorHeader(IProcessMemory memory, ulong addr)
    {
        ulong q0 = ReadU64Safe(memory, addr);
        if (q0 < 0x1_0000_0000UL || (q0 & 7) != 0) return false;
        ulong q1 = ReadU64Safe(memory, addr + 8);
        ulong q2 = ReadU64Safe(memory, addr + 16);
        return q1 >= q0 && (q1 - q0) % 8 == 0 && (q1 - q0) / 8 <= 500_000 &&
               q2 >= q1 && (q2 - q0) % 8 == 0;
    }

    static void ProbeStaffJobPersonAnchor(
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.Staff is not { } so) return;
        var samples = new List<(ulong Person, ulong Job)>();
        try
        {
            ulong root = so.DirectStaffVector ? moduleBase + so.PersonListRootRva : ReadU64Safe(memory, moduleBase + so.PersonListRootRva);
            ulong begin = ReadU64Safe(memory, root + (ulong)so.PersonListOffset);
            ulong end = ReadU64Safe(memory, root + (ulong)so.PersonListOffset + 8);
            if (begin != 0 && end > begin)
            {
                int total = (int)Math.Min(500, (end - begin) / 8);
                for (int i = 0; i < total && samples.Count < 60; i++)
                {
                    ulong entry = ReadU64Safe(memory, begin + (ulong)i * 8);
                    ulong vt = ReadU64Safe(memory, entry);
                    var kind = so.Kinds.FirstOrDefault(k => moduleBase + (so.DirectStaffVector ? k.StaffVtableRva : k.PersonVtableRva) == vt);
                    if (kind is null || kind.Manager) continue;
                    ulong person = so.DirectStaffVector ? entry + (ulong)kind.DirectPersonDelta : entry + (ulong)(kind.PersonAnchor ?? 0);
                    ulong job = ReadU64Safe(memory, person + (ulong)so.Job);
                    if (job >= 0x1_0000_0000UL) samples.Add((person, job));
                }
            }
        }
        catch { }

        if (samples.Count == 0)
        {
            items.Add(new("staff.jobPersonAnchor", DiscoveryStatus.Warn, Hex((uint)so.JobPersonAnchor), "No employed non-manager staff sampled"));
            return;
        }

        int[] anchors = new[] { so.JobPersonAnchor, 0, 0x100, 0x208, 0x210, 0x340 }.Distinct().ToArray();
        foreach (int anchor in anchors)
        {
            int hits = 0;
            foreach (var (person, job) in samples)
                if (ReadU64Safe(memory, job + (ulong)so.JobPerson) == person + (ulong)anchor) hits++;
            if (hits * 20 >= samples.Count * 19)
            {
                string hex = Hex((uint)anchor);
                ProfileJsonResolver.SetByPath(working, "staff.jobPersonAnchor", hex);
                RecordStructuralIfDifferent(rawBase, "staff.jobPersonAnchor", hex, structuralOverrides);
                items.Add(new("staff.jobPersonAnchor", DiscoveryStatus.Pass, hex, $"back-pointer match {hits}/{samples.Count}"));
                return;
            }
        }
        items.Add(new("staff.jobPersonAnchor", DiscoveryStatus.Manual, Hex((uint)so.JobPersonAnchor),
            $"No candidate anchor matched >=95% of {samples.Count} staff job back-pointers"));
    }

    static void ProbeCallUpContainerNation(
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.CallUps is not { } cu || cu.ContainerVtable is not { } cvt || profile.Nation is null) return;
        ulong begin = ReadU64Safe(memory, moduleBase + cu.ListRva);
        ulong end = ReadU64Safe(memory, moduleBase + cu.ListRva + 8);
        if (begin < 0x1_0000_0000UL || end <= begin || (end - begin) / 8 > 200_000) return;

        ulong teamVt = moduleBase + cu.Vtable;
        ulong contVt = moduleBase + cvt;
        ulong nationVt = moduleBase + profile.Nation.VtableRva;
        var containers = new List<ulong>();
        var buf = memory.Read(begin, (int)Math.Min(end - begin, 8 * 8000));
        for (int i = 0; i + 8 <= buf.Length && containers.Count < 24; i += 8)
        {
            ulong t = BitConverter.ToUInt64(buf, i);
            if (t == 0 || ReadU64Safe(memory, t) != teamVt) continue;
            ulong n = ReadU64Safe(memory, t + (ulong)cu.Nation);
            if (n != 0 && ReadU64Safe(memory, n) == contVt) containers.Add(n);
        }
        if (containers.Count == 0) return;

        var cands = new List<int>();
        if (cu.ContainerNation is { } cur) cands.Add(cur);
        for (int off = 0x100; off <= 0x240; off += 8) if (!cands.Contains(off)) cands.Add(off);
        foreach (int off in cands)
        {
            int hits = 0;
            foreach (ulong c in containers)
            {
                ulong np = ReadU64Safe(memory, c + (ulong)off);
                if (np != 0 && ReadU64Safe(memory, np) == nationVt) hits++;
            }
            if (hits * 2 >= containers.Count && hits > 0)
            {
                string hex = Hex((uint)off);
                ProfileJsonResolver.SetByPath(working, "player.callUps.containerNation", hex);
                RecordStructuralIfDifferent(rawBase, "player.callUps.containerNation", hex, structuralOverrides);
                items.Add(new("player.callUps.containerNation", DiscoveryStatus.Pass, hex, $"matched {hits}/{containers.Count} nation containers"));
                return;
            }
        }
        items.Add(new("player.callUps.containerNation", DiscoveryStatus.Manual, "NOT_FOUND",
            $"No offset in 0x100..0x240 of {containers.Count} national-team containers points at a nation"));
    }

    static void ProbePeCodeSignatures(
        ModuleImage image,
        IProcessMemory memory,
        ulong moduleBase,
        OffsetProfile profile,
        JsonObject working,
        JsonObject rawBase,
        List<string> discoveredRootPaths,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        if (profile.IsArm64) return;
        if (!TryReadPrimaryPeCodeSection(image.Bytes, memory, moduleBase, out uint codeRva, out byte[] code))
            return;

        if (TryFindStaffAllowanceOffsetsInCode(code, codeRva, out int humanMscc, out int msccExtraStaff, out uint allowCodeRva))
        {
            string tmHex = "0x80";
            string msccHex = Hex((uint)humanMscc);
            string mestHex = Hex((uint)msccExtraStaff);
            ProfileJsonResolver.SetByPath(working, "staff.allowances.teamManager", tmHex);
            ProfileJsonResolver.SetByPath(working, "staff.allowances.humanMscc", msccHex);
            ProfileJsonResolver.SetByPath(working, "staff.allowances.msccExtraStaff", mestHex);
            RecordStructuralIfDifferent(rawBase, "staff.allowances.teamManager", tmHex, structuralOverrides);
            RecordStructuralIfDifferent(rawBase, "staff.allowances.humanMscc", msccHex, structuralOverrides);
            RecordStructuralIfDifferent(rawBase, "staff.allowances.msccExtraStaff", mestHex, structuralOverrides);
            items.Add(new("staff.allowances", DiscoveryStatus.Pass, $"mscc={msccHex}, extra={mestHex}",
                $"unique code pair at RVA {Hex(allowCodeRva)}"));
        }
        else
        {
            items.Add(new("staff.allowances", DiscoveryStatus.Warn, "NOT_FOUND",
                "Could not uniquely match staff allowance clamp sequence in executable section"));
        }

        if (TryFindForceBoardSitesInCode(code, codeRva, out uint gate1Rva, out byte[] gate1Orig, out uint gate2Rva, out byte[] gate2Orig))
        {
            string g1Hex = Hex(gate1Rva);
            string g1OrigHex = string.Join(' ', gate1Orig.Select(b => $"{b:X2}"));
            string g2Hex = Hex(gate2Rva);
            string g2OrigHex = string.Join(' ', gate2Orig.Select(b => $"{b:X2}"));
            ProfileJsonResolver.SetByPath(working, "forceAccept.boardRequestsRva", g1Hex);
            ProfileJsonResolver.SetByPath(working, "forceAccept.boardRequestsOriginal", g1OrigHex);
            ProfileJsonResolver.SetByPath(working, "forceAccept.clubVisionRva", g2Hex);
            ProfileJsonResolver.SetByPath(working, "forceAccept.clubVisionOriginal", g2OrigHex);
            if (!discoveredRootPaths.Contains("forceAccept.boardRequestsRva")) discoveredRootPaths.Add("forceAccept.boardRequestsRva");
            if (!discoveredRootPaths.Contains("forceAccept.boardRequestsOriginal")) discoveredRootPaths.Add("forceAccept.boardRequestsOriginal");
            if (!discoveredRootPaths.Contains("forceAccept.clubVisionRva")) discoveredRootPaths.Add("forceAccept.clubVisionRva");
            if (!discoveredRootPaths.Contains("forceAccept.clubVisionOriginal")) discoveredRootPaths.Add("forceAccept.clubVisionOriginal");
            items.Add(new("forceAccept.boardRequests", DiscoveryStatus.Pass, $"{g1Hex} / {g2Hex}",
                $"gate1={g1OrigHex}, gate2={g2OrigHex}"));
        }
        else
        {
            items.Add(new("forceAccept.boardRequests", DiscoveryStatus.Warn, "NOT_FOUND",
                "Could not uniquely match both Force Board Request AOB sites in executable section"));
        }
    }

    /// <summary>
    /// Force Board Request gates on macOS: the conditional branch that turns down a board request and the one that turns down
    /// a club vision change. Steam macOS 27.1.0+2505117 (0x2766368, 0x1AB0744) and FM Touch 27.1.0+2505719 (0x284B124,
    /// 0x1B081F4) carry the same words at both sites; the branch target is masked. Each pattern matches once in each build.
    /// A site already patched by the editor does not match; the probe then asks for the switch to be turned off.
    /// </summary>
    static readonly (string Name, Arm64Word[] Pattern)[] Arm64ForceBoardSites =
    [
        ("boardRequests", [new(0x540005A1, 0xFF00001F), new(0xB90047F9), new(0x7940FA88)]), // b.ne; str w25,[sp,#0x44]; ldrh w8,[x20,#0x7c]
        ("clubVision", [new(0x540004A4, 0xFF00001F), new(0x52800088), new(0x39019E68)]),    // b.mi; mov w8,#4; strb w8,[x19,#0x67]
    ];

    static void ProbeArm64CodeSignatures(
        IProcessMemory memory,
        ulong moduleBase,
        ulong moduleSize,
        OffsetProfile profile,
        JsonObject working,
        List<string> discoveredRootPaths,
        List<DiscoveryItem> items)
    {
        if (!profile.IsArm64) return;
        var snapshot = Arm64SiteFinder.TryCaptureCode(memory, moduleBase, moduleSize);
        var found = new List<(string Name, string Rva, string Original)>();
        foreach (var (name, pattern) in Arm64ForceBoardSites)
        {
            try
            {
                ulong at = Arm64SiteFinder.Find(memory, moduleBase, moduleSize, $"forceAccept.{name}", pattern, snapshot: snapshot);
                var original = memory.Read(at, 4);
                found.Add((name, Hex((uint)(at - moduleBase)), string.Join(' ', original.Select(b => $"{b:X2}"))));
            }
            catch (InvalidOperationException ex)
            {
                items.Add(new("forceAccept.boardRequests", DiscoveryStatus.Warn, "NOT_FOUND", ex.Message,
                    "turn Force Board Request off in the editor, restart the game if it was on, and run newbuild again"));
                return;
            }
        }
        foreach (var (name, rva, original) in found)
        {
            ProfileJsonResolver.SetByPath(working, $"forceAccept.{name}Rva", rva);
            ProfileJsonResolver.SetByPath(working, $"forceAccept.{name}Original", original);
            foreach (string key in new[] { $"forceAccept.{name}Rva", $"forceAccept.{name}Original" })
                if (!discoveredRootPaths.Contains(key)) discoveredRootPaths.Add(key);
        }
        items.Add(new("forceAccept.boardRequests", DiscoveryStatus.Pass, $"{found[0].Rva} / {found[1].Rva}",
            $"gate1={found[0].Original}, gate2={found[1].Original}"));
    }

    static void ProbeVectorAllocator(
        ModuleImage image,
        IProcessMemory memory,
        OffsetProfile profile,
        JsonObject working,
        List<string> discoveredRootPaths,
        List<DiscoveryItem> items)
    {
        if (profile.VectorAllocator is not { } layout) return;
        var hits = RootFinder.FindVectorAllocator(image, memory, layout);
        if (hits.Count == 0)
        {
            items.Add(new("vectorAllocator.instanceRva", DiscoveryStatus.Manual, "NOT_FOUND",
                "No inline vector allocator matched in writable module data"));
            return;
        }
        string rvaHex = Hex(hits[0].Rva);
        ProfileJsonResolver.SetByPath(working, "vectorAllocator.instanceRva", JsonValue.Create(rvaHex));
        if (!discoveredRootPaths.Contains("vectorAllocator.instanceRva", StringComparer.Ordinal))
            discoveredRootPaths.Add("vectorAllocator.instanceRva");
        items.Add(new("vectorAllocator.instanceRva", hits.Count == 1 ? DiscoveryStatus.Pass : DiscoveryStatus.Warn,
            rvaHex, $"inline allocator ({hits[0].PoolCount} pools, {hits.Count} candidate(s))"));
    }

    public static bool TryReadPrimaryPeCodeSection(ReadOnlySpan<byte> header, IProcessMemory memory, ulong moduleBase, out uint codeRva, out byte[] code)
    {
        codeRva = 0;
        code = [];
        if (header.Length < 0x400 || header[0] != 'M' || header[1] != 'Z') return false;
        int peOff = BitConverter.ToInt32(header[0x3C..]);
        if (peOff <= 0 || peOff + 24 > header.Length) return false;
        if (BitConverter.ToUInt32(header[peOff..]) != 0x00004550u) return false;
        int numSections = BitConverter.ToUInt16(header[(peOff + 6)..]);
        int optSize = BitConverter.ToUInt16(header[(peOff + 20)..]);
        int secTable = peOff + 24 + optSize;
        if (secTable + numSections * 40 > header.Length) return false;

        for (int i = 0; i < numSections; i++)
        {
            int s = secTable + i * 40;
            uint vsize = BitConverter.ToUInt32(header[(s + 8)..]);
            uint vrva = BitConverter.ToUInt32(header[(s + 12)..]);
            uint chars = BitConverter.ToUInt32(header[(s + 36)..]);
            // First executable section (typically .text or .idata at RVA 0x1000, ~70-90 MB, before .udata/.rdata).
            if ((chars & 0x20000000u) != 0 && vrva == 0x1000 && vsize > 0x100000 && vsize <= 128 * 1024 * 1024)
            {
                codeRva = vrva;
                code = new byte[vsize];
                const int chunk = 1 << 20;
                for (int off = 0; off < code.Length; off += chunk)
                {
                    int n = Math.Min(chunk, code.Length - off);
                    memory.TryRead(moduleBase + codeRva + (ulong)off, code.AsSpan(off, n));
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Locates the staff-allowance calculation sequence on x86-64:
    /// a pointer load <c>mov reg, [reg + humanMscc]</c> (<c>(48|49|4C|4D) 8B &lt;disp32 modrm&gt;</c>)
    /// followed closely by an indexed byte load <c>[r64+r64+msccExtraStaff]</c> (<c>movzx</c> <c>0F B6</c> or <c>mov</c> <c>8A</c>)
    /// and a clamp against <c>10</c> (<c>cmp reg8, 0x0A</c>), paired immediately after by Physios with the same
    /// <c>humanMscc</c> and <c>msccExtraStaff + 3</c>. Handles both Steam and Game Pass register encodings.
    /// </summary>
    public static bool TryFindStaffAllowanceOffsetsInCode(ReadOnlySpan<byte> code, uint codeBaseRva, out int humanMscc, out int msccExtraStaff, out uint matchedRva)
    {
        humanMscc = 0;
        msccExtraStaff = 0;
        matchedRva = 0;
        var candidates = new List<(int Offset, int Mscc, int Mest)>();

        for (int i = 0; i + 32 <= code.Length; i++)
        {
            if (code[i] is not (0x48 or 0x49 or 0x4C or 0x4D)) continue;
            if (code[i + 1] != 0x8B) continue;
            byte modrm = code[i + 2];
            if (modrm < 0x80 || modrm > 0xBF || (modrm & 7) == 4) continue;

            int mscc = BitConverter.ToInt32(code[(i + 3)..]);
            if (mscc is < 0x100 or > 0x1000) continue;

            int mest = 0;
            int afterLoad = -1;
            int scanEnd = Math.Min(code.Length - 8, i + 16);
            for (int k = i + 7; k < scanEnd; k++)
            {
                int p = (code[k] is >= 0x40 and <= 0x4F) ? k + 1 : k;
                if (p + 7 < code.Length && code[p] == 0x0F && code[p + 1] == 0xB6 && (code[p + 2] & 0xC7) == 0x84)
                {
                    int candidateMest = BitConverter.ToInt32(code[(p + 4)..]);
                    if (candidateMest is >= 0x400 and <= 0x4000)
                    {
                        mest = candidateMest;
                        afterLoad = p + 8;
                        break;
                    }
                }
                if (p + 6 < code.Length && code[p] == 0x8A && (code[p + 1] & 0xC7) == 0x84)
                {
                    int candidateMest = BitConverter.ToInt32(code[(p + 3)..]);
                    if (candidateMest is >= 0x400 and <= 0x4000)
                    {
                        mest = candidateMest;
                        afterLoad = p + 7;
                        break;
                    }
                }
            }
            if (afterLoad < 0) continue;

            bool hasClamp10 = false;
            int lim = Math.Min(code.Length - 3, afterLoad + 36);
            for (int j = afterLoad; j <= lim; j++)
            {
                if (code[j] == 0x80 && code[j + 1] is >= 0xF8 and <= 0xFF && code[j + 2] == 0x0A)
                {
                    hasClamp10 = true;
                    break;
                }
            }
            if (hasClamp10)
                candidates.Add((i, mscc, mest));
        }

        var pairs = new List<(int Offset, int Mscc, int Mest)>();
        for (int a = 0; a < candidates.Count; a++)
        {
            for (int b = a + 1; b < candidates.Count; b++)
            {
                int dist = candidates[b].Offset - candidates[a].Offset;
                if (dist > 0x140) break;
                if (candidates[a].Mscc == candidates[b].Mscc && candidates[b].Mest == candidates[a].Mest + 3)
                    pairs.Add(candidates[a]);
            }
        }

        if (pairs.Count != 1) return false;
        humanMscc = pairs[0].Mscc;
        msccExtraStaff = pairs[0].Mest;
        matchedRva = codeBaseRva + (uint)pairs[0].Offset;
        return true;
    }

    /// <summary>
    /// Locates the two x86-64 conditional jump sites patched by the Force Board Request preset:
    /// Gate 1 (<c>80 ?? ?? 00 00 00 01 48 89 ?? ?? 03 00 00 0F 85 ?? ?? 00 00</c>, 6-byte <c>jne</c> at +14) and
    /// Gate 2 (<c>0F BF 46 62 0F B6 4E 6B 01 C1 78 ?? C6 46 67 04</c>, 2-byte <c>js</c> at +10).
    /// Succeeds only when both AOBs match uniquely across the executable section.
    /// </summary>
    public static bool TryFindForceBoardSitesInCode(
        ReadOnlySpan<byte> code,
        uint codeBaseRva,
        out uint boardRequestsRva,
        out byte[] boardRequestsOriginal,
        out uint clubVisionRva,
        out byte[] clubVisionOriginal)
    {
        boardRequestsRva = 0;
        boardRequestsOriginal = [];
        clubVisionRva = 0;
        clubVisionOriginal = [];

        int gate1Hit = -1;
        int gate1Count = 0;
        int gate2Hit = -1;
        int gate2Count = 0;

        for (int i = 0; i + 20 <= code.Length; i++)
        {
            // Gate 1: 80 ?? ?? 00 00 00 01 48 89 ?? ?? 03 00 00 0F 85 ?? ?? 00 00
            if (code[i] == 0x80
                && code[i + 3] == 0x00 && code[i + 4] == 0x00 && code[i + 5] == 0x00 && code[i + 6] == 0x01
                && code[i + 7] == 0x48 && code[i + 8] == 0x89
                && code[i + 11] == 0x03 && code[i + 12] == 0x00 && code[i + 13] == 0x00
                && code[i + 14] == 0x0F && code[i + 15] == 0x85
                && code[i + 18] == 0x00 && code[i + 19] == 0x00)
            {
                gate1Hit = i + 14;
                gate1Count++;
            }

            // Gate 2: 0F BF 46 62 0F B6 4E 6B 01 C1 78 ?? C6 46 67 04
            if (code[i] == 0x0F && code[i + 1] == 0xBF && code[i + 2] == 0x46 && code[i + 3] == 0x62
                && code[i + 4] == 0x0F && code[i + 5] == 0xB6 && code[i + 6] == 0x4E && code[i + 7] == 0x6B
                && code[i + 8] == 0x01 && code[i + 9] == 0xC1
                && code[i + 10] == 0x78
                && code[i + 12] == 0xC6 && code[i + 13] == 0x46 && code[i + 14] == 0x67 && code[i + 15] == 0x04)
            {
                gate2Hit = i + 10;
                gate2Count++;
            }
        }

        if (gate1Count != 1 || gate2Count != 1)
            return false;

        boardRequestsRva = codeBaseRva + (uint)gate1Hit;
        boardRequestsOriginal = code.Slice(gate1Hit, 6).ToArray();
        clubVisionRva = codeBaseRva + (uint)gate2Hit;
        clubVisionOriginal = code.Slice(gate2Hit, 2).ToArray();
        return true;
    }

    static void SetWorkingPath(JsonObject working, string path, string value, List<string> pathLog, HashSet<string> touchedArrayRoots)
    {
        ProfileJsonResolver.SetByPath(working, path, JsonValue.Create(value));
        if (!pathLog.Contains(path, StringComparer.Ordinal)) pathLog.Add(path);
        int bracket = path.IndexOf('[');
        if (bracket > 0) touchedArrayRoots.Add(path[..bracket]);
    }

    static void RecordStructuralIfDifferent(JsonObject rawBase, string path, string value, HashSet<string> structuralOverrides)
    {
        var baseNode = ProfileJsonResolver.GetByPath(rawBase, path);
        string? baseStr = baseNode is JsonValue bv && bv.GetValueKind() == JsonValueKind.String ? bv.GetValue<string>() : null;
        if (!string.Equals(baseStr, value, StringComparison.OrdinalIgnoreCase))
            structuralOverrides.Add(path);
    }

    static JsonObject BuildCompactProfile(
        string layoutRef,
        JsonObject working,
        string rawBuild,
        string buildName,
        uint buildRva,
        string moduleName,
        bool isGamePass,
        string layoutShortName,
        GameProcess game,
        ModuleImage image,
        List<string> discoveredVtablePaths,
        List<string> discoveredRootPaths,
        HashSet<string> structuralOverrides,
        HashSet<string> touchedArrayRoots)
    {
        var compact = new JsonObject
        {
            ["extends"] = layoutRef,
            ["build"] = buildName,
            ["module"] = moduleName,
            ["buildStringRva"] = Hex(buildRva),
        };
        if (layoutShortName is "macos" or "touch-macos")
        {
            compact["matchBuild"] = rawBuild;
        }
        else if (isGamePass)
        {
            compact["matchBuild"] = rawBuild;
            compact["variant"] = "gamepass";
        }
        if (working["architecture"] is { } arch) compact["architecture"] = arch.DeepClone();
        compact["imageSize"] = Hex((uint)game.ModuleSize);
        if (image.PeTimestamp is { } ts) compact["peTimestamp"] = Hex(ts);
        compact["readOnly"] = true;

        foreach (var path in discoveredVtablePaths.Concat(discoveredRootPaths).Concat(structuralOverrides))
        {
            if (path.Contains('[')) continue;
            if (ProfileJsonResolver.GetByPath(working, path) is { } val)
                ProfileJsonResolver.SetByPath(compact, path, val.DeepClone());
        }
        foreach (var arrRoot in touchedArrayRoots.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (ProfileJsonResolver.GetByPath(working, arrRoot) is { } arr)
                ProfileJsonResolver.SetByPath(compact, arrRoot, arr.DeepClone());
        }
        return compact;
    }

    static void NormalizeGameUiTuple(
        JsonObject working,
        JsonObject rawBase,
        List<string> discoveredVtablePaths,
        List<string> discoveredRootPaths,
        HashSet<string> structuralOverrides,
        List<DiscoveryItem> items)
    {
        bool hasRva = working["gameUiRva"] is JsonValue rv && rv.GetValueKind() == JsonValueKind.String;
        bool hasVt = working["gameUiVtable"] is JsonValue vv && vv.GetValueKind() == JsonValueKind.String;
        bool hasLoads = working["gameUiLoadsOffset"] is JsonValue lv && lv.GetValueKind() == JsonValueKind.String;
        if (hasRva && hasVt)
        {
            if (!hasLoads)
            {
                working["gameUiLoadsOffset"] = "0x100";
                RecordStructuralIfDifferent(rawBase, "gameUiLoadsOffset", "0x100", structuralOverrides);
                items.Add(new("gameUiLoadsOffset", DiscoveryStatus.Warn, "0x100", "defaulted; verify via --crash-gate-verify"));
            }
        }
        else
        {
            working.Remove("gameUiRva");
            working.Remove("gameUiVtable");
            working.Remove("gameUiLoadsOffset");
            discoveredRootPaths.Remove("gameUiRva");
            discoveredVtablePaths.Remove("gameUiVtable");
        }
    }

    static bool NormalizeOptionalSections(
        JsonObject working,
        List<string> discoveredVtablePaths,
        List<string> discoveredRootPaths)
    {
        bool seededCrossCheckDate = false;
        if (working["gameDate"] is JsonObject gd && gd["crossCheck"] is JsonObject cc)
        {
            if (cc["rootRva"] is not JsonValue rv || rv.GetValueKind() != JsonValueKind.String)
            {
                gd.Remove("crossCheck");
                discoveredVtablePaths.Remove("gameDate.crossCheck.objectVtable");
                discoveredRootPaths.Remove("gameDate.crossCheck.rootRva");
            }
            else if (cc["dateOffset"] is not JsonValue dv || dv.GetValueKind() != JsonValueKind.String)
            {
                cc["dateOffset"] = "0x7FC";
                seededCrossCheckDate = true;
            }
        }

        if (working["officials"] is JsonObject off)
        {
            bool hasRoot = off["listRootRva"] is JsonValue lr && lr.GetValueKind() == JsonValueKind.String;
            bool hasFirst = off["firstName"] is JsonValue fn && fn.GetValueKind() == JsonValueKind.String;
            if (!hasRoot || !hasFirst)
            {
                working.Remove("officials");
                discoveredVtablePaths.Remove("officials.vtable");
                discoveredRootPaths.Remove("officials.listRootRva");
            }
        }
        return seededCrossCheckDate;
    }

    static bool SeedCallUpContainerNationIfMissing(JsonObject working)
    {
        if (working["player"]?["callUps"] is not JsonObject cu) return false;
        if (cu["containerVtable"] is JsonValue && cu["containerNation"] is null)
        {
            cu["containerNation"] = "0x170";
            return true;
        }
        return false;
    }

    static string Hex(uint value) => $"0x{value:X}";

    static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        return s is not null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    static ulong ReadU64Safe(IProcessMemory memory, ulong address)
    {
        Span<byte> buf = stackalloc byte[8];
        return address != 0 && memory.TryRead(address, buf) ? BitConverter.ToUInt64(buf) : 0;
    }

    static string ToIndentedJson(JsonObject obj)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriteOptions)) obj.WriteTo(w);
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
