using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    static EntityTabIdentity PlayerIdentity(PlayerRecord player) =>
        EntityTabIdentity.Person(player.Id, player.FullName ?? player.DisplayName, player.DateOfBirth);

    static void ClearPlayerTabView(PlayerTab tab)
    {
        tab.Dirty = false;
        tab.Save = null;
        tab.InformationDirty = null;
        tab.SaveInformation = null;
        tab.Panel.Children.Clear();
        tab.Sticky.Content = null;
        tab.Header.Content = null;
        tab.Rail.Content = null;
    }

    void SuspendEntityTabs()
    {
        CloseUnified();
        ClearEntityTabDrag();
        openLinkedEntityInNewTab = false;
        pendingSection = null;
        selected = null;
        saveButton = null;
        informationDirty = null;
        saveInformation = null;
        editor = new StackPanel();
        contractDrafts.Clear();
        goalkeeperColumn.Clear();
        cloneTargets.Clear();
        playerDirtySections.Clear();
        playerPageSections.Clear();
        foreach (var tab in playerTabs)
        {
            tab.Player = null;
            ClearPlayerTabView(tab);
            SetEntityTitle(tab.Button, tab.Title);
        }
        foreach (var tab in kindTabs)
        {
            tab.Content = null;
            tab.Dirty = false;
            SetEntityTitle(tab.Button, tab.Title);
        }
        if (activeEntityButton is null) ShowModule(activeModule ?? "Players");
        else ShowUnavailableEntityTab(Loc.S("Load Game to refresh this tab."));
    }

    void RebindEntityTabs(GameLoadReport report)
    {
        RebindPlayerTabs();
        foreach (var tab in kindTabs)
        {
            tab.Content = tab.Kind == "list" ? ListTabContent(tab.Identity.Id) :
                screens.FirstOrDefault(screen => screen.Kind == tab.Kind && screen.IsLoaded)?.FindTab(tab.Identity);
            if (tab.Content is { } content) tab.Title = content.Title;
            SetEntityTitle(tab.Button, tab.Title);
        }
        if (activeEntityButton is { } active)
        {
            try { ActivateEntityButton(active); }
            catch (Exception ex)
            {
                ShowUnavailableEntityTab(UserMessage.For(ex));
                report.Fail("open tab", ex);
            }
        }
    }

    bool CanActivateEntityTab(Border tab) => entityStrip.Children.Contains(tab) && !backend.SaveChanged;

    void ActivateEntityButton(Border button)
    {
        if (playerTabs.FirstOrDefault(tab => tab.Button == button) is { } player) ActivatePlayerTab(player);
        else if (kindTabs.FirstOrDefault(tab => tab.Button == button) is { } kind) ActivateKindTab(kind);
    }

    void RemoveEntityButton(Border button)
    {
        int index = entityStrip.Children.IndexOf(button);
        if (index < 0) return;
        entityStrip.Children.RemoveAt(index);
        UpdateEntityRow();
        if (button != activeEntityButton) return;

        activeEntityButton = null;
        if (entityStrip.Children.Count > 0 && !backend.SaveChanged &&
            entityStrip.Children[Math.Min(index, entityStrip.Children.Count - 1)] is Border adjacent)
            ActivateEntityButton(adjacent);
        else ShowModule(activeModule ?? "Players");
    }

    void ShowUnavailableEntityTab(string? error = null)
    {
        bodyHost.Content = new TextBlock
        {
            Text = error ?? Loc.S(backend.Data is null ? "Load Game to refresh this tab." : "This item is unavailable in the loaded save."),
            Foreground = UiKit.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16),
        };
    }

    void FinishTabActivation(Border button)
    {
        HighlightEntityTab();
        UpdateModuleBarStyles();
        Dispatcher.UIThread.Post(() => button.BringIntoView(), DispatcherPriority.Loaded);
    }

    void ShowListTab(int id)
    {
        if (busy || backend.SaveChanged || ListTabContent(id) is not { } content) return;
        OpenKindTab("list", new(id), content);
        content.Restore();
    }

    EntityTabContent? ListTabContent(int? id) => id switch
    {
        1 => new(1, Loc.S("Retire Date"), retiringView ??= BuildRetiringView(), ShowLoadedRetirements),
        2 => new(2, Loc.S("Future Transfers"), futureView ??= BuildFutureTransfersView(), ShowLoadedFutureTransfers),
        _ => null,
    };
}
