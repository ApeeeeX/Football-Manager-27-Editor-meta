using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

[Collection("Theme")]
public sealed class TableLayoutTests : IDisposable
{
    readonly string settings = Path.Combine(Path.GetTempPath(), "fm27-table-layout-" + Guid.NewGuid().ToString("N") + ".txt");

    public TableLayoutTests() => AppTheme.SettingPathOverride = settings;

    public void Dispose()
    {
        AvaloniaTestThread.Run(() => AppTheme.Set(ThemeMode.Dark));
        AppTheme.SettingPathOverride = null;
        if (File.Exists(settings)) File.Delete(settings);
    }

    sealed record StaffSample(ulong Id, string Job, string Name, int Age, string Team, string Nat, int CA, int PA, string Expires);
    sealed record SquadSample(ulong Id, string Num, string Inf, string Name, string Pos, string Nat, int CA, int PA, int Age, string Cond, string Price);

    [Theory]
    [InlineData(1100, 820)]
    [InlineData(1920, 1080)]
    public void Club_staff_table_keeps_tools_visible_and_fits_without_horizontal_scroll(int width, int height)
    {
        AvaloniaTestThread.Run(() =>
        {
            AppTheme.Set(ThemeMode.Light);
            var rows = new[]
            {
                new StaffSample(1, "Head of Youth Development", "Stephen Torpey", 42, "First team", "England", 145, 160, "30-06-2029"),
                new StaffSample(2, "GK Coach", "José Ramón De La Fuente", 51, "First team", "Spain", 160, 170, "30-06-2028"),
                new StaffSample(3, "Director of Football", "Jason Wilcox", 54, "First team", "England", 158, 170, "30-06-2028"),
            };
            var excluded = new HashSet<ulong>();
            var grid = ToolGrid.Create();
            grid.ItemsSource = rows;
            ToolGrid.AddTick<StaffSample>(grid, excluded, r => r.Id, () => rows, () => { });
            grid.Columns.Add(ToolGrid.StarText<StaffSample>("Job", r => r.Job, 1.1, 100, "Job"));
            var nameCol = ToolGrid.StarText<StaffSample>("Name", r => r.Name, 1.4, 120, "Staff name", "#F1F5F9", bold: true);
            grid.Columns.Add(nameCol);
            grid.Columns.Add(ToolGrid.Number<StaffSample>("Age", r => r.Age.ToString(), 56, "Age"));
            grid.Columns.Add(ToolGrid.StarText<StaffSample>("Team", r => r.Team, 0.8, 76, "Team"));
            grid.Columns.Add(ToolGrid.StarText<StaffSample>("Nat", r => r.Nat, 0.85, 80, "Nationality"));
            grid.Columns.Add(ToolGrid.Number<StaffSample>("CA", r => r.CA.ToString(), 48, "Current Ability"));
            grid.Columns.Add(ToolGrid.Number<StaffSample>("PA", r => r.PA.ToString(), 48, "Potential Ability"));
            grid.Columns.Add(ToolGrid.Number<StaffSample>("Expires", r => r.Expires, 84, "Club contract expiry"));

            var toolsCard = UiKit.Card("Tools", new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(7, 7),
                Children =
                {
                    ToolGrid.ToolButton("Build backroom", () => { }, "Build a Backroom Team"),
                    ToolGrid.ToolSectionHeader("CONTRACT"),
                    ToolGrid.ToolButton("Renew for 4 Years", () => { }),
                    ToolGrid.ToolButton("Make it expire", () => { }),
                    ToolGrid.ToolPair(ToolGrid.ToolButton("−1 Year", () => { }), ToolGrid.ToolButton("+1 Year", () => { })),
                },
            });

            AssertDetailTableFitsWindow("club_staff", width, height, grid, toolsCard, nameCol);
        });
    }

    [Theory]
    [InlineData(1100, 820)]
    [InlineData(1920, 1080)]
    public void Club_squad_table_keeps_tools_visible_and_fits_without_horizontal_scroll(int width, int height)
    {
        AvaloniaTestThread.Run(() =>
        {
            AppTheme.Set(ThemeMode.Light);
            var rows = new[]
            {
                new SquadSample(1, "10", "", "Bruno Fernandes", "AM (C), M (C)", "Portugal", 174, 178, 31, "9800", "95,000,000"),
                new SquadSample(2, "37", "", "Kobbie Mainoo", "DM, M (C)", "England", 156, 178, 21, "9900", "88,000,000"),
            };
            var excluded = new HashSet<ulong>();
            var grid = ToolGrid.Create();
            grid.ItemsSource = rows;
            ToolGrid.AddTick<SquadSample>(grid, excluded, r => r.Id, () => rows, () => { });
            grid.Columns.Add(ToolGrid.Number<SquadSample>("#", r => r.Num, 38, "Squad Number"));
            grid.Columns.Add(ToolGrid.Text<SquadSample>("Inf", r => r.Inf, 44, "Status / Injuries / Bans"));
            var nameCol = ToolGrid.StarText<SquadSample>("Name", r => r.Name, 1.5, 118, "Player Name", "#F1F5F9", bold: true);
            grid.Columns.Add(nameCol);
            grid.Columns.Add(ToolGrid.StarText<SquadSample>("Pos", r => r.Pos, 0.9, 82, "Positions", "#C4B5FD"));
            grid.Columns.Add(ToolGrid.StarText<SquadSample>("Nat", r => r.Nat, 0.8, 76, "Nationality"));
            grid.Columns.Add(ToolGrid.Number<SquadSample>("CA", r => r.CA.ToString(), 46, "Current Ability"));
            grid.Columns.Add(ToolGrid.Number<SquadSample>("PA", r => r.PA.ToString(), 44, "Potential Ability"));
            grid.Columns.Add(ToolGrid.Number<SquadSample>("Age", r => r.Age.ToString(), 54, "Age"));
            grid.Columns.Add(ToolGrid.Number<SquadSample>("Cond", r => r.Cond, 74, "Condition (0–10000)"));
            grid.Columns.Add(ToolGrid.Number<SquadSample>("Price", r => r.Price, 86, "Asking Price (£)"));

            var toolsCard = UiKit.Card("Presets", new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(7, 7),
                Children =
                {
                    ToolGrid.ToolSectionHeader("CONTRACT"),
                    ToolGrid.ToolButton("Renew for 4 Years", () => { }),
                    ToolGrid.ToolButton("Make it Expire", () => { }),
                    ToolGrid.ToolPair(ToolGrid.ToolButton("−1 Year", () => { }), ToolGrid.ToolButton("+1 Year", () => { })),
                },
            });

            AssertDetailTableFitsWindow("club_squad", width, height, grid, toolsCard, nameCol);
        });
    }

    static void AssertDetailTableFitsWindow(string name, int width, int height, DataGrid grid, Control toolsCard, DataGridColumn nameCol)
    {
        var body = ToolGrid.WithTools(grid, toolsCard);
        var editor = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(12, 0, 12, 12),
            Children = { new Border { Margin = new Thickness(8, 0, 0, 0), Child = body } },
        };
        var scroll = new ScrollViewer
        {
            Content = editor,
            Background = UiKit.WindowBg,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var header = new Border
        {
            Margin = new Thickness(12, 9, 12, 8),
            Child = UiKit.EntityBanner("Manchester United", new StackPanel { Children = { UiKit.MetaText("ID 680") } }, null),
        };
        var rail = new Border { Width = 168, Margin = new Thickness(12, 0, 0, 10), Child = SectionHost.CreateRailListBox(stretchVertically: true) };
        var dock = new DockPanel { Background = UiKit.WindowBg };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(rail, Dock.Left);
        dock.Children.Add(header);
        dock.Children.Add(rail);
        dock.Children.Add(scroll);

        var window = new Window { Width = width, Height = height, Background = UiKit.WindowBg, Content = dock };
        window.Show();
        AvaloniaTestThread.RunJobs();
        try
        {
            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.Equal(ToolGrid.ToolsWidth, toolsCard.Bounds.Width, precision: 1);
            Assert.True(toolsCard.Bounds.Right <= body.Bounds.Width + 1, $"Tools card overflowed right edge ({toolsCard.Bounds.Right:F1} > {body.Bounds.Width:F1})");
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"ScrollViewer extent overflowed viewport ({scroll.Extent.Width:F1} > {scroll.Viewport.Width:F1})");

            double sumColumns = grid.Columns.Sum(c => c.ActualWidth);
            Assert.True(sumColumns <= grid.Bounds.Width + 2, $"Columns ({sumColumns:F1}) overflowed DataGrid ({grid.Bounds.Width:F1}) at {width}x{height}");
            Assert.True(nameCol.ActualWidth >= 118, $"Name column too narrow ({nameCol.ActualWidth:F1})");
            Assert.True(nameCol.ActualWidth <= grid.Bounds.Width * 0.42, $"Name column bloated ({nameCol.ActualWidth:F1} of {grid.Bounds.Width:F1})");

            if (width == 1100 && Environment.GetEnvironmentVariable("FM27_RENDER_TABLE_PREVIEW_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                rtb.Render(window);
                rtb.Save(Path.Combine(dir, $"{name}_{width}x{height}.png"));
            }
        }
        finally
        {
            window.Close();
            AvaloniaTestThread.RunJobs();
        }
    }
}
