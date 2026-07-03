using OllamaToolkit.ModelRegistry.Models;

namespace OllamaToolkit.App.Services;

public sealed class CatalogRowRefreshAnimator
{
    private readonly MasterFlashClock _clock;
    private readonly HashSet<CatalogRowViewModel> _processingRows = new();
    private IDisposable? _subscription;

    public CatalogRowRefreshAnimator(MasterFlashClock clock) => _clock = clock;

    public void BeginRow(CatalogRowViewModel row)
    {
        if (row.IsTesting)
        {
            return;
        }

        row.RefreshHighlight = CatalogRowRefreshHighlight.None;
        row.RefreshState = CatalogRowRefreshState.Processing;
        row.RefreshFlashPhase = _clock.IsAccentPhase;
        _processingRows.Add(row);
        EnsureSubscribed();
    }

    public void CompleteRow(
        CatalogRowViewModel row,
        CatalogRowRefreshHighlight highlight = CatalogRowRefreshHighlight.None)
    {
        if (row.IsTesting)
        {
            return;
        }

        row.RefreshHighlight = highlight;
        row.RefreshState = highlight != CatalogRowRefreshHighlight.None
            ? CatalogRowRefreshState.Complete
            : CatalogRowRefreshState.None;
        row.RefreshFlashPhase = false;
        _processingRows.Remove(row);
        if (_processingRows.Count == 0)
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        _processingRows.Clear();
    }

    public static void ResetAll(IEnumerable<CatalogRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsTesting)
            {
                continue;
            }

            row.RefreshState = CatalogRowRefreshState.None;
            row.RefreshHighlight = CatalogRowRefreshHighlight.None;
            row.RefreshFlashPhase = false;
        }
    }

    private void EnsureSubscribed()
    {
        if (_subscription is not null)
        {
            return;
        }

        _subscription = _clock.Subscribe(OnPhaseChanged);
    }

    private void OnPhaseChanged(bool accentPhase)
    {
        foreach (var row in _processingRows)
        {
            if (row.RefreshState == CatalogRowRefreshState.Processing)
            {
                row.RefreshFlashPhase = accentPhase;
            }
        }
    }
}