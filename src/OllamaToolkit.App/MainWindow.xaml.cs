using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OllamaToolkit.AiAssist;
using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.App.Services;
using OllamaToolkit.BenchmarkRunner;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCatalog.Models;
using OllamaToolkit.ModelCategory;
using OllamaToolkit.ModelRegistry.Models;

namespace OllamaToolkit.App;

public partial class MainWindow : Window
{
    private readonly AppServices _svc = App.Services;
    private readonly Dictionary<string, Button> _modeCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModeCardPresenter _modeCardPresenter;
    private CancellationTokenSource? _chatCts;
    private CancellationTokenSource? _benchmarkCts;
    private CancellationTokenSource? _undownloadCts;
    private int _testOperations;
    private Task? _activeTestWork;
    private readonly List<Button> _activeTestFlashButtons = new();
    private readonly HashSet<string> _undownloadPurpleLibraries = new(StringComparer.OrdinalIgnoreCase);
    private bool _undownloadBatchActive;
    private string? _runModel;
    private readonly List<ChatMessage> _chatMessages = new();
    private readonly DispatcherTimer _activityTimer;
    private readonly DispatcherTimer _catalogSearchTimer;
    private readonly DispatcherTimer _testSettingsTimer;
    private readonly ThrottledUpdater _chatUpdater;
    private readonly ThrottledUpdater _testLogUpdater;
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Status)> _testModeProgress = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _nlRankedCatalog = new();
    private bool _suppressSummarizerComboSave;
    private CatalogDescriptionDisplayMode _catalogDescriptionMode = CatalogDescriptionDisplayMode.Download;
    private readonly FlashButtonRegistry _flashButtons;
    private readonly AiProcessingFlashPresenter _aiProcessingFlash;
    private readonly ObservableCollection<CatalogRowViewModel> _catalogRows = new();
    private readonly CatalogRowRefreshAnimator _catalogRowAnimator;
    private Dictionary<string, CatalogRowViewModel> _catalogRowByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressCatalogUiEvents;
    private bool _catalogRefreshInProgress;
    private bool _descriptionRefreshInProgress;
    private bool _catalogDownloadInProgress;
    private CancellationTokenSource? _catalogDownloadCts;
    private CancellationTokenSource? _refreshCatalogCts;
    private CancellationTokenSource? _refreshDescriptionsCts;
    private CancellationTokenSource? _classificationCts;
    private int _catalogToolbarOperations;
    private int _aiActivityDepth;

    public MainWindow()
    {
        InitializeComponent();
        _flashButtons = new FlashButtonRegistry(this);
        _aiProcessingFlash = new AiProcessingFlashPresenter(AiStatusButton, this);
        _catalogRowAnimator = new CatalogRowRefreshAnimator(Dispatcher);
        CatalogGrid.ItemsSource = _catalogRows;
        _modeCardPresenter = new ModeCardPresenter(this);
        _modeCards["CPU"] = CpuCard;
        _modeCards["APU"] = ApuCard;
        _modeCards["GPU"] = GpuCard;
        _modeCards["Hybrid"] = HybridCard;
        _modeCards["ROCm"] = RocmCard;

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _activityTimer.Tick += (_, _) => RefreshActivityLog();
        _catalogSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _catalogSearchTimer.Tick += async (_, _) =>
        {
            _catalogSearchTimer.Stop();
            await RefreshCatalogUiAsync().ConfigureAwait(true);
        };
        _testSettingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _testSettingsTimer.Tick += async (_, _) =>
        {
            _testSettingsTimer.Stop();
            await SuggestBenchmarkSettingsAsync().ConfigureAwait(true);
        };
        _chatUpdater = new ThrottledUpdater(TimeSpan.FromMilliseconds(33), Dispatcher);
        _testLogUpdater = new ThrottledUpdater(TimeSpan.FromMilliseconds(33), Dispatcher);
        DataGridColumnHelper.AttachAutoFit(ModelsGrid, FitGridColumns);
        DataGridColumnHelper.AttachAutoFit(CatalogGrid, FitGridColumns);
        DataGridColumnHelper.AttachAutoFit(TestResultsGrid, FitGridColumns);
        Loaded += OnLoadedAsync;
        Closed += (_, _) =>
        {
            _activityTimer.Stop();
            _catalogSearchTimer.Stop();
            _testSettingsTimer.Stop();
            _modeCardPresenter.Stop();
            _flashButtons.StopAll();
            _aiProcessingFlash.Stop();
            _catalogRowAnimator.Stop();
        };
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        ApplyCatalogDescriptionModeUi();
        UpdateCatalogStopButtonUi();
        UpdateStopTestButtonUi();
        InitModeCards();
        InitAiFeatureToggles();
        InitCategoryFilter();
        _activityTimer.Start();
        await RefreshAllAsync().ConfigureAwait(true);
        _ = ScheduleLogScanAsync();
    }

    private void InitCategoryFilter()
    {
        CategoryFilterCombo.ItemsSource = new[] { "All" }.Concat(CategoryNormalizer.AllCategories).ToList();
        CategoryFilterCombo.SelectedIndex = 0;
    }

    private static Button? TaskButton(object? sender) => sender as Button;

    private bool BeginTaskFlash(object sender)
    {
        if (TaskButton(sender) is not { } button)
        {
            return true;
        }

        return _flashButtons.TryBegin(button);
    }

    private void EndTaskFlashSuccess(
        object sender,
        string successLabel = "Done",
        int holdSeconds = 10,
        Action? onRestored = null,
        FlashSuccessStyle style = FlashSuccessStyle.Active)
    {
        if (TaskButton(sender) is { } button)
        {
            _flashButtons.EndSuccess(button, successLabel, holdSeconds, onRestored, style);
        }
    }

    private void EndTaskFlashIdle(object sender)
    {
        if (TaskButton(sender) is { } button)
        {
            _flashButtons.EndIdle(button);
        }
    }

    private bool BeginTestOperation(object? sender, FlashColorScheme scheme = FlashColorScheme.YellowBlack)
    {
        _testOperations++;
        UpdateStopTestButtonUi();

        if (TaskButton(sender) is not { } button)
        {
            return true;
        }

        if (!_flashButtons.TryBegin(button, scheme))
        {
            _testOperations--;
            UpdateStopTestButtonUi();
            return false;
        }

        if (!_activeTestFlashButtons.Contains(button))
        {
            _activeTestFlashButtons.Add(button);
        }

        return true;
    }

    private void EndTestOperationSuccess(
        object? sender,
        string successLabel = "Complete",
        int holdSeconds = 10,
        Action? onRestored = null)
    {
        if (TaskButton(sender) is { } button)
        {
            _flashButtons.EndSuccess(button, successLabel, holdSeconds, onRestored);
            _activeTestFlashButtons.Remove(button);
        }

        if (_testOperations > 0)
        {
            _testOperations--;
        }

        UpdateStopTestButtonUi();
    }

    private void FinishTestOperation(object? sender, bool success, bool cancelled)
    {
        if (TaskButton(sender) is { } button)
        {
            if (success && !cancelled)
            {
                _flashButtons.EndSuccess(button, "Complete", 10);
            }
            else
            {
                _flashButtons.EndIdle(button);
            }

            _activeTestFlashButtons.Remove(button);
        }

        if (_testOperations > 0)
        {
            _testOperations--;
        }

        UpdateStopTestButtonUi();
        ClearCatalogTestingHighlights();
    }

    private void UpdateStopTestButtonUi()
    {
        if (_testOperations > 0)
        {
            StopTestBtn.Background = (Brush)FindResource("Brush.Accent");
            StopTestBtn.BorderBrush = (Brush)FindResource("Brush.Accent");
            StopTestBtn.Foreground = Brushes.White;
        }
        else
        {
            StopTestBtn.Background = (Brush)FindResource("Brush.Button");
            StopTestBtn.BorderBrush = (Brush)FindResource("Brush.PanelBorder");
            StopTestBtn.Foreground = (Brush)FindResource("Brush.Text");
        }
    }

    private async Task CancelTestOperationsAsync()
    {
        _benchmarkCts?.Cancel();
        _undownloadCts?.Cancel();

        if (_activeTestWork is not null)
        {
            try
            {
                await _activeTestWork.ConfigureAwait(true);
            }
            catch
            {
                // Work item may throw on cancel.
            }
        }

        await UiDispatcher.InvokeAsync(() =>
        {
            foreach (var button in _activeTestFlashButtons.ToList())
            {
                _flashButtons.EndIdle(button);
            }

            _activeTestFlashButtons.Clear();
            _testOperations = 0;
            UpdateStopTestButtonUi();
            ClearCatalogTestingHighlights();
            TestStatusLabel.Text = "Benchmark queue stopped.";
        }).ConfigureAwait(true);
    }

    private void SetCatalogTestingHighlight(string modelOrLibrary, bool on)
    {
        var library = modelOrLibrary.Split(':')[0];
        if (!_catalogRowByName.TryGetValue(library, out var row))
        {
            return;
        }

        row.IsTesting = on;
        row.RefreshHighlight = on ? CatalogRowRefreshHighlight.Testing : CatalogRowRefreshHighlight.None;
        row.RefreshState = on ? CatalogRowRefreshState.Complete : CatalogRowRefreshState.None;
    }

    private void ClearCatalogTestingHighlights()
    {
        _undownloadBatchActive = false;
        _undownloadPurpleLibraries.Clear();
        foreach (var row in _catalogRows)
        {
            if (row.IsTesting || row.IsDownloading)
            {
                row.IsTesting = false;
                row.IsDownloading = false;
                row.RefreshHighlight = CatalogRowRefreshHighlight.None;
                row.RefreshState = CatalogRowRefreshState.None;
            }
        }
    }

    private void ReapplyUndownloadPurpleHighlights()
    {
        if (!_undownloadBatchActive)
        {
            return;
        }

        foreach (var library in _undownloadPurpleLibraries)
        {
            SetCatalogTestingHighlight(library, on: true);
        }
    }

    private void AppendTestLog(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            _testLogUpdater.Append(Environment.NewLine, _ => ScrollTestLogToEnd(), null, null);
            return;
        }

        _testLogUpdater.Append(line + Environment.NewLine, appended =>
        {
            TestLogBox.AppendText(appended);
            ScrollTestLogToEnd();
        }, null, null);
    }

    private void ScrollTestLogToEnd()
    {
        TestLogBox.CaretIndex = TestLogBox.Text.Length;
        TestLogBox.ScrollToEnd();
        TestLogBox.Dispatcher.BeginInvoke(() =>
        {
            TestLogBox.CaretIndex = TestLogBox.Text.Length;
            TestLogBox.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    private void InitModeCards()
    {
        var map = _svc.ModeDefinitions.DeviceMap;
        VulkanLabel.Text =
            $"Your GPUs: Integrated {map.ApuName} (Vulkan #{map.ApuVulkanIndex}) · Discrete {map.GpuName} (Vulkan #{map.GpuVulkanIndex})";
        foreach (var def in _svc.ModeDefinitions.Definitions.Values)
        {
            if (!_modeCards.TryGetValue(def.Mode.ToString(), out var card))
            {
                continue;
            }

            var content = ModeCardPresenter.BuildContent(
                def.ShortLabel, def.CardSubtitle, out var title, out var subtitle);
            card.Content = content;
            _modeCardPresenter.Register(def.Mode.ToString(), card, title, subtitle);
            _modeCardPresenter.ApplyDefinition(def);
        }
    }

    private async void InitAiFeatureToggles()
    {
        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        AiFeaturesPanel.Children.Clear();

        var master = new CheckBox
        {
            Content = "Enable toolkit AI (master)",
            IsChecked = settings.ToolkitAiEnabled,
            Foreground = (Brush)FindResource("Brush.Text"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        master.Checked += async (_, _) => await SaveMasterAiAsync(true).ConfigureAwait(true);
        master.Unchecked += async (_, _) => await SaveMasterAiAsync(false).ConfigureAwait(true);
        AiFeaturesPanel.Children.Add(master);

        foreach (var (key, label) in GetFeatureLabels())
        {
            var cb = new CheckBox
            {
                Content = label,
                Tag = key,
                IsChecked = settings.FeatureFlags.GetValueOrDefault(key, true),
                Foreground = (Brush)FindResource("Brush.Text"),
                Margin = new Thickness(0, 4, 0, 4)
            };
            cb.Checked += async (_, _) => await SaveFeatureFlagAsync(key, true).ConfigureAwait(true);
            cb.Unchecked += async (_, _) => await SaveFeatureFlagAsync(key, false).ConfigureAwait(true);
            AiFeaturesPanel.Children.Add(cb);
        }
    }

    private static IEnumerable<(string Key, string Label)> GetFeatureLabels() =>
    [
        ("DescriptionSummarization", "1. Description summarization"),
        ("CatalogCategorization", "2. Catalog categorization"),
        ("BenchmarkInterpreter", "3. Benchmark result interpreter"),
        ("FailureDiagnosis", "4. Failure diagnosis"),
        ("NaturalLanguageSearch", "5. Natural-language model search"),
        ("ModelPickerAdvisor", "6. Model picker advisor"),
        ("OptimalBenchmarkSettings", "7. Optimal benchmark settings"),
        ("TestQueuePrioritization", "8. Test untested prioritization"),
        ("ModelComparison", "9. Model comparison blurb"),
        ("PlainLanguageErrors", "10. Plain-language errors"),
        ("LogAnomalyDetection", "11. Log anomaly detection")
    ];

    private async Task SaveMasterAiAsync(bool enabled)
    {
        var s = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        s.ToolkitAiEnabled = enabled;
        await _svc.AiSettings.SaveAsync(s).ConfigureAwait(true);
    }

    private async Task SaveFeatureFlagAsync(string key, bool enabled)
    {
        var s = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        s.FeatureFlags[key] = enabled;
        await _svc.AiSettings.SaveAsync(s).ConfigureAwait(true);
    }

    private async Task RefreshAllAsync()
    {
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                var imported = await _svc.ReportImporter.ImportReportsAsync(cancellationToken: ct).ConfigureAwait(false);
                if (imported > 0)
                {
                    _svc.Profiles.ClearCache();
                    _svc.ActivityLog.Write("Task", $"Imported {imported} benchmark report(s).");
                }

                await UiDispatcher.InvokeAsync(async () =>
                {
                    await RefreshModesUiAsync().ConfigureAwait(true);
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                    await RefreshTestResultsUiAsync().ConfigureAwait(true);
                    await UpdateAiStatusAsync().ConfigureAwait(true);
                    await RefreshAiSettingsUiAsync().ConfigureAwait(true);
                    RefreshActivityLog();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _svc.ActivityLog.Write("Error", $"Startup refresh failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                    AlertText.Text = $"Data load error: {ex.Message}").ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task RefreshModesUiAsync()
    {
        var detected = _svc.ModeService.DetectCurrentMode();
        var apiReady = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        var modeLabel = _svc.ModeDefinitions.TryParse(detected, out var mode)
            ? _svc.ModeDefinitions.Get(mode).ShortLabel
            : detected;
        ModeStatusLabel.Text = apiReady
            ? $"Active: {modeLabel} — Ollama is running and ready"
            : $"Active: {modeLabel} — Ollama API not reachable (start Ollama if needed)";

        _modeCardPresenter.ApplyActiveMode(detected);

        var snapshot = _svc.EnvBackup.ReadUserSnapshot();
        EnvBox.Text = ModeEnvSummaryBuilder.Build(
            detected,
            snapshot,
            _svc.ModeDefinitions.DeviceMap,
            _svc.ModeDefinitions);
    }

    private async Task<Dictionary<string, string>> GetCategoryMapAsync()
    {
        var doc = await _svc.CategoryStore.LoadAsync().ConfigureAwait(true);
        return doc.Models.ToDictionary(
            k => k.Key,
            v => v.Value.Category,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task RefreshModelsUiAsync()
    {
        var selectedModel = ModelsGrid.SelectedItem is ModelProfileSummary selected
            ? selected.Model
            : null;
        var testComboSelection = TestModelCombo.SelectedItem as string;

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var categories = await GetCategoryMapAsync().ConfigureAwait(true);
        var enriched = summaries.Select(s =>
        {
            var lib = s.Model.Split(':')[0];
            var category = categories.TryGetValue(lib, out var c)
                ? c
                : CategoryNormalizer.HeuristicCategory(lib, string.Empty, string.Empty);
            return new ModelProfileSummary
            {
                Model = s.Model,
                SizeGB = s.SizeGB,
                Quantization = s.Quantization,
                ParameterSize = s.ParameterSize,
                Digest = s.Digest,
                Status = s.Status,
                BestMode = s.BestMode,
                BestTps = s.BestTps,
                LastTested = s.LastTested,
                NeedsRetest = s.NeedsRetest,
                RecommendedCtx = s.RecommendedCtx,
                Category = category,
                Results = s.Results
            };
        }).ToList();
        ModelsGrid.ItemsSource = enriched;
        TestModelCombo.ItemsSource = enriched.Select(s => s.Model).ToList();

        if (!string.IsNullOrEmpty(selectedModel))
        {
            var match = enriched.FirstOrDefault(e =>
                e.Model.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                ModelsGrid.SelectedItem = match;
            }
        }

        if (!string.IsNullOrEmpty(testComboSelection))
        {
            var comboIndex = -1;
            for (var i = 0; i < TestModelCombo.Items.Count; i++)
            {
                if (TestModelCombo.Items[i]?.ToString()
                        ?.Equals(testComboSelection, StringComparison.OrdinalIgnoreCase) == true)
                {
                    comboIndex = i;
                    break;
                }
            }

            if (comboIndex >= 0)
            {
                TestModelCombo.SelectedIndex = comboIndex;
            }
        }
        else if (TestModelCombo.Items.Count > 0 && TestModelCombo.SelectedIndex < 0)
        {
            TestModelCombo.SelectedIndex = 0;
        }

        var untested = enriched.Where(s => s.NeedsRetest).Select(s => s.Model).ToList();
        await UpdateAiButtonStatesAsync().ConfigureAwait(true);
        if (untested.Count > 0)
        {
            AlertBar.Visibility = Visibility.Visible;
            AlertText.Text = $"Untested models: {string.Join(", ", untested)}";
        }
        else
        {
            AlertBar.Visibility = Visibility.Collapsed;
        }

        ScheduleFitGridColumns(ModelsGrid);
    }

    private async Task UpdateAiStatusAsync()
    {
        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        string content;
        if (!settings.ToolkitAiEnabled)
        {
            content = "AI Disabled";
        }
        else if (!ready)
        {
            content = "AI Inactive";
        }
        else if (!string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel))
        {
            content = "AI Active";
        }
        else
        {
            content = "AI Inactive";
        }

        _aiProcessingFlash.SetIdlePresentation(
            content,
            (Brush)FindResource("Brush.Button"),
            (Brush)FindResource("Brush.PanelBorder"),
            (Brush)FindResource("Brush.Text"));

        await UpdateAiButtonStatesAsync().ConfigureAwait(true);
    }

    private void EnterAiActivity()
    {
        void Enter()
        {
            if (_aiActivityDepth++ == 0)
            {
                _aiProcessingFlash.BeginProcessingFlash();
            }
        }

        if (CheckAccess())
        {
            Enter();
        }
        else
        {
            Dispatcher.Invoke(Enter);
        }
    }

    private void ExitAiActivity()
    {
        void Exit()
        {
            if (_aiActivityDepth <= 0)
            {
                return;
            }

            if (--_aiActivityDepth == 0)
            {
                _aiProcessingFlash.EndProcessingFlash();
                _ = UpdateAiStatusAsync();
            }
        }

        if (CheckAccess())
        {
            Exit();
        }
        else
        {
            Dispatcher.Invoke(Exit);
        }
    }

    private void BeginCatalogToolbarOperation()
    {
        _catalogToolbarOperations++;
        UpdateCatalogStopButtonUi();
    }

    private void EndCatalogToolbarOperation()
    {
        if (_catalogToolbarOperations > 0)
        {
            _catalogToolbarOperations--;
        }

        UpdateCatalogStopButtonUi();
    }

    private void UpdateCatalogStopButtonUi()
    {
        if (_catalogToolbarOperations > 0)
        {
            CatalogStopBtn.Background = (Brush)FindResource("Brush.Accent");
            CatalogStopBtn.BorderBrush = (Brush)FindResource("Brush.AccentHover");
            CatalogStopBtn.Foreground = Brushes.White;
        }
        else
        {
            CatalogStopBtn.Background = (Brush)FindResource("Brush.Button");
            CatalogStopBtn.BorderBrush = (Brush)FindResource("Brush.PanelBorder");
            CatalogStopBtn.Foreground = (Brush)FindResource("Brush.Text");
        }
    }

    private async Task UpdateAiButtonStatesAsync()
    {
        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        var enabled = settings.ToolkitAiEnabled && ready
            && !string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel);
        AskAiModelsBtn.IsEnabled = enabled;
        AskAiRunBtn.IsEnabled = enabled;
        CompareModelsBtn.IsEnabled = enabled;
        CompareCatalogBtn.IsEnabled = enabled;
    }

    private void RefreshActivityLog() => AiActivityLog.Text = _svc.ActivityLog.ReadTail();

    private async void ModeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<ComputeMode>(tag, out var mode))
        {
            return;
        }

        if (_modeCardPresenter.IsTransitionActive)
        {
            return;
        }

        var previousMode = _svc.ModeService.DetectCurrentMode();
        var restart = RestartCheck.IsChecked == true;
        if (!restart)
        {
            var proceed = MessageBox.Show(
                "Env vars will be saved but Ollama only reads them at startup. "
                + "The running backend will not change until you restart Ollama (check the box or restart manually). "
                + "Apply anyway?",
                "Restart Ollama Recommended",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _modeCardPresenter.BeginTransition(previousMode, tag);
        ModeStatusLabel.Text = restart ? $"Applying {tag} and restarting Ollama…" : $"Applying {tag}…";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeAsync(mode, restartOllama: restart, cancellationToken: ct)
                    .ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                var envSummary = _svc.ModeService.GetManagedEnvSummary();
                _svc.ActivityLog.Write("Task", restart
                    ? $"Applied mode {mode} and restarted Ollama. Env: {envSummary}"
                    : $"Applied mode {mode} env (no restart). Env: {envSummary}");
                await UiDispatcher.InvokeAsync(async () =>
                {
                    _modeCardPresenter.EndTransition();
                    await RefreshModesUiAsync().ConfigureAwait(true);
                    ModeStatusLabel.Text = restart
                        ? $"Current mode: {mode} — Ollama restarted with new backend"
                        : $"Current mode: {mode} — env saved; restart Ollama to activate backend";
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(async () =>
                {
                    _modeCardPresenter.EndTransition();
                    await RefreshModesUiAsync().ConfigureAwait(true);
                    ModeStatusLabel.Text = $"Mode apply failed: {ex.Message}";
                    MessageBox.Show(msg, "Mode Apply", MessageBoxButton.OK, MessageBoxImage.Warning);
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void RefreshModes_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        try
        {
            await RefreshModesUiAsync().ConfigureAwait(true);
            EndTaskFlashSuccess(sender, "Refreshed");
        }
        catch
        {
            EndTaskFlashIdle(sender);
            throw;
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        try
        {
            _svc.Profiles.ClearCache();
            await RefreshModelsUiAsync().ConfigureAwait(true);
            EndTaskFlashSuccess(sender, "Refreshed");
        }
        catch
        {
            EndTaskFlashIdle(sender);
            throw;
        }
    }

    private async void ImportReports_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        try
        {
            var count = await _svc.ReportImporter.ImportReportsAsync().ConfigureAwait(true);
            _svc.ActivityLog.Write("Task", $"Manual import: {count} report(s).");
            _svc.Profiles.ClearCache();
            await RefreshModelsUiAsync().ConfigureAwait(true);
            EndTaskFlashSuccess(sender, "Imported");
        }
        catch
        {
            EndTaskFlashIdle(sender);
            throw;
        }
    }

    private ModelProfileSummary? GetSelectedModel()
    {
        if (ModelsGrid.SelectedItem is ModelProfileSummary s)
        {
            return s;
        }

        if (ModelsGrid.Items.Count > 0)
        {
            ModelsGrid.SelectedIndex = 0;
            return ModelsGrid.SelectedItem as ModelProfileSummary;
        }

        return null;
    }

    private async void LaunchBestMode_Click(object sender, RoutedEventArgs e)
    {
        var model = GetSelectedModel();
        if (model is null)
        {
            MessageBox.Show("No model selected.", "Launch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (model.NeedsRetest || string.IsNullOrEmpty(model.BestMode))
        {
            MessageBox.Show($"Model '{model.Model}' has no benchmark profile. Run a test first.", "Launch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Enum.TryParse<ComputeMode>(model.BestMode, out var mode))
        {
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        MainTabs.SelectedItem = ModelRunTab;
        _runModel = model.Model;
        ModelRunStatus.Text = $"Applying {model.BestMode} and restarting Ollama…";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeWithRestartAsync(mode, cancellationToken: ct)
                    .ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                await _svc.ModelSessions.SwitchToModelAsync(model.Model, warmLoad: true, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ModelRunStatus.Text =
                        $"{model.Model} | {model.BestMode} ({model.BestMetricDisplay}) | Ollama restarted, model loaded";
                    ChatHistory.Text = string.Empty;
                    _chatMessages.Clear();
                    EndTaskFlashSuccess(sender, "Launched");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ModelRunStatus.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void TestSelected_Click(object sender, RoutedEventArgs e)
    {
        var model = TestModelCombo.SelectedItem as string ?? GetSelectedModel()?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (!BeginTestOperation(sender))
        {
            return;
        }

        await RunBenchmarkQueueAsync(new[] { model }, sender).ConfigureAwait(true);
    }

    private async void TestUntested_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTestOperation(sender))
        {
            return;
        }

        await RunUntestedBenchmarkQueueAsync(sender).ConfigureAwait(true);
    }

    private async void TestUndownload_Click(object sender, RoutedEventArgs e)
    {
        const string message =
            "Warning: Each LLM that has not been downloaded will be downloaded from the smallest file size to the largest file size. "
            + "Only one LLM that is not part of the downloaded list will be downloaded at a time and tested. "
            + "When a test is complete the LLM will be deleted and the data will be recorded and retained. "
            + "This will happen sequentially until all undownloaded LLMs have been tested.";

        if (!ToolkitConfirmDialog.ShowAccept(this, message, "Test Undownload"))
        {
            return;
        }

        if (!BeginTestOperation(sender, FlashColorScheme.PurpleBlack))
        {
            return;
        }

        await RunUndownloadTestQueueAsync(sender).ConfigureAwait(true);
    }

    private async void ClearAllTestResults_Click(object sender, RoutedEventArgs e)
    {
        const string message =
            "Warning: All Test Data will be Cleared and cannot be Restored. "
            + "The tests must be rerun again to get Data.";

        if (!ToolkitConfirmDialog.ShowAccept(this, message, "Clear All Test Results"))
        {
            return;
        }

        if (_testOperations > 0)
        {
            await CancelTestOperationsAsync().ConfigureAwait(true);
        }

        var cleared = await _svc.Profiles.ClearAllTestDataAsync().ConfigureAwait(true);
        await _svc.BenchmarkInsights.ClearAllAsync().ConfigureAwait(true);
        await _svc.BenchmarkSettingsAdvisor.ClearAllAsync().ConfigureAwait(true);
        _svc.Profiles.ClearCache();
        _svc.BenchmarkInsights.ClearCache();
        _svc.BenchmarkSettingsAdvisor.ClearCache();

        await UiDispatcher.InvokeAsync(async () =>
        {
            TestProgressPanel.Visibility = Visibility.Collapsed;
            TestOverallProgress.Value = 0;
            AppendTestLog($"--- Cleared all test data ({cleared.ReportDirsRemoved} report folder(s)) ---");
            TestStatusLabel.Text = "All test data cleared.";
            await RefreshModelsUiAsync().ConfigureAwait(true);
            await RefreshTestResultsUiAsync().ConfigureAwait(true);
            await RefreshCatalogUiAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        _svc.ActivityLog.Write("Benchmark", $"Cleared test data: {cleared.ReportDirsRemoved} report dirs");
    }

    private async Task RunUntestedBenchmarkQueueAsync(object? flashSender = null)
    {
        var untested = await _svc.Profiles.GetUntestedAsync().ConfigureAwait(true);
        var names = untested.Select(u => u.Model).ToList();
        if (names.Count == 0)
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                TestStatusLabel.Text = "No untested models.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var queue = await _svc.QueueAdvisor.PrioritizeAsync(names, summaries).ConfigureAwait(true);
        await UiDispatcher.InvokeAsync(() =>
            TestStatusLabel.Text = $"AI ordered queue: {string.Join(" -> ", queue.Models)}").ConfigureAwait(true);
        _svc.ActivityLog.Write("AI", queue.Rationale);
        await RunBenchmarkQueueAsync(queue.Models, flashSender).ConfigureAwait(true);
    }

    private void TestModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _testSettingsTimer.Stop();
        _testSettingsTimer.Start();
    }

    private async Task SuggestBenchmarkSettingsAsync()
    {
        var model = TestModelCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model))
        {
            TestSettingsLabel.Text = string.Empty;
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var summary = summaries.FirstOrDefault(s => s.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
        {
            return;
        }

        var categories = await GetCategoryMapAsync().ConfigureAwait(true);
        var lib = model.Split(':')[0];
        var category = categories.TryGetValue(lib, out var c) ? c : string.Empty;
        EnterAiActivity();
        BenchmarkSettingsEntry settings;
        try
        {
            settings = await _svc.BenchmarkSettingsAdvisor.SuggestAsync(summary, category).ConfigureAwait(true);
        }
        finally
        {
            ExitAiActivity();
        }

        TestNumCtxBox.Text = settings.NumCtx.ToString();
        TestNumPredictBox.Text = settings.NumPredict.ToString();
        TestSettingsLabel.Text = settings.Rationale ?? string.Empty;
    }

    private static (int NumCtx, int NumPredict) ParseBenchmarkSpinners(string ctxText, string predictText)
    {
        var ctx = int.TryParse(ctxText, out var c) && c > 0 ? c : 8192;
        var pred = int.TryParse(predictText, out var p) && p > 0 ? p : 32;
        return (ctx, pred);
    }

    private async Task<(int NumCtx, int NumPredict)> ResolveBenchmarkSettingsForModelAsync(
        string model,
        CancellationToken cancellationToken)
    {
        var summaries = await _svc.Profiles.GetAllSummariesAsync(cancellationToken).ConfigureAwait(false);
        var summary = summaries.FirstOrDefault(s => s.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
        {
            var sizeGb = 4.0;
            var categories = await GetCategoryMapAsync().ConfigureAwait(true);
            var lib = model.Split(':')[0];
            var category = categories.TryGetValue(lib, out var c) ? c : string.Empty;
            var catalogRows = await _svc.Registry.GetCatalogRowsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var catalogRow = catalogRows.FirstOrDefault(r =>
                r.Name.Equals(lib, StringComparison.OrdinalIgnoreCase));
            if (catalogRow is not null
                && ModelSizeFormatter.TryParseSizeLabelToBytes(catalogRow.FileSize, out var bytes))
            {
                sizeGb = bytes / 1_073_741_824.0;
            }

            summary = new ModelProfileSummary
            {
                Model = model,
                SizeGB = sizeGb,
                RecommendedCtx = ProfileStoreService.GetRecommendedBenchmarkNumCtx(sizeGb),
                Category = category
            };
        }

        var categoriesMap = await GetCategoryMapAsync().ConfigureAwait(false);
        var library = model.Split(':')[0];
        var modelCategory = categoriesMap.TryGetValue(library, out var cat) ? cat : string.Empty;
        if (string.IsNullOrWhiteSpace(summary.Category))
        {
            summary = new ModelProfileSummary
            {
                Model = summary.Model,
                SizeGB = summary.SizeGB,
                Category = modelCategory,
                RecommendedCtx = summary.RecommendedCtx,
                BenchmarkKind = summary.BenchmarkKind,
                BestEmbedMs = summary.BestEmbedMs,
                BestMode = summary.BestMode,
                BestTps = summary.BestTps,
                Digest = summary.Digest,
                LastTested = summary.LastTested,
                NeedsRetest = summary.NeedsRetest,
                ParameterSize = summary.ParameterSize,
                Quantization = summary.Quantization,
                Results = summary.Results,
                Status = summary.Status
            };
        }

        if (CategoryNormalizer.IsEmbeddingModel(model, modelCategory))
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                TestNumCtxBox.Text = "—";
                TestNumPredictBox.Text = "—";
                TestSettingsLabel.Text =
                    "Embedding model — /api/embed benchmark (latency ms; no generation settings).";
            }).ConfigureAwait(false);
            return (0, 0);
        }

        EnterAiActivity();
        BenchmarkSettingsEntry settings;
        try
        {
            settings = await _svc.BenchmarkSettingsAdvisor
                .SuggestAsync(summary, modelCategory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitAiActivity();
        }

        settings = BenchmarkSettingsAdvisorService.NormalizeEntry(settings, summary);
        await UiDispatcher.InvokeAsync(() =>
        {
            TestNumCtxBox.Text = settings.NumCtx.ToString();
            TestNumPredictBox.Text = settings.NumPredict.ToString();
            TestSettingsLabel.Text = settings.Rationale ?? string.Empty;
        }).ConfigureAwait(false);

        return (settings.NumCtx, settings.NumPredict);
    }

    private void BuildTestModeProgressRows(IReadOnlyList<string> modes, bool includeDownloadRow = false)
    {
        TestModeProgressPanel.Children.Clear();
        _testModeProgress.Clear();

        if (includeDownloadRow)
        {
            AddTestProgressRow("Download", 72);
        }

        foreach (var mode in modes)
        {
            AddTestProgressRow(mode, 56);
        }
    }

    private void AddTestProgressRow(string label, int labelWidth)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        var name = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Brush.Text"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);

        var bar = new ProgressBar
        {
            Style = (Style)FindResource("ToolkitProgressBar"),
            Height = 8,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bar, 1);

        var status = new TextBlock
        {
            Text = "Pending",
            Foreground = (Brush)FindResource("Brush.Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(status, 2);

        grid.Children.Add(name);
        grid.Children.Add(bar);
        grid.Children.Add(status);
        TestModeProgressPanel.Children.Add(grid);
        _testModeProgress[label] = (bar, status);
    }

    private void ApplyDownloadProgressUpdate(ModelPullProgress update)
    {
        if (!_testModeProgress.TryGetValue("Download", out var row))
        {
            return;
        }

        row.Bar.Foreground = (Brush)FindResource("Brush.Purple");
        if (update.Percent is >= 0)
        {
            row.Bar.Value = update.Percent.Value;
            row.Status.Text = $"{update.Percent.Value}%";
        }
        else
        {
            row.Bar.Value = row.Bar.Value > 0 ? row.Bar.Value : 5;
            row.Status.Text = update.Status;
        }

        row.Status.Foreground = (Brush)FindResource("Brush.Purple");
    }

    private void ResetTestProgressUi(
        string model,
        int modelIndex,
        int modelCount,
        IReadOnlyList<string> modes,
        string? aiSummarizer,
        bool includeDownloadRow = false)
    {
        TestProgressPanel.Visibility = Visibility.Visible;
        TestOverallProgress.Value = 0;
        var aiLine = string.IsNullOrWhiteSpace(aiSummarizer)
            ? "AI insights LLM: (none)"
            : $"AI insights LLM: {aiSummarizer}";
        TestOverallLabel.Text =
            $"Overall: {model} ({modelIndex + 1}/{modelCount}) — {aiLine}";
        BuildTestModeProgressRows(modes, includeDownloadRow);

        foreach (var (bar, status) in _testModeProgress.Values)
        {
            bar.Value = 0;
            status.Text = "Pending";
            status.Foreground = (Brush)FindResource("Brush.Muted");
        }
    }

    private void ApplyBenchmarkProgressUpdate(BenchmarkProgressUpdate update)
    {
        TestOverallProgress.Value = update.OverallPercent;
        var isEmbed = update.BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(update.Model))
        {
            var kindLabel = isEmbed ? "embed" : "generate";
            var aiLine = string.IsNullOrWhiteSpace(update.AiSummarizerModel)
                ? "AI insights LLM: (none)"
                : $"AI insights LLM: {update.AiSummarizerModel}";
            TestOverallLabel.Text =
                $"Overall: {update.Model} ({update.ModelIndex + 1}/{update.ModelCount}) [{kindLabel}] — {aiLine}";
        }

        if (string.IsNullOrWhiteSpace(update.Mode) || !_testModeProgress.TryGetValue(update.Mode, out var row))
        {
            if (update.Phase == BenchmarkProgressPhase.ModelCompleted && update.BestMode is not null)
            {
                TestStatusLabel.Text = isEmbed
                    ? $"{update.Model} complete — winner: {update.BestMode} @ {update.BestEmbedMs:F1} ms/embed"
                    : $"{update.Model} complete — winner: {update.BestMode} @ {update.BestTps:F2} tok/s";
            }

            return;
        }

        row.Bar.Foreground = (Brush)FindResource("Brush.Accent");
        switch (update.Phase)
        {
            case BenchmarkProgressPhase.ModeApplying:
                row.Bar.Value = update.CurrentModePercent;
                row.Status.Text = "Applying mode…";
                row.Status.Foreground = (Brush)FindResource("Brush.Warning");
                break;
            case BenchmarkProgressPhase.ModeBenchmarking:
                row.Bar.Value = update.CurrentModePercent;
                row.Status.Text = isEmbed ? "Embedding…" : "Benchmarking…";
                row.Status.Foreground = (Brush)FindResource("Brush.Warning");
                break;
            case BenchmarkProgressPhase.ModeCompleted:
                row.Bar.Value = 100;
                row.Status.Text = isEmbed
                    ? $"{update.EmbedLatencyMs:F1} ms"
                    : $"{update.GenerationTps:F1} tok/s";
                row.Status.Foreground = (Brush)FindResource("Brush.Active");
                break;
            case BenchmarkProgressPhase.ModeFailed:
                row.Bar.Value = 100;
                row.Bar.Foreground = (Brush)FindResource("Brush.Accent");
                row.Status.Text = "Failed";
                row.Status.Foreground = (Brush)FindResource("Brush.Accent");
                break;
        }

        if (!string.IsNullOrWhiteSpace(update.Mode))
        {
            TestStatusLabel.Text = update.Phase switch
            {
                BenchmarkProgressPhase.ModeApplying =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — applying compute mode…",
                BenchmarkProgressPhase.ModeBenchmarking when isEmbed =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — running embed benchmark on {update.Model}…",
                BenchmarkProgressPhase.ModeBenchmarking =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — running benchmark on {update.Model}…",
                BenchmarkProgressPhase.ModeCompleted when isEmbed =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — {update.EmbedLatencyMs:F1} ms/embed",
                BenchmarkProgressPhase.ModeCompleted =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — {update.GenerationTps:F2} tok/s",
                BenchmarkProgressPhase.ModeFailed =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — failed",
                BenchmarkProgressPhase.ModelCompleted when update.BestMode is not null && isEmbed =>
                    $"{update.Model} complete — winner: {update.BestMode} @ {update.BestEmbedMs:F1} ms/embed",
                BenchmarkProgressPhase.ModelCompleted when update.BestMode is not null =>
                    $"{update.Model} complete — winner: {update.BestMode} @ {update.BestTps:F2} tok/s",
                _ => TestStatusLabel.Text
            };
        }
    }

    private async Task RunBenchmarkQueueAsync(IReadOnlyList<string> models, object? flashSender = null)
    {
        _benchmarkCts?.Cancel();
        _benchmarkCts = new CancellationTokenSource();
        var ct = _benchmarkCts.Token;
        var workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTestWork = workTcs.Task;

        await _svc.WorkQueue.EnqueueAsync(async _ =>
        {
            var cancelled = false;
            try
            {
                var aiSummarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: ct).ConfigureAwait(false);

                await UiDispatcher.InvokeAsync(() =>
                {
                    AppendTestLog($"--- Benchmark queue started ({models.Count} model(s)) ---");
                    if (!string.IsNullOrWhiteSpace(aiSummarizer))
                    {
                        AppendTestLog($"AI insights LLM: {aiSummarizer}");
                    }
                }).ConfigureAwait(false);

                try
                {
                    await RunBenchmarkQueueCoreAsync(
                        models,
                        flashSender: null,
                        externalCt: ct,
                        modelIndexOffset: 0,
                        totalModels: models.Count).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                await UiDispatcher.InvokeAsync(async () =>
                {
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    await RefreshTestResultsUiAsync().ConfigureAwait(true);
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                    TestOverallProgress.Value = 100;
                    TestStatusLabel.Text = cancelled
                        ? "Benchmark queue stopped."
                        : "Benchmark queue complete.";
                    FinishTestOperation(flashSender, success: !cancelled, cancelled);
                }).ConfigureAwait(false);
            }
            finally
            {
                workTcs.TrySetResult();
            }
        }).ConfigureAwait(true);

        await workTcs.Task.ConfigureAwait(true);
    }

    private async Task RunUndownloadTestQueueAsync(object? flashSender = null)
    {
        var startup = await EnsureOllamaApiReadyAsync(
            CancellationToken.None, "Undownload test requires Ollama", timeoutSec: 90).ConfigureAwait(true);
        if (!startup.Success)
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                AppendTestLog($"ABORT: {startup.Message}");
                TestStatusLabel.Text = "Undownload test aborted — Ollama API not reachable.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        var candidates = await _svc.Registry.GetUndownloadTestQueueAsync().ConfigureAwait(true);
        if (candidates.Count == 0)
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                TestStatusLabel.Text = "No undownloaded models need testing.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        _undownloadCts?.Cancel();
        _undownloadCts = new CancellationTokenSource();
        var ct = _undownloadCts.Token;
        var workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTestWork = workTcs.Task;

        await UiDispatcher.InvokeAsync(() =>
        {
            _undownloadBatchActive = true;
            _undownloadPurpleLibraries.Clear();
            AppendTestLog(
                $"--- Undownload test queue ({candidates.Count} model(s), smallest file size first) ---");
            AppendTestLog(
                $"Order: {string.Join(" -> ", candidates.Select(c => c.LibraryName))}");
            TestStatusLabel.Text = $"Undownload batch started — {candidates.Count} model(s) queued.";
        }).ConfigureAwait(true);

        await _svc.WorkQueue.EnqueueAsync(async _ =>
        {
            var cancelled = false;
            try
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    var pullTag = candidate.PullTag;
                    var fileSizeLabel = candidate.FileSizeBytes < long.MaxValue
                        ? ModelSizeFormatter.FormatBytes(candidate.FileSizeBytes)
                        : "unknown";

                    await UiDispatcher.InvokeAsync(() =>
                    {
                        _undownloadPurpleLibraries.Add(candidate.LibraryName);
                        SetCatalogTestingHighlight(candidate.LibraryName, on: true);
                        TestStatusLabel.Text =
                            $"[{i + 1}/{candidates.Count}] Downloading {candidate.LibraryName} ({pullTag})…";
                    }).ConfigureAwait(false);

                    try
                    {
                        await UiDispatcher.InvokeAsync(() =>
                        {
                            AppendTestLog(string.Empty);
                            AppendTestLog(
                                $"=== Undownload test [{i + 1}/{candidates.Count}]: {candidate.LibraryName} ===");
                            AppendTestLog($"Tag: {pullTag} | Catalog file size: {fileSizeLabel}");
                            AppendTestLog($"Step 1/3: Downloading {pullTag} from Ollama library…");
                        }).ConfigureAwait(false);

                        var aiSummarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: ct)
                            .ConfigureAwait(false);
                        await UiDispatcher.InvokeAsync(() =>
                            ResetTestProgressUi(
                                pullTag,
                                i,
                                candidates.Count,
                                new[] { "CPU", "APU", "GPU", "Hybrid", "ROCm" },
                                aiSummarizer,
                                includeDownloadRow: true))
                            .ConfigureAwait(false);

                        var pullStartup = await EnsureOllamaApiReadyAsync(
                            ct, $"Download requires Ollama ({pullTag})", timeoutSec: 60).ConfigureAwait(false);
                        if (!pullStartup.Success)
                        {
                            throw new InvalidOperationException(pullStartup.Message);
                        }

                        var lastPullLogPercent = -1;
                        var pullProgress = new Progress<ModelPullProgress>(update =>
                        {
                            UiDispatcher.InvokeAsync(() =>
                            {
                                ApplyDownloadProgressUpdate(update);
                                if (update.Percent is int percent)
                                {
                                    if (percent >= 100 || percent - lastPullLogPercent >= 10)
                                    {
                                        lastPullLogPercent = percent;
                                        AppendTestLog($"Download: {update.Status} ({percent}%)");
                                    }
                                }
                                else if (!string.IsNullOrWhiteSpace(update.Status))
                                {
                                    AppendTestLog($"Download: {update.Status}");
                                }
                            });
                        });

                        await _svc.ApiClient.PullAsync(pullTag, pullProgress, ct).ConfigureAwait(false);
                        _svc.Profiles.ClearCache();

                        if (!await _svc.ApiClient.IsModelInstalledAsync(pullTag, ct).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"Download finished but {pullTag} was not found in the local Ollama model list.");
                        }

                        await UiDispatcher.InvokeAsync(() =>
                        {
                            if (_testModeProgress.TryGetValue("Download", out var row))
                            {
                                row.Bar.Value = 100;
                                row.Status.Text = "Complete";
                                row.Status.Foreground = (Brush)FindResource("Brush.Active");
                            }

                            AppendTestLog($"Download verified: {pullTag} is installed locally.");
                            AppendTestLog("Step 2/3: Running 5-mode benchmark on downloaded model…");
                        }).ConfigureAwait(false);

                        await RunBenchmarkQueueCoreAsync(
                            new[] { pullTag },
                            flashSender: null,
                            externalCt: ct,
                            modelIndexOffset: i,
                            totalModels: candidates.Count,
                            holdCatalogHighlight: true,
                            skipDownloadProgressRow: true)
                            .ConfigureAwait(false);

                        if (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                        }

                        await UiDispatcher.InvokeAsync(() =>
                            AppendTestLog($"Step 3/3: Removing local copy {pullTag} (keeping benchmark data)…"))
                            .ConfigureAwait(false);

                        await _svc.ModelSessions.UninstallModelAsync(pullTag, ct).ConfigureAwait(false);
                        _svc.ApiClient.InvalidateCaches();
                        _svc.Profiles.ClearCache();

                        await UiDispatcher.InvokeAsync(() =>
                        {
                            AppendTestLog($"Removed {pullTag} from disk (ollama rm). Benchmark profile retained.");
                            AppendTestLog(
                                $"=== Finished [{i + 1}/{candidates.Count}]: {candidate.LibraryName} ===");
                        }).ConfigureAwait(false);

                        await UiDispatcher.InvokeAsync(async () =>
                        {
                            await RefreshModelsUiAsync().ConfigureAwait(true);
                            await RefreshCatalogUiAsync().ConfigureAwait(true);
                            await RefreshTestResultsUiAsync().ConfigureAwait(true);
                            ReapplyUndownloadPurpleHighlights();
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        await UiDispatcher.InvokeAsync(() =>
                            AppendTestLog($"STOPPED ({pullTag}): download/test batch cancelled."))
                            .ConfigureAwait(false);
                        try
                        {
                            await _svc.ModelSessions.UninstallModelAsync(pullTag, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort cleanup after cancel.
                        }

                        break;
                    }
                    catch (Exception ex)
                    {
                        var connectionLost = OllamaConnectionHelper.IsConnectionError(ex);
                        var userMessage = OllamaConnectionHelper.FormatUserMessage(ex);
                        await UiDispatcher.InvokeAsync(() =>
                        {
                            AppendTestLog($"FAIL ({pullTag}): {userMessage}");
                            TestStatusLabel.Text = connectionLost
                                ? "Undownload test aborted — Ollama API connection lost."
                                : $"Undownload test failed for {pullTag}: {ex.Message} — continuing queue…";
                            if (connectionLost)
                            {
                                AppendTestLog($"ABORT: {userMessage}");
                            }
                        }).ConfigureAwait(false);

                        if (connectionLost)
                        {
                            cancelled = true;
                            break;
                        }

                        try
                        {
                            await _svc.ModelSessions.UninstallModelAsync(pullTag, CancellationToken.None)
                                .ConfigureAwait(false);
                            await UiDispatcher.InvokeAsync(() =>
                                AppendTestLog($"Cleanup: removed {pullTag} after failure (ollama rm)."))
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort cleanup after failure.
                        }
                    }
                }

                await UiDispatcher.InvokeAsync(() =>
                {
                    TestStatusLabel.Text = cancelled
                        ? "Undownload test queue stopped."
                        : "Undownload test queue complete.";
                    ClearCatalogTestingHighlights();
                    FinishTestOperation(flashSender, success: !cancelled, cancelled);
                }).ConfigureAwait(false);
            }
            finally
            {
                workTcs.TrySetResult();
            }
        }).ConfigureAwait(true);

        await workTcs.Task.ConfigureAwait(true);
    }

    private async Task RunBenchmarkQueueCoreAsync(
        IReadOnlyList<string> models,
        object? flashSender,
        CancellationToken externalCt,
        int modelIndexOffset,
        int totalModels,
        bool holdCatalogHighlight = false,
        bool skipDownloadProgressRow = false)
    {
        var ct = externalCt;
        var benchmarkModes = new[] { "CPU", "APU", "GPU", "Hybrid", "ROCm" };
        var aiSummarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: ct).ConfigureAwait(false);

        for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            var model = models[modelIndex];
            ct.ThrowIfCancellationRequested();

            var globalIndex = modelIndexOffset + modelIndex;
            await UiDispatcher.InvokeAsync(() =>
            {
                if (!holdCatalogHighlight)
                {
                    SetCatalogTestingHighlight(model, on: true);
                }

                if (!skipDownloadProgressRow)
                {
                    ResetTestProgressUi(
                        model, globalIndex, totalModels, benchmarkModes, aiSummarizer);
                }

                TestStatusLabel.Text = $"Resolving AI benchmark settings for {model}…";
            }).ConfigureAwait(false);

            try
            {
                var categoriesMap = await GetCategoryMapAsync().ConfigureAwait(false);
                var library = model.Split(':')[0];
                var modelCategory = categoriesMap.TryGetValue(library, out var cat) ? cat : string.Empty;
                var isEmbed = CategoryNormalizer.IsEmbeddingModel(model, modelCategory);

                var (numCtx, numPredict) = await ResolveBenchmarkSettingsForModelAsync(model, ct)
                    .ConfigureAwait(false);

                await UiDispatcher.InvokeAsync(() =>
                {
                    TestStatusLabel.Text = isEmbed
                        ? $"Embedding benchmark {model} ({globalIndex + 1}/{totalModels}) — /api/embed latency test…"
                        : $"Benchmarking {model} ({globalIndex + 1}/{totalModels}) — num_ctx={numCtx}, num_predict={numPredict}…";
                }).ConfigureAwait(false);

                var log = new Progress<string>(AppendTestLog);

                var progress = new Progress<BenchmarkProgressUpdate>(update =>
                {
                    UiDispatcher.InvokeAsync(() => ApplyBenchmarkProgressUpdate(update));
                });

                await _svc.BenchmarkRunner.RunAsync(
                    model,
                    numPredict: numPredict,
                    numCtx: numCtx,
                    modelIndex: globalIndex,
                    modelCount: totalModels,
                    aiSummarizerModel: aiSummarizer,
                    category: modelCategory,
                    log: log,
                    progress: progress,
                    cancellationToken: ct)
                    .ConfigureAwait(false);

                _svc.Profiles.ClearCache();
                var summary = (await _svc.Profiles.GetAllSummariesAsync(ct).ConfigureAwait(false))
                    .FirstOrDefault(s => s.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
                if (summary is not null)
                {
                    await _svc.BenchmarkInsights.InterpretProfileAsync(summary, ct).ConfigureAwait(false);
                    await _svc.BenchmarkInsights.DiagnoseFailuresAsync(summary, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    AppendTestLog($"FAIL ({model}): {ex.Message}");
                    TestStatusLabel.Text = $"Benchmark failed for {model}: {ex.Message}";
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!holdCatalogHighlight)
                {
                    await UiDispatcher.InvokeAsync(() => SetCatalogTestingHighlight(model, on: false))
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private async void StopTest_Click(object sender, RoutedEventArgs e) =>
        await CancelTestOperationsAsync().ConfigureAwait(true);

    private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync(sender).ConfigureAwait(true);

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SendChatAsync().ConfigureAwait(true);
        }
    }

    private async Task SendChatAsync(object? flashSender = null)
    {
        if (string.IsNullOrWhiteSpace(_runModel))
        {
            ModelRunStatus.Text = "Launch a model first from Models & Launch.";
            return;
        }

        var text = ChatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (flashSender is not null && !BeginTaskFlash(flashSender))
        {
            return;
        }

        ChatInput.Text = string.Empty;
        _chatMessages.Add(new ChatMessage { Role = "user", Content = text });
        ChatHistory.Text += $"You: {text}{Environment.NewLine}";

        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;

        ChatHistory.Text += "Assistant: ";
        var streamBuffer = string.Empty;

        await _svc.WorkQueue.EnqueueAsync(async token =>
        {
            EnterAiActivity();
            try
            {
                await foreach (var chunk in _svc.ApiClient.ChatStreamAsync(_runModel!, _chatMessages, ct)
                                   .ConfigureAwait(false))
                {
                    streamBuffer += chunk;
                    var captured = streamBuffer;
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        _chatUpdater.Append(chunk, _ =>
                        {
                            var prefix = ChatHistory.Text;
                            var marker = "Assistant: ";
                            var idx = prefix.LastIndexOf(marker, StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                ChatHistory.Text = prefix[..(idx + marker.Length)] + captured;
                            }
                        }, () => ChatHistory.Text, v => ChatHistory.Text = v);
                    }).ConfigureAwait(false);
                }

                _chatMessages.Add(new ChatMessage { Role = "assistant", Content = streamBuffer.Trim() });
                if (flashSender is not null)
                {
                    await UiDispatcher.InvokeAsync(() => EndTaskFlashSuccess(flashSender, "Sent", 5))
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ChatHistory.Text += msg;
                    if (flashSender is not null)
                    {
                        EndTaskFlashIdle(flashSender);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
            }
        }).ConfigureAwait(true);
    }

    private async void StopChat_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        try
        {
            await _svc.ModelSessions.StopActiveModelAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(_runModel))
            {
                ModelRunStatus.Text = $"{_runModel} unloaded (ollama stop /bye equivalent).";
            }
        }
        catch (Exception ex)
        {
            ModelRunStatus.Text = ex.Message;
        }
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _chatMessages.Clear();
        ChatHistory.Text = string.Empty;
    }

    private void OpenPowerShell_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_runModel))
        {
            return;
        }

        var ollama = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{ollama}\" run \"{_runModel}\"\"",
            UseShellExecute = true
        });
    }

    private async void AiStatusButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        var target = (!settings.ToolkitAiEnabled || !ready
            || string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel))
            ? AiSettingsTab
            : MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header?.ToString() == "AI Features");

        if (target is not null)
        {
            MainTabs.SelectedItem = target;
        }
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Contains(ModelLibraryTab))
        {
            await LoadCatalogTabAsync().ConfigureAwait(true);
            DataGridColumnHelper.ScheduleAutoFit(CatalogGrid);
        }
        else if (MainTabs.SelectedItem == ModelsLaunchTab)
        {
            DataGridColumnHelper.ScheduleAutoFit(ModelsGrid);
        }
        else if (MainTabs.SelectedItem == TestResultsTab)
        {
            await RefreshTestResultsUiAsync().ConfigureAwait(true);
            DataGridColumnHelper.ScheduleAutoFit(TestResultsGrid);
        }
        else if (MainTabs.SelectedItem == AiSettingsTab)
        {
            await RefreshAiSettingsUiAsync().ConfigureAwait(true);
            RefreshActivityLog();
        }
    }

    private void ScheduleFitGridColumns(DataGrid grid)
    {
        DataGridColumnHelper.ScheduleAutoFit(grid);
    }

    private void FitGridColumns(DataGrid grid)
    {
        if (ReferenceEquals(grid, TestResultsGrid))
        {
            DataGridColumnHelper.AutoFitColumnsDense(TestResultsGrid);
            return;
        }

        var starIndex = DataGridColumnHelper.IndexOfStarColumn(grid, "Description");
        if (starIndex < 0)
        {
            starIndex = DataGridColumnHelper.IndexOfStarColumn(grid, "Model");
        }

        if (starIndex < 0)
        {
            starIndex = 0;
        }

        DataGridColumnHelper.AutoFitColumns(grid, starIndex);
    }

    private void ApplyCatalogDescriptionModeUi()
    {
        DownloadDescriptionsModeBtn.Style = _catalogDescriptionMode == CatalogDescriptionDisplayMode.Download
            ? (Style)FindResource("CatalogDescriptionModeActive")
            : (Style)FindResource("ToolkitButton");
        AiDescriptionsModeBtn.Style = _catalogDescriptionMode == CatalogDescriptionDisplayMode.Ai
            ? (Style)FindResource("CatalogDescriptionModeActive")
            : (Style)FindResource("ToolkitButton");
    }

    private async void DownloadDescriptionsMode_Click(object sender, RoutedEventArgs e)
    {
        _catalogDescriptionMode = CatalogDescriptionDisplayMode.Download;
        ApplyCatalogDescriptionModeUi();
        await RefreshCatalogUiAsync().ConfigureAwait(true);
    }

    private async void AiDescriptionsMode_Click(object sender, RoutedEventArgs e)
    {
        _catalogDescriptionMode = CatalogDescriptionDisplayMode.Ai;
        ApplyCatalogDescriptionModeUi();
        await RefreshCatalogUiAsync().ConfigureAwait(true);
    }

    private async void RefreshDescriptions_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        _catalogDescriptionMode = CatalogDescriptionDisplayMode.Ai;
        ApplyCatalogDescriptionModeUi();

        _refreshDescriptionsCts?.Cancel();
        _refreshDescriptionsCts?.Dispose();
        _refreshDescriptionsCts = new CancellationTokenSource();
        _descriptionRefreshInProgress = true;
        BeginCatalogToolbarOperation();
        CatalogStatusLabel.Text = "Refreshing descriptions from ollama.com...";

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, _refreshDescriptionsCts.Token);
            var ct = linked.Token;
            EnterAiActivity();
            try
            {
                await _svc.CatalogStore.RefreshFromWebAsync(cancellationToken: ct).ConfigureAwait(false);
                _svc.CatalogStore.ClearCache();
                _svc.Descriptions.ClearCache();

                var entries = (await _svc.CatalogStore.GetEntriesAsync(cancellationToken: ct)
                    .ConfigureAwait(false))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await UiDispatcher.InvokeAsync(async () =>
                    await BindFullCatalogGridAsync("Refreshing descriptions", sortAlphabetically: true)
                        .ConfigureAwait(true)).ConfigureAwait(false);

                var progress = new Progress<string>(msg =>
                    UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg));
                var itemProgress = new Progress<DescriptionRefreshItemProgress>(p =>
                    UiDispatcher.InvokeAsync(() => HandleDescriptionRefreshProgress(p)));

                var count = await _svc.Descriptions.RefreshAllListDescriptionsAsync(
                    entries,
                    forceRegenerate: true,
                    progress,
                    itemProgress,
                    ct).ConfigureAwait(false);

                _svc.ActivityLog.Write("AI", $"Refreshed {count} catalog description(s) from web + AI.");

                var sizeProgress = new Progress<string>(msg =>
                    UiDispatcher.Invoke(() => CatalogStatusLabel.Text = msg));

                await _svc.CatalogStore.EnrichFileSizesAsync(sizeProgress, cancellationToken: ct)
                    .ConfigureAwait(false);

                _svc.CatalogStore.ClearCache();
                await ApplyCatalogFileSizesToRowsAsync(ct).ConfigureAwait(false);

                await UiDispatcher.InvokeAsync(() =>
                {
                    _descriptionRefreshInProgress = false;
                    _catalogRowAnimator.Stop();
                    ScrollCatalogGridToTop();
                    CatalogStatusLabel.Text = $"Descriptions refreshed — {count} AI summary(s) updated.";
                    EndTaskFlashSuccess(
                        sender,
                        "Refreshed",
                        10,
                        FinishDescriptionRefreshHoldover,
                        FlashSuccessStyle.Info);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _descriptionRefreshInProgress = false;
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = "Description refresh cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(() =>
                {
                    _descriptionRefreshInProgress = false;
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
                EndCatalogToolbarOperation();
            }
        }).ConfigureAwait(true);
    }

    private async Task ApplyCatalogFileSizesToRowsAsync(CancellationToken cancellationToken)
    {
        var entries = await _svc.CatalogStore.GetEntriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await UiDispatcher.InvokeAsync(() =>
        {
            foreach (var entry in entries)
            {
                if (!_catalogRowByName.TryGetValue(entry.Name, out var row)
                    || string.IsNullOrWhiteSpace(entry.FileSize)
                    || entry.FileSize == "-")
                {
                    continue;
                }

                row.FileSize = OllamaToolkit.Core.ModelSizeFormatter.FormatSizeLabel(entry.FileSize);
            }
        }).ConfigureAwait(false);
    }

    private void BindCatalogRows(IReadOnlyList<CatalogRowViewModel> rows)
    {
        _catalogRows.Clear();
        foreach (var row in rows)
        {
            _catalogRows.Add(row);
        }

        _catalogRowByName = _catalogRows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        ScheduleFitGridColumns(CatalogGrid);
        ReapplyUndownloadPurpleHighlights();
    }

    private async Task BindFullCatalogGridAsync(string statusPrefix, bool sortAlphabetically = false)
    {
        _suppressCatalogUiEvents = true;
        try
        {
            CategoryFilterCombo.SelectedIndex = 0;
            CatalogSearchBox.Text = string.Empty;
            var rows = (await _svc.Registry.GetCatalogRowsAsync(
                search: null,
                categoryFilter: "All",
                descriptionMode: _catalogDescriptionMode).ConfigureAwait(true)).ToList();
            if (sortAlphabetically)
            {
                rows = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            BindCatalogRows(rows);
            CatalogStatusLabel.Text = $"{statusPrefix} for {rows.Count} model(s)...";
        }
        finally
        {
            _suppressCatalogUiEvents = false;
        }
    }

    private void ScrollCatalogGridToTop()
    {
        if (_catalogRows.Count == 0)
        {
            return;
        }

        var first = _catalogRows[0];
        CatalogGrid.UpdateLayout();
        CatalogGrid.SelectedItem = first;
        CatalogGrid.ScrollIntoView(first);
        if (CatalogGrid.ItemContainerGenerator.ContainerFromItem(first) is System.Windows.Controls.DataGridRow row)
        {
            row.BringIntoView();
        }
    }

    private void FinishDescriptionRefreshHoldover()
    {
        CatalogRowRefreshAnimator.ResetAll(_catalogRows);
        _ = RefreshCatalogUiAsync();
    }

    private void FinishCatalogRefreshHoldover()
    {
        CatalogRowRefreshAnimator.ResetAll(_catalogRows);
        _ = RefreshCatalogUiAsync();
    }

    private void HandleCatalogRefreshProgress(CatalogRefreshItemProgress progress)
    {
        if (!_catalogRowByName.TryGetValue(progress.ModelName, out var row))
        {
            return;
        }

        if (progress.Phase == CatalogRefreshPhase.Started)
        {
            _catalogRowAnimator.BeginRow(row);
            CatalogGrid.SelectedItem = row;
            CatalogGrid.ScrollIntoView(row);
        }
        else
        {
            row.Installed = progress.IsInstalled;
            row.InstalledDisplay = progress.IsInstalled ? "Yes" : "No";
            if (!string.IsNullOrWhiteSpace(progress.FileSize))
            {
                row.FileSize = OllamaToolkit.Core.ModelSizeFormatter.FormatSizeLabel(progress.FileSize);
            }

            if (!string.IsNullOrWhiteSpace(progress.Description))
            {
                row.DownloadDescription = progress.Description.Trim();
                if (_catalogDescriptionMode == CatalogDescriptionDisplayMode.Download)
                {
                    row.DisplayDescription = row.DownloadDescription;
                }
            }

            var highlight = progress.IsInstalled
                ? CatalogRowRefreshHighlight.Installed
                : CatalogRowRefreshHighlight.None;
            _catalogRowAnimator.CompleteRow(row, highlight);
        }
    }

    private void HandleDescriptionRefreshProgress(DescriptionRefreshItemProgress progress)
    {
        if (!_catalogRowByName.TryGetValue(progress.ModelName, out var row))
        {
            return;
        }

        if (progress.Phase == DescriptionRefreshPhase.Started)
        {
            _catalogRowAnimator.BeginRow(row);
            CatalogGrid.SelectedItem = row;
            CatalogGrid.ScrollIntoView(row);
        }
        else
        {
            _catalogRowAnimator.CompleteRow(row, CatalogRowRefreshHighlight.Description);
            if (!string.IsNullOrWhiteSpace(progress.ListDescription))
            {
                row.AiDescription = progress.ListDescription;
                if (_catalogDescriptionMode == CatalogDescriptionDisplayMode.Ai)
                {
                    row.DisplayDescription = progress.ListDescription;
                }
            }
        }
    }

    private void SetAiSettingsActionStatus(string text) => AiSettingsActionStatus.Text = text;

    private void SetAiSettingsButtonsEnabled(bool enabled)
    {
        RefreshSummarizerListBtn.IsEnabled = enabled;
        DownloadSummarizerBtn.IsEnabled = enabled;
        TestSummarizerBtn.IsEnabled = enabled;
        CategorizeAllBtn.IsEnabled = enabled;
        RecategorizeAllBtn.IsEnabled = enabled;
    }

    private async Task LoadCatalogTabAsync()
    {
        if (_catalogRefreshInProgress || _descriptionRefreshInProgress)
        {
            return;
        }

        await RefreshCatalogUiAsync().ConfigureAwait(true);
    }

    private void CancelCatalogFileSizeEnrichment() => _svc.CatalogStore.CancelFileSizeEnrichment();

    private async Task CancelActiveCatalogOperationsAsync()
    {
        _refreshCatalogCts?.Cancel();
        _refreshDescriptionsCts?.Cancel();
        _classificationCts?.Cancel();
        _catalogDownloadCts?.Cancel();
        CancelCatalogFileSizeEnrichment();
        _catalogRefreshInProgress = false;
        _descriptionRefreshInProgress = false;
        _catalogDownloadInProgress = false;
        _catalogToolbarOperations = 0;
        UpdateCatalogStopButtonUi();

        await UiDispatcher.InvokeAsync(() =>
        {
            _catalogRowAnimator.Stop();
            CatalogRowRefreshAnimator.ResetAll(_catalogRows);
            HideCatalogDownloadProgress();
            EndTaskFlashIdle(RefreshCatalogBtn);
            EndTaskFlashIdle(DownloadCatalogModelBtn);
            EndTaskFlashIdle(RefreshDescriptionsBtn);
            EndTaskFlashIdle(CatalogCategorizeAllBtn);
            EndTaskFlashIdle(ClearCatalogBtn);
        }).ConfigureAwait(true);
    }

    private async void CatalogStop_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogToolbarOperations == 0)
        {
            return;
        }

        await CancelActiveCatalogOperationsAsync().ConfigureAwait(true);
        CatalogStatusLabel.Text = "Catalog operation stopped.";
    }

    private async Task RefreshCatalogUiAsync()
    {
        if (_catalogRefreshInProgress || _descriptionRefreshInProgress || _catalogDownloadInProgress)
        {
            return;
        }

        var search = CatalogSearchBox.Text;
        var category = CategoryFilterCombo.SelectedItem as string;
        var rows = (await _svc.Registry.GetCatalogRowsAsync(search, category, _catalogDescriptionMode)
            .ConfigureAwait(true)).ToList();
        if (NaturalLanguageSearchService.LooksNaturalLanguage(search) && rows.Count > 1)
        {
            var candidates = rows.Select(r => new NlSearchCandidate(r.Name, r.Category, r.ParameterSize, r.DisplayDescription))
                .ToList();
            EnterAiActivity();
            IReadOnlyList<string> ranked;
            try
            {
                ranked = await _svc.NlSearch.RankModelsAsync(search, candidates).ConfigureAwait(true);
            }
            finally
            {
                ExitAiActivity();
            }

            if (ranked.Count > 0)
            {
                _nlRankedCatalog = ranked.ToList();
                var rankMap = ranked.Select((name, i) => (name, i))
                    .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
                rows = rows.OrderBy(r => rankMap.TryGetValue(r.Name, out var i) ? i : 999).ThenBy(r => r.Name).ToList();
                CatalogStatusLabel.Text = $"NL-ranked {ranked.Count} model(s); showing {rows.Count}.";
                BindCatalogRows(rows);
                return;
            }
        }

        BindCatalogRows(rows);
        CatalogStatusLabel.Text = $"Showing {rows.Count} catalog model(s).";
    }

    private void CatalogSearchBox_KeyUp(object sender, KeyEventArgs e)
    {
        _catalogSearchTimer.Stop();
        _catalogSearchTimer.Start();
    }

    private async void CategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatalogUiEvents || _catalogRefreshInProgress || _descriptionRefreshInProgress)
        {
            return;
        }

        await RefreshCatalogUiAsync().ConfigureAwait(true);
    }

    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        _refreshCatalogCts?.Cancel();
        _refreshCatalogCts?.Dispose();
        _refreshCatalogCts = new CancellationTokenSource();
        _catalogRefreshInProgress = true;
        BeginCatalogToolbarOperation();
        CatalogStatusLabel.Text = "Refreshing from ollama.com...";
        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, _refreshCatalogCts.Token);
            var ct = linked.Token;
            try
            {
                await _svc.CatalogStore.RefreshFromWebAsync(cancellationToken: ct).ConfigureAwait(false);
                _svc.CatalogStore.ClearCache();

                await UiDispatcher.InvokeAsync(async () =>
                    await BindFullCatalogGridAsync("Refreshing catalog").ConfigureAwait(true)).ConfigureAwait(false);

                var progress = new Progress<string>(msg =>
                    UiDispatcher.Invoke(() => CatalogStatusLabel.Text = msg));

                var installed = await _svc.Registry.GetInstalledModelNamesAsync(ct).ConfigureAwait(false);
                await _svc.CatalogStore.ProcessCatalogEntriesUiPassAsync(
                    installed,
                    progress,
                    async (item, token) =>
                    {
                        await UiDispatcher.InvokeAsync(() => HandleCatalogRefreshProgress(item)).ConfigureAwait(true);
                    },
                    ct).ConfigureAwait(false);

                var sizeProgress = new Progress<string>(msg =>
                    UiDispatcher.Invoke(() => CatalogStatusLabel.Text = msg));

                await _svc.CatalogStore.EnrichFileSizesAsync(sizeProgress, cancellationToken: ct)
                    .ConfigureAwait(false);

                _svc.CatalogStore.ClearCache();

                await UiDispatcher.InvokeAsync(async () =>
                {
                    _catalogRefreshInProgress = false;
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                    _catalogRowAnimator.Stop();
                    ScrollCatalogGridToTop();
                    CatalogStatusLabel.Text = "Catalog refreshed.";
                    EndTaskFlashSuccess(sender, "Refreshed", 10, FinishCatalogRefreshHoldover);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRefreshInProgress = false;
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = "Catalog refresh cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRefreshInProgress = false;
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                EndCatalogToolbarOperation();
            }
        }).ConfigureAwait(true);
    }

    private async void ClearCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Delete all cached catalog data?\n\nThis removes:\n" +
                "• Cached ollama.com catalog\n" +
                "• AI descriptions\n" +
                "• Usage categories\n\n" +
                "Installed Ollama models on this PC are not removed.",
                "Clear Catalog",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await CancelActiveCatalogOperationsAsync().ConfigureAwait(true);

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        BeginCatalogToolbarOperation();
        CatalogStatusLabel.Text = "Clearing catalog...";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.CatalogStore.ResetStoreAsync(ct).ConfigureAwait(false);
                await _svc.Descriptions.ResetStoreAsync(ct).ConfigureAwait(false);
                await _svc.CategoryStore.ResetStoreAsync(ct).ConfigureAwait(false);

                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    _catalogRows.Clear();
                    _catalogRowByName = new Dictionary<string, CatalogRowViewModel>(StringComparer.OrdinalIgnoreCase);
                    CatalogStatusLabel.Text = "Catalog cleared — use Refresh Catalog to reload.";
                    _svc.ActivityLog.Write("Task", "Catalog metadata cleared.");
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(() =>
                {
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                EndCatalogToolbarOperation();
            }
        }).ConfigureAwait(true);
    }

    private async void CategorizeAll_Click(object sender, RoutedEventArgs e) =>
        await RunClassificationAsync(
            recategorize: false,
            fromAiSettings: MainTabs.SelectedItem == AiSettingsTab,
            sender,
            trackCatalogStop: ReferenceEquals(sender, CatalogCategorizeAllBtn)).ConfigureAwait(true);

    private async void RecategorizeAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reclassify the entire catalog?", "Recategorize All",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunClassificationAsync(
            recategorize: true,
            fromAiSettings: MainTabs.SelectedItem == AiSettingsTab,
            sender,
            trackCatalogStop: false).ConfigureAwait(true);
    }

    private async Task RunClassificationAsync(
        bool recategorize,
        bool fromAiSettings,
        object? sender = null,
        bool trackCatalogStop = false)
    {
        if (sender is not null && !BeginTaskFlash(sender))
        {
            return;
        }

        if (!await _svc.AiSettings.IsFeatureEnabledAsync(AiFeatureKeys.CatalogCategorization).ConfigureAwait(true))
        {
            var disabledMessage = "Catalog categorization is disabled in AI Settings.";
            if (fromAiSettings)
            {
                SetAiSettingsActionStatus(disabledMessage);
            }
            else
            {
                CatalogStatusLabel.Text = disabledMessage;
            }

            if (sender is not null)
            {
                EndTaskFlashIdle(sender);
            }

            return;
        }

        var aiSettings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        if (!aiSettings.ToolkitAiEnabled)
        {
            var disabledMessage = "Enable Toolkit AI in AI Settings to categorize models.";
            if (fromAiSettings)
            {
                SetAiSettingsActionStatus(disabledMessage);
            }
            else
            {
                CatalogStatusLabel.Text = disabledMessage;
            }

            if (sender is not null)
            {
                EndTaskFlashIdle(sender);
            }

            return;
        }

        var summarizer = await _svc.Summarizer.ResolveAsync().ConfigureAwait(true);
        var ollamaReady = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        if (!ollamaReady || string.IsNullOrWhiteSpace(summarizer))
        {
            var inactiveMessage =
                "AI categorization requires Ollama running with a summarizer model installed.";
            if (fromAiSettings)
            {
                SetAiSettingsActionStatus(inactiveMessage);
            }
            else
            {
                CatalogStatusLabel.Text = inactiveMessage;
            }

            if (sender is not null)
            {
                EndTaskFlashIdle(sender);
            }

            return;
        }

        if (fromAiSettings)
        {
            SetAiSettingsActionStatus("Classifying catalog models...");
            SetAiSettingsButtonsEnabled(false);
        }
        else
        {
            CatalogStatusLabel.Text = "Classifying catalog models...";
        }

        if (trackCatalogStop)
        {
            BeginCatalogToolbarOperation();
        }

        _classificationCts?.Cancel();
        _classificationCts?.Dispose();
        _classificationCts = new CancellationTokenSource();

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, _classificationCts.Token);
            var ct = linked.Token;
            EnterAiActivity();
            try
            {
                var progress = new Progress<string>(msg =>
                {
                    UiDispatcher.InvokeAsync(() =>
                    {
                        if (fromAiSettings)
                        {
                            SetAiSettingsActionStatus(msg);
                        }
                        else
                        {
                            CatalogStatusLabel.Text = msg;
                        }
                    });
                });
                var count = await _svc.Classification.ClassifyAllAsync(recategorize, progress, ct)
                    .ConfigureAwait(false);
                _svc.ActivityLog.Write("AI", $"Classified {count} catalog model(s).");
                await UiDispatcher.InvokeAsync(async () =>
                {
                    _svc.CategoryStore.ClearCache();
                    _svc.CatalogStore.ClearCache();

                    var doneMessage = count == 0
                        ? "All catalog models are already AI-categorized."
                        : $"Done — AI classified {count} catalog model(s).";
                    if (fromAiSettings)
                    {
                        SetAiSettingsActionStatus(doneMessage);
                    }
                    else
                    {
                        CatalogStatusLabel.Text = doneMessage;
                    }

                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                    await RefreshCategoryStatusAsync().ConfigureAwait(true);
                    if (sender is not null)
                    {
                        EndTaskFlashSuccess(sender, "Categorized", 10);
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    if (fromAiSettings)
                    {
                        SetAiSettingsActionStatus("Classification cancelled.");
                    }
                    else
                    {
                        CatalogStatusLabel.Text = "Classification cancelled.";
                    }

                    if (sender is not null)
                    {
                        EndTaskFlashIdle(sender);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(() =>
                {
                    if (fromAiSettings)
                    {
                        SetAiSettingsActionStatus(msg);
                    }
                    else
                    {
                        CatalogStatusLabel.Text = msg;
                    }

                    if (sender is not null)
                    {
                        EndTaskFlashIdle(sender);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
                if (trackCatalogStop)
                {
                    EndCatalogToolbarOperation();
                }

                if (fromAiSettings)
                {
                    await UiDispatcher.InvokeAsync(() => SetAiSettingsButtonsEnabled(true)).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(true);
    }

    private void CatalogGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_catalogRefreshInProgress || _descriptionRefreshInProgress)
        {
            return;
        }

        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row
            || row.Item is not CatalogRowViewModel item)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        CatalogGrid.SelectedItem = item;
        CatalogGrid.CurrentItem = item;
    }

    private void ClearCatalogSelections_Click(object sender, RoutedEventArgs e) => CatalogGrid.UnselectAll();

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private async Task<(bool Success, string Message)> EnsureOllamaApiReadyAsync(
        CancellationToken cancellationToken,
        string? statusPrefix = null,
        int timeoutSec = 90)
    {
        if (await _svc.ApiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return (true, "Ollama API is ready.");
        }

        var prefix = string.IsNullOrWhiteSpace(statusPrefix) ? "Ollama API not reachable" : statusPrefix;
        await UiDispatcher.InvokeAsync(() =>
        {
            CatalogStatusLabel.Text = $"{prefix} — starting Ollama and waiting for localhost:11434…";
            BeginCatalogDownloadProgressUi("Ollama", "Starting API…");
        }).ConfigureAwait(false);

        var startup = await _svc.ModeService.Processes
            .EnsureApiReadyAsync(timeoutSec, cancellationToken, autoStart: true)
            .ConfigureAwait(false);
        _svc.ApiClient.InvalidateCaches();

        if (!startup.Success)
        {
            _svc.ActivityLog.Write("Error", startup.Message);
            return (false, startup.Message);
        }

        return (true, startup.Message);
    }

    private void BeginCatalogDownloadProgressUi(string model, string status)
    {
        CatalogDownloadProgressPanel.Visibility = Visibility.Visible;
        CatalogDownloadProgress.Value = 0;
        CatalogDownloadProgressLabel.Text = $"{model} — {status}";
        CatalogStatusLabel.Text = CatalogDownloadProgressLabel.Text;
    }

    private void ShowCatalogDownloadProgress(string model, ModelPullProgress update)
    {
        CatalogDownloadProgressPanel.Visibility = Visibility.Visible;
        CatalogDownloadProgress.Value = update.Percent ?? CatalogDownloadProgress.Value;
        CatalogDownloadProgressLabel.Text = update.Percent is int percent
            ? $"{model} — {update.Status} ({percent}%)"
            : $"{model} — {update.Status}";
        CatalogStatusLabel.Text = CatalogDownloadProgressLabel.Text;
    }

    private void HideCatalogDownloadProgress()
    {
        CatalogDownloadProgressPanel.Visibility = Visibility.Collapsed;
        CatalogDownloadProgress.Value = 0;
        CatalogDownloadProgressLabel.Text = string.Empty;
    }

    private void BeginCatalogDownloadRow(CatalogRowViewModel row)
    {
        row.IsDownloading = true;
        _catalogRowAnimator.BeginRow(row);
        CatalogGrid.SelectedItem = row;
        CatalogGrid.ScrollIntoView(row);
    }

    private void EndCatalogDownloadRow(CatalogRowViewModel row, bool installed)
    {
        row.IsDownloading = false;
        row.Installed = installed;
        row.InstalledDisplay = installed ? "Yes" : "No";
        _catalogRowAnimator.CompleteRow(
            row,
            installed ? CatalogRowRefreshHighlight.Installed : CatalogRowRefreshHighlight.None);
    }

    private async void DownloadCatalogModel_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRowViewModel row)
        {
            MessageBox.Show("Select a catalog model first.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_catalogDownloadInProgress)
        {
            MessageBox.Show("Another catalog download is already in progress.", "Download",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        _catalogDownloadCts?.Cancel();
        _catalogDownloadCts = new CancellationTokenSource();
        var ct = _catalogDownloadCts.Token;
        var model = $"{row.Name}:latest";

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, ct);
            var token = linked.Token;
            _catalogDownloadInProgress = true;

            await UiDispatcher.InvokeAsync(() =>
            {
                BeginCatalogDownloadRow(row);
                BeginCatalogDownloadProgressUi(model, "Preparing download…");
            }).ConfigureAwait(false);

            try
            {
                var startup = await EnsureOllamaApiReadyAsync(token, $"Cannot download {model}", timeoutSec: 90)
                    .ConfigureAwait(false);
                if (!startup.Success)
                {
                    throw new InvalidOperationException(startup.Message);
                }

                await UiDispatcher.InvokeAsync(() =>
                    BeginCatalogDownloadProgressUi(model, "Downloading…")).ConfigureAwait(false);

                var progress = new Progress<ModelPullProgress>(update =>
                {
                    UiDispatcher.InvokeAsync(() =>
                    {
                        ShowCatalogDownloadProgress(model, update);
                        _svc.ActivityLog.Write("Download", update.Status);
                    });
                });

                await _svc.ApiClient.PullAsync(model, progress, token).ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                _svc.Profiles.ClearCache();

                var installed = await _svc.ApiClient.IsModelInstalledAsync(model, token).ConfigureAwait(false);
                if (!installed)
                {
                    throw new InvalidOperationException(
                        $"Download finished but {model} was not found in the local Ollama model list.");
                }

                _svc.ActivityLog.Write("Task", $"Downloaded {model}.");
                await UiDispatcher.InvokeAsync(async () =>
                {
                    EndCatalogDownloadRow(row, installed: true);
                    HideCatalogDownloadProgress();
                    CatalogStatusLabel.Text = $"Downloaded {model}.";
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    EndTaskFlashSuccess(sender, "Downloaded", 10);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    EndCatalogDownloadRow(row, row.Installed);
                    HideCatalogDownloadProgress();
                    CatalogStatusLabel.Text = $"Download cancelled for {model}.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, token).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    EndCatalogDownloadRow(row, row.Installed);
                    HideCatalogDownloadProgress();
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                _catalogDownloadInProgress = false;
            }
        }).ConfigureAwait(true);
    }

    private async void UninstallCatalogModel_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRowViewModel row)
        {
            MessageBox.Show("Select an installed catalog model first.", "Uninstall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!row.Installed && !row.InstalledDisplay.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show($"{row.Name} is not installed locally.", "Uninstall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var model = $"{row.Name}:latest";
        if (!ToolkitConfirmDialog.ShowAccept(
                this,
                $"Remove {model} from this PC? Benchmark data in the toolkit will be kept.",
                "Uninstall LLM"))
        {
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModelSessions.UninstallModelAsync(model, ct).ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                _svc.Profiles.ClearCache();
                await UiDispatcher.InvokeAsync(async () =>
                {
                    row.Installed = false;
                    row.InstalledDisplay = "No";
                    CatalogStatusLabel.Text = $"Uninstalled {model}.";
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                    EndTaskFlashSuccess(sender, "Removed", 10);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task<string> FormatInsightColumnAsync(string? insight)
    {
        if (!string.IsNullOrWhiteSpace(insight))
        {
            return insight.Trim();
        }

        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        if (!settings.ToolkitAiEnabled)
        {
            return "(AI disabled)";
        }

        if (!await _svc.AiSettings.IsFeatureEnabledAsync(AiFeatureKeys.BenchmarkInterpreter).ConfigureAwait(true))
        {
            return "(interpreter off)";
        }

        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        if (!ready || string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel))
        {
            return "(AI inactive)";
        }

        return string.Empty;
    }

    private async Task RefreshTestResultsUiAsync()
    {
        var rows = await _svc.Registry.GetTestResultRowsAsync().ConfigureAwait(true);
        var enriched = new List<TestResultRowViewModel>();
        foreach (var row in rows)
        {
            var insight = await _svc.BenchmarkInsights.GetInsightAsync(row.Model).ConfigureAwait(true);
            var insightText = await FormatInsightColumnAsync(insight).ConfigureAwait(true);
            enriched.Add(new TestResultRowViewModel
            {
                Model = row.Model,
                Category = row.Category,
                BenchmarkKind = row.BenchmarkKind,
                BestMode = row.BestMode,
                BestTps = row.BestTps,
                BestEmbedMs = row.BestEmbedMs,
                CpuResult = row.CpuResult,
                ApuResult = row.ApuResult,
                GpuResult = row.GpuResult,
                HybridResult = row.HybridResult,
                RocmResult = row.RocmResult,
                Insight = insightText,
                LastTested = row.LastTested,
                ReportPath = row.ReportPath
            });
        }

        TestResultsGrid.ItemsSource = enriched;
        ScheduleFitGridColumns(TestResultsGrid);
    }

    private async void ClearTestResultRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TestResultRowViewModel row })
        {
            return;
        }

        var message =
            $"Clear all test data for '{row.Model}'? Benchmark results, AI insights, and report files "
            + "for this model will be removed and cannot be restored.";

        if (!ToolkitConfirmDialog.ShowAccept(this, message, "Clear Test Data"))
        {
            return;
        }

        try
        {
            var cleared = await _svc.Profiles.ClearTestDataForModelAsync(row.Model).ConfigureAwait(true);
            await _svc.BenchmarkInsights.ClearForModelAsync(row.Model).ConfigureAwait(true);
            await _svc.BenchmarkSettingsAdvisor.ClearForModelAsync(row.Model).ConfigureAwait(true);
            _svc.Profiles.ClearCache();
            _svc.BenchmarkInsights.ClearCache();
            _svc.BenchmarkSettingsAdvisor.ClearCache();

            await UiDispatcher.InvokeAsync(async () =>
            {
                TestResultDetail.Text = string.Empty;
                await RefreshModelsUiAsync().ConfigureAwait(true);
                await RefreshTestResultsUiAsync().ConfigureAwait(true);
                await RefreshCatalogUiAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);

            _svc.ActivityLog.Write(
                "Benchmark",
                $"Cleared test data for {row.Model} ({cleared.ReportDirsRemoved} report dir(s))");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to clear test data for {row.Model}: {ex.Message}",
                "Clear Test Data",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void RefreshTestResults_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        try
        {
            await RefreshTestResultsUiAsync().ConfigureAwait(true);
            EndTaskFlashSuccess(sender, "Refreshed");
        }
        catch
        {
            EndTaskFlashIdle(sender);
            throw;
        }
    }

    private async void TestResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TestResultsGrid.SelectedItem is not TestResultRowViewModel row)
        {
            TestResultDetail.Text = string.Empty;
            return;
        }

        var entry = await _svc.BenchmarkInsights.GetEntryAsync(row.Model).ConfigureAwait(true);
        if (entry is null || (string.IsNullOrWhiteSpace(entry.Interpretation)
            && entry.FailureDiagnosis is not { Count: > 0 }))
        {
            TestResultDetail.Text = await FormatInsightColumnAsync(entry?.Interpretation).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(TestResultDetail.Text))
            {
                TestResultDetail.Text = $"No AI insight for {row.Model} yet.";
            }

            return;
        }

        var detail = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(entry.Interpretation))
        {
            detail.Append(entry.Interpretation);
        }

        if (entry.FailureDiagnosis is { Count: > 0 })
        {
            if (detail.Length > 0)
            {
                detail.AppendLine().AppendLine();
            }

            detail.AppendLine("Failure diagnosis:");
            foreach (var diagnosis in entry.FailureDiagnosis)
            {
                detail.AppendLine($"  {diagnosis.Key}: {diagnosis.Value}");
            }
        }

        TestResultDetail.Text = detail.ToString().TrimEnd();
    }

    private async void LaunchFromResults_Click(object sender, RoutedEventArgs e)
    {
        if (TestResultsGrid.SelectedItem is not TestResultRowViewModel row)
        {
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var match = summaries.FirstOrDefault(s => s.Model.Equals(row.Model, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            EndTaskFlashIdle(sender);
            return;
        }

        ModelsGrid.ItemsSource = summaries;
        ModelsGrid.SelectedItem = match;
        try
        {
            await LaunchBestMode_Click_Internal(match).ConfigureAwait(true);
            EndTaskFlashSuccess(sender, "Launched");
        }
        catch
        {
            EndTaskFlashIdle(sender);
            throw;
        }
    }

    private async Task LaunchBestMode_Click_Internal(ModelProfileSummary model)
    {
        if (model.NeedsRetest || string.IsNullOrEmpty(model.BestMode))
        {
            return;
        }

        if (!Enum.TryParse<ComputeMode>(model.BestMode, out var mode))
        {
            return;
        }

        MainTabs.SelectedItem = ModelRunTab;
        _runModel = model.Model;
        ModelRunStatus.Text = $"Applying {model.BestMode} and restarting Ollama…";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeWithRestartAsync(mode, cancellationToken: ct)
                    .ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                await _svc.ModelSessions.SwitchToModelAsync(model.Model, warmLoad: true, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ModelRunStatus.Text =
                        $"{model.Model} | {model.BestMode} ({model.BestMetricDisplay}) | Ollama restarted, model loaded";
                    ChatHistory.Text = string.Empty;
                    _chatMessages.Clear();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() => ModelRunStatus.Text = msg).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task RefreshAiSettingsUiAsync()
    {
        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        var summarizer = await _svc.Summarizer.ResolveAsync().ConfigureAwait(true);

        AiSettingsStatus.Text = ready
            ? (summarizer is not null ? $"AI Active — summarizer: {summarizer}" : "AI Inactive — no summarizer installed")
            : "AI Inactive — Ollama API not reachable";

        var tags = await _svc.ApiClient.GetTagsAsync(forceRefresh: true).ConfigureAwait(true);
        var installed = tags.Select(t => t.Name).ToList();
        _suppressSummarizerComboSave = true;
        try
        {
            SummarizerCombo.ItemsSource = installed;
            if (!string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel)
                && installed.Contains(settings.PreferredSummarizerModel, StringComparer.OrdinalIgnoreCase))
            {
                SummarizerCombo.SelectedItem = settings.PreferredSummarizerModel;
            }
            else if (installed.Count > 0)
            {
                SummarizerCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressSummarizerComboSave = false;
        }

        var suggested = SummarizerModelResolver.DefaultPreferenceOrder
            .Where(m => !installed.Contains(m, StringComparer.OrdinalIgnoreCase))
            .ToList();
        SuggestedModelsList.ItemsSource = suggested;

        await RefreshCategoryStatusAsync().ConfigureAwait(true);
    }

    private async Task RefreshCategoryStatusAsync()
    {
        var entries = await _svc.CatalogStore.GetEntriesAsync().ConfigureAwait(true);
        var (classified, total) = await _svc.CategoryStore.GetStatusAsync(entries.Count).ConfigureAwait(true);
        CategoryStatusLabel.Text = $"Catalog categorization: {classified}/{total} classified";
    }

    private async void SummarizerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSummarizerComboSave || SummarizerCombo.SelectedItem is not string model)
        {
            return;
        }

        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        settings.PreferredSummarizerModel = model;
        await _svc.AiSettings.SaveAsync(settings).ConfigureAwait(true);
        await UpdateAiStatusAsync().ConfigureAwait(true);
    }

    private async void RefreshSummarizerList_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        SetAiSettingsActionStatus("Refreshing summarizer list...");
        SetAiSettingsButtonsEnabled(false);
        try
        {
            await RefreshAiSettingsUiAsync().ConfigureAwait(true);
            SetAiSettingsActionStatus("Summarizer list refreshed.");
            EndTaskFlashSuccess(sender, "Refreshed");
        }
        catch (Exception ex)
        {
            SetAiSettingsActionStatus($"Refresh failed: {ex.Message}");
            _svc.ActivityLog.Write("Error", ex.Message);
            EndTaskFlashIdle(sender);
        }
        finally
        {
            SetAiSettingsButtonsEnabled(true);
        }
    }

    private async void DownloadSummarizer_Click(object sender, RoutedEventArgs e)
    {
        var model = SummarizerCombo.SelectedItem as string
            ?? SuggestedModelsList.SelectedItem as string
            ?? SummarizerModelResolver.DefaultPreferenceOrder.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(model))
        {
            SetAiSettingsActionStatus("Select a summarizer or suggested model to download.");
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        SetAiSettingsActionStatus($"Downloading {model}...");
        SetAiSettingsButtonsEnabled(false);
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                var progress = new Progress<ModelPullProgress>(p =>
                {
                    _svc.ActivityLog.Write("Download", p.Status);
                    UiDispatcher.InvokeAsync(() =>
                        SetAiSettingsActionStatus($"Downloading {model}: {p.Status}"));
                });
                await _svc.ApiClient.PullAsync(model, progress, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Task", $"Downloaded summarizer {model}.");
                await UiDispatcher.InvokeAsync(async () =>
                {
                    SetAiSettingsActionStatus($"Downloaded {model}.");
                    await RefreshAiSettingsUiAsync().ConfigureAwait(true);
                    EndTaskFlashSuccess(sender, "Downloaded", 10);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(() =>
                {
                    SetAiSettingsActionStatus(msg);
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() => SetAiSettingsButtonsEnabled(true)).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void TestSummarizer_Click(object sender, RoutedEventArgs e)
    {
        var model = SummarizerCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model))
        {
            SummarizerTestResult.Text = "Select a summarizer model first.";
            SetAiSettingsActionStatus("Select a summarizer model first.");
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        SetAiSettingsActionStatus($"Testing summarizer ({model})...");
        SummarizerTestResult.Text = string.Empty;
        SetAiSettingsButtonsEnabled(false);
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            EnterAiActivity();
            try
            {
                var result = await _svc.ApiClient.GenerateAsync(
                    model,
                    "Summarize in one short phrase: Llama is a family of open large language models.",
                    32, 2048, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    SummarizerTestResult.Text = $"Test OK: {result.Trim()}";
                    SetAiSettingsActionStatus("Summarizer test complete.");
                    EndTaskFlashSuccess(sender, "Tested", 10);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    SummarizerTestResult.Text = msg;
                    SetAiSettingsActionStatus(msg);
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
                await UiDispatcher.InvokeAsync(() => SetAiSettingsButtonsEnabled(true)).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void ScanLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            EnterAiActivity();
            try
            {
                var doc = await _svc.LogAnomalies.ScanAsync(ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    AnomalySummary.Text = doc.Anomalies.Count == 0
                        ? "No anomalies detected."
                        : string.Join(" | ", doc.Anomalies.Select(a => $"{a.Pattern} ({a.Count}): {a.Summary}"));
                    RefreshActivityLog();
                    EndTaskFlashSuccess(sender, "Scanned", 10);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    AnomalySummary.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
            }
        }).ConfigureAwait(true);
    }

    private async void AskAiModels_Click(object sender, RoutedEventArgs e) =>
        await RunAskAiAsync(fromModelRun: false, sender).ConfigureAwait(true);

    private async void AskAiRun_Click(object sender, RoutedEventArgs e) =>
        await RunAskAiAsync(fromModelRun: true, sender).ConfigureAwait(true);

    private async Task RunAskAiAsync(bool fromModelRun, object? sender = null)
    {
        var intent = PromptForIntent("What do you want to do?");
        if (string.IsNullOrWhiteSpace(intent))
        {
            return;
        }

        if (sender is not null && !BeginTaskFlash(sender))
        {
            return;
        }

        var summaries = (ModelsGrid.ItemsSource as IEnumerable<ModelProfileSummary>)?.ToList()
            ?? await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var categories = await GetCategoryMapAsync().ConfigureAwait(true);
        AiFlyoutTitle.Text = $"Recommended for \"{intent}\"";
        AiFlyoutBody.Text = "Thinking...";
        AiFlyoutPopup.IsOpen = true;

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            EnterAiActivity();
            try
            {
                var recs = await _svc.ModelAdvisor.RecommendInstalledAsync(intent, summaries, categories, ct)
                    .ConfigureAwait(false);
                var body = recs.Count == 0
                    ? "No installed models matched."
                    : string.Join(Environment.NewLine, recs.Select(r =>
                        $"{r.Rank}. {r.Model}  {r.BestMode}  {r.BestTps:F1} tok/s  ({r.Category})"));
                _svc.ActivityLog.Write("AI", $"Advisor: {intent} -> {recs.Count} model(s)");
                await UiDispatcher.InvokeAsync(() =>
                {
                    AiFlyoutBody.Text = body;
                    if (fromModelRun && recs.Count > 0)
                    {
                        ModelRunStatus.Text = $"Top pick: {recs[0].Model}";
                    }

                    if (sender is not null)
                    {
                        EndTaskFlashSuccess(sender, "Done", 10);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    AiFlyoutBody.Text = msg;
                    if (sender is not null)
                    {
                        EndTaskFlashIdle(sender);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
            }
        }).ConfigureAwait(true);
    }

    private async void CompareModels_Click(object sender, RoutedEventArgs e)
    {
        var selected = ModelsGrid.SelectedItems.Cast<ModelProfileSummary>().ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show("Select at least two models (Ctrl+click).", "Compare", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        var models = selected
            .Select(s => (s.Model, (string?)s.Category, (ModelProfileSummary?)s))
            .ToList();
        await RunCompareManyAsync(models, sender).ConfigureAwait(true);
    }

    private async void CompareCatalog_Click(object sender, RoutedEventArgs e)
    {
        var selected = CatalogGrid.SelectedItems.Cast<CatalogRowViewModel>().ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show("Select at least two catalog models (Ctrl+click).", "Compare", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var models = new List<(string Model, string? Category, ModelProfileSummary? Summary)>();
        foreach (var row in selected)
        {
            var summary = summaries.FirstOrDefault(s =>
                s.Model.StartsWith($"{row.Name}:", StringComparison.OrdinalIgnoreCase)
                || s.Model.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
            models.Add((
                row.Name,
                row.Category,
                summary ?? new ModelProfileSummary { Model = row.Name, Category = row.Category }));
        }

        await RunCompareManyAsync(models, sender).ConfigureAwait(true);
    }

    private async Task RunCompareManyAsync(
        IReadOnlyList<(string Model, string? Category, ModelProfileSummary? Summary)> models,
        object? sender = null)
    {
        AiFlyoutTitle.Text = models.Count == 2
            ? $"{models[0].Model} vs {models[1].Model}"
            : $"Comparing {models.Count} models";
        AiFlyoutBody.Text = "Comparing...";
        AiFlyoutPopup.IsOpen = true;

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            EnterAiActivity();
            try
            {
                var text = await _svc.ModelComparison.CompareManyAsync(models, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("AI", $"Compared {models.Count} model(s): {string.Join(", ", models.Select(m => m.Model))}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    AiFlyoutBody.Text = text;
                    if (sender is not null)
                    {
                        EndTaskFlashSuccess(sender, "Compared", 10);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    AiFlyoutBody.Text = msg;
                    if (sender is not null)
                    {
                        EndTaskFlashIdle(sender);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
            }
        }).ConfigureAwait(true);
    }

    private void CloseAiFlyout_Click(object sender, RoutedEventArgs e) => AiFlyoutPopup.IsOpen = false;

    private static string? PromptForIntent(string title)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
        };
        var box = new TextBox
        {
            Margin = new Thickness(12),
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Top,
            Height = 32
        };
        var ok = new Button
        {
            Content = "OK",
            Width = 80,
            Margin = new Thickness(0, 0, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.FindResource("ToolkitButton")
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(box, 0);
        Grid.SetRow(ok, 1);
        panel.Children.Add(box);
        panel.Children.Add(ok);
        dialog.Content = panel;
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; dialog.DialogResult = true; };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                dialog.DialogResult = true;
            }
        };
        dialog.ShowDialog();
        return result?.Trim();
    }

    private async Task ScheduleLogScanAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        if (!IsLoaded)
        {
            return;
        }

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            if (!await _svc.ApiClient.IsReadyAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            var doc = await _svc.LogAnomalies.ScanAsync(ct).ConfigureAwait(false);
            if (doc.Anomalies.Count > 0)
            {
                await UiDispatcher.InvokeAsync(() =>
                    AnomalySummary.Text = $"{doc.Anomalies.Count} anomaly pattern(s) detected — see AI Activity.")
                    .ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }
}