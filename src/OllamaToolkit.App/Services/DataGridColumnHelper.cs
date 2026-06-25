using System.Windows.Controls;

namespace OllamaToolkit.App.Services;

public static class DataGridColumnHelper
{
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
            if (starSet.Contains(i))
            {
                if (grid.Columns[i].Width.IsAbsolute || grid.Columns[i].Width.IsSizeToCells)
                {
                    grid.Columns[i].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                }

                continue;
            }

            grid.Columns[i].Width = DataGridLength.SizeToCells;
            grid.Columns[i].MinWidth = 48;
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
}