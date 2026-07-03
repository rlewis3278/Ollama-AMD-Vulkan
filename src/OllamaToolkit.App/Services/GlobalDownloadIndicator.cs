using System.Windows;
using System.Windows.Controls;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.App.Services;

public sealed class GlobalDownloadIndicator
{
    private readonly TextBlock _label;
    private int _activeDownloads;

    public GlobalDownloadIndicator(TextBlock label) => _label = label;

    public void Begin(string modelName)
    {
        _activeDownloads++;
        _label.Visibility = Visibility.Visible;
        _label.Text = $"{modelName} — Starting…";
    }

    public void Update(string modelName, ModelPullProgress update)
    {
        if (_activeDownloads <= 0)
        {
            return;
        }

        var speedEta = DownloadProgressFormatter.FormatSpeedEta(update);
        if (!string.IsNullOrWhiteSpace(speedEta))
        {
            _label.Text = $"{modelName} — {speedEta}";
            return;
        }

        if (update.TotalBytes is > 0 && update.CompletedBytes is >= 0)
        {
            var pct = update.Percent ?? (int)Math.Clamp(
                100.0 * update.CompletedBytes.Value / update.TotalBytes.Value,
                0,
                100);
            _label.Text =
                $"{modelName} — {ModelSizeFormatter.FormatBytes(update.CompletedBytes.Value)} / {ModelSizeFormatter.FormatBytes(update.TotalBytes.Value)} ({pct}%)";
            return;
        }

        if (update.Percent is int percent)
        {
            _label.Text = $"{modelName} — {percent}%";
            return;
        }

        _label.Text = string.IsNullOrWhiteSpace(update.Status)
            ? $"{modelName} — Downloading…"
            : $"{modelName} — {update.Status}";
    }

    public void End()
    {
        if (_activeDownloads > 0)
        {
            _activeDownloads--;
        }

        if (_activeDownloads <= 0)
        {
            _activeDownloads = 0;
            _label.Visibility = Visibility.Collapsed;
            _label.Text = string.Empty;
        }
    }

    public void ForceEndAll()
    {
        _activeDownloads = 0;
        _label.Visibility = Visibility.Collapsed;
        _label.Text = string.Empty;
    }
}