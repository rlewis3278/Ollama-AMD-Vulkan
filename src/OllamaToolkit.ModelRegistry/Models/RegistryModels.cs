using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OllamaToolkit.ModelRegistry.Models;

public enum CatalogDescriptionDisplayMode
{
    Download,
    Ai
}

public enum CatalogRowRefreshState
{
    None,
    Processing,
    Complete,
    Testing
}

public enum CatalogRowRefreshHighlight
{
    None,
    Installed,
    Description,
    Testing
}

public sealed class CatalogRowViewModel : INotifyPropertyChanged
{
    private string _description = string.Empty;
    private string _listDescription = string.Empty;
    private string _downloadDescription = string.Empty;
    private string _aiDescription = string.Empty;
    private string _displayDescription = string.Empty;
    private string _category = string.Empty;
    private string _parameterSize = "-";
    private string _fileSize = "-";
    private string _tags = string.Empty;
    private bool _installed;
    private string _installedDisplay = "Unknown";
    private bool _isDownloading;
    private int _sortOrder;
    private CatalogRowRefreshState _refreshState = CatalogRowRefreshState.None;
    private CatalogRowRefreshHighlight _refreshHighlight = CatalogRowRefreshHighlight.None;
    private bool _refreshFlashPhase;
    private string _bestMode = "-";
    private string _bestTps = "-";
    private bool _isTesting;

    public required string Name { get; init; }
    public string DefaultPullTag { get; init; } = string.Empty;
    public bool IsCloudOnly { get; init; }

    public string Description
    {
        get => _description;
        init => SetField(ref _description, value);
    }

    public string ListDescription
    {
        get => _listDescription;
        init => SetField(ref _listDescription, value);
    }

    public string DownloadDescription
    {
        get => _downloadDescription;
        set => SetField(ref _downloadDescription, value);
    }

    public string AiDescription
    {
        get => _aiDescription;
        set => SetField(ref _aiDescription, value);
    }

    public string DisplayDescription
    {
        get => _displayDescription;
        set => SetField(ref _displayDescription, value);
    }

    public string Category
    {
        get => _category;
        set => SetField(ref _category, value);
    }

    public string ParameterSize
    {
        get => _parameterSize;
        init => SetField(ref _parameterSize, value);
    }

    public string FileSize
    {
        get => _fileSize;
        set => SetField(ref _fileSize, value);
    }

    public string Tags
    {
        get => _tags;
        init => SetField(ref _tags, value);
    }

    public bool Installed
    {
        get => _installed;
        set => SetField(ref _installed, value);
    }

    public string InstalledDisplay
    {
        get => _installedDisplay;
        set => SetField(ref _installedDisplay, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetField(ref _isDownloading, value);
    }

    public int SortOrder
    {
        get => _sortOrder;
        init => SetField(ref _sortOrder, value);
    }

    public CatalogRowRefreshState RefreshState
    {
        get => _refreshState;
        set => SetField(ref _refreshState, value);
    }

    public bool RefreshFlashPhase
    {
        get => _refreshFlashPhase;
        set => SetField(ref _refreshFlashPhase, value);
    }

    public CatalogRowRefreshHighlight RefreshHighlight
    {
        get => _refreshHighlight;
        set => SetField(ref _refreshHighlight, value);
    }

    public string BestMode
    {
        get => _bestMode;
        set => SetField(ref _bestMode, value);
    }

    public string BestTps
    {
        get => _bestTps;
        set => SetField(ref _bestTps, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => SetField(ref _isTesting, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class UndownloadTestCandidate
{
    public required string LibraryName { get; init; }
    public required string PullTag { get; init; }
    public long FileSizeBytes { get; init; }
    public bool HasKnownFileSize { get; init; }
}

public sealed class TestResultRowViewModel
{
    public required string Model { get; init; }
    public string Category { get; init; } = string.Empty;
    public string BenchmarkKind { get; init; } = "Generate";
    public string BestMode { get; init; } = string.Empty;
    public double BestTps { get; init; }
    public double BestEmbedMs { get; init; }
    public string BestMetricDisplay =>
        OllamaToolkit.BenchmarkStore.BenchmarkMetricFormatter.Format(BenchmarkKind, BestTps, BestEmbedMs);
    public string CpuResult { get; init; } = "-";
    public string ApuResult { get; init; } = "-";
    public string GpuResult { get; init; } = "-";
    public string HybridResult { get; init; } = "-";
    public string Insight { get; init; } = string.Empty;
    public string LastTested { get; init; } = string.Empty;
    public string? ReportPath { get; init; }
}