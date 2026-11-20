using Avalonia.Controls;
using Avalonia.Media;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    void AddLegendPreset(StackPanel panel, PlayerRecord player, PlayerDraft draft, Func<string, Action, bool, Button> actionButton)
    {
        var choose = actionButton("Make a Legend", () => _ = ChooseLegendAsync(), true);
        ToolTip.SetTip(choose, Loc.S("Copy attributes, positions, personality and ability from a historical player preset."));
        panel.Children.Add(choose);
        if (draft.Legend is { } pending)
        {
            var name = new TextBlock
            {
                Text = pending.Name, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = UiKit.TextSecondary,
            };
            panel.Children.Add(name);
            panel.Children.Add(actionButton("Clear legend", () => Stage(null), true));
        }

        void Stage(PlayerLegend? preset)
        {
            if (selected?.Address != player.Address || !drafts.TryGetValue(player.Address, out var current) || !ReferenceEquals(current, draft)) return;
            draft.SetLegend(preset);
            MarkSection(player, "Information", draft.Count > 0);
            pendingSection = "Information";
            RenderEditor();
            if (preset is not null) status.Text = Loc.F("{0} selected. Save changes to apply.", preset.Name);
        }

        async Task ChooseLegendAsync()
        {
            var presets = PlayerLegends.All;
            var groups = presets.Select(p => p.Group).Distinct().ToArray();
            var labels = new[] { Loc.S("All position groups") }.Concat(groups.Select(Loc.S)).ToArray();
            var preset = await PickerDialog.ShowGrouped<PlayerLegend>(this, "Choose a Legend", labels, (query, group) =>
                presets.Where(p => group == 0 || p.Group == groups[group - 1])
                    .Select(p => (Preset: p, Score: PickerSearch.Score(query, p.Name, id: p.Id)))
                    .Where(p => p.Score >= 0).OrderByDescending(p => p.Score).ThenBy(p => p.Preset.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => (LegendLabel(p.Preset), p.Preset)).ToArray(), "Search legends…", "Use preset");
            if (preset is not null) Stage(preset);
        }
    }

    static string LegendLabel(PlayerLegend legend) => Loc.F("{0}, age {1}, CA {2} / PA {3}",
        legend.Nationality is { } nation ? $"{legend.Name} ({nation})" : legend.Name,
        legend.Age, legend.CurrentAbility, legend.PotentialAbility);
}
