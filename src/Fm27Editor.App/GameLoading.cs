using System.Diagnostics;
using Fm27Editor.Core.Diagnostics;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    void ResetLoadedGame()
    {
        SuspendEntityTabs();
        ScoutingMetrics.Reset();
        RegenIds.Current = null;
        talentedRegensTab?.SetPlayers([]);
        generation++;
        searchVersion++;
        unifiedSearchVersion++;
        latestPlayerSearch.Cancel();
        playerSearchIndex.Clear();
        ReleaseUnifiedCache();
        lastUnifiedResults.Clear();
        unifiedList.Children.Clear();
        rows = [];
        grid.ItemsSource = null;
        playerSource = null;
        personNames = null;
        clubCache = null;
        nationCatalogue = null;
        competitionCache = null;
        cityCatalogue = null;
        languageCatalogue = null;
        careerCatalogue = null;
        regionalCompetitionNames = null;
        watchedSave = null;
        WorkPermitTable.Forget();
        SectionHost.Release(this);
        foreach (var screen in screens) screen.ResetForConnection();
        drafts.Clear();
        playerScalarDrafts.Clear();
        clubDrafts.Clear();
        retiringAllRows = [];
        futureAllRows = [];
        if (retiringGrid is not null) retiringGrid.ItemsSource = null;
        if (futureGrid is not null) futureGrid.ItemsSource = null;
        InvalidateRetiringPlayers();
        InvalidateFutureTransfers();
    }

    static (int Pid, ulong ModuleBase, ulong ModuleSize, ulong? AcademySite, ulong? DevelopmentSite) cachedArm64Sites;

    async Task PrepareLoadedGameAsync(LoadedGameData data, GameLoadReport report)
    {
        clubCache = data.Clubs;
        nationCatalogue = data.Nations.Where(n => n.DisplayName.Length > 0)
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        cityCatalogue = data.Cities;
        competitionCache = data.Competitions.GroupBy(c => c.Index)
            .ToDictionary(g => g.Key, g => new CompetitionInfo(g.First().Address, g.First().Name, g.First().ShortName));
        regionalCompetitionNames = data.Competitions.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name!);
        playerSource = data.Players.ToArray();
        EnsureUnifiedCache();

        var screensTask = Task.WhenAll(screens.Select(async screen =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await screen.PreloadAsync(data);
                if (!screen.IsLoaded) throw new InvalidOperationException("The connection changed while preparing the list");
                FlightRecorder.Current?.Log("load", $"{screen.Kind} rows: {watch.ElapsedMilliseconds} ms; count {screen.RowCount}");
            }
            catch (Exception ex) { report.Fail(screen.Kind + " rows", ex); }
        }));

        var metadataTask = Task.Run(() =>
        {
            var session = data.Session;
            languageCatalogue = report.Read("languages", "Reading…", () =>
            {
                using var reads = new BulkReadScope(session.Memory);
                return new LanguageReader(reads.Memory, session.Profile, session.ModuleBase).Catalogue(data.Players);
            });
            report.Run("person names", "Reading…", () => PersonNames(), () => personNames?.Count ?? 0);
            personNames ??= [];
            report.Run("career plans", "Reading career plans…", () => WarmCareerCatalogue(data.Players), () => careerCatalogue?.Count ?? 0);
            careerCatalogue ??= [];
            report.Run("future transfers", "Reading…", () => futureAllRows = ReadFutureTransferRows(data), () => futureAllRows.Count);
            report.Run("retirements", "Reading…", () => retiringAllRows = ReadRetiringRows(data), () => retiringAllRows.Count);
            if (session.Profile.Proposals is not null)
                report.Run("transfer proposals", "Reading…", session.Proposals.EnsureLoaded);
            if (!data.ClauseIndexAttempted && session.Profile.Club?.TransferClauses is { Scan: true })
                report.Run("transfer clauses", "Reading…", session.EnsureTransferClausesLoaded);
            if (session.Profile.YouthCandidates is not null)
                report.Run("youth candidate manager", "Reading…", () => session.YouthCandidates.FindManager());
            if (session.Profile.Nation is not null)
                report.Run("work permit table", "Reading…", () => new WorkPermitTable(session.Memory, session.Profile, session.ModuleBase).Locate());
            if (session.Profile.IsArm64)
            {
                var key = (session.Game.ProcessId, session.ModuleBase, (ulong)session.Game.ModuleSize);
                var cached = cachedArm64Sites;
                bool hit = key.ProcessId > 0 && cached.Pid == key.Item1 && cached.ModuleBase == key.Item2 && cached.ModuleSize == key.Item3
                    && cached.AcademySite is not null && cached.DevelopmentSite is not null;
                ulong? academyFound = null, developmentFound = null;
                if (hit)
                {
                    report.Run("academy site", "Reading…", () => academyFound = session.GoldenGenerationHook.FindSite(null, cached.AcademySite));
                    report.Run("development site", "Reading…", () => developmentFound = session.AcceleratedDevelopment.FindSite(null, cached.DevelopmentSite));
                }
                else
                {
                    var codeSnapshot = Fm27Editor.Core.CodeGen.Arm64SiteFinder.TryCaptureCode(session.Memory, session.ModuleBase, (ulong)session.Game.ModuleSize);
                    if (codeSnapshot is not null)
                    {
                        var academyTask = Task.Run(() => report.Run("academy site", "Reading…", () => academyFound = session.GoldenGenerationHook.FindSite(codeSnapshot)));
                        var devTask = Task.Run(() => report.Run("development site", "Reading…", () => developmentFound = session.AcceleratedDevelopment.FindSite(codeSnapshot)));
                        Task.WaitAll(academyTask, devTask);
                    }
                    else
                    {
                        report.Run("academy site", "Reading…", () => academyFound = session.GoldenGenerationHook.FindSite(null));
                        report.Run("development site", "Reading…", () => developmentFound = session.AcceleratedDevelopment.FindSite(null));
                    }
                }
                if (key.ProcessId > 0 && academyFound is not null && developmentFound is not null)
                    cachedArm64Sites = (key.Item1, key.Item2, key.Item3, academyFound, developmentFound);
            }
            else
            {
                report.Run("academy site", "Reading…", () => session.GoldenGenerationHook.FindSite(null));
                report.Run("development site", "Reading…", () => session.AcceleratedDevelopment.FindSite(null));
            }
            report.Run("freezer", "Reading…", () => backend.InitializeFreezer(data));
        });

        await Task.WhenAll(screensTask, metadataTask);
        futureDirty = retiringDirty = false;
        ApplyFutureFilter();
        ApplyRetiringFilter();
    }
}
