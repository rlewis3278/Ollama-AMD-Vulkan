using System.Windows.Threading;
using OllamaToolkit.ModelRegistry.Models;

namespace OllamaToolkit.App.Services;

public sealed class CatalogRowRefreshAnimator
{
    private readonly DispatcherTimer _flashTimer;
    private CatalogRowViewModel? _activeRow;

    public CatalogRowRefreshAnimator(Dispatcher dispatcher)
    {
        _flashTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _flashTimer.Tick += (_, _) =>
        {
            if (_activeRow?.RefreshState == CatalogRowRefreshState.Processing)
            {
                _activeRow.RefreshFlashPhase = !_activeRow.RefreshFlashPhase;
            }
        };
    }

    public void BeginRow(CatalogRowViewModel row)
    {
        _activeRow = row;
        row.RefreshState = CatalogRowRefreshState.Processing;
        row.RefreshFlashPhase = true;
        if (!_flashTimer.IsEnabled)
        {
            _flashTimer.Start();
        }
    }

    public void CompleteRow(CatalogRowViewModel row, bool highlightComplete = true)
    {
        row.RefreshState = highlightComplete
            ? CatalogRowRefreshState.Complete
            : CatalogRowRefreshState.None;
        row.RefreshFlashPhase = false;
        if (_activeRow == row)
        {
            _activeRow = null;
        }
    }

    public void Stop()
    {
        _flashTimer.Stop();
        _activeRow = null;
    }

    public static void ResetAll(IEnumerable<CatalogRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            row.RefreshState = CatalogRowRefreshState.None;
            row.RefreshFlashPhase = false;
        }
    }
}