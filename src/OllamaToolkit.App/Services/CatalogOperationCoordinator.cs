namespace OllamaToolkit.App.Services;

public enum CatalogToolbarOperationType
{
    None,
    Categorize,
    RefreshCatalog,
    RefreshDescriptions,
    ClearCatalog
}

public sealed class CatalogOperationCoordinator
{
    private CatalogToolbarOperationType _active = CatalogToolbarOperationType.None;
    private int _generation;
    private CancellationTokenSource? _cts;

    public CatalogToolbarOperationType ActiveOperation => _active;

    public bool IsToolbarBusy => _active != CatalogToolbarOperationType.None;

    public object? ActiveFlashSender { get; private set; }

    public bool TryBegin(
        CatalogToolbarOperationType operation,
        object? flashSender,
        out CancellationToken token,
        out int generation)
    {
        if (_active != CatalogToolbarOperationType.None)
        {
            token = default;
            generation = 0;
            return false;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _active = operation;
        ActiveFlashSender = flashSender;
        _generation++;
        generation = _generation;
        token = _cts.Token;
        return true;
    }

    public void Cancel() => _cts?.Cancel();

    public void End(int generation)
    {
        if (generation != _generation)
        {
            return;
        }

        _active = CatalogToolbarOperationType.None;
        ActiveFlashSender = null;
    }

    public CancellationTokenSource? CreateLinkedTokenSource(CancellationToken workToken) =>
        _cts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(workToken)
            : CancellationTokenSource.CreateLinkedTokenSource(workToken, _cts.Token);
}