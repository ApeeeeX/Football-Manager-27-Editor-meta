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

        foreach (var screen in screens)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await screen.PreloadAsync(data);
                if (!screen.IsLoaded) throw new InvalidOperationException("The connection changed while preparing the list");
                FlightRecorder.Current?.Log("load", $"{screen.Kind} rows: {watch.ElapsedMilliseconds} ms; count {screen.RowCount}");
            }
            catch (Exception ex) { report.Fail(screen.Kind + " rows", ex); }
        }

        await Task.Run(() =>
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
            if (session.Profile.Club?.TransferClauses is { Scan: true })
                report.Run("transfer clauses", "Reading…", session.EnsureTransferClausesLoaded);
            if (session.Profile.YouthCandidates is not null)
                report.Run("youth candidate manager", "Reading…", () => session.YouthCandidates.FindManager());
            if (session.Profile.Nation is not null)
                report.Run("work permit table", "Reading…", () => new WorkPermitTable(session.Memory, session.Profile, session.ModuleBase).Locate());
            report.Run("academy site", "Reading…", () => session.GoldenGenerationHook.FindSite());
            report.Run("development site", "Reading…", () => session.AcceleratedDevelopment.FindSite());
            report.Run("freezer", "Reading…", () => backend.InitializeFreezer(data));
        });
        futureDirty = retiringDirty = false;
        ApplyFutureFilter();
        ApplyRetiringFilter();
    }
}
