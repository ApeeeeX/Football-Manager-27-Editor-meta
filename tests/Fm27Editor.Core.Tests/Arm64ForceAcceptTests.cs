using System.Text.Json.Nodes;
using Fm27Editor.Core.ForceAccept;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public class Arm64ForceAcceptTests
{
    const ulong Module = 0x1_0000_0000;

    static OffsetProfile Profile(bool offsets)
    {
        var json = JsonNode.Parse(File.ReadAllText(TestWorld.ProfilePath))!;
        json["architecture"] = "arm64";
        json.AsObject().Remove("forceAccept");
        if (offsets)
            json["forceAccept"] = JsonNode.Parse("""
                {"boardRequestsRva":"0x1000","clubVisionRva":"0x1400",
                 "contractOffersRva":"0x1800","transferOffersRva":"0x1C00"}
                """);
        return OffsetProfile.Load(json.ToJsonString());
    }

    [Theory]
    [InlineData(ForceAcceptPreset.BoardRequests)]
    [InlineData(ForceAcceptPreset.ContractOffers)]
    [InlineData(ForceAcceptPreset.TransferOffers)]
    public void Unmeasured_arm64_presets_never_read_memory_or_offer_patch_bytes(ForceAcceptPreset preset)
    {
        var plan = new ForceAcceptPlanner(Profile(offsets: false), new NoAccessMemory(), Module).Plan(preset);

        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        Assert.Empty(plan.Pending);
        Assert.All(plan.Sites, site =>
        {
            Assert.Equal(preset == ForceAcceptPreset.ContractOffers
                ? ForceAcceptSiteState.NotImplemented : ForceAcceptSiteState.MissingOffset, site.State);
            Assert.Null(site.Rva);
            Assert.Null(site.Address);
            Assert.Null(site.Current);
            Assert.Empty(site.Original);
            Assert.Empty(site.Replacement);
        });
    }

    [Theory]
    [InlineData(ForceAcceptPreset.BoardRequests)]
    [InlineData(ForceAcceptPreset.ContractOffers)]
    [InlineData(ForceAcceptPreset.TransferOffers)]
    public void An_rva_cannot_enable_an_unimplemented_arm64_preset(ForceAcceptPreset preset)
    {
        var planner = new ForceAcceptPlanner(Profile(offsets: true), new NoAccessMemory(), Module);
        var plan = planner.Plan(preset);

        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        Assert.Empty(plan.Pending);
        Assert.All(plan.Sites, site =>
        {
            Assert.Equal(ForceAcceptSiteState.NotImplemented, site.State);
            Assert.Equal(Module + site.Rva!.Value, site.Address);
            Assert.Null(site.Current);
            Assert.Empty(site.Original);
            Assert.Empty(site.Replacement);
        });
        Assert.Throws<InvalidOperationException>(() => planner.Apply(plan, gamePaused: true));
        Assert.Throws<InvalidOperationException>(() => planner.Restore(plan, gamePaused: true));
    }

    [Theory]
    [InlineData("27.0.1+2503222-mac.json")]
    [InlineData("27.0.0+2492889-touch-mac.json")]
    public void Mac_profiles_cannot_plan_the_old_libcurl_overwrite(string file)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", file));
        var plan = new ForceAcceptPlanner(profile, new NoAccessMemory(), Module).Plan(ForceAcceptPreset.TransferOffers);

        // Both former candidates load a byte at +0x800 in libcurl's hsts_pull, not an offer's status.
        // An RVA in an older profile must never make that instruction appliable again.
        Assert.Equal(ForceAcceptPlanState.Unavailable, plan.State);
        Assert.Empty(plan.Pending);
        Assert.Null(Assert.Single(plan.Sites).Current);
    }

    [Fact]
    public void Arm64_writers_refuse_even_a_plan_created_by_the_x64_planner()
    {
        var json = JsonNode.Parse(Profile(offsets: true).ToJson())!;
        json.AsObject().Remove("architecture");
        var memory = new FakeMemory();
        byte[] original = [0x44, 0x0F, 0xB6, 0xAF, 0, 8, 0, 0];
        original.CopyTo(memory.Map(Module + 0x1C00, original.Length), 0);
        var x64 = new ForceAcceptPlanner(OffsetProfile.Load(json.ToJsonString()), memory, Module);
        var ready = x64.Plan(ForceAcceptPreset.TransferOffers);
        var arm64 = new ForceAcceptPlanner(Profile(offsets: true), new NoAccessMemory(), Module);

        Assert.Throws<InvalidOperationException>(() => arm64.Apply(ready, gamePaused: true));
        x64.Apply(ready, gamePaused: true);
        var active = x64.Plan(ForceAcceptPreset.TransferOffers);
        Assert.Throws<InvalidOperationException>(() => arm64.Restore(active, gamePaused: true));
    }

    [Fact]
    public void Measured_arm64_board_gates_are_replaced_by_nop_and_restored()
    {
        // Original branches read from the live Steam macOS 27.1.0+2505117 game: b.ne at the template verdict and
        // b.mi at the club-vision check.
        byte[] gate1 = [0xA1, 0x05, 0x00, 0x54], gate2 = [0xA4, 0x04, 0x00, 0x54], nop = [0x1F, 0x20, 0x03, 0xD5];
        var json = JsonNode.Parse(File.ReadAllText(TestWorld.ProfilePath))!;
        json["architecture"] = "arm64";
        json["forceAccept"] = JsonNode.Parse("""
            {"boardRequestsRva":"0x1000","boardRequestsOriginal":"A1 05 00 54",
             "clubVisionRva":"0x1400","clubVisionOriginal":"A4 04 00 54"}
            """);
        var memory = new FakeMemory();
        gate1.CopyTo(memory.Map(Module + 0x1000, 4), 0);
        gate2.CopyTo(memory.Map(Module + 0x1400, 4), 0);
        var planner = new ForceAcceptPlanner(OffsetProfile.Load(json.ToJsonString()), memory, Module);

        var plan = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Appliable, plan.State);
        Assert.Equal(2, planner.Apply(plan, gamePaused: true));
        var read = new byte[4];
        memory.TryRead(Module + 0x1000, read); Assert.Equal(nop, read);
        memory.TryRead(Module + 0x1400, read); Assert.Equal(nop, read);

        var active = planner.Plan(ForceAcceptPreset.BoardRequests);
        Assert.Equal(ForceAcceptPlanState.Active, active.State);
        planner.Restore(active, gamePaused: true);
        memory.TryRead(Module + 0x1000, read); Assert.Equal(gate1, read);
        memory.TryRead(Module + 0x1400, read); Assert.Equal(gate2, read);
    }

    sealed class NoAccessMemory : IProcessMemory
    {
        public bool TryRead(ulong address, Span<byte> buffer) =>
            throw new Xunit.Sdk.XunitException("An unimplemented preset must not read the candidate site");

        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) =>
            throw new Xunit.Sdk.XunitException("An unimplemented preset must not write memory");
    }
}
