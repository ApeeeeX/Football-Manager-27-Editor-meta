using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace Fm27Editor.App.Tests;

/// <summary>The shared Relationships layout: a short list on the left, the selected row's form on its right.</summary>
public class RelationshipSplitViewTests
{
    static RelationshipRow Row(string group, string name, string value) => new()
    {
        Group = group,
        Name = name,
        Value = value,
        Detail = RelationshipSplitView.Form(RelationshipSplitView.FormRow("Value", RelationshipSplitView.Number(value))),
    };

    static Button GroupButton(RelationshipSplitView view, string text) =>
        view.View.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, text));

    static Border DetailHost(RelationshipRow row) => (Border)row.Detail.Parent!;

    [Fact]
    public void The_form_sits_right_of_a_short_list_at_a_wide_window()
    {
        AvaloniaTestThread.Run(() =>
        {
            var liverpool = Row("Rival Clubs", "Liverpool", "100");
            var view = new RelationshipSplitView(["Rival Clubs", "Favorite People", "Other"], [liverpool], _ => { }, _ => null);
            var window = new Window { Width = 1400, Height = 800, Content = view.View };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var form = liverpool.Detail.TranslatePoint(default, window)!.Value;
                var table = view.View.GetLogicalDescendants().OfType<DataGrid>().Single();
                Assert.True(table.Bounds.Width <= 440, $"table is {table.Bounds.Width} wide");
                Assert.True(form.X > 440, $"form starts at {form.X}");
                Assert.True(form.Y < 100, $"form starts at y {form.Y}");
                Assert.False(GroupButton(view, "Other").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Changing_group_keeps_each_rows_form_and_remove_reports_the_row()
    {
        AvaloniaTestThread.Run(() =>
        {
            var rival = Row("Rival Clubs", "Liverpool", "100");
            var liked = Row("Favorite People", "Alex Ferguson", "100");
            var removed = new List<RelationshipRow>();
            var view = new RelationshipSplitView(["Rival Clubs", "Favorite People"], [rival, liked], removed.Add, _ => null);
            var window = new Window { Width = 1400, Height = 800, Content = view.View };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var box = rival.Detail.GetLogicalDescendants().OfType<TextBox>().Single();
                box.Text = "77";
                view.Select(liked);
                Assert.Same(liked.Detail, DetailHost(liked).Child);
                view.Select(rival);
                Assert.Equal("77", rival.Detail.GetLogicalDescendants().OfType<TextBox>().Single().Text);

                GroupButton(view, "Remove").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal([rival], removed);
                Assert.Equal("Rival Clubs", view.CurrentGroup);
            }
            finally { window.Close(); }
        });
    }
}
