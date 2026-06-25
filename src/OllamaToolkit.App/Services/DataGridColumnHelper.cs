using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OllamaToolkit.App.Services;

public static class DataGridColumnHelper
{
    private const double MinColumnWidth = 48;
    private const double HeaderExtraPadding = 20;

    public static void AutoFitColumns(DataGrid grid, params int[] starColumnIndices)
    {
        if (grid.Columns.Count == 0)
        {
            return;
        }

        grid.UpdateLayout();
        var starSet = starColumnIndices.ToHashSet();

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            var headerWidth = MeasureHeaderWidth(grid, column);

            if (starSet.Contains(i))
            {
                if (column.Width.IsAbsolute || column.Width.IsSizeToCells || column.Width.IsSizeToHeader)
                {
                    column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                }

                column.MinWidth = Math.Max(column.MinWidth, headerWidth);
                continue;
            }

            column.Width = DataGridLength.SizeToCells;
            column.MinWidth = Math.Max(MinColumnWidth, headerWidth);
        }

        grid.UpdateLayout();

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (starSet.Contains(i))
            {
                continue;
            }

            var column = grid.Columns[i];
            var headerWidth = MeasureHeaderWidth(grid, column);
            var required = Math.Max(column.MinWidth, Math.Max(headerWidth, column.ActualWidth));
            if (column.ActualWidth < required - 0.5)
            {
                column.Width = new DataGridLength(required);
            }
        }
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

        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        var formatted = new FormattedText(
            headerText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            grid.FontSize,
            Brushes.White,
            dpi);

        // DataGridColumnHeader padding (8px horizontal each side) + sort glyph / border slack
        return formatted.Width + 16 + HeaderExtraPadding;
    }
}