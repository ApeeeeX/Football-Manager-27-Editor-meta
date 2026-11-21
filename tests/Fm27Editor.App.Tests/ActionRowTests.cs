using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public sealed class ActionRowTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(564)]
    public void Long_status_and_action_stay_aligned_and_do_not_overlap(double width)
    {
        AvaloniaTestThread.Run(() =>
        {
            var summary = new TextBlock
            {
                Text = "Sân vận động mới đang lập kế hoạch. Chưa có ngày hoàn thành.",
                TextWrapping = TextWrapping.Wrap,
            };
            var button = new Button { Content = "Hoàn thành hôm nay", MinWidth = 120 };
            var row = UiKit.ActionRow(summary, button);

            row.Measure(new Size(width, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));

            Assert.True(summary.Bounds.Right + 11 <= button.Bounds.Left);
            Assert.Equal(width, button.Bounds.Right, 1);
            Assert.True(summary.Bounds.Height > 0);
        });
    }
}
