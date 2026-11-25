using System.Runtime.CompilerServices;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

/// <summary>Derived values belong to a loaded immutable snapshot, never to a live memory address.</summary>
internal static class ScoutingMetrics
{
    sealed class Entry(PlayerRecord player)
    {
        int rca;
        bool hasRca;
        double mca;
        bool hasMca;
        RoleScore bestRole = default!;
        bool hasBestRole;

        public int Rca
        {
            get
            {
                if (Volatile.Read(ref hasRca)) return rca;
                lock (this)
                {
                    if (!hasRca) { rca = RcaCalculator.Calculate(player); Volatile.Write(ref hasRca, true); }
                    return rca;
                }
            }
        }

        public RoleScore BestRole
        {
            get
            {
                if (Volatile.Read(ref hasBestRole)) return bestRole;
                lock (this)
                {
                    if (!hasBestRole) { bestRole = RoleScores.BestFitRole(player); Volatile.Write(ref hasBestRole, true); }
                    return bestRole;
                }
            }
        }

        public double Mca
        {
            get
            {
                if (Volatile.Read(ref hasMca)) return mca;
                lock (this)
                {
                    if (!hasMca) { mca = McaCalculator.Calculate(player); Volatile.Write(ref hasMca, true); }
                    return mca;
                }
            }
        }

        public DateOnly? Date;
        public int? Floor;
        public PersonalityClassification? Personality;
    }
    static ConditionalWeakTable<PlayerRecord, Entry> entries = new();
    static Entry Get(PlayerRecord player) => entries.GetValue(player, p => new(p));
    public static int Rca(PlayerRecord player) => Get(player).Rca;
    public static double Mca(PlayerRecord player) => Get(player).Mca;
    public static RoleScore BestRole(PlayerRecord player) => Get(player).BestRole;
    public static PersonalityClassification Personality(PlayerRecord player)
    {
        var e = Get(player);
        lock (e)
        {
            if (e.Personality is null || e.Date != player.GameDate || e.Floor != RegenIds.Current)
            { e.Personality = PersonalityClassifier.Classify(player); e.Date = player.GameDate; e.Floor = RegenIds.Current; }
            return e.Personality;
        }
    }
    public static void Reset() => entries = new();
    public static void Warm(IEnumerable<PlayerRecord> players) =>
        Parallel.ForEach(players, player => { _ = Get(player).Mca; _ = Personality(player); });
}
