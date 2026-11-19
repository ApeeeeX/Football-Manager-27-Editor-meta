using Fm27Editor.Core.Game;
using Fm27Editor.Core.Offsets;

/// <summary>ptrdump &lt;address&gt; [from] [to]: every pointer field of an object with the RTTI class of what it points at.</summary>
static class AllowProbe
{
    public static int Run(GameSession session, string[] args)
    {
        ulong address = Convert.ToUInt64(args[0].Replace("0x", ""), 16);
        int from = args.Length > 1 ? Convert.ToInt32(args[1].Replace("0x", ""), 16) : 0;
        int to = args.Length > 2 ? Convert.ToInt32(args[2].Replace("0x", ""), 16) : 0x800;
        var memory = session.Memory;
        var rtti = RttiIndex.Build(ModuleImage.ReadForDiscovery(memory, session.Game.ModuleBase, session.Game.ModuleSize));
        ulong Q(ulong a) { var b = new byte[8]; return memory.TryRead(a, b) ? BitConverter.ToUInt64(b) : 0; }
        string Name(ulong p)
        {
            ulong vt = Q(p);
            if (vt < session.Game.ModuleBase || vt >= session.Game.ModuleBase + (ulong)session.Game.ModuleSize) return "";
            return rtti.CanonicalNameOf((uint)(vt - session.Game.ModuleBase)) ?? $"vt+0x{vt - session.Game.ModuleBase:X}";
        }
        Console.WriteLine($"0x{address:X} {Name(address)} vt rva 0x{Q(address) - session.Game.ModuleBase:X}");
        if (args.Contains("--bytes"))
        {
            var raw = new byte[to - from];
            memory.TryRead(address + (ulong)from, raw);
            for (int i = 0; i < raw.Length; i += 16)
                Console.WriteLine($"  +0x{from + i:X4} {Convert.ToHexString(raw, i, Math.Min(16, raw.Length - i))}");
            return 0;
        }
        for (int off = from; off < to; off += 8)
        {
            ulong v = Q(address + (ulong)off);
            if (v < 0x100000000 || v > 0x7FFFFFFFFFFF) continue;
            Console.WriteLine($"  +0x{off:X3} -> 0x{v:X} {Name(v)}");
        }
        return 0;
    }

    /// <summary>codepatch &lt;rva&gt; [hex]: prints 16 bytes of game code at an RVA, and writes hex there if given.</summary>
    public static int Patch(GameSession session, string[] args)
    {
        ulong at = session.Game.ModuleBase + Convert.ToUInt64(args[0].Replace("0x", ""), 16);
        var memory = session.Memory;
        var before = new byte[16];
        memory.TryRead(at, before);
        Console.WriteLine($"before {Convert.ToHexString(before)}");
        if (args.Length < 2) return 0;
        var bytes = Convert.FromHexString(args[1]);
        var host = (Fm27Editor.Core.Presets.ICodePatchHost)memory;
        using (host.Pause())
        {
            uint previous = host.Protect(at, bytes.Length, 0x40);
            bool ok = memory.TryWrite(at, bytes);
            host.Protect(at, bytes.Length, previous);
            host.FlushCode(at, bytes.Length);
            Console.WriteLine(ok ? "written" : "write failed");
        }
        memory.TryRead(at, before);
        Console.WriteLine($"after  {Convert.ToHexString(before)}");
        return 0;
    }
}
