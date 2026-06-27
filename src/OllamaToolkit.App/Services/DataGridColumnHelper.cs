using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public static class DataGridColumnHelper
{
    private const double MinColumnWidth = 44;
    private const double CellHorizontalPadding = 28;
    private const double HeaderHorizontalPadding = 44;
    private const double ColumnWidthSlack = 14;
    private const double TemplateColumnMinWidth = 92;
    private const double DenseWrapColumnMinWidth = 240;
    private const double DenseInsightColumnMinWidth = 160;
    private const int MaxRowsToMeasure = 500;

    private static readonly ConditionalWeakTable<DataGrid, FitState> FitStates = new();

    public static void AttachAutoFit(DataGrid grid, Action<DataGrid> fitAction)
    {
        var state = FitStates.GetOrCreateValue(grid);
        state.FitAction = fitAction;

        if (state.Attached)
        {
            return;
        }

        state.Attached = true;
        state.Timer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        state.Timer.Tick += (_, _) =>
        {
            state.Timer!.Stop();
            if (state.PendingFit && grid.IsVisible && grid.ActualWidth > 0)
            {
                state.PendingFit = false;
                state.FitAction?.Invoke(grid);
            }
        };

        grid.SizeChanged += (_, _) => QueueFit(grid);
    }

    public static void QueueFit(DataGrid grid)
    {
        if (!FitStates.TryGetValue(grid, out var state))
        {
            return;
        }

        state.PendingFit = true;
        state.Timer?.Stop();
        state.Timer?.Start();
    }

    public static void ScheduleAutoFit(DataGrid grid)
    {
        if (!FitStates.TryGetValue(grid, out _))
        {
            return;
        }

        grid.Dispatcher.BeginInvoke(() => QueueFit(grid), DispatcherPriority.Loaded);
        grid.Dispatcher.BeginInvoke(() => QueueFit(grid), DispatcherPriority.Render);
        grid.Dispatcher.BeginInvoke(() => QueueFit(grid), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Dense multi-column grids (e.g. Test Results): fixed widths from header + cell content.
    /// Insight column is star-sized so it expands and never clips AI summary text.
    /// </summary>
    public static void AutoFitColumnsDense(DataGrid grid)
    {
        if (grid.Columns.Count == 0)
        {
            return;
        }

        ApplyDefaultCellTextStyles(grid);
        grid.UpdateLayout();

        var items = GetItems(grid).Take(MaxRowsToMeasure).ToList();

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            var headerWidth = MeasureHeaderWidth(grid, column);

            if (IsInsightColumn(column))
            {
                var contentWidth = MeasureColumnContentWidth(grid, column, items);
                var min = Math.Ceiling(
                    Math.Max(DenseInsightColumnMinWidth, Math.Max(headerWidth, contentWidth)) + ColumnWidthSlack);
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                column.MinWidth = min;
                column.MaxWidth = double.PositiveInfinity;
                continue;
            }

            double required;

            if (IsWrappingColumn(column))
            {
                var contentWidth = MeasureColumnContentWidth(grid, column, items);
                required = Math.Max(
                    DenseWrapColumnMinWidth,
                    Math.Max(headerWidth, contentWidth));
            }
            else if (column is DataGridTemplateColumn)
            {
                required = Math.Max(
                    MinColumnWidth,
                    Math.Max(headerWidth, TemplateColumnMinWidth));
            }
            else
            {
                var contentWidth = MeasureColumnContentWidth(grid, column, items);
                required = Math.Max(MinColumnWidth, Math.Max(headerWidth, contentWidth));
            }

            required = Math.Ceiling(required + ColumnWidthSlack);
            column.Width = new DataGridLength(required);
            column.MinWidth = required;
            column.MaxWidth = double.PositiveInfinity;
        }

        grid.UpdateLayout();
    }

    public static void AutoFitColumns(DataGrid grid, params int[] starColumnIndices)
    {
        if (grid.Columns.Count == 0)
        {
            return;
        }

        ApplyDefaultCellTextStyles(grid);
        grid.UpdateLayout();

        var starSet = starColumnIndices.ToHashSet();
        var items = GetItems(grid).Take(MaxRowsToMeasure).ToList();

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            var headerWidth = MeasureHeaderWidth(grid, column);

            if (starSet.Contains(i))
            {
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                var min = Math.Max(MinColumnWidth, headerWidth);
                if (IsWrappingColumn(column))
                {
                    min = Math.Max(min, 160);
                }

                column.MinWidth = min;
                continue;
            }

            if (IsWrappingColumn(column))
            {
                var wrapWidth = Math.Max(MinColumnWidth, headerWidth);
                column.Width = new DataGridLength(wrapWidth);
                column.MinWidth = wrapWidth;
                continue;
            }

            if (column is DataGridTemplateColumn)
            {
                var templateWidth = Math.Max(MinColumnWidth, Math.Max(headerWidth, TemplateColumnMinWidth));
                column.Width = new DataGridLength(templateWidth);
                column.MinWidth = templateWidth;
                continue;
            }

            var contentWidth = MeasureColumnContentWidth(grid, column, items);
            var required = Math.Ceiling(Math.Max(MinColumnWidth, Math.Max(headerWidth, contentWidth)) + ColumnWidthSlack);
            column.Width = new DataGridLength(required);
            column.MinWidth = required;
        }

        grid.UpdateLayout();
    }

    public static int IndexOfStarColumn(DataGrid grid, string headerContains)
    {
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (grid.Columns[i].Header?.ToString()?.Contains(headerContains, StringComparison.OrdinalIgnoreCase) == true)
            {
                return i;
            }
        }

        return -1;
    }

    private static void ApplyDefaultCellTextStyles(DataGrid grid)
    {
        var noTrim = grid.TryFindResource("DataGridCellText") as Style;
        if (noTrim is null)
        {
            return;
        }

        foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
        {
            if (IsWrappingColumn(column))
            {
                continue;
            }

            if (column.ElementStyle is null)
            {
                column.ElementStyle = noTrim;
            }
            else if (column.ElementStyle.Setters.OfType<Setter>()
                         .All(s => s.Property != TextBlock.TextTrimmingProperty))
            {
                var merged = new Style(column.ElementStyle.TargetType, column.ElementStyle);
                merged.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None));
                column.ElementStyle = merged;
            }
        }
    }

    private static bool IsInsightColumn(DataGridColumn column) =>
        column.Header?.ToString()?.Equals("Insight", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsWrappingColumn(DataGridColumn column) =>
        column is DataGridTextColumn { ElementStyle: { } style } && StyleSetsWrapping(style);

    private static bool StyleSetsWrapping(Style style)
    {
        foreach (var setter in style.Setters.OfType<Setter>())
        {
            if (setter.Property == TextBlock.TextWrappingProperty
                && setter.Value is TextWrapping wrap
                && wrap != TextWrapping.NoWrap)
            {
                return true;
            }
        }

        return style.BasedOn is not null && StyleSetsWrapping(style.BasedOn);
    }

    private static double MeasureColumnContentWidth(DataGrid grid, DataGridColumn column, IReadOnlyList<object> items)
    {
        if (column is not DataGridTextColumn textColumn)
        {
            return 0;
        }

        var bindingPath = GetBindingPath(textColumn.Binding);
        if (bindingPath is null)
        {
            return 0;
        }

        var max = 0.0;
        foreach (var item in items)
        {
            var text = GetCellDisplayText(item, bindingPath);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            max = Math.Max(max, MeasureTextWidth(grid, text, grid.FontSize));
        }

        return max + CellHorizontalPadding;
    }

    private static double MeasureHeaderWidth(DataGrid grid, DataGridColumn column)
    {
        var headerText = column.Header switch
        {
            null => string.Empty,
            string text => text,
            _ => column.Header.ToString() ?? string.Empty
        };

        if (string.IsNullOrEmpty(headerText))
        {
            return MinColumnWidth;
        }

        return MeasureTextWidth(grid, headerText, grid.FontSize) + HeaderHorizontalPadding;
    }

    private static double MeasureTextWidth(DataGrid grid, string text, double fontSize)
    {
        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            dpi);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static string? GetBindingPath(BindingBase? bindingBase)
    {
        if (bindingBase is not Binding binding || binding.Path is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(binding.Path.Path) ? null : binding.Path.Path;
    }

    private static string? GetBoundText(object item, string bindingPath)
    {
        object? current = item;
        foreach (var segment in bindingPath.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            var prop = current.GetType().GetProperty(
                segment,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null)
            {
                return null;
            }

            current = prop.GetValue(current);
        }

        return current?.ToString();
    }

    private static string? GetCellDisplayText(object item, string bindingPath)
    {
        var text = GetBoundText(item, bindingPath);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (bindingPath.Equals("BestMetricDisplay", StringComparison.OrdinalIgnoreCase))
        {
            var kind = GetBoundText(item, "BenchmarkKind");
            var embedMs = GetBoundText(item, "BestEmbedMs");
            var tps = GetBoundText(item, "BestTps");
            if (kind?.Equals("Embed", StringComparison.OrdinalIgnoreCase) == true)
            {
                return double.TryParse(embedMs, out var ms) && ms > 0 ? $"{ms:F1} ms" : "-";
            }

            return double.TryParse(tps, out var tok) && tok > 0 ? $"{tok:F1}" : "-";
        }

        return text;
    }

    private static IEnumerable<object> GetItems(DataGrid grid)
    {
        if (grid.ItemsSource is IEnumerable source)
        {
            foreach (var item in source)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        foreach (var item in grid.Items)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private sealed class FitState
    {
        public bool Attached { get; set; }
        public bool PendingFit { get; set; }
        public Action<DataGrid>? FitAction { get; set; }
        public DispatcherTimer? Timer { get; set; }
    }
}