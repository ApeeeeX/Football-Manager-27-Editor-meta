using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.Clubs;

namespace Fm27Editor.App;

internal static class ClubAdditionalStaffCard
{
    internal sealed record RowSpec(
        string Label,
        StaffAllowanceField? MainTeam,
        StaffAllowanceField? U21,
        StaffAllowanceField? U18);

    internal static readonly IReadOnlyList<RowSpec> Rows =
    [
        new("Coaches", StaffAllowanceField.CoachesFirstTeam, StaffAllowanceField.CoachesU21, StaffAllowanceField.CoachesU18),
        new("Physios", StaffAllowanceField.PhysiosFirstTeam, StaffAllowanceField.PhysiosU21, StaffAllowanceField.PhysiosU18),
        new("Sports Scientists", StaffAllowanceField.SportsScientistsFirstTeam, StaffAllowanceField.SportsScientistsU21, StaffAllowanceField.SportsScientistsU18),
        new("Performance Analysts", StaffAllowanceField.PerformanceAnalystsFirstTeam, StaffAllowanceField.PerformanceAnalystsU21, StaffAllowanceField.PerformanceAnalystsU18),
        new("Scouts", StaffAllowanceField.Scouts, null, null),
        new("Recruitment Analysts", StaffAllowanceField.RecruitmentAnalysts, null, null),
    ];

    internal static int ClampValue(decimal value, int max) =>
        (int)Math.Clamp(Math.Round(value, 0, MidpointRounding.AwayFromZero), 0, max);

    internal static Border Build(
        StaffAllowanceSnapshot? snapshot,
        Func<StaffAllowanceField, int?> getStaged,
        Action<StaffAllowanceField, int?> stageValue,
        Action<StaffAllowanceField, bool> setInvalid,
        Func<bool> isUpdating)
    {
        var titleBlock = new TextBlock
        {
            Text = Loc.S("Additional Staff (Experimental)"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleControl = UiKit.TitleWithInfo(
            titleBlock,
            Loc.S("Extra staff slots added on top of the club's base allowance. Values outside the allowed range are clamped."));

        if (snapshot is null)
        {
            return UiKit.Card(
                Loc.S("Additional Staff (Experimental)"),
                new TextBlock
                {
                    Text = Loc.S("Extra staff allowances are available only for human-managed clubs."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = UiKit.TextMuted,
                },
                customTitle: titleControl);
        }

        var stack = new StackPanel { Spacing = 0 };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,64,54,54"),
            ColumnSpacing = 6,
            Margin = new Thickness(6, 0, 6, 4),
        };
        header.Children.Add(HeaderText("Main Team", column: 1));
        header.Children.Add(HeaderText("U21", column: 2));
        header.Children.Add(HeaderText("U18", column: 3));
        stack.Children.Add(header);

        for (int i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,64,54,54"),
                ColumnSpacing = 6,
                MinHeight = 30,
            };

            var name = new TextBlock
            {
                Text = Loc.S(row.Label),
                FontSize = 13,
                Foreground = UiKit.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            rowGrid.Children.Add(name);

            AddCell(rowGrid, 1, row.MainTeam, snapshot, getStaged, stageValue, setInvalid, isUpdating);
            AddCell(rowGrid, 2, row.U21, snapshot, getStaged, stageValue, setInvalid, isUpdating);
            AddCell(rowGrid, 3, row.U18, snapshot, getStaged, stageValue, setInvalid, isUpdating);

            var border = new Border
            {
                Padding = new Thickness(6, 4),
                CornerRadius = new CornerRadius(3),
                Background = i % 2 == 1 ? UiKit.SurfaceZebra : Brushes.Transparent,
                BorderBrush = i > 0 ? UiKit.BorderSubtle : Brushes.Transparent,
                BorderThickness = i > 0 ? new Thickness(0, 1, 0, 0) : new Thickness(0),
                Child = rowGrid,
            };
            stack.Children.Add(border);
        }

        return UiKit.Card(
            Loc.S("Additional Staff (Experimental)"),
            stack,
            customTitle: titleControl);
    }

    static TextBlock HeaderText(string key, int column)
    {
        var text = new TextBlock
        {
            Text = Loc.S(key),
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = UiKit.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(text, column);
        return text;
    }

    static void AddCell(
        Grid rowGrid,
        int column,
        StaffAllowanceField? maybeField,
        StaffAllowanceSnapshot snapshot,
        Func<StaffAllowanceField, int?> getStaged,
        Action<StaffAllowanceField, int?> stageValue,
        Action<StaffAllowanceField, bool> setInvalid,
        Func<bool> isUpdating)
    {
        if (maybeField is not { } field)
            return;

        int max = StaffAllowanceAccess.MaxValue(field);
        int baseVal = snapshot[field];
        int current = ClampValue(getStaged(field) ?? baseVal, max);

        var nud = new NumericUpDown
        {
            Minimum = 0,
            Maximum = max,
            Increment = 1,
            FormatString = "0",
            ClipValueToMinMax = true,
            ShowButtonSpinner = false,
            AllowSpin = true,
            Width = 52,
            MinWidth = 52,
            Height = 24,
            MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Value = current,
            Tag = field,
        };
        ToolTip.SetTip(nud, $"0–{max}");

        bool internalSync = false;

        void Commit(int clamped)
        {
            if ((int?)nud.Value != clamped || !string.Equals(nud.Text, clamped.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                internalSync = true;
                try
                {
                    nud.Value = clamped;
                    nud.Text = clamped.ToString(CultureInfo.InvariantCulture);
                }
                finally { internalSync = false; }
            }
            setInvalid(field, false);
            stageValue(field, clamped == baseVal ? null : clamped);
        }

        nud.ValueChanged += (_, _) =>
        {
            if (isUpdating() || internalSync) return;
            if (nud.Value is { } raw)
            {
                Commit(ClampValue(raw, max));
                return;
            }
            setInvalid(field, true);
        };

        nud.PropertyChanged += (_, e) =>
        {
            if (e.Property != NumericUpDown.TextProperty || isUpdating() || internalSync) return;
            var rawText = nud.Text?.Trim();
            if (string.IsNullOrEmpty(rawText))
            {
                setInvalid(field, true);
                return;
            }
            if (decimal.TryParse(rawText, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) ||
                decimal.TryParse(rawText, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            {
                int clamped = ClampValue(parsed, max);
                setInvalid(field, false);
                stageValue(field, clamped == baseVal ? null : clamped);
            }
            else
            {
                setInvalid(field, true);
            }
        };

        nud.LostFocus += (_, _) =>
        {
            if (isUpdating() || internalSync) return;
            var rawText = nud.Text?.Trim();
            if (!string.IsNullOrEmpty(rawText) &&
                (decimal.TryParse(rawText, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) ||
                 decimal.TryParse(rawText, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)))
            {
                Commit(ClampValue(parsed, max));
                return;
            }

            if (nud.Value is { } raw)
            {
                Commit(ClampValue(raw, max));
                return;
            }

            Commit(ClampValue(getStaged(field) ?? baseVal, max));
        };

        Grid.SetColumn(nud, column);
        rowGrid.Children.Add(nud);
    }
}
