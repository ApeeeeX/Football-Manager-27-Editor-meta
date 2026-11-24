using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

[Collection("Theme")]
public sealed class PlayerInformationLayoutTests : IDisposable
{
    readonly string settings = Path.Combine(Path.GetTempPath(), "fm27-layout-theme-" + Guid.NewGuid().ToString("N") + ".txt");

    public PlayerInformationLayoutTests() => AppTheme.SettingPathOverride = settings;

    public void Dispose()
    {
        AvaloniaTestThread.Run(() => AppTheme.Set(ThemeMode.Dark));
        AppTheme.SettingPathOverride = null;
        if (File.Exists(settings)) File.Delete(settings);
    }

    [Fact]
    public void Player_information_attribute_labels_do_not_wrap_or_overlap_at_default_window_width()
    {
        AvaloniaTestThread.Run(() =>
        {
            AppTheme.Set(ThemeMode.Light);

            var mainWindow = new MainWindow();
            try
            {
                Assert.Equal(1100, mainWindow.Width);
                Assert.Equal(820, mainWindow.Height);
                Assert.Equal(900, mainWindow.MinWidth);
                Assert.Equal(600, mainWindow.MinHeight);
            }
            finally
            {
                mainWindow.Close();
            }

            var host = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 144 },
                    new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 144 },
                    new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 144 },
                },
                ColumnSpacing = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var (personal, natInfoCombo) = BuildPersonalCard();
            personal.Width = 262;
            personal.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(personal, 0);
            host.Children.Add(personal);

            var rowLabels = new List<(TextBlock Label, Control Value)>();
            Control MakeAttrCard(string title, (string Name, int Val)[] attrs)
            {
                var rows = new List<Control>();
                foreach (var (name, val) in attrs)
                {
                    var rating = MainWindow.RatingBox(val, 1, 20, _ => { });
                    var row = (DockPanel)MainWindow.AttributeRow(name, rating);
                    var label = row.Children.OfType<TextBlock>().Single();
                    rowLabels.Add((label, rating));
                    rows.Add(row);
                }
                return MainWindow.AttributeCard(title, rows);
            }

            var col1 = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    MakeAttrCard("Technical", [
                        ("Corners", 14), ("Crossing", 13), ("Dribbling", 18), ("Finishing", 18),
                        ("First Touch", 18), ("Free Kick Taking", 11), ("Heading", 10), ("Long Shots", 15),
                        ("Long Throws", 3), ("Marking", 4), ("Passing", 15), ("Penalty Taking", 18),
                        ("Tackling", 4), ("Technique", 17),
                    ])
                }
            };
            var col2 = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    MakeAttrCard("Mental", [
                        ("Aggression", 6), ("Anticipation", 16), ("Bravery", 12), ("Composure", 17),
                        ("Concentration", 15), ("Decisions", 15), ("Determination", 18), ("Flair", 18),
                        ("Leadership", 13), ("Off The Ball", 16), ("Positioning", 4), ("Teamwork", 8),
                        ("Vision", 15), ("Work Rate", 12),
                    ]),
                    MakeAttrCard("Personality", [
                        ("Adaptability", 15), ("Ambition", 20), ("Loyalty", 9), ("Pressure", 16),
                    ])
                }
            };
            var col3 = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    MakeAttrCard("Physical", [
                        ("Acceleration", 20), ("Agility", 16), ("Balance", 16), ("Jumping Reach", 8),
                        ("Natural Fitness", 15), ("Pace", 19), ("Stamina", 15), ("Strength", 12),
                        ("Left Foot", 14), ("Right Foot", 20),
                    ]),
                    MakeAttrCard("Hidden", [
                        ("Consistency", 14), ("Dirtiness", 7), ("Important Matches", 14),
                        ("Injury Proneness", 7), ("Versatility", 14),
                    ])
                }
            };

            Grid.SetColumn(col1, 1);
            Grid.SetColumn(col2, 2);
            Grid.SetColumn(col3, 3);
            host.Children.Add(col1);
            host.Children.Add(col2);
            host.Children.Add(col3);

            var toolsPanel = new StackPanel { Spacing = 5, Margin = new Thickness(7, 7) };
            toolsPanel.Children.Add(ToolGrid.ToolSectionHeader("CONTRACT", first: true));
            foreach (var btnText in new[]
            {
                "Renew for 4 Years", "Make it Expire", "Release Player",
                "Make Captain", "Make Vice Captain", "Remove Int. Withdrawal",
                "Register for UEFA", "Register for Domestic", "Tactical Understanding"
            })
            {
                toolsPanel.Children.Add(ToolGrid.ToolButton(btnText, () => { }));
            }
            var toolsCard = UiKit.Card("Tools", toolsPanel);
            var bodyGrid = ToolGrid.WithTools(host, toolsCard);

            var allSections = new[]
            {
                "Information", "Role Scores", "Positions", "Career Plans", "Move to club",
                "Swap Player", "Work Permit", "Promises", "Unavailability", "Transfer Proposals",
                "Injury", "Contract", "Preferred Moves", "Relationships", "Languages",
                "Comparison", "Clone Center", "Swap National Call Up"
            };
            var railLabels = new List<TextBlock>();
            var railList = SectionHost.CreateRailListBox(stretchVertically: false);
            railList.ItemTemplate = new FuncDataTemplate<string>((s, _) =>
            {
                var tb = new TextBlock
                {
                    Text = s,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                railLabels.Add(tb);
                return tb;
            }, false);
            railList.ItemsSource = allSections;
            railList.SelectedIndex = 0;

            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 8, 10), Children = { bodyGrid } };
            var scroll = new ScrollViewer
            {
                Content = panel,
                Background = UiKit.WindowBg,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var railHost = new ContentControl { Margin = new Thickness(10, 0, 0, 8), VerticalAlignment = VerticalAlignment.Top, Content = railList };
            var mainCols = new Grid { ColumnDefinitions = new ColumnDefinitions("168,*") };
            Grid.SetColumn(railHost, 0);
            Grid.SetColumn(scroll, 1);
            mainCols.Children.Add(railHost);
            mainCols.Children.Add(scroll);

            var badges = new StackPanel { Orientation = Orientation.Horizontal, Children = { UiKit.MetaText("ID 85139014"), UiKit.MetaDot(), UiKit.MetaText("Manchester United", highlight: true), UiKit.MetaDot(), UiKit.MetaText("Premier League"), UiKit.MetaDot(), UiKit.MetaText("France"), UiKit.MetaDot(), UiKit.MetaText("27 yrs") } };
            var statRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    UiKit.StatBox("CA", new TextBlock { Text = "192", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = UiKit.Brush("#38BDF8") }),
                    UiKit.StatBox("PA", new TextBlock { Text = "197", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = UiKit.Brush("#C084FC") }),
                }
            };
            var bannerWrap = new Border
            {
                Margin = new Thickness(10, 8, 10, 7),
                Child = UiKit.EntityBanner("Kylian Mbappé", badges, statRight),
            };
            DockPanel.SetDock(bannerWrap, Dock.Top);

            var bottomBar = new Border
            {
                Background = UiKit.SurfaceAlt,
                BorderBrush = UiKit.BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(12, 5),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 0,
                    Children =
                    {
                        ScalarPair("Condition", "8500"),
                        ScalarPair("Sharpness", "10000"),
                        ScalarPair("Jadedness", "-388"),
                    }
                }
            };
            DockPanel.SetDock(bottomBar, Dock.Bottom);

            // Simulate the top chrome bars height (~96px: toolbar + module bar + entity tab strip + status bar).
            var topChrome = new Border { Height = 96, Background = UiKit.SurfaceHeader };
            var statusChrome = new Border { Height = 26, Background = UiKit.SurfaceHeader };
            DockPanel.SetDock(topChrome, Dock.Top);
            DockPanel.SetDock(statusChrome, Dock.Bottom);

            var page = new DockPanel
            {
                Background = UiKit.WindowBg,
                Children = { topChrome, statusChrome, bannerWrap, bottomBar, mainCols },
            };

            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Background = UiKit.WindowBg,
                Content = page,
            };
            window.Show();
            AvaloniaTestThread.RunJobs();
            try
            {
                // Left rail must display all 18 items without triggering a vertical scrollbar or clipping labels.
                var railScroll = railList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (railScroll is not null)
                    Assert.True(railScroll.Extent.Height <= railScroll.Viewport.Height + 1,
                        $"Left section rail scrolled vertically ({railScroll.Extent.Height:F1} > {railScroll.Viewport.Height:F1})");
                foreach (var rl in railLabels)
                    Assert.True(rl.DesiredSize.Width <= rl.Bounds.Width + 1,
                        $"Rail label '{rl.Text}' clipped ({rl.DesiredSize.Width:F1} > {rl.Bounds.Width:F1})");

                // Outer scroll must not overflow horizontally.
                Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                    $"Detail scroll overflowed horizontally ({scroll.Extent.Width:F1} > {scroll.Viewport.Width:F1})");
                Assert.True(toolsCard.Bounds.Width <= ToolGrid.ToolsWidth + 1);
                Assert.True(bodyGrid.Bounds.Width <= scroll.Viewport.Width + 1);

                // Every attribute label must fit on one line without mid-word wrapping or clipping.
                foreach (var (label, value) in rowLabels)
                {
                    Assert.True(label.Bounds.Height <= 22, $"Label '{label.Text}' wrapped to height {label.Bounds.Height:F1}");
                    Assert.True(label.DesiredSize.Width <= label.Bounds.Width + 1, $"Label '{label.Text}' clipped (desired {label.DesiredSize.Width:F1} > bounds {label.Bounds.Width:F1})");
                    Assert.True(label.Bounds.Right <= value.Bounds.Left - 3, $"Label '{label.Text}' too close to value");
                }

                Assert.True(natInfoCombo.Bounds.Width >= 144, $"Nationality Info combo too narrow ({natInfoCombo.Bounds.Width:F1})");
                Assert.True(col1.Bounds.Left - personal.Bounds.Right >= 6);
                Assert.True(col2.Bounds.Left - col1.Bounds.Right >= 6);
                Assert.True(col3.Bounds.Left - col2.Bounds.Right >= 6);

                if (Environment.GetEnvironmentVariable("FM27_RENDER_PREVIEW_PATH") is { Length: > 0 } outPath)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    var rtb = new RenderTargetBitmap(new PixelSize(1100, 820), new Vector(96, 96));
                    rtb.Render(window);
                    rtb.Save(outPath);
                }
            }
            finally
            {
                window.Close();
                AvaloniaTestThread.RunJobs();
            }
        });
    }

    static (Border Card, ComboBox NatInfoCombo) BuildPersonalCard()
    {
        var stack = new StackPanel { Spacing = 0 };
        Grid.SetIsSharedSizeScope(stack, true);
        int idx = 0;
        void Add(string label, Control value)
        {
            bool isCheck = value is CheckBox;
            var text = new TextBlock
            {
                Text = label,
                MaxWidth = isCheck ? 210 : 114,
                FontSize = 12,
                Foreground = UiKit.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = isCheck ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = isCheck ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                Margin = isCheck ? new Thickness(0, 2, 8, 2) : new Thickness(0, 0, 4, 0),
            };
            Control inner;
            if (value is CheckBox or NumericUpDown || label == "Personality")
            {
                if (value is NumericUpDown)
                {
                    value.Width = 102;
                    value.HorizontalAlignment = HorizontalAlignment.Right;
                    value.MinHeight = 24;
                }
                var dock = new DockPanel { LastChildFill = true, MinHeight = 25 };
                DockPanel.SetDock(value, Dock.Right);
                dock.Children.Add(value);
                dock.Children.Add(text);
                inner = dock;
            }
            else
            {
                value.HorizontalAlignment = HorizontalAlignment.Stretch;
                value.MinHeight = 24;
                var line = new Grid
                {
                    MinHeight = 25,
                    ColumnSpacing = 5,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "InfoLbl" },
                        new ColumnDefinition(GridLength.Star),
                    },
                };
                Grid.SetColumn(text, 0);
                Grid.SetColumn(value, 1);
                line.Children.Add(text);
                line.Children.Add(value);
                inner = line;
            }
            stack.Children.Add(new Border
            {
                Background = idx++ % 2 == 1 ? UiKit.SurfaceZebra : Brushes.Transparent,
                Padding = new Thickness(6, 2, 6, 2),
                Child = inner,
            });
        }

        NumericUpDown Num(decimal v, string fmt = "0") => new()
        {
            Value = v, ShowButtonSpinner = false, FormatString = fmt, HorizontalContentAlignment = HorizontalAlignment.Right
        };

        var natInfo = new ComboBox { FontSize = 12, Padding = new Thickness(5, 0, 0, 0), ItemsSource = new[] { "Declared For Nation" }, SelectedIndex = 0 };
        Add("Personality", new TextBlock { Text = "Driven", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Add("Born", new TextBox { Text = "20-12-1998" });
        Add("Height", Num(178));
        Add("Home Reputation", Num(9400));
        Add("Current Reputation", Num(9600));
        Add("World Reputation", Num(9600));
        Add("Morale", Num(20));
        Add("Asking Price", Num(144507104, "N0"));
        Add("Caps", Num(106));
        Add("Int Goals", Num(66));
        Add("Nationality", new Button { Content = "France", HorizontalContentAlignment = HorizontalAlignment.Left });
        Add("Nationality Info", natInfo);
        Add("City Of Birth", new Button { Content = "Bondy (France)", HorizontalContentAlignment = HorizontalAlignment.Left });
        Add("Body Type", new ComboBox { FontSize = 12, ItemsSource = new[] { "Mesomorph" }, SelectedIndex = 0 });
        Add("Hair Colour", new ComboBox { FontSize = 12, ItemsSource = new[] { "Black 2" }, SelectedIndex = 0 });
        Add("Hair Length", new ComboBox { FontSize = 12, ItemsSource = new[] { "Short" }, SelectedIndex = 0 });
        Add("Skin Colour", new ComboBox { FontSize = 12, ItemsSource = new[] { "Skin Tone 12" }, SelectedIndex = 0 });
        Add("Treated As Non-EU In Italy", new CheckBox { IsChecked = false });
        return (UiKit.Card("Personal Details", stack), natInfo);
    }

    static StackPanel ScalarPair(string label, string value) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Margin = new Thickness(0, 0, 18, 0),
        Children =
        {
            new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = UiKit.Brush("#94A3B8"), VerticalAlignment = VerticalAlignment.Center },
            new TextBox { Text = value, Width = 74, Height = 24, MinHeight = 24, HorizontalContentAlignment = HorizontalAlignment.Right },
        }
    };
}
