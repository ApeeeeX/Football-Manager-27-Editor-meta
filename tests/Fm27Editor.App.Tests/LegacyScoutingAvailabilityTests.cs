using Avalonia.Controls;
using Avalonia.LogicalTree;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public class LegacyScoutingAvailabilityTests
{
    [Fact]
    public void Advanced_search_keeps_Regen_and_personality_without_showing_uncalibrated_metrics()
    {
        AvaloniaTestThread.Run(() =>
        {
            Assert.False(LegacyScoutingAvailability.Enabled);
            var search = new AttributeFilterDialog(new PlayerFilter(), []);
            try
            {
                var visibleText = search.GetLogicalDescendants().OfType<TextBlock>().Select(control => control.Text).ToArray();
                Assert.DoesNotContain(Loc.S("RCA"), visibleText);
                Assert.DoesNotContain(Loc.S("Best Role (Any)"), visibleText);
                Assert.DoesNotContain(Loc.S("Role %"), visibleText);
                Assert.Contains(Loc.S("Personality"), visibleText);
                var rule = search.GetLogicalDescendants().OfType<StackPanel>().Single(panel =>
                    panel.Children.OfType<TextBlock>().Any(label => label.Text == Loc.S("Regen")));
                var choice = Assert.Single(rule.Children.OfType<ComboBox>());
                Assert.False(search.ResultNumericRanges.ContainsKey("Regen"));
                choice.SelectedIndex = 1;
                Assert.Equal(new NumericRange(1, 1), search.ResultNumericRanges["Regen"]);
                choice.SelectedIndex = 2;
                Assert.Equal(new NumericRange(0, 0), search.ResultNumericRanges["Regen"]);
            }
            finally { search.Close(); }
        });
    }

    [Fact]
    public void Main_navigation_has_Players_without_a_separate_Regens_tab()
    {
        AvaloniaTestThread.Run(() =>
        {
            var app = new MainWindow();
            try
            {
                var buttons = app.GetLogicalDescendants().OfType<Button>().Select(button => button.Content).ToArray();
                Assert.Contains(Loc.S("Players"), buttons);
                Assert.DoesNotContain(Loc.S("Regens"), buttons);
                Assert.DoesNotContain("Cầu thủ tái sinh", buttons);
            }
            finally { app.Close(); }
        });
    }
}
