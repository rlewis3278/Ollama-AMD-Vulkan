using OllamaToolkit.ModelRegistry.Models;

namespace OllamaToolkit.App.Services;

public sealed class TestingRowHighlightCoordinator : IDisposable
{
    private readonly MasterFlashClock _clock;
    private readonly HashSet<string> _activeLibraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _testedUndownloadLibraries = new(StringComparer.OrdinalIgnoreCase);
    private Func<IReadOnlyList<CatalogRowViewModel>>? _catalogRows;
    private Func<IEnumerable<ModelLaunchRowViewModel>>? _modelRows;
    private IDisposable? _subscription;

    public TestingRowHighlightCoordinator(MasterFlashClock clock) => _clock = clock;

    public void Bind(
        Func<IReadOnlyList<CatalogRowViewModel>> catalogRows,
        Func<IEnumerable<ModelLaunchRowViewModel>> modelRows)
    {
        _catalogRows = catalogRows;
        _modelRows = modelRows;
    }

    public bool IsActive(string library) => _activeLibraries.Contains(NormalizeLibrary(library));

    public void SetExclusiveActive(string modelOrLibrary)
    {
        foreach (var active in _activeLibraries.ToList())
        {
            SetActive(active, on: false);
        }

        SetActive(modelOrLibrary, on: true);
    }

    public void SetActive(string modelOrLibrary, bool on)
    {
        var library = NormalizeLibrary(modelOrLibrary);
        if (on)
        {
            _activeLibraries.Add(library);
            EnsureSubscribed();
            ApplyTestingState(library, active: true, _clock.IsAccentPhase);
        }
        else
        {
            _activeLibraries.Remove(library);
            if (_testedUndownloadLibraries.Contains(library))
            {
                ApplyTestedUndownloadState(library);
            }
            else
            {
                ApplyTestingState(library, active: false, accentPhase: false);
            }

            if (_activeLibraries.Count == 0)
            {
                _subscription?.Dispose();
                _subscription = null;
            }
        }
    }

    public void MarkTestedUndownload(string modelOrLibrary)
    {
        var library = NormalizeLibrary(modelOrLibrary);
        _testedUndownloadLibraries.Add(library);
        _activeLibraries.Remove(library);
        ApplyTestedUndownloadState(library);
    }

    public void ClearAll()
    {
        foreach (var library in _activeLibraries.ToList())
        {
            if (_testedUndownloadLibraries.Contains(library))
            {
                ApplyTestedUndownloadState(library);
            }
            else
            {
                ApplyTestingState(library, active: false, accentPhase: false);
            }
        }

        _activeLibraries.Clear();
        _subscription?.Dispose();
        _subscription = null;
    }

    public void ClearTestedUndownloadMarks() => _testedUndownloadLibraries.Clear();

    public void ReapplyActive()
    {
        foreach (var library in _testedUndownloadLibraries)
        {
            ApplyTestedUndownloadState(library);
        }

        if (_activeLibraries.Count == 0)
        {
            return;
        }

        ApplyPhase(_clock.IsAccentPhase);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _activeLibraries.Clear();
        _testedUndownloadLibraries.Clear();
    }

    private void EnsureSubscribed()
    {
        if (_subscription is not null)
        {
            return;
        }

        _subscription = _clock.Subscribe(ApplyPhase);
    }

    private void ApplyPhase(bool accentPhase)
    {
        foreach (var library in _activeLibraries)
        {
            ApplyTestingState(library, active: true, accentPhase);
        }
    }

    private void ApplyTestedUndownloadState(string library)
    {
        if (_catalogRows?.Invoke() is not { } catalogRows)
        {
            return;
        }

        foreach (var row in catalogRows.Where(r => MatchesLibrary(r.Name, library)))
        {
            row.IsTesting = false;
            row.RefreshFlashPhase = false;
            row.RefreshState = CatalogRowRefreshState.None;
            row.RefreshHighlight = CatalogRowRefreshHighlight.TestedUndownload;
        }
    }

    private void ApplyTestingState(string library, bool active, bool accentPhase)
    {
        if (_catalogRows?.Invoke() is { } catalogRows)
        {
            foreach (var row in catalogRows.Where(r => MatchesLibrary(r.Name, library)))
            {
                if (active)
                {
                    row.IsTesting = true;
                    row.RefreshState = CatalogRowRefreshState.Testing;
                    row.RefreshHighlight = CatalogRowRefreshHighlight.Testing;
                    row.RefreshFlashPhase = accentPhase;
                }
                else if (_testedUndownloadLibraries.Contains(library))
                {
                    ApplyTestedUndownloadState(library);
                }
                else
                {
                    row.IsTesting = false;
                    row.RefreshFlashPhase = false;
                    if (row.Installed)
                    {
                        row.RefreshState = CatalogRowRefreshState.Complete;
                        row.RefreshHighlight = CatalogRowRefreshHighlight.Installed;
                    }
                    else if (row.RefreshHighlight == CatalogRowRefreshHighlight.TestedUndownload)
                    {
                        row.RefreshState = CatalogRowRefreshState.None;
                    }
                    else
                    {
                        row.RefreshState = CatalogRowRefreshState.None;
                        row.RefreshHighlight = CatalogRowRefreshHighlight.None;
                    }
                }
            }
        }

        if (_modelRows?.Invoke() is { } modelRows)
        {
            foreach (var row in modelRows.Where(r => MatchesLibrary(r.Model, library)))
            {
                row.IsBeingTested = active;
                row.TestingFlashPhase = active && accentPhase;
            }
        }
    }

    private static string NormalizeLibrary(string modelOrLibrary) =>
        modelOrLibrary.Split(':')[0];

    private static bool MatchesLibrary(string modelOrLibrary, string library) =>
        NormalizeLibrary(modelOrLibrary).Equals(library, StringComparison.OrdinalIgnoreCase);
}