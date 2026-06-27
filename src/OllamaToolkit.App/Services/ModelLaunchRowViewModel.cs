using System.ComponentModel;
using System.Runtime.CompilerServices;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;

namespace OllamaToolkit.App.Services;

public sealed class ModelLaunchRowViewModel : INotifyPropertyChanged
{
    private bool _isBeingTested;
    private bool _testingFlashPhase;

    public required string Model { get; init; }
    public string BenchmarkKind { get; init; } = BenchmarkKinds.Generate;
    public double SizeGB { get; init; }
    public string FileSize => OllamaToolkit.Core.ModelSizeFormatter.FormatGb(SizeGB);
    public string Quantization { get; init; } = "-";
    public string ParameterSize { get; init; } = "-";
    public string Digest { get; init; } = string.Empty;
    public string Status { get; init; } = "Untested";
    public string BestMode { get; init; } = string.Empty;
    public double BestTps { get; init; }
    public double BestEmbedMs { get; init; }
    public string BestMetricDisplay { get; init; } = "-";
    public string LastTested { get; init; } = string.Empty;
    public bool NeedsRetest { get; init; } = true;
    public int RecommendedCtx { get; init; }
    public string Category { get; init; } = string.Empty;
    public string DisplayDescription { get; init; } = string.Empty;
    public Dictionary<string, ModeResultEntry>? Results { get; init; }

    public bool IsBeingTested
    {
        get => _isBeingTested;
        set => SetField(ref _isBeingTested, value);
    }

    public bool TestingFlashPhase
    {
        get => _testingFlashPhase;
        set => SetField(ref _testingFlashPhase, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ModelProfileSummary WithCategory(ModelProfileSummary summary, string category) =>
        new()
        {
            Model = summary.Model,
            BenchmarkKind = summary.BenchmarkKind,
            SizeGB = summary.SizeGB,
            Quantization = summary.Quantization,
            ParameterSize = summary.ParameterSize,
            Digest = summary.Digest,
            Status = summary.Status,
            BestMode = summary.BestMode,
            BestTps = summary.BestTps,
            BestEmbedMs = summary.BestEmbedMs,
            LastTested = summary.LastTested,
            NeedsRetest = summary.NeedsRetest,
            RecommendedCtx = summary.RecommendedCtx,
            Category = category,
            Results = summary.Results
        };

    public static ModelLaunchRowViewModel FromSummary(ModelProfileSummary summary, string? displayDescription = null) =>
        new()
        {
            Model = summary.Model,
            BenchmarkKind = summary.BenchmarkKind,
            SizeGB = summary.SizeGB,
            Quantization = summary.Quantization,
            ParameterSize = summary.ParameterSize,
            Digest = summary.Digest,
            Status = summary.Status,
            BestMode = summary.BestMode,
            BestTps = summary.BestTps,
            BestEmbedMs = summary.BestEmbedMs,
            BestMetricDisplay = BenchmarkMetricFormatter.Format(
                summary.BenchmarkKind, summary.BestTps, summary.BestEmbedMs),
            LastTested = summary.LastTested,
            NeedsRetest = summary.NeedsRetest,
            RecommendedCtx = summary.RecommendedCtx,
            Category = summary.Category,
            DisplayDescription = displayDescription ?? string.Empty,
            Results = summary.Results
        };

    public ModelProfileSummary ToSummary() =>
        new()
        {
            Model = Model,
            BenchmarkKind = BenchmarkKind,
            SizeGB = SizeGB,
            Quantization = Quantization,
            ParameterSize = ParameterSize,
            Digest = Digest,
            Status = Status,
            BestMode = BestMode,
            BestTps = BestTps,
            BestEmbedMs = BestEmbedMs,
            LastTested = LastTested,
            NeedsRetest = NeedsRetest,
            RecommendedCtx = RecommendedCtx,
            Category = Category,
            Results = Results
        };

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