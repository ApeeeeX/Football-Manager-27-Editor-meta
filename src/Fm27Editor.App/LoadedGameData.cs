using System.Diagnostics;
using Fm27Editor.Core.Awards;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Competitions;
using Fm27Editor.Core.Diagnostics;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Officials;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Stadiums;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

/// <summary>
/// Lists read for one connection. These are the browse/search catalogue, never an authority for a write or an explicit
/// refresh. The backend drops the whole catalogue before replacing its session, including when the next attach fails.
/// </summary>
internal sealed class LoadedGameData(GameSession session)
{
    public GameSession Session { get; } = session;
    public IReadOnlyList<PlayerRecord> Players { get; private set; } = [];
    public IReadOnlyList<ClubEntry> Clubs { get; private set; } = [];
    public IReadOnlyList<NationRecord> Nations { get; private set; } = [];
    public IReadOnlyList<CompetitionRecord> Competitions { get; private set; } = [];
    public IReadOnlyList<StaffRecord> Staff { get; private set; } = [];
    public IReadOnlyList<StadiumRecord> Stadiums { get; private set; } = [];
    public IReadOnlyList<AwardRecord> Awards { get; private set; } = [];
    public IReadOnlyList<RefereeRecord> Referees { get; private set; } = [];
    public IReadOnlyList<CityRecord> Cities { get; private set; } = [];
    public IReadOnlyDictionary<CityLink, IReadOnlyList<CityLinkTarget>> CityLinkCatalogues { get; private set; } = new Dictionary<CityLink, IReadOnlyList<CityLinkTarget>>();
    public IReadOnlyList<AgreementTargetOption> AgreementOrganisations { get; private set; } = [];
    public DateOnly? GameDate { get; private set; }
    public bool DealIndexAttempted { get; private set; }
    public bool DealIndexLoaded { get; private set; }
    public Exception? DealIndexError { get; private set; }
    public bool ClauseIndexAttempted { get; private set; }
    IReadOnlyList<ulong> dealCandidates = [];

    // Only Load Game creates a new index. Tab reads and fee/status writes never trigger discovery.
    internal void LoadTransferDeals(GameLoadReport report, Func<IReadOnlyList<ulong>> scan)
    {
        if (DealIndexAttempted) return;
        DealIndexAttempted = true;
        report.Run("transfer deals", "Reading…", () =>
        {
            try { dealCandidates = scan(); DealIndexLoaded = true; }
            catch (Exception ex) { DealIndexError = ex; throw; }
        }, () => dealCandidates.Count);
    }

    public IReadOnlyList<ulong> TransferDealCandidates(GameSession current)
    {
        if (!ReferenceEquals(Session, current))
            throw new InvalidOperationException("The game connection changed while reading transfer deals");
        if (!DealIndexLoaded)
            throw new InvalidOperationException("Transfer deal index is unavailable. Load Game to refresh it.", DealIndexError);
        return dealCandidates;
    }

    public static LoadedGameData Read(GameSession session, GameLoadReport report)
    {
        var data = new LoadedGameData(session) { GameDate = session.ReadGameDate() };
        // Deals, agreement regions, and scanned transfer clauses otherwise read the same multi-gigabyte heap independently.
        bool needsRegions = session.Profile.AgreementRegionVtable is not null && session.Profile.Nation?.ListRootRva is not null;
        ulong regionVtable = needsRegions ? session.ModuleBase + session.Profile.AgreementRegionVtable!.Value : 0;
        var clauseVtables = session.TransferClauseVtableAddresses;
        bool needsClauseScan = clauseVtables.Count > 0;
        IReadOnlyList<ulong>? ownedDeals = null;
        bool ownedRegions = false;
        var index = new Lazy<IReadOnlyList<HeapVtableMatch>>(() =>
        {
            ownedDeals = session.TransferDeals.TryReadOwnedCandidates();
            ownedRegions = !needsRegions || session.NationAgreements.TryReadOwnedRegions() is not null;
            if (ownedDeals is not null && ownedRegions && !needsClauseScan)
            {
                FlightRecorder.Current?.Log("load", "owned indexes: transfer deals and agreement regions; heap scan avoided");
                return [];
            }
            var missing = new List<ulong>();
            if (ownedDeals is null) missing.AddRange(session.TransferDeals.VtableAddresses);
            if (!ownedRegions) missing.Add(regionVtable);
            if (needsClauseScan) missing.AddRange(clauseVtables);
            FlightRecorder.Current?.Log("load", $"owned indexes: fallback; deals {ownedDeals is null}; regions {!ownedRegions}; clauses {needsClauseScan}");
            return HeapVtableScanner.Scan(session.Memory, missing.ToArray());
        });
        // Concurrent memory providers (Windows) overlap heap discovery with catalogue reads. Serial providers (macOS IPC)
        // run discovery after catalogues so small reads never contend on the socket lock.
        Task<Exception?>? discovery = session.Memory is IConcurrentMemoryReads
            ? Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                try { _ = index.Value; return (Exception?)null; }
                catch (Exception ex) { return ex; }
                finally { FlightRecorder.Current?.Log("load", $"shared discovery elapsed: {watch.ElapsedMilliseconds} ms"); }
            })
            : null;
        Task? metricsTask = null;
        try
        {
            if (session.Memory is IConcurrentMemoryReads)
            {
                Task[] loaders =
                [
                    Task.Run(() =>
                    {
                        data.Players = report.Read("players", "Reading players…", () => session.Players.LoadAll());
                        foreach (var player in data.Players) player.GameDate = data.GameDate;
                        RegenIds.Current = RegenIds.FirstCreatedId(data.Players.Where(p => p.Id is not null).Select(p => p.Id!.Value));
                        metricsTask = Task.Run(() => report.Run("scouting metrics", "Reading scouting metrics…", () => ScoutingMetrics.Warm(data.Players)));
                        data.Clubs = report.Read("clubs", "Reading clubs…", () => session.LoadClubs(data.Players));
                    }),
                    Task.Run(() =>
                    {
                        if (session.Profile.Staff is not null)
                            data.Staff = report.Read("staff", "Reading staff…", () => session.Staff.LoadAll());
                        data.Nations = report.Read("nations", "Reading nations…", () => session.Nations.LoadAll());
                        if (session.Profile.Nation is not null)
                            data.AgreementOrganisations = report.Read("agreement organisations", "Reading nations…", () =>
                            {
                                using var reads = new BulkReadScope(session.Memory);
                                var agreements = new NationAgreements(reads.Memory, session.Profile, session.ModuleBase);
                                var continents = session.NationLinks.Continents(data.Nations);
                                var found = new Dictionary<ulong, AgreementTargetOption>();
                                foreach (var nation in data.Nations)
                                    foreach (int vector in new[] { NationAgreements.EuVector, NationAgreements.NonForeignVector })
                                        foreach (var row in agreements.Read(nation.Address, vector, continents, data.Nations))
                                            if (row.Kind == AgreementKind.Agreement && !string.IsNullOrWhiteSpace(row.TargetName))
                                                found.TryAdd(row.Target, new(row.Target, row.TargetName));
                                return found.Values.OrderBy(x => x.Name).ToArray();
                            });
                    }),
                    Task.Run(() =>
                    {
                        if (session.Profile.Competition is not null)
                            data.Competitions = report.Read("competitions", "Reading competitions…", () => session.Competitions.LoadAll());
                        if (session.Profile.Stadium is not null)
                            data.Stadiums = report.Read("stadiums", "Reading stadiums…", () => session.Stadiums.LoadAll());
                        if (session.Profile.Award is not null)
                            data.Awards = report.Read("awards", "Reading awards…", () => session.Awards.LoadAll());
                        if (Fm27Editor.Core.Officials.RefereeDirectory.Supported(session.Profile.Officials))
                            data.Referees = report.Read("referees", "Reading referees…", () => session.ReadReferees());
                    }),
                    Task.Run(() =>
                    {
                        data.Cities = report.Read("cities", "Reading cities…", () => session.Cities.LoadAll());
                        if (session.Profile.Cities?.Links is not null)
                        {
                            using var reads = new BulkReadScope(session.Memory);
                            var links = new CityLinks(reads.Memory, session.Profile, session.ModuleBase);
                            var catalogues = new Dictionary<CityLink, IReadOnlyList<CityLinkTarget>>();
                            foreach (var link in new[] { CityLink.Region, CityLink.Language, CityLink.Weather })
                                catalogues[link] = report.Read($"city {link.ToString().ToLowerInvariant()} catalogue", "Reading cities…", () => links.Catalogue(data.Cities, link));
                            data.CityLinkCatalogues = catalogues;
                        }
                    }),
                ];
                Task.WaitAll(loaders);
            }
            else
            {
                data.Players = report.Read("players", "Reading players…", () => session.Players.LoadAll());
                foreach (var player in data.Players) player.GameDate = data.GameDate;
                RegenIds.Current = RegenIds.FirstCreatedId(data.Players.Where(p => p.Id is not null).Select(p => p.Id!.Value));
                metricsTask = Task.Run(() => report.Run("scouting metrics", "Reading scouting metrics…", () => ScoutingMetrics.Warm(data.Players)));
                data.Clubs = report.Read("clubs", "Reading clubs…", () => session.LoadClubs(data.Players));
                data.Nations = report.Read("nations", "Reading nations…", () => session.Nations.LoadAll());
                if (session.Profile.Competition is not null)
                    data.Competitions = report.Read("competitions", "Reading competitions…", () => session.Competitions.LoadAll());
                if (session.Profile.Staff is not null)
                    data.Staff = report.Read("staff", "Reading staff…", () => session.Staff.LoadAll());
                if (session.Profile.Stadium is not null)
                    data.Stadiums = report.Read("stadiums", "Reading stadiums…", () => session.Stadiums.LoadAll());
                if (session.Profile.Award is not null)
                    data.Awards = report.Read("awards", "Reading awards…", () => session.Awards.LoadAll());
                if (Fm27Editor.Core.Officials.RefereeDirectory.Supported(session.Profile.Officials))
                    data.Referees = report.Read("referees", "Reading referees…", () => session.ReadReferees());
                data.Cities = report.Read("cities", "Reading cities…", () => session.Cities.LoadAll());
                if (session.Profile.Cities?.Links is not null)
                {
                    using var reads = new BulkReadScope(session.Memory);
                    var links = new CityLinks(reads.Memory, session.Profile, session.ModuleBase);
                    var catalogues = new Dictionary<CityLink, IReadOnlyList<CityLinkTarget>>();
                    foreach (var link in new[] { CityLink.Region, CityLink.Language, CityLink.Weather })
                        catalogues[link] = report.Read($"city {link.ToString().ToLowerInvariant()} catalogue", "Reading cities…", () => links.Catalogue(data.Cities, link));
                    data.CityLinkCatalogues = catalogues;
                }
                if (session.Profile.Nation is not null)
                    data.AgreementOrganisations = report.Read("agreement organisations", "Reading nations…", () =>
                    {
                        using var reads = new BulkReadScope(session.Memory);
                        var agreements = new NationAgreements(reads.Memory, session.Profile, session.ModuleBase);
                        var continents = session.NationLinks.Continents(data.Nations);
                        var found = new Dictionary<ulong, AgreementTargetOption>();
                        foreach (var nation in data.Nations)
                            foreach (int vector in new[] { NationAgreements.EuVector, NationAgreements.NonForeignVector })
                                foreach (var row in agreements.Read(nation.Address, vector, continents, data.Nations))
                                    if (row.Kind == AgreementKind.Agreement && !string.IsNullOrWhiteSpace(row.TargetName))
                                        found.TryAdd(row.Target, new(row.Target, row.TargetName));
                        return found.Values.OrderBy(x => x.Name).ToArray();
                    });
            }
        }
        finally
        {
            if (discovery is not null)
            {
                if (!discovery.IsCompleted) report.Progress("Scanning transfer deals…");
                discovery.GetAwaiter().GetResult();
            }
            metricsTask?.GetAwaiter().GetResult();
        }
        report.Run("shared discovery join", "Reading…", () => _ = index.Value, () => index.Value.Count);
        var dealVtables = session.TransferDeals.VtableAddresses;
        data.LoadTransferDeals(report, () => ownedDeals ?? index.Value.Where(m => dealVtables.Contains(m.Vtable)).Select(m => m.Address).ToArray());
        if (needsClauseScan)
        {
            data.ClauseIndexAttempted = true;
            ulong[] foundClauses = [];
            report.Run("transfer clauses", "Reading…", () =>
            {
                foundClauses = index.Value.Where(m => clauseVtables.Contains(m.Vtable)).Select(m => m.Address).ToArray();
                session.PreloadTransferClauses(foundClauses);
            }, () => foundClauses.Length);
        }
        if (needsRegions)
            report.Run("agreement regions", "Reading…", () => session.NationAgreements.PreloadRegions(() =>
                index.Value.Where(m => m.Vtable == regionVtable).Select(m => m.Address).ToArray()));
        return data;
    }
}

/// <summary>One full-load operation's results, written to the existing flight recorder.</summary>
internal sealed class GameLoadReport(Action<string>? progress = null)
{
    readonly List<string> errors = [];
    readonly object gate = new();
    public IReadOnlyList<string> Errors { get { lock (gate) return errors.ToArray(); } }
    public bool Succeeded { get { lock (gate) return errors.Count == 0; } }

    internal void Progress(string label) => progress?.Invoke(Loc.S(label));

    public IReadOnlyList<T> Read<T>(string stage, string label, Func<IReadOnlyList<T>> read)
    {
        IReadOnlyList<T> result = [];
        Run(stage, label, () => result = read(), () => result.Count);
        return result;
    }

    public void Run(string stage, string label, Action read, Func<int>? count = null)
    {
        progress?.Invoke(Loc.S(label));
        var watch = Stopwatch.StartNew();
        try
        {
            read();
            FlightRecorder.Current?.Log("load", $"{stage}: {watch.ElapsedMilliseconds} ms{(count is null ? "" : $"; count {count()}")}");
        }
        catch (Exception ex)
        {
            Fail(stage, ex);
            FlightRecorder.Current?.Log("load", $"{stage}: failed after {watch.ElapsedMilliseconds} ms");
        }
    }

    public void Fail(string stage, Exception error)
    {
        lock (gate) errors.Add($"{stage}: {UserMessage.For(error)}");
        FlightRecorder.Current?.Log("load", $"{stage}: {error.GetType().Name}: {error.Message}");
    }
}
