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
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
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
    private string? _runModel;
    private readonly List<ChatMessage> _chatMessages = new();
    private readonly DispatcherTimer _activityTimer;
    private readonly DispatcherTimer _catalogSearchTimer;
    private readonly DispatcherTimer _testSettingsTimer;
    private readonly ThrottledUpdater _chatUpdater;
    private readonly ThrottledUpdater _testLogUpdater;
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Status)> _testModeProgress = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _nlRankedCatalog = new();
    public MainWindow()
    {
        InitializeComponent();
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
        _chatUpdater = new ThrottledUpdater(TimeSpan.FromMilliseconds(33));
        _testLogUpdater = new ThrottledUpdater(TimeSpan.FromMilliseconds(33));
        Loaded += OnLoadedAsync;
        Closed += (_, _) =>
        {
            _activityTimer.Stop();
            _catalogSearchTimer.Stop();
            _testSettingsTimer.Stop();
            _modeCardPresenter.Stop();
        };
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
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
    }

    private async Task UpdateAiStatusAsync()
    {
        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        if (!settings.ToolkitAiEnabled)
        {
            AiStatusButton.Content = "AI Disabled";
        }
        else if (!ready)
        {
            AiStatusButton.Content = "AI Inactive";
        }
        else if (!string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel))
        {
            AiStatusButton.Content = "AI Active";
        }
        else
        {
            AiStatusButton.Content = "AI Inactive";
        }

        await UpdateAiButtonStatesAsync().ConfigureAwait(true);
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
        _modeCardPresenter.BeginTransition(previousMode, tag);
        ModeStatusLabel.Text = $"Applying {tag}…";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeAsync(mode, restartOllama: restart, cancellationToken: ct)
                    .ConfigureAwait(false);
                _svc.ActivityLog.Write("Task", $"Applied mode {mode}.");
                await UiDispatcher.InvokeAsync(async () =>
                {
                    _modeCardPresenter.EndTransition();
                    await RefreshModesUiAsync().ConfigureAwait(true);
                    ModeStatusLabel.Text = $"Current mode: {mode} (applied) | Ollama restarted: {restart}";
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

    private async void RefreshModes_Click(object sender, RoutedEventArgs e) =>
        await RefreshModesUiAsync().ConfigureAwait(true);

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        _svc.Profiles.ClearCache();
        await RefreshModelsUiAsync().ConfigureAwait(true);
    }

    private async void ImportReports_Click(object sender, RoutedEventArgs e)
    {
        var count = await _svc.ReportImporter.ImportReportsAsync().ConfigureAwait(true);
        _svc.ActivityLog.Write("Task", $"Manual import: {count} report(s).");
        _svc.Profiles.ClearCache();
        await RefreshModelsUiAsync().ConfigureAwait(true);
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

        MainTabs.SelectedItem = ModelRunTab;
        _runModel = model.Model;
        ModelRunStatus.Text = $"Applying best mode {model.BestMode} for {model.Model}...";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeAsync(mode, restartOllama: true, cancellationToken: ct)
                    .ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ModelRunStatus.Text = $"{model.Model} | Mode: {model.BestMode} ({model.BestTps:F1} tok/s) | Ready";
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

    private async void TestSelected_Click(object sender, RoutedEventArgs e)
    {
        var model = TestModelCombo.SelectedItem as string ?? GetSelectedModel()?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        await RunBenchmarkQueueAsync(new[] { model }).ConfigureAwait(true);
    }

    private async void TestUntested_Click(object sender, RoutedEventArgs e) =>
        await RunUntestedBenchmarkQueueAsync().ConfigureAwait(true);

    private async void ClearAndRerunTests_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Clear all benchmark data?\n\n"
                + "This removes model profiles, saved reports, and AI benchmark insights. "
                + "Every installed model will be marked untested, then the full test queue will start.",
                "Clear & Rerun Tests",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _benchmarkCts?.Cancel();
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
            TestLogBox.Text +=
                $"--- Cleared all test data ({cleared.ReportDirsRemoved} report folder(s)) ---{Environment.NewLine}";
            TestStatusLabel.Text = "All test data cleared. Starting benchmark queue…";
            await RefreshModelsUiAsync().ConfigureAwait(true);
            await RefreshTestResultsUiAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        _svc.ActivityLog.Write("Benchmark", $"Cleared test data: {cleared.ReportDirsRemoved} report dirs");
        await RunUntestedBenchmarkQueueAsync().ConfigureAwait(true);
    }

    private async Task RunUntestedBenchmarkQueueAsync()
    {
        var untested = await _svc.Profiles.GetUntestedAsync().ConfigureAwait(true);
        var names = untested.Select(u => u.Model).ToList();
        if (names.Count == 0)
        {
            await UiDispatcher.InvokeAsync(() => TestStatusLabel.Text = "No untested models.").ConfigureAwait(true);
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var queue = await _svc.QueueAdvisor.PrioritizeAsync(names, summaries).ConfigureAwait(true);
        await UiDispatcher.InvokeAsync(() =>
            TestStatusLabel.Text = $"AI ordered queue: {string.Join(" -> ", queue.Models)}").ConfigureAwait(true);
        _svc.ActivityLog.Write("AI", queue.Rationale);
        await RunBenchmarkQueueAsync(queue.Models).ConfigureAwait(true);
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
        var settings = await _svc.BenchmarkSettingsAdvisor.SuggestAsync(summary, category).ConfigureAwait(true);
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

    private void BuildTestModeProgressRows(IReadOnlyList<string> modes)
    {
        TestModeProgressPanel.Children.Clear();
        _testModeProgress.Clear();

        foreach (var mode in modes)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            var name = new TextBlock
            {
                Text = mode,
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
            _testModeProgress[mode] = (bar, status);
        }
    }

    private void ResetTestProgressUi(string model, int modelIndex, int modelCount, IReadOnlyList<string> modes, string? aiSummarizer)
    {
        TestProgressPanel.Visibility = Visibility.Visible;
        TestOverallProgress.Value = 0;
        var aiLine = string.IsNullOrWhiteSpace(aiSummarizer)
            ? "AI insights LLM: (none)"
            : $"AI insights LLM: {aiSummarizer}";
        TestOverallLabel.Text =
            $"Overall: {model} ({modelIndex + 1}/{modelCount}) — {aiLine}";
        BuildTestModeProgressRows(modes);

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

        if (!string.IsNullOrWhiteSpace(update.Model))
        {
            var aiLine = string.IsNullOrWhiteSpace(update.AiSummarizerModel)
                ? "AI insights LLM: (none)"
                : $"AI insights LLM: {update.AiSummarizerModel}";
            TestOverallLabel.Text =
                $"Overall: {update.Model} ({update.ModelIndex + 1}/{update.ModelCount}) — {aiLine}";
        }

        if (string.IsNullOrWhiteSpace(update.Mode) || !_testModeProgress.TryGetValue(update.Mode, out var row))
        {
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
                row.Status.Text = "Benchmarking…";
                row.Status.Foreground = (Brush)FindResource("Brush.Warning");
                break;
            case BenchmarkProgressPhase.ModeCompleted:
                row.Bar.Value = 100;
                row.Status.Text = $"{update.GenerationTps:F1} tok/s";
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
                BenchmarkProgressPhase.ModeBenchmarking =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — running benchmark on {update.Model}…",
                BenchmarkProgressPhase.ModeCompleted =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — {update.GenerationTps:F2} tok/s",
                BenchmarkProgressPhase.ModeFailed =>
                    $"[{update.ModeIndex + 1}/{update.ModeCount}] {update.Mode} — failed",
                BenchmarkProgressPhase.ModelCompleted when update.BestMode is not null =>
                    $"{update.Model} complete — winner: {update.BestMode} @ {update.BestTps:F2} tok/s",
                _ => TestStatusLabel.Text
            };
        }
    }

    private async Task RunBenchmarkQueueAsync(IReadOnlyList<string> models)
    {
        _benchmarkCts?.Cancel();
        _benchmarkCts = new CancellationTokenSource();
        var ct = _benchmarkCts.Token;
        var (numCtx, numPredict) = ParseBenchmarkSpinners(TestNumCtxBox.Text, TestNumPredictBox.Text);
        var benchmarkModes = new[] { "CPU", "APU", "GPU", "Hybrid", "ROCm" };
        var aiSummarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: ct).ConfigureAwait(true);

        await _svc.WorkQueue.EnqueueAsync(async token =>
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                TestLogBox.Text += $"--- Benchmark queue started ({models.Count} model(s)) ---{Environment.NewLine}";
                if (!string.IsNullOrWhiteSpace(aiSummarizer))
                {
                    TestLogBox.Text += $"AI insights LLM: {aiSummarizer}{Environment.NewLine}";
                }

                TestLogBox.CaretIndex = TestLogBox.Text.Length;
                TestLogBox.ScrollToEnd();
            }).ConfigureAwait(false);

            for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                var model = models[modelIndex];
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await UiDispatcher.InvokeAsync(() =>
                {
                    ResetTestProgressUi(model, modelIndex, models.Count, benchmarkModes, aiSummarizer);
                    TestStatusLabel.Text = $"Starting benchmark for {model} ({modelIndex + 1}/{models.Count})…";
                }).ConfigureAwait(false);

                var log = new Progress<string>(line =>
                {
                    _testLogUpdater.Append(line + Environment.NewLine, appended =>
                    {
                        TestLogBox.Text += appended;
                        TestLogBox.CaretIndex = TestLogBox.Text.Length;
                        TestLogBox.ScrollToEnd();
                    }, () => TestLogBox.Text, v => TestLogBox.Text = v);
                });

                var progress = new Progress<BenchmarkProgressUpdate>(update =>
                {
                    UiDispatcher.InvokeAsync(() => ApplyBenchmarkProgressUpdate(update));
                });

                try
                {
                    await _svc.BenchmarkRunner.RunAsync(
                        model,
                        numPredict: numPredict,
                        numCtx: numCtx,
                        modelIndex: modelIndex,
                        modelCount: models.Count,
                        aiSummarizerModel: aiSummarizer,
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

                    await UiDispatcher.InvokeAsync(async () =>
                    {
                        await RefreshModelsUiAsync().ConfigureAwait(true);
                        await RefreshTestResultsUiAsync().ConfigureAwait(true);
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        TestLogBox.Text += $"FAIL ({model}): {ex.Message}{Environment.NewLine}";
                        TestStatusLabel.Text = $"Benchmark failed for {model}: {ex.Message}";
                    }).ConfigureAwait(false);
                }
            }

            await UiDispatcher.InvokeAsync(() =>
            {
                TestOverallProgress.Value = 100;
                TestStatusLabel.Text = ct.IsCancellationRequested
                    ? "Benchmark queue stopped."
                    : "Benchmark queue complete.";
            }).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private void StopTest_Click(object sender, RoutedEventArgs e) => _benchmarkCts?.Cancel();

    private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync().ConfigureAwait(true);

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SendChatAsync().ConfigureAwait(true);
        }
    }

    private async Task SendChatAsync()
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() => ChatHistory.Text += msg).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private void StopChat_Click(object sender, RoutedEventArgs e) => _chatCts?.Cancel();

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
        if (MainTabs.SelectedItem == ModelLibraryTab)
        {
            await LoadCatalogTabAsync().ConfigureAwait(true);
        }
        else if (MainTabs.SelectedItem == TestResultsTab)
        {
            await RefreshTestResultsUiAsync().ConfigureAwait(true);
        }
        else if (MainTabs.SelectedItem == AiSettingsTab)
        {
            await RefreshAiSettingsUiAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadCatalogTabAsync()
    {
        CatalogStatusLabel.Text = "Loading catalog...";
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            if (await _svc.CatalogStore.IsStaleAsync(ct).ConfigureAwait(false))
            {
                await _svc.CatalogStore.RefreshFromWebAsync(cancellationToken: ct).ConfigureAwait(false);
            }

            await UiDispatcher.InvokeAsync(async () => await RefreshCatalogUiAsync().ConfigureAwait(true))
                .ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async Task RefreshCatalogUiAsync()
    {
        var search = CatalogSearchBox.Text;
        var category = CategoryFilterCombo.SelectedItem as string;
        var rows = (await _svc.Registry.GetCatalogRowsAsync(search, category).ConfigureAwait(true)).ToList();
        if (NaturalLanguageSearchService.LooksNaturalLanguage(search) && rows.Count > 1)
        {
            var candidates = rows.Select(r => new NlSearchCandidate(r.Name, r.Category, r.ParameterSize, r.ListDescription))
                .ToList();
            var ranked = await _svc.NlSearch.RankModelsAsync(search, candidates).ConfigureAwait(true);
            if (ranked.Count > 0)
            {
                _nlRankedCatalog = ranked.ToList();
                var rankMap = ranked.Select((name, i) => (name, i))
                    .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
                rows = rows.OrderBy(r => rankMap.TryGetValue(r.Name, out var i) ? i : 999).ThenBy(r => r.Name).ToList();
                CatalogStatusLabel.Text = $"NL-ranked {ranked.Count} model(s); showing {rows.Count}.";
                CatalogGrid.ItemsSource = rows;
                return;
            }
        }

        CatalogGrid.ItemsSource = rows;
        CatalogStatusLabel.Text = $"Showing {rows.Count} catalog model(s).";
    }

    private void CatalogSearchBox_KeyUp(object sender, KeyEventArgs e)
    {
        _catalogSearchTimer.Stop();
        _catalogSearchTimer.Start();
    }

    private async void CategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RefreshCatalogUiAsync().ConfigureAwait(true);

    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e)
    {
        CatalogStatusLabel.Text = "Refreshing from ollama.com...";
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            await _svc.CatalogStore.RefreshFromWebAsync(cancellationToken: ct).ConfigureAwait(false);
            _svc.CatalogStore.ClearCache();
            await UiDispatcher.InvokeAsync(async () => await RefreshCatalogUiAsync().ConfigureAwait(true))
                .ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async void CategorizeAll_Click(object sender, RoutedEventArgs e) =>
        await RunClassificationAsync(recategorize: false).ConfigureAwait(true);

    private async void RecategorizeAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reclassify the entire catalog?", "Recategorize All",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunClassificationAsync(recategorize: true).ConfigureAwait(true);
    }

    private async Task RunClassificationAsync(bool recategorize)
    {
        CatalogStatusLabel.Text = "Classifying catalog models...";
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            var progress = new Progress<string>(msg =>
            {
                UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg);
            });
            var count = await _svc.Classification.ClassifyAllAsync(recategorize, progress, ct)
                .ConfigureAwait(false);
            _svc.ActivityLog.Write("AI", $"Classified {count} catalog model(s).");
            await UiDispatcher.InvokeAsync(async () =>
            {
                await RefreshCatalogUiAsync().ConfigureAwait(true);
                await RefreshCategoryStatusAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async void DownloadCatalogModel_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRowViewModel row)
        {
            MessageBox.Show("Select a catalog model first.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var model = $"{row.Name}:latest";
        CatalogStatusLabel.Text = $"Pulling {model}...";
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                var progress = new Progress<string>(s => _svc.ActivityLog.Write("Download", s));
                await _svc.ApiClient.PullAsync(model, progress, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Task", $"Downloaded {model}.");
                _svc.Profiles.ClearCache();
                await UiDispatcher.InvokeAsync(async () =>
                {
                    CatalogStatusLabel.Text = $"Downloaded {model}.";
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task<string> FormatInsightColumnAsync(string? insight)
    {
        if (!string.IsNullOrWhiteSpace(insight))
        {
            return insight.Length > 60 ? insight[..57] + "..." : insight;
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
            var shortInsight = await FormatInsightColumnAsync(insight).ConfigureAwait(true);
            enriched.Add(new TestResultRowViewModel
            {
                Model = row.Model,
                Category = row.Category,
                BestMode = row.BestMode,
                BestTps = row.BestTps,
                CpuResult = row.CpuResult,
                ApuResult = row.ApuResult,
                GpuResult = row.GpuResult,
                HybridResult = row.HybridResult,
                RocmResult = row.RocmResult,
                Insight = shortInsight,
                LastTested = row.LastTested,
                ReportPath = row.ReportPath
            });
        }

        TestResultsGrid.ItemsSource = enriched;
    }

    private async void RefreshTestResults_Click(object sender, RoutedEventArgs e) =>
        await RefreshTestResultsUiAsync().ConfigureAwait(true);

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

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var match = summaries.FirstOrDefault(s => s.Model.Equals(row.Model, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        ModelsGrid.ItemsSource = summaries;
        ModelsGrid.SelectedItem = match;
        await LaunchBestMode_Click_Internal(match).ConfigureAwait(true);
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
        ModelRunStatus.Text = $"Applying best mode {model.BestMode} for {model.Model}...";

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeAsync(mode, restartOllama: true, cancellationToken: ct)
                    .ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ModelRunStatus.Text = $"{model.Model} | Mode: {model.BestMode} ({model.BestTps:F1} tok/s) | Ready";
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
        if (SummarizerCombo.SelectedItem is not string model)
        {
            return;
        }

        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        settings.PreferredSummarizerModel = model;
        await _svc.AiSettings.SaveAsync(settings).ConfigureAwait(true);
        await UpdateAiStatusAsync().ConfigureAwait(true);
    }

    private async void RefreshSummarizerList_Click(object sender, RoutedEventArgs e) =>
        await RefreshAiSettingsUiAsync().ConfigureAwait(true);

    private async void DownloadSummarizer_Click(object sender, RoutedEventArgs e)
    {
        var model = SummarizerCombo.SelectedItem as string
            ?? SuggestedModelsList.SelectedItem as string
            ?? SummarizerModelResolver.DefaultPreferenceOrder.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                var progress = new Progress<string>(s => _svc.ActivityLog.Write("Download", s));
                await _svc.ApiClient.PullAsync(model, progress, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Task", $"Downloaded summarizer {model}.");
                await UiDispatcher.InvokeAsync(async () => await RefreshAiSettingsUiAsync().ConfigureAwait(true))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
            }
        }).ConfigureAwait(true);
    }

    private async void TestSummarizer_Click(object sender, RoutedEventArgs e)
    {
        var model = SummarizerCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model))
        {
            SummarizerTestResult.Text = "Select a summarizer model first.";
            return;
        }

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                var result = await _svc.ApiClient.GenerateAsync(
                    model,
                    "Summarize in one short phrase: Llama is a family of open large language models.",
                    32, 2048, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                    SummarizerTestResult.Text = $"Test OK: {result.Trim()}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() => SummarizerTestResult.Text = msg).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void ScanLogs_Click(object sender, RoutedEventArgs e)
    {
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            var doc = await _svc.LogAnomalies.ScanAsync(ct).ConfigureAwait(false);
            await UiDispatcher.InvokeAsync(() =>
            {
                AnomalySummary.Text = doc.Anomalies.Count == 0
                    ? "No anomalies detected."
                    : string.Join(" | ", doc.Anomalies.Select(a => $"{a.Pattern} ({a.Count}): {a.Summary}"));
                RefreshActivityLog();
            }).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async void AskAiModels_Click(object sender, RoutedEventArgs e) =>
        await RunAskAiAsync(fromModelRun: false).ConfigureAwait(true);

    private async void AskAiRun_Click(object sender, RoutedEventArgs e) =>
        await RunAskAiAsync(fromModelRun: true).ConfigureAwait(true);

    private async Task RunAskAiAsync(bool fromModelRun)
    {
        var intent = PromptForIntent("What do you want to do?");
        if (string.IsNullOrWhiteSpace(intent))
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
            }).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async void CompareModels_Click(object sender, RoutedEventArgs e)
    {
        var selected = ModelsGrid.SelectedItems.Cast<ModelProfileSummary>().Take(2).ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show("Select exactly two models (Ctrl+click).", "Compare", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunCompareAsync(selected[0], selected[1]).ConfigureAwait(true);
    }

    private async void CompareCatalog_Click(object sender, RoutedEventArgs e)
    {
        var selected = CatalogGrid.SelectedItems.Cast<CatalogRowViewModel>().Take(2).ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show("Select exactly two catalog models (Ctrl+click).", "Compare", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var a = summaries.FirstOrDefault(s =>
            s.Model.StartsWith($"{selected[0].Name}:", StringComparison.OrdinalIgnoreCase)
            || s.Model.Equals(selected[0].Name, StringComparison.OrdinalIgnoreCase));
        var b = summaries.FirstOrDefault(s =>
            s.Model.StartsWith($"{selected[1].Name}:", StringComparison.OrdinalIgnoreCase)
            || s.Model.Equals(selected[1].Name, StringComparison.OrdinalIgnoreCase));
        await RunCompareAsync(
            a ?? new ModelProfileSummary { Model = selected[0].Name, Category = selected[0].Category },
            b ?? new ModelProfileSummary { Model = selected[1].Name, Category = selected[1].Category },
            selected[0].Category,
            selected[1].Category).ConfigureAwait(true);
    }

    private async Task RunCompareAsync(
        ModelProfileSummary modelA,
        ModelProfileSummary modelB,
        string? categoryA = null,
        string? categoryB = null)
    {
        categoryA ??= modelA.Category;
        categoryB ??= modelB.Category;
        AiFlyoutTitle.Text = $"{modelA.Model} vs {modelB.Model}";
        AiFlyoutBody.Text = "Comparing...";
        AiFlyoutPopup.IsOpen = true;

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            var text = await _svc.ModelComparison.CompareAsync(
                modelA.Model, modelB.Model, modelA, modelB, categoryA, categoryB, ct).ConfigureAwait(false);
            _svc.ActivityLog.Write("AI", $"Compared {modelA.Model} vs {modelB.Model}");
            await UiDispatcher.InvokeAsync(() => AiFlyoutBody.Text = text).ConfigureAwait(false);
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
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
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