using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;

/// <summary><c>rootcompare &lt;rva&gt;[+off] ...</c> (read only): what each static (and an offset into its object) leads to, so two
/// discovered paths can be checked for reaching the same object or vector.</summary>
static class RootCompare
{
    public static int Run(string[] specs)
    {
        var game = GameProcess.Locate();
        using var memory = new WindowsProcessMemory(game.ProcessId, false);
        foreach (string spec in specs)
        {
            if (spec.EndsWith('v'))
            {
                ulong h = game.ModuleBase + Convert.ToUInt64(spec[..^1], 16);
                ulong vb = memory.ReadUInt64(h), ve = memory.ReadUInt64(h + 8);
                ulong f0 = ve > vb ? memory.ReadUInt64(vb) : 0;
                Console.WriteLine($"{spec,-22} vector at module: begin 0x{vb:X} count {(ve - vb) / 8}; first item 0x{f0:X} vtable +0x{(f0 == 0 ? 0 : memory.ReadUInt64(f0) - game.ModuleBase):X}");
                continue;
            }
            var parts = spec.Split('+');
            ulong rva = Convert.ToUInt64(parts[0], 16);
            ulong obj = memory.ReadUInt64(game.ModuleBase + rva);
            string line = $"{spec,-22} -> object 0x{obj:X} (vtable +0x{memory.ReadUInt64(obj) - game.ModuleBase:X})";
            if (parts.Length > 1)
            {
                ulong at = obj + Convert.ToUInt64(parts[1], 16);
                ulong b = memory.ReadUInt64(at), e = memory.ReadUInt64(at + 8);
                ulong first = e > b ? memory.ReadUInt64(b) : 0;
                line += $"; vector begin 0x{b:X} count {(e - b) / 8}; first item vtable +0x{(first == 0 ? 0 : memory.ReadUInt64(first) - game.ModuleBase):X}";
            }
            Console.WriteLine(line);
        }
        return 0;
    }
}

/// <summary><c>vtname &lt;rva&gt;...</c> (read only): the RTTI class of each vtable RVA in the running game's module.</summary>
static class VtName
{
    public static int Run(string[] rvas)
    {
        var game = GameProcess.LocateAll(OperatingSystem.IsMacOS() ? "game_plugin" : GameProcess.DefaultModule)[0];
        IProcessMemory memory = OperatingSystem.IsMacOS()
            ? MacMemory.Open(game.ProcessId, writable: false)
            : new WindowsProcessMemory(game.ProcessId, writable: false);
        using var owned = memory as IDisposable;
        var rtti = Fm27Editor.Core.Offsets.RttiIndex.Build(Fm27Editor.Core.Offsets.ModuleImage.ReadForDiscovery(memory, game.ModuleBase, game.ModuleSize));
        foreach (string s in rvas)
        {
            uint rva = Convert.ToUInt32(s, 16);
            Console.WriteLine($"0x{rva:X}  {rtti.CanonicalNameOf(rva) ?? "(no RTTI)"}");
        }
        return 0;
    }
}
