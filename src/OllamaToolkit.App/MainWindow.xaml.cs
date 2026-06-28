using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;

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
    private CancellationTokenSource? _testCts;
    private CancellationTokenSource? _undownloadCts;
    private int _progressSession;
    private volatile bool _acceptProgressUpdates;
    private volatile bool _benchmarkQueueRunning;
    private int _testOperations;
    private Task? _activeTestWork;
    private readonly List<Button> _activeTestFlashButtons = new();
    private string? _runModel;
    private readonly List<ChatMessage> _chatMessages = new();
    private readonly DispatcherTimer _activityTimer;
    private readonly DispatcherTimer _catalogSearchTimer;
    private readonly DispatcherTimer _testResultsSearchTimer;
    private readonly DispatcherTimer _testSettingsTimer;
    private readonly ThrottledUpdater _chatUpdater;
    private readonly StringBuilder _testLogBuilder = new();
    private bool _testLogStickToBottom = true;
    private bool _testLogAutoScrolling;
    private const int ActivityTimerIntervalMs = 4000;
    private const int ActivityTimerActiveTestMs = 1000;
    private static readonly string[] BenchmarkModeOrder = ["CPU", "APU", "GPU", "Hybrid"];
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Status)> _testModeProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly SmoothProgressPresenter _testProgressAnimator;
    private DispatcherTimer? _testSpinUpTimer;
    private double _testSpinUpProgress;
    private List<string> _nlRankedCatalog = new();
    private bool _suppressSummarizerComboSave;
    private bool _summarizerDropdownOpen;
    private readonly SemaphoreSlim _aiSettingsRefreshGate = new(1, 1);
    private int _aiSettingsRefreshGeneration;
    private CatalogDescriptionDisplayMode _catalogDescriptionMode = CatalogDescriptionDisplayMode.Download;
    private readonly FlashButtonRegistry _flashButtons;
    private readonly AiProcessingFlashPresenter _aiProcessingFlash;
    private readonly OllamaAiFlashPresenter _ollamaAiFlash;
    private readonly StartupSplashPresenter _startupSplash;
    private readonly ObservableCollection<CatalogRowViewModel> _catalogRows = new();
    private readonly CatalogRowRefreshAnimator _catalogRowAnimator;
    private readonly TestingRowHighlightCoordinator _testingHighlight;
    private Dictionary<string, CatalogRowViewModel> _catalogRowByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressCatalogUiEvents;
    private bool _suppressTestResultsSelectionEvents;
    private bool _suppressTestResultsUiEvents;
    private CancellationTokenSource? _testResultsDetailCts;
    private List<TestResultRowViewModel> _testResultsAllRows = new();
    private bool _testResultsDownloadInProgress;
    private bool _catalogDownloadInProgress;
    private bool _catalogFileSizesInProgress;
    private CancellationTokenSource? _catalogFileSizesCts;
    private CancellationTokenSource? _catalogDownloadCts;
    private readonly CatalogOperationCoordinator _catalogOps = new();
    private int _catalogBulkUiCounter;
    private CancellationTokenSource? _aiRecommendationsCts;
    private object? _generateRecommendationsFlashSender;
    private int _catalogToolbarOperations;
    private bool _suppressReportImport;

    public MainWindow()
    {
        InitializeComponent();
        _startupSplash = new StartupSplashPresenter(StartupSplashOverlay, MainContentRoot, SplashRevisionText);
        _flashButtons = new FlashButtonRegistry(this, _svc.FlashClock);
        _aiProcessingFlash = new AiProcessingFlashPresenter(AiStatusButton, this, _svc.FlashClock);
        _ollamaAiFlash = new OllamaAiFlashPresenter(OllamaAiStatusButton, this, _svc.FlashClock);
        WireAiActivityPresenters();
        _catalogRowAnimator = new CatalogRowRefreshAnimator(_svc.FlashClock);
        _testingHighlight = new TestingRowHighlightCoordinator(_svc.FlashClock);
        _testingHighlight.Bind(
            () => _catalogRows,
            () => ModelsGrid.ItemsSource as IEnumerable<ModelLaunchRowViewModel> ?? []);
        CatalogGrid.ItemsSource = _catalogRows;
        _modeCardPresenter = new ModeCardPresenter(this, _svc.FlashClock);
        _modeCards["CPU"] = CpuCard;
        _modeCards["APU"] = ApuCard;
        _modeCards["GPU"] = GpuCard;
        _modeCards["Hybrid"] = HybridCard;

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _activityTimer.Tick += (_, _) =>
        {
            RefreshActivityLog();
            RefreshDiagnosticsLog();
            _ = SyncComputeModeDisplayAsync();
        };
        _catalogSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _catalogSearchTimer.Tick += async (_, _) =>
        {
            _catalogSearchTimer.Stop();
            try
            {
                if (_catalogDownloadInProgress)
                {
                    ApplyCatalogFilterInMemory();
                }
                else if (!_catalogOps.IsToolbarBusy)
                {
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _svc.Diagnostics.Write("Catalog", $"Search refresh failed: {ex.Message}");
                CatalogStatusLabel.Text = ex.Message;
            }
        };
        _testResultsSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _testResultsSearchTimer.Tick += async (_, _) =>
        {
            _testResultsSearchTimer.Stop();
            try
            {
                ApplyTestResultsFilter();
            }
            catch (Exception ex)
            {
                _svc.Diagnostics.Write("TestResults", $"Search refresh failed: {ex.Message}");
            }

            await Task.CompletedTask.ConfigureAwait(true);
        };
        _testSettingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _testSettingsTimer.Tick += async (_, _) =>
        {
            _testSettingsTimer.Stop();
            await SuggestBenchmarkSettingsAsync().ConfigureAwait(true);
        };
        _chatUpdater = new ThrottledUpdater(TimeSpan.FromMilliseconds(33), Dispatcher);
        _testProgressAnimator = new SmoothProgressPresenter(Dispatcher);
        TestLogText.SelectionChanged += (_, _) =>
        {
            if (_testLogAutoScrolling || _testOperations > 0)
            {
                return;
            }

            _testLogStickToBottom = TestLogText.CaretIndex >= Math.Max(0, TestLogText.Text.Length - 2);
        };
        DataGridColumnHelper.AttachAutoFit(ModelsGrid, FitGridColumns);
        DataGridColumnHelper.AttachAutoFit(CatalogGrid, FitGridColumns);
        DataGridColumnHelper.AttachAutoFit(TestResultsGrid, FitGridColumns);
        SummarizerCombo.DropDownOpened += (_, _) => _summarizerDropdownOpen = true;
        SummarizerCombo.DropDownClosed += (_, _) => _summarizerDropdownOpen = false;
        Loaded += OnLoadedAsync;
        Closed += (_, _) =>
        {
            _activityTimer.Stop();
            _catalogSearchTimer.Stop();
            _testResultsSearchTimer.Stop();
            _testSettingsTimer.Stop();
            _modeCardPresenter.Stop();
            _flashButtons.StopAll();
            _aiProcessingFlash.Stop();
            _ollamaAiFlash.Stop();
            _catalogRowAnimator.Stop();
            _testingHighlight.Dispose();
            StopTestSpinUpCreep();
            _testProgressAnimator.Dispose();
        };
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await _startupSplash.ShowAndFadeInAsync().ConfigureAwait(true);
            _svc.Diagnostics.Write("App", "Startup splash visible");

            _svc.Diagnostics.Write("App", "MainWindow loaded");
            LogBuildStamp();
            ApplyStatusButtonPresentations();
            UpdateFooterVersionLabel();
            ApplyCatalogDescriptionModeUi();
            UpdateCatalogStopButtonUi();
            UpdateCatalogDownloadButtonUi();
            UpdateStopTestButtonUi();
            InitModeCards();
            InitAiFeatureToggles();
            InitCategoryFilter();
            InitTestResultsCategoryFilter();
            BindSummarizerComboImmediate();

            var loadTask = RefreshAllAsync();
            var loadTimeout = Task.Delay(TimeSpan.FromSeconds(90));
            var completed = await Task.WhenAny(loadTask, loadTimeout).ConfigureAwait(true);
            if (completed == loadTimeout)
            {
                _svc.Diagnostics.Write("App", "Startup load exceeded 90s; dismissing splash");
            }
            else
            {
                await loadTask.ConfigureAwait(true);
            }
        }
        finally
        {
            await _startupSplash.HideAndFadeOutAsync().ConfigureAwait(true);
        }

        _activityTimer.Start();
        RefreshDiagnosticsLog();
        _ = ScheduleLogScanAsync();
    }

    private void InitCategoryFilter()
    {
        CategoryFilterCombo.ItemsSource = new[] { "All" }.Concat(CategoryNormalizer.AllCategories).ToList();
        CategoryFilterCombo.SelectedIndex = 0;
    }

    private void InitTestResultsCategoryFilter()
    {
        TestResultsCategoryFilterCombo.ItemsSource = new List<string> { "All" };
        TestResultsCategoryFilterCombo.SelectedIndex = 0;
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

    private bool TryBeginTaskFlashWithFeedback(object sender, Action<string> setStatus)
    {
        if (BeginTaskFlash(sender))
        {
            return true;
        }

        setStatus("Wait for the current operation to finish.");
        return false;
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

    private bool IsBenchmarkQueueRunning() =>
        _benchmarkQueueRunning || _activeTestWork is { IsCompleted: false };

    private bool IsCatalogDownloadActive => _catalogDownloadInProgress;

    private bool TryBlockDownloadIfTestActive()
    {
        if (!IsBenchmarkQueueRunning())
        {
            return true;
        }

        MessageBox.Show(
            "A benchmark test is running. Click Stop Test before downloading models.",
            "Download",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private bool TryBlockWorkloadIfDownloadActive(string operationName)
    {
        if (!IsCatalogDownloadActive)
        {
            return true;
        }

        var message = $"A Model Library download is in progress. Click Stop Download or wait for it to finish before {operationName}.";
        MessageBox.Show(message, operationName, MessageBoxButton.OK, MessageBoxImage.Information);
        CatalogStatusLabel.Text = message;
        return false;
    }

    private bool BeginTestOperation(object? sender, FlashColorScheme scheme = FlashColorScheme.YellowBlack)
    {
        if (IsCatalogDownloadActive)
        {
            if (TaskButton(sender) is not null)
            {
                TestStatusLabel.Text = "Model Library download in progress — wait or click Stop Download.";
            }

            return false;
        }

        if (IsBenchmarkQueueRunning())
        {
            return false;
        }

        _testOperations++;
        _testLogStickToBottom = true;
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

        if (_testOperations == 0)
        {
            _testProgressAnimator.SetActive(false);
            _testProgressAnimator.SetActiveModeBar(null);
            _testCts?.Dispose();
            _testCts = null;
        }
    }

    private void BeginTestProgressSession()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();
        _progressSession++;
        _acceptProgressUpdates = true;
        _testProgressAnimator.BeginSession();
        _testProgressAnimator.SetOverallBar(TestOverallProgress);
        _testProgressAnimator.SetActive(true);
    }

    private void HaltTestProgressAnimation()
    {
        StopTestSpinUpCreep();
        _progressSession++;
        _acceptProgressUpdates = false;
        _testProgressAnimator.Halt();
    }

    private void ApplyTestStoppedProgressUi()
    {
        StopTestSpinUpCreep();
        _progressSession++;
        _acceptProgressUpdates = false;

        var warningBrush = (Brush)FindResource("Brush.Warning");
        TestOverallProgress.IsIndeterminate = false;
        _testProgressAnimator.SetImmediate(TestOverallProgress, 100);
        TestOverallProgress.Foreground = warningBrush;
        TestOverallLabel.Text = "Overall: Stopped";

        foreach (var (_, row) in _testModeProgress)
        {
            row.Bar.IsIndeterminate = false;
            _testProgressAnimator.SetImmediate(row.Bar, 100);
            row.Bar.Foreground = warningBrush;
            row.Status.Text = "Test Stopped";
            row.Status.Foreground = warningBrush;
        }

        TestStatusLabel.Text = "Test Stopped";
        _testProgressAnimator.Halt();
    }

    private void UpdateStopTestButtonUi()
    {
        _activityTimer.Interval = _testOperations > 0
            ? TimeSpan.FromMilliseconds(ActivityTimerActiveTestMs)
            : TimeSpan.FromMilliseconds(ActivityTimerIntervalMs);

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

    private void ResetTestOperationState()
    {
        HaltTestProgressAnimation();

        foreach (var button in _activeTestFlashButtons.ToList())
        {
            _flashButtons.EndIdle(button);
        }

        _activeTestFlashButtons.Clear();
        _testOperations = 0;
        _benchmarkQueueRunning = false;
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;
        UpdateStopTestButtonUi();
        ClearCatalogTestingHighlights();
    }

    private async Task CancelTestOperationsAsync()
    {
        _testCts?.Cancel();
        _benchmarkCts?.Cancel();
        _undownloadCts?.Cancel();

        UiDispatcher.Invoke(ApplyTestStoppedProgressUi);

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

        _benchmarkQueueRunning = false;
        _svc.ApiClient.InvalidateCaches();
        _svc.Diagnostics.Write("Testing", "Stop Test clicked — cancelling benchmark queue");

        await UiDispatcher.InvokeAsync(ResetTestOperationState).ConfigureAwait(true);
    }

    private void SetCatalogTestingHighlight(string modelOrLibrary, bool on)
    {
        if (on)
        {
            _testingHighlight.SetExclusiveActive(modelOrLibrary);
        }
        else
        {
            _testingHighlight.SetActive(modelOrLibrary, on: false);
        }
    }

    private void ClearCatalogTestingHighlights()
    {
        _testingHighlight.ClearAll();
        foreach (var row in _catalogRows)
        {
            if (row.IsDownloading)
            {
                row.IsDownloading = false;
            }
        }
    }

    private void AppendTestLog(string line)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => AppendTestLog(line));
                return;
            }

            if (string.IsNullOrEmpty(line))
            {
                _testLogBuilder.AppendLine();
            }
            else
            {
                _testLogBuilder.AppendLine(line);
            }

            _testLogAutoScrolling = true;
            TestLogText.Text = _testLogBuilder.ToString();

            if (_testOperations > 0 || _testLogStickToBottom)
            {
                LogScrollHelper.ScrollTextBoxToEndDeferred(TestLogText, Dispatcher);
            }

            _testLogAutoScrolling = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppendTestLog failed: {ex.Message}");
        }
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
        var checkBoxStyle = (Style)FindResource("ToolkitAiFeatureCheckBox");
        var muted = (Brush)FindResource("Brush.Muted");

        var master = new CheckBox
        {
            Content = BuildFeatureContent(
                "Enable toolkit AI (master)",
                "Master switch for all Program AI features below. When off, features fall back to non-AI behavior instantly.",
                muted),
            IsChecked = settings.ToolkitAiEnabled,
            Style = checkBoxStyle,
            Margin = new Thickness(0, 0, 0, 12)
        };
        master.Checked += async (_, _) => await SaveMasterAiAsync(true).ConfigureAwait(true);
        master.Unchecked += async (_, _) => await SaveMasterAiAsync(false).ConfigureAwait(true);
        AiFeaturesPanel.Children.Add(master);

        foreach (var (key, label, description) in GetFeatureDefinitions())
        {
            var cb = new CheckBox
            {
                Content = BuildFeatureContent(label, description, muted),
                Tag = key,
                IsChecked = settings.FeatureFlags.GetValueOrDefault(key, true),
                Style = checkBoxStyle
            };
            cb.Checked += async (_, _) => await SaveFeatureFlagAsync(key, true).ConfigureAwait(true);
            cb.Unchecked += async (_, _) => await SaveFeatureFlagAsync(key, false).ConfigureAwait(true);
            AiFeaturesPanel.Children.Add(cb);
        }
    }

    private static StackPanel BuildFeatureContent(string label, string description, Brush mutedForeground) =>
        new()
        {
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.FindResource("Brush.Text")
                },
                new TextBlock
                {
                    Text = description,
                    Foreground = mutedForeground,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                }
            }
        };

    private static IEnumerable<(string Key, string Label, string Description)> GetFeatureDefinitions() =>
    [
        ("DescriptionSummarization", "1. Description summarization",
            "Condenses long model pages from ollama.com into short catalog descriptions during Refresh Descriptions."),
        ("CatalogCategorization", "2. Catalog categorization",
            "Assigns usage categories (chat, code, embedding, etc.) to library models via Categorize All / Recategorize All."),
        ("BenchmarkInterpreter", "3. Benchmark result interpreter",
            "Writes a plain-language summary of each model's best mode and throughput in Test Results."),
        ("FailureDiagnosis", "4. Failure diagnosis",
            "Explains why specific compute modes failed after a benchmark run."),
        ("NaturalLanguageSearch", "5. Natural-language model search",
            "Ranks catalog models by intent when you type a natural-language query in the Model Library search box."),
        ("ModelPickerAdvisor", "6. Model picker advisor",
            "Ranks installed/uninstalled LLMs on AI Settings and Ask AI on Models & Launch."),
        ("OptimalBenchmarkSettings", "7. Optimal benchmark settings",
            "Suggests num_ctx, num_predict, and per-mode OLLAMA_NUM_PARALLEL before each test run."),
        ("TestQueuePrioritization", "8. Test untested prioritization",
            "Orders the Test Local Untested queue by model size and profile state using AI."),
        ("ModelComparison", "9. Model comparison blurb",
            "Generates comparison text for Compare with AI (installed or catalog models)."),
        ("PlainLanguageErrors", "10. Plain-language errors",
            "Rewrites technical error messages into readable explanations across the app."),
        ("LogAnomalyDetection", "11. Log anomaly detection",
            "Scans the AI activity log on startup and via Scan Logs for recurring error patterns.")
    ];

    private async Task SaveMasterAiAsync(bool enabled)
    {
        var s = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        s.ToolkitAiEnabled = enabled;
        await _svc.AiSettings.SaveAsync(s).ConfigureAwait(true);
        AiFeatureToggleStatus.Text = enabled
            ? "Toolkit AI enabled — saved instantly."
            : "Toolkit AI disabled — saved instantly.";
        await UpdateAiStatusAsync().ConfigureAwait(true);
    }

    private async Task SaveFeatureFlagAsync(string key, bool enabled)
    {
        var s = await _svc.AiSettings.LoadAsync().ConfigureAwait(true);
        s.FeatureFlags[key] = enabled;
        await _svc.AiSettings.SaveAsync(s).ConfigureAwait(true);
        var label = GetFeatureDefinitions().FirstOrDefault(f => f.Key == key).Label ?? key;
        AiFeatureToggleStatus.Text = enabled
            ? $"{label} enabled — saved instantly."
            : $"{label} disabled — saved instantly.";
    }

    private async Task RefreshAllAsync()
    {
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                ImportReportsResult imported;
                if (_suppressReportImport)
                {
                    imported = new ImportReportsResult();
                    _svc.Diagnostics.Write("Import", "Startup import skipped (test data was just cleared)");
                }
                else
                {
                    imported = await _svc.ReportImporter.ImportReportsAsync(cancellationToken: ct).ConfigureAwait(false);
                }

                if (imported.Count > 0)
                {
                    _svc.Profiles.ClearCache();
                    _svc.ActivityLog.Write("Task", $"Imported {imported.Count} benchmark report(s).");
                    _svc.Diagnostics.Write("Import",
                        $"Startup import: {imported.Count} model(s): {string.Join(", ", imported.ModelNames)}");
                }
                else
                {
                    _svc.Diagnostics.Write("Import", "Startup import: no reports found");
                }

                if (_svc.ModeService.NeedsStaleEnvMigration())
                {
                    _svc.Diagnostics.Write("Modes", "Stale non-Vulkan env detected — migrating to GPU mode");
                    await _svc.ModeService.MigrateStaleEnvToGpuAsync(restartOllama: true, cancellationToken: ct)
                        .ConfigureAwait(false);
                    _svc.ApiClient.InvalidateCaches();
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
                _svc.Diagnostics.Write("App", $"Startup refresh failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                    AlertText.Text = $"Data load error: {ex.Message}").ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task RefreshModesUiAsync()
    {
        await SyncComputeModeDisplayAsync().ConfigureAwait(true);
    }

    private async Task SyncComputeModeDisplayAsync()
    {
        var apiReady = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        if (apiReady)
        {
            _svc.ModeService.InitializeRunningModeFromConfigured();
        }

        var running = _svc.ModeService.GetRunningModeLabel(apiReady);
        var configured = _svc.ModeService.DetectConfiguredMode();
        var runningLabel = _svc.ModeDefinitions.TryParse(running, out var runningMode)
            ? _svc.ModeDefinitions.Get(runningMode).ShortLabel
            : running;
        var pending = _svc.ModeService.HasPendingModeChange(apiReady);
        var configuredLabel = _svc.ModeDefinitions.TryParse(configured, out var configuredMode)
            ? _svc.ModeDefinitions.Get(configuredMode).ShortLabel
            : configured;
        var modeStatusText = pending
            ? apiReady
                ? $"Running: {runningLabel} — saved {configuredLabel} (restart Ollama to apply)"
                : $"Saved: {configuredLabel} — Ollama API not reachable"
            : apiReady
                ? $"Running: {runningLabel} — Ollama is running and ready"
                : $"Saved: {runningLabel} — Ollama API not reachable (start Ollama if needed)";
        var footerText = pending
            ? $"Mode: {runningLabel} (running)"
            : $"Mode: {runningLabel}";
        var snapshot = _svc.EnvBackup.ReadUserSnapshot();
        var envText = ModeEnvSummaryBuilder.Build(
            configured,
            snapshot,
            _svc.ModeDefinitions.DeviceMap,
            _svc.ModeDefinitions);

        await UiDispatcher.InvokeAsync(() =>
        {
            ModeStatusLabel.Text = modeStatusText;
            _modeCardPresenter.ApplyActiveMode(running);
            EnvBox.Text = envText;
            ComputeModeFooterText.Text = footerText;
        }).ConfigureAwait(true);
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
        await UiDispatcher.InvokeAsync(async () =>
        {
            await RefreshModelsUiCoreAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task RefreshModelsUiCoreAsync()
    {
        var selectedModel = ModelsGrid.SelectedItem is ModelLaunchRowViewModel selected
            ? selected.Model
            : null;
        var testComboSelection = TestModelCombo.SelectedItem as string;

        var summaries = await _svc.Profiles.GetInstalledSummariesAsync().ConfigureAwait(true);
        var categories = await GetCategoryMapAsync().ConfigureAwait(true);
        var catalogEntries = (await _svc.CatalogStore.GetEntriesAsync().ConfigureAwait(true))
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var descriptionDoc = await _svc.Descriptions.LoadAsync().ConfigureAwait(true);
        var enriched = summaries.Select(s =>
        {
            var lib = s.Model.Split(':')[0];
            var category = categories.TryGetValue(lib, out var c)
                ? c
                : CategoryNormalizer.HeuristicCategory(lib, string.Empty, string.Empty);
            return ModelLaunchRowViewModel.WithCategory(s, category);
        }).ToList();
        var rows = enriched.Select(s =>
        {
            var lib = s.Model.Split(':')[0];
            var displayDescription = ResolveLaunchDisplayDescription(lib, catalogEntries, descriptionDoc);
            return ModelLaunchRowViewModel.FromSummary(s, displayDescription);
        }).ToList();
        ModelsGrid.ItemsSource = rows;
        TestModelCombo.ItemsSource = enriched.Select(s => s.Model).ToList();
        _testingHighlight.ReapplyActive();

        if (!string.IsNullOrEmpty(selectedModel))
        {
            var match = rows.FirstOrDefault(e =>
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

    private void ApplyStatusButtonPresentations()
    {
        _aiProcessingFlash.SetPresentation(
            "Program AI Inactive",
            "Program AI Active",
            (Brush)FindResource("Brush.Button"),
            (Brush)FindResource("Brush.PanelBorder"),
            (Brush)FindResource("Brush.Text"));

        _ollamaAiFlash.SetPresentation(
            "Ollama AI Inactive",
            "Ollama Active",
            (Brush)FindResource("Brush.Button"),
            (Brush)FindResource("Brush.PanelBorder"),
            (Brush)FindResource("Brush.Text"));
    }

    private void LogBuildStamp() =>
        _svc.Diagnostics.Write("App", AppBuildInfo.GetDiagnosticStamp());

    private void UpdateFooterVersionLabel() =>
        AppVersionFooterText.Text = AppBuildInfo.GetShortFooterLabel();

    private void UpdateFooterComputeModeLabel() => _ = SyncComputeModeDisplayAsync();

    private async Task UpdateAiStatusAsync()
    {
        ApplyStatusButtonPresentations();
        await UpdateAiButtonStatesAsync().ConfigureAwait(true);
    }

    private void WireAiActivityPresenters()
    {
        _svc.ActivityHub.ToolkitActivityChanged += active =>
        {
            if (CheckAccess())
            {
                _aiProcessingFlash.SetActive(active);
            }
            else
            {
                Dispatcher.Invoke(() => _aiProcessingFlash.SetActive(active));
            }
        };

        _svc.ActivityHub.OllamaActivityChanged += active =>
        {
            if (CheckAccess())
            {
                _ollamaAiFlash.SetActive(active);
            }
            else
            {
                Dispatcher.Invoke(() => _ollamaAiFlash.SetActive(active));
            }
        };
    }

    private void EnterAiActivity() => _svc.ActivityHub.EnterToolkitAi();

    private void ExitAiActivity()
    {
        _svc.ActivityHub.ExitToolkitAi();
        if (!_svc.ActivityHub.IsToolkitActive)
        {
            _ = UpdateAiStatusAsync();
        }
    }

    private static bool ShouldShowRawErrorMessage(string message) =>
        message.Contains("different thread owns it", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Sequence contains no matching element", StringComparison.OrdinalIgnoreCase)
        || message.Contains("file does not exist", StringComparison.OrdinalIgnoreCase)
        || message.Contains("pull model manifest", StringComparison.OrdinalIgnoreCase);

    private async Task<string> ExplainErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        if (ShouldShowRawErrorMessage(message))
        {
            return message;
        }

        if (!await _svc.AiSettings.IsFeatureEnabledAsync(AiFeatureKeys.PlainLanguageErrors, cancellationToken)
                .ConfigureAwait(false))
        {
            return message;
        }

        using var scope = new ToolkitAiActivityScope(_svc.ActivityHub);
        return await _svc.PlainErrors.ExplainAsync(message, cancellationToken).ConfigureAwait(false);
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

    private Brush ThemeBrush(string key, Brush fallback) =>
        TryFindResource(key) is Brush brush ? brush : fallback;

    private void UpdateCatalogStopButtonUi()
    {
        if (IsCatalogStopActive)
        {
            var accent = ThemeBrush("Brush.Accent", Brushes.Red);
            CatalogStopBtn.Background = accent;
            CatalogStopBtn.BorderBrush = accent;
            CatalogStopBtn.Foreground = Brushes.White;
        }
        else
        {
            CatalogStopBtn.Background = ThemeBrush("Brush.Button", Brushes.Black);
            CatalogStopBtn.BorderBrush = ThemeBrush("Brush.PanelBorder", Brushes.Gray);
            CatalogStopBtn.Foreground = ThemeBrush("Brush.Text", Brushes.White);
        }
    }

    private void UpdateCatalogDownloadButtonUi()
    {
        if (_catalogDownloadInProgress)
        {
            var accent = ThemeBrush("Brush.Accent", Brushes.Red);
            CatalogStopDownloadBtn.Background = accent;
            CatalogStopDownloadBtn.BorderBrush = accent;
            CatalogStopDownloadBtn.Foreground = Brushes.White;
        }
        else
        {
            CatalogStopDownloadBtn.Background = ThemeBrush("Brush.Button", Brushes.Black);
            CatalogStopDownloadBtn.BorderBrush = ThemeBrush("Brush.PanelBorder", Brushes.Gray);
            CatalogStopDownloadBtn.Foreground = ThemeBrush("Brush.Text", Brushes.White);
        }
    }

    private bool IsCatalogToolbarLocked => _catalogOps.IsToolbarBusy;

    private bool IsCatalogUiLocked => _catalogOps.IsToolbarBusy || _catalogDownloadInProgress;

    private bool IsCatalogStopActive =>
        _catalogToolbarOperations > 0 || _catalogDownloadInProgress || _catalogFileSizesInProgress;

    private bool TryBeginCatalogToolbarOperation(
        CatalogToolbarOperationType type,
        object sender,
        out CancellationToken token,
        out int generation)
    {
        if (!_catalogOps.TryBegin(type, sender, out token, out generation))
        {
            return false;
        }

        try
        {
            BeginCatalogToolbarOperation();
        }
        catch (Exception ex)
        {
            _catalogOps.ForceReset();
            _svc.Diagnostics.Write("Catalog", $"Toolbar UI setup failed: {ex.Message}");
            return false;
        }

        _svc.Diagnostics.Write("Catalog", $"Started {type}.");
        return true;
    }

    private async Task EndCatalogToolbarOperationScopeAsync(int generation)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            _catalogOps.End(generation);
            EndCatalogToolbarOperation();
        }).ConfigureAwait(false);
    }

    private async Task RefreshCategoryFilterComboAsync(CancellationToken cancellationToken = default)
    {
        var categoryDoc = await _svc.CategoryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var present = categoryDoc.Models.Values
            .Select(e => e.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(CategoryNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(CategoryNormalizer.GetSortOrder)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<string> { "All" };
        items.AddRange(present);

        await UiDispatcher.InvokeAsync(() =>
        {
            var previous = CategoryFilterCombo.SelectedItem as string;
            _suppressCatalogUiEvents = true;
            try
            {
                CategoryFilterCombo.ItemsSource = items;
                if (!string.IsNullOrWhiteSpace(previous)
                    && items.Contains(previous, StringComparer.OrdinalIgnoreCase))
                {
                    CategoryFilterCombo.SelectedItem = previous;
                }
                else
                {
                    CategoryFilterCombo.SelectedIndex = 0;
                }
            }
            finally
            {
                _suppressCatalogUiEvents = false;
            }
        }).ConfigureAwait(false);
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

    private void RefreshActivityLog()
    {
        AiActivityLog.Text = _svc.ActivityLog.ReadTail();
        LogScrollHelper.ScrollTextBoxToEndDeferred(AiActivityLog, Dispatcher);
    }

    private void RefreshDiagnosticsLog()
    {
        DiagnosticsLog.Text = _svc.Diagnostics.ReadTail();
        LogScrollHelper.ScrollTextBoxToEndDeferred(DiagnosticsLog, Dispatcher);
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var text = _svc.Diagnostics.ReadAll();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "(no diagnostics logged yet)";
        }

        Clipboard.SetText(text);
        _svc.Diagnostics.Write("App", "Diagnostics copied to clipboard");
        RefreshDiagnosticsLog();
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshDiagnosticsLog();

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
        _svc.Diagnostics.Write("Modes",
            $"Apply {tag} requested (restart={restart}, previous={previousMode})");

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
                _svc.Diagnostics.Write("Modes",
                    restart
                        ? $"Applied {mode} and restarted Ollama. Env: {envSummary}"
                        : $"Applied {mode} env (no restart)");

                var finalStatus = restart
                    ? $"Current mode: {mode} — Ollama restarted with new backend"
                    : $"Current mode: {mode} — env saved; restart Ollama to activate backend";
                await UiDispatcher.InvokeAsync(async () =>
                {
                    _modeCardPresenter.EndTransition();
                    await RefreshModesUiAsync().ConfigureAwait(true);
                    ModeStatusLabel.Text = finalStatus;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                _svc.Diagnostics.Write("Modes", $"Apply {mode} failed: {ex.Message}");
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
            var imported = await _svc.ReportImporter.ImportReportsAsync().ConfigureAwait(true);
            _svc.ActivityLog.Write("Task", $"Manual import: {imported.Count} report(s).");
            _svc.Diagnostics.Write("Import",
                imported.Count > 0
                    ? $"Manual import: {imported.Count} model(s): {string.Join(", ", imported.ModelNames)}"
                    : "Manual import: no reports found");
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
        if (ModelsGrid.SelectedItem is ModelLaunchRowViewModel row)
        {
            return row.ToSummary();
        }

        if (ModelsGrid.Items.Count > 0)
        {
            ModelsGrid.SelectedIndex = 0;
            return (ModelsGrid.SelectedItem as ModelLaunchRowViewModel)?.ToSummary();
        }

        return null;
    }

    private async void LaunchBestMode_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBlockWorkloadIfDownloadActive("launching a model"))
        {
            return;
        }

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
                await ApplyLaunchParallelAndModeAsync(model, mode, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    EndTaskFlashSuccess(sender, "Launched");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
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
        if (!TryBlockWorkloadIfDownloadActive("running tests"))
        {
            return;
        }

        var model = TestModelCombo.SelectedItem as string ?? GetSelectedModel()?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (!BeginTestOperation(sender))
        {
            if (IsBenchmarkQueueRunning())
            {
                TestStatusLabel.Text = "Benchmark already running — click Stop Test first.";
                AppendTestLog("Benchmark already running — click Stop Test first.");
            }

            return;
        }

        ShowTestSpinUpUi();
        _svc.Diagnostics.Write("Testing", $"Test Selected clicked: {model}");
        await RunBenchmarkQueueAsync(new[] { model }, sender).ConfigureAwait(true);
    }

    private async void TestUntested_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBlockWorkloadIfDownloadActive("running tests"))
        {
            return;
        }

        if (!BeginTestOperation(sender))
        {
            if (IsBenchmarkQueueRunning())
            {
                TestStatusLabel.Text = "Benchmark already running — click Stop Test first.";
                AppendTestLog("Benchmark already running — click Stop Test first.");
            }
            else
            {
                TestStatusLabel.Text = "Test operation already in progress on this button.";
            }

            return;
        }

        ShowTestSpinUpUi();
        await RunUntestedBenchmarkQueueAsync(sender).ConfigureAwait(true);
    }

    private async void RetestFailed_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBlockWorkloadIfDownloadActive("running tests"))
        {
            return;
        }

        if (!BeginTestOperation(sender))
        {
            if (IsBenchmarkQueueRunning())
            {
                TestStatusLabel.Text = "Benchmark already running — click Stop Test first.";
                AppendTestLog("Benchmark already running — click Stop Test first.");
            }

            return;
        }

        ShowTestSpinUpUi();
        var ct = _testCts?.Token ?? CancellationToken.None;

        try
        {
            await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Building failed-mode retest queue…"))
                .ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            var summaries = await _svc.Profiles.GetAllSummariesAsync(ct).ConfigureAwait(true);
            var failed = summaries
                .Where(BenchmarkCompletion.HasFailedModeResults)
                .Select(s => s.Model)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (failed.Count == 0)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    TestStatusLabel.Text = "No models with failed benchmark modes to retest.";
                    FinishTestOperation(sender, success: false, cancelled: false);
                }).ConfigureAwait(true);
                return;
            }

            _svc.Diagnostics.Write("Testing", $"Retest Failed queue ({failed.Count} model(s)): {string.Join(", ", failed)}");
            await RunBenchmarkQueueAsync(failed, sender).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                TestStatusLabel.Text = "Test stopped";
                FinishTestOperation(sender, success: false, cancelled: true);
            }).ConfigureAwait(true);
        }
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

        if (!TryBlockWorkloadIfDownloadActive("running tests"))
        {
            return;
        }

        if (!BeginTestOperation(sender, FlashColorScheme.PurpleBlack))
        {
            return;
        }

        ShowTestSpinUpUi();
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

        var storeBefore = await _svc.Profiles.LoadAsync().ConfigureAwait(true);
        var profileCountBefore = storeBefore.Models.Count;
        _svc.Diagnostics.Write("Profile",
            $"ClearAll requested — profiles before: {profileCountBefore}");

        var cleared = await _svc.Profiles.ClearAllTestDataAsync().ConfigureAwait(true);
        _suppressReportImport = true;
        await _svc.BenchmarkInsights.ClearAllAsync().ConfigureAwait(true);
        await _svc.BenchmarkSettingsAdvisor.ClearAllAsync().ConfigureAwait(true);
        _svc.Profiles.ClearCache();
        _svc.BenchmarkInsights.ClearCache();
        _svc.BenchmarkSettingsAdvisor.ClearCache();
        _svc.ApiClient.InvalidateCaches();

        var storeAfter = await _svc.Profiles.LoadAsync().ConfigureAwait(true);
        var profileCountAfter = storeAfter.Models.Count;
        _svc.Diagnostics.Write("Profile",
            $"ClearAll complete — removed {cleared.ReportDirsRemoved} report dir(s), " +
            $"{cleared.ReportFilesRemoved} file(s); profiles after: {profileCountAfter}");

        await UiDispatcher.InvokeAsync(async () =>
        {
            ResetTestOperationState();
            StopTestSpinUpCreep();
            TestProgressPanel.Visibility = Visibility.Collapsed;
            _testProgressAnimator.SetImmediate(TestOverallProgress, 0);
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
        _svc.Diagnostics.Write("Testing", "Test Local Untested clicked");
        var ct = _testCts?.Token ?? CancellationToken.None;

        try
        {
        await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Checking Ollama API…")).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var startup = await EnsureOllamaApiReadyAsync(
            ct, "Test Local Untested requires Ollama", timeoutSec: 90).ConfigureAwait(true);
        if (!startup.Success)
        {
            _svc.Diagnostics.Write("Ollama", $"Test Local Untested aborted: {startup.Message}");
            await UiDispatcher.InvokeAsync(() =>
            {
                AppendTestLog($"ABORT: {startup.Message}");
                TestStatusLabel.Text = "Test Local Untested aborted — Ollama API not reachable.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Building local retest queue…")).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var build = await _svc.Profiles.BuildLocalRetestQueueAsync().ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        var names = build.Queue.Select(u => u.Model).ToList();

        foreach (var decision in build.Decisions)
        {
            _svc.Diagnostics.Write("Testing",
                $"Retest decision: {decision.Model} => {(decision.Included ? "INCLUDE" : "SKIP")} " +
                $"({decision.Reason}, NeedsRetest={decision.NeedsRetest}, Results={decision.ResultCount})");
        }

        _svc.Diagnostics.Write("Testing",
            $"Retest queue built: rawTags={build.RawTagCount}, local={build.LocalModelCount}, " +
            $"included={build.IncludedCount}, excluded={build.ExcludedCount}, " +
            $"profiles={build.ProfileCount}, apiReachable={build.ApiReachable}");
        if (!string.IsNullOrWhiteSpace(build.TagsError))
        {
            _svc.Diagnostics.Write("Ollama", build.TagsError);
        }

        if (names.Count == 0)
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                var detail = build.LocalModelCount == 0
                    ? build.RawTagCount == 0
                        ? "Retest queue empty: Ollama returned 0 models (is Ollama running? Try Refresh on Models tab)."
                        : $"Retest queue empty: Ollama returned {build.RawTagCount} model(s) but 0 passed local filter."
                    : $"Retest queue empty: {build.LocalModelCount} local model(s), " +
                      $"{build.ExcludedCount} excluded, {build.ProfileCount} profile(s) in store.";
                AppendTestLog(detail);
                foreach (var excluded in build.Decisions.Where(d => !d.Included).Take(10))
                {
                    AppendTestLog($"  SKIP {excluded.Model}: {excluded.Reason}");
                }

                if (build.Decisions.Count(d => !d.Included) > 10)
                {
                    AppendTestLog($"  … and {build.Decisions.Count(d => !d.Included) - 10} more excluded (see Diagnostics tab)");
                }

                TestStatusLabel.Text = build.LocalModelCount == 0
                    ? "No local models detected — start Ollama and refresh the Models tab."
                    : "No local models need testing or failed-mode retest.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        var failedRetests = build.Queue.Count(s => !s.NeedsRetest);
        await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Prioritizing queue with AI…")).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        var summaries = await _svc.Profiles.GetAllSummariesAsync(ct).ConfigureAwait(true);
        EnterAiActivity();
        BenchmarkQueueAdvisorService.PrioritizedQueue queue;
        try
        {
            queue = await _svc.QueueAdvisor.PrioritizeAsync(names, summaries).ConfigureAwait(true);
        }
        finally
        {
            ExitAiActivity();
        }

        ct.ThrowIfCancellationRequested();
        _svc.Diagnostics.Write("Testing",
            $"AI prioritized queue ({queue.Models.Count} model(s)): {string.Join(" -> ", queue.Models)}");
        await UiDispatcher.InvokeAsync(() =>
        {
            var queueLabel = failedRetests > 0
                ? $"AI ordered queue ({failedRetests} failed retest(s)): {string.Join(" -> ", queue.Models)}"
                : $"AI ordered queue: {string.Join(" -> ", queue.Models)}";
            TestStatusLabel.Text = queueLabel;
        }).ConfigureAwait(true);
        _svc.ActivityLog.Write("AI", queue.Rationale);
        await RunBenchmarkQueueAsync(queue.Models, flashSender).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _svc.Diagnostics.Write("Testing", "Test Local Untested cancelled during pre-queue");
            await UiDispatcher.InvokeAsync(() =>
            {
                if (_acceptProgressUpdates)
                {
                    ApplyTestStoppedProgressUi();
                }

                FinishTestOperation(flashSender, success: false, cancelled: true);
            }).ConfigureAwait(true);
        }
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
        TestSettingsLabel.Text = FormatTestSettingsLabel(settings);
    }

    private static string FormatTestSettingsLabel(BenchmarkSettingsEntry settings)
    {
        var parallel = BenchmarkParallelSettings.FormatParallelSummary(settings.NumParallelByMode);
        var rationale = settings.Rationale ?? string.Empty;
        return string.IsNullOrWhiteSpace(rationale) ? parallel : $"{rationale} | {parallel}";
    }

    private static (int NumCtx, int NumPredict) ParseBenchmarkSpinners(string ctxText, string predictText)
    {
        var ctx = int.TryParse(ctxText, out var c) && c > 0 ? c : 8192;
        var pred = int.TryParse(predictText, out var p) && p > 0 ? p : 32;
        return (ctx, pred);
    }

    private async Task<BenchmarkSettingsEntry> ResolveBenchmarkSettingsForModelAsync(
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
            var embedSettings = new BenchmarkSettingsEntry
            {
                NumCtx = 0,
                NumPredict = 0,
                NumParallelByMode = BenchmarkParallelSettings.EmbedDefaults(),
                Rationale = "Embedding model — /api/embed benchmark (latency ms; no generation settings)."
            };
            await UiDispatcher.InvokeAsync(() =>
            {
                TestNumCtxBox.Text = "—";
                TestNumPredictBox.Text = "—";
                TestSettingsLabel.Text = FormatTestSettingsLabel(embedSettings);
            }).ConfigureAwait(false);
            return embedSettings;
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
            TestSettingsLabel.Text = FormatTestSettingsLabel(settings);
        }).ConfigureAwait(false);

        return settings;
    }

    private void ShowTestSpinUpUi()
    {
        MainTabs.SelectedItem = TestingSuiteTab;
        TestProgressPanel.Visibility = Visibility.Visible;
        TestOverallLabel.Text = "Overall: Initializing tests…";
        TestOverallProgress.IsIndeterminate = false;
        TestOverallProgress.ClearValue(Control.ForegroundProperty);
        _testProgressAnimator.SetImmediate(TestOverallProgress, 0);

        BuildTestModeProgressRows(Array.Empty<string>());
        AddTestProgressRow("Initializing…", 72);
        if (_testModeProgress.TryGetValue("Initializing…", out var row))
        {
            row.Bar.IsIndeterminate = true;
            row.Status.Text = "Spinning up…";
            row.Status.Foreground = (Brush)FindResource("Brush.Warning");
        }

        TestStatusLabel.Text = "Tests Spinning Up — Please Wait…";
        AppendTestLog("Tests Spinning Up.....Please Wait.");
        BeginTestProgressSession();
        StartTestSpinUpCreep();
    }

    private void AppendTestSpinUpStatus(string message)
    {
        TestStatusLabel.Text = message;
        AppendTestLog(message);
    }

    private void StartTestSpinUpCreep()
    {
        StopTestSpinUpCreep();
        _testSpinUpProgress = 0;
        _testSpinUpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _testSpinUpTimer.Tick += (_, _) =>
        {
            if (_testSpinUpProgress >= 8)
            {
                return;
            }

            _testSpinUpProgress = Math.Min(8, _testSpinUpProgress + 0.2);
            _testProgressAnimator.SetCeiling(TestOverallProgress, _testSpinUpProgress);
        };
        _testSpinUpTimer.Start();
    }

    private void StopTestSpinUpCreep()
    {
        _testSpinUpTimer?.Stop();
        _testSpinUpTimer = null;
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
            Height = 10,
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
        if (!_acceptProgressUpdates)
        {
            return;
        }

        if (!_testModeProgress.TryGetValue("Download", out var row))
        {
            return;
        }

        row.Bar.Foreground = (Brush)FindResource("Brush.Purple");
        if (update.Percent is >= 0)
        {
            _testProgressAnimator.SetAuthoritative(row.Bar, update.Percent.Value);
            _testProgressAnimator.SetAuthoritative(TestOverallProgress, update.Percent.Value);
            row.Status.Text = FormatDownloadProgressStatus(update);
        }
        else if (!string.IsNullOrWhiteSpace(update.Status))
        {
            row.Status.Text = update.Status;
        }

        row.Status.Foreground = (Brush)FindResource("Brush.Purple");
    }

    private static string FormatDownloadProgressStatus(ModelPullProgress update)
    {
        if (update.TotalBytes is > 0 && update.CompletedBytes is >= 0)
        {
            var pct = update.Percent ?? (int)Math.Clamp(
                100.0 * update.CompletedBytes.Value / update.TotalBytes.Value,
                0,
                100);
            return
                $"{ModelSizeFormatter.FormatBytes(update.CompletedBytes.Value)} / {ModelSizeFormatter.FormatBytes(update.TotalBytes.Value)} ({pct}%)";
        }

        if (update.Percent is int percent)
        {
            return $"{percent}%";
        }

        return string.IsNullOrWhiteSpace(update.Status) ? "Downloading…" : update.Status;
    }

    private void ResetTestProgressUi(
        string model,
        int modelIndex,
        int modelCount,
        IReadOnlyList<string> modes,
        string? aiSummarizer,
        bool includeDownloadRow = false)
    {
        StopTestSpinUpCreep();
        TestProgressPanel.Visibility = Visibility.Visible;
        TestOverallProgress.IsIndeterminate = false;
        TestOverallProgress.ClearValue(Control.ForegroundProperty);
        _testProgressAnimator.SetImmediate(TestOverallProgress, 0);
        var aiLine = string.IsNullOrWhiteSpace(aiSummarizer)
            ? "AI insights LLM: (none)"
            : $"AI insights LLM: {aiSummarizer}";
        TestOverallLabel.Text =
            $"Overall: {model} ({modelIndex + 1}/{modelCount}) — {aiLine}";
        BuildTestModeProgressRows(modes, includeDownloadRow);

        _testProgressAnimator.ResetModeBars();
        _testProgressAnimator.SetOverallBar(TestOverallProgress);
        _testProgressAnimator.SetAuthoritativeBenchmark(true);
        foreach (var (bar, status) in _testModeProgress.Values)
        {
            bar.IsIndeterminate = false;
            _testProgressAnimator.SetImmediate(bar, 0);
            status.Text = "Pending";
            status.Foreground = (Brush)FindResource("Brush.Muted");
        }
    }

    private void SyncModeBarStates(BenchmarkProgressUpdate update, string activeMode)
    {
        for (var i = 0; i < BenchmarkModeOrder.Length; i++)
        {
            var mode = BenchmarkModeOrder[i];
            if (mode.Equals(activeMode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_testModeProgress.TryGetValue(mode, out var row))
            {
                continue;
            }

            if (i < update.ModeIndex)
            {
                _testProgressAnimator.FreezeBar(row.Bar, 100);
                row.Status.Text = "Complete";
                row.Status.Foreground = (Brush)FindResource("Brush.Active");
            }
            else if (i > update.ModeIndex)
            {
                _testProgressAnimator.FreezeBar(row.Bar, 0);
                row.Status.Text = "Pending";
                row.Status.Foreground = (Brush)FindResource("Brush.Muted");
            }
        }
    }

    private void ApplyBenchmarkProgressUpdate(BenchmarkProgressUpdate update)
    {
        if (!_acceptProgressUpdates)
        {
            return;
        }

        _testProgressAnimator.SetAuthoritative(TestOverallProgress, update.OverallPercent);
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
            if (update.Phase == BenchmarkProgressPhase.ModelCompleted)
            {
                TestStatusLabel.Text = update.BestMode is not null
                    ? isEmbed
                        ? $"{update.Model} complete — winner: {update.BestMode} @ {update.BestEmbedMs:F1} ms/embed"
                        : $"{update.Model} complete — winner: {update.BestMode} @ {update.BestTps:F2} tok/s"
                    : $"{update.Model} complete — all modes failed";
            }

            return;
        }

        row.Bar.IsIndeterminate = false;
        row.Bar.Foreground = (Brush)FindResource("Brush.Accent");
        var modeBarValue = update.CurrentModePercent;
        var statusDetail = string.IsNullOrWhiteSpace(update.ModeStatusDetail)
            ? null
            : update.ModeStatusDetail;

        SyncModeBarStates(update, update.Mode);

        switch (update.Phase)
        {
            case BenchmarkProgressPhase.ModeApplying:
            case BenchmarkProgressPhase.ModeBenchmarking:
                _testProgressAnimator.SetActiveModeBar(row.Bar);
                _testProgressAnimator.SetAuthoritative(row.Bar, modeBarValue);
                row.Bar.Foreground = (Brush)FindResource("Brush.Accent");
                row.Status.Text = statusDetail ?? update.Phase switch
                {
                    BenchmarkProgressPhase.ModeApplying => "Applying mode…",
                    BenchmarkProgressPhase.ModeBenchmarking when isEmbed && update.EmbedLatencyMs is > 0 =>
                        $"{update.EmbedLatencyMs:F1} ms/embed",
                    BenchmarkProgressPhase.ModeBenchmarking when update.GenerationTps is > 0 =>
                        $"{update.GenerationTps:F1} tok/s",
                    _ => isEmbed ? "Embedding…" : "Benchmarking…"
                };
                row.Status.Foreground = (Brush)FindResource("Brush.Warning");
                break;
            case BenchmarkProgressPhase.ModeCompleted:
                _testProgressAnimator.SetAuthoritative(row.Bar, 100);
                _testProgressAnimator.FreezeBar(row.Bar, 100);
                _testProgressAnimator.SetActiveModeBar(null);
                row.Status.Text = statusDetail ?? (isEmbed
                    ? $"{update.EmbedLatencyMs:F1} ms"
                    : $"{update.GenerationTps:F1} tok/s");
                row.Status.Foreground = (Brush)FindResource("Brush.Active");
                break;
            case BenchmarkProgressPhase.ModeFailed:
                _testProgressAnimator.SetAuthoritative(row.Bar, modeBarValue);
                _testProgressAnimator.FreezeBar(row.Bar, modeBarValue);
                _testProgressAnimator.SetActiveModeBar(null);
                row.Bar.Foreground = (Brush)FindResource("Brush.FailedBg");
                row.Status.Text = statusDetail ?? "Failed";
                row.Status.Foreground = (Brush)FindResource("Brush.FailedBg");
                _ = RunIncrementalFailureDiagnosisAsync(update.Model);
                break;
            default:
                _testProgressAnimator.SetAuthoritative(row.Bar, modeBarValue);
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
                BenchmarkProgressPhase.ModelCompleted when update.BestMode is null =>
                    $"{update.Model} complete — all modes failed",
                _ => TestStatusLabel.Text
            };
        }
    }

    private async Task RunIncrementalFailureDiagnosisAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        try
        {
            _svc.Profiles.ClearCache();
            var summary = (await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true))
                .FirstOrDefault(s => s.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (summary is null || !BenchmarkCompletion.HasFailedModeResults(summary))
            {
                return;
            }

            EnterAiActivity();
            try
            {
                await _svc.BenchmarkInsights.DiagnoseFailuresAsync(summary).ConfigureAwait(true);
            }
            finally
            {
                ExitAiActivity();
            }
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Testing", $"Incremental failure diagnosis skipped: {ex.Message}");
        }
    }

    private async Task RunBenchmarkQueueAsync(IReadOnlyList<string> models, object? flashSender = null)
    {
        if (IsBenchmarkQueueRunning())
        {
            _svc.Diagnostics.Write("Testing",
                $"Benchmark queue rejected — already running ({models.Count} model(s) requested)");
            await UiDispatcher.InvokeAsync(() =>
            {
                AppendTestLog("Benchmark already running — click Stop Test first.");
                TestStatusLabel.Text = "Benchmark already running — click Stop Test first.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        _svc.Diagnostics.Write("Testing",
            $"Benchmark queue starting ({models.Count} model(s)): {string.Join(", ", models)}");
        _benchmarkCts = new CancellationTokenSource();
        var ct = _benchmarkCts.Token;
        var workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTestWork = workTcs.Task;
        _benchmarkQueueRunning = true;

        await _svc.WorkQueue.EnqueueAsync(async _ =>
        {
            var cancelled = false;
            try
            {
                await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Resolving AI summarizer…"))
                    .ConfigureAwait(false);

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
            }
            catch (Exception ex)
            {
                _svc.Diagnostics.Write("Testing", $"Benchmark queue error: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    AppendTestLog($"ABORT: Benchmark queue error: {ex.Message}");
                    TestStatusLabel.Text = $"Benchmark queue error: {ex.Message}";
                }).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    _svc.Diagnostics.Write("Testing",
                        cancelled ? "Benchmark queue stopped" : "Benchmark queue complete");
                    await UiDispatcher.InvokeAsync(async () =>
                    {
                        await RefreshModelsUiAsync().ConfigureAwait(true);
                        await RefreshTestResultsUiAsync().ConfigureAwait(true);
                        await RefreshCatalogUiAsync().ConfigureAwait(true);
                        await SyncComputeModeDisplayAsync().ConfigureAwait(true);
                        if (!cancelled)
                        {
                            _testProgressAnimator.SetAuthoritative(TestOverallProgress, 100);
                        }

                        TestStatusLabel.Text = cancelled
                            ? "Test Stopped"
                            : "Benchmark queue complete.";
                        FinishTestOperation(flashSender, success: !cancelled, cancelled);
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Benchmark queue cleanup failed: {ex.Message}");
                }
                finally
                {
                    _benchmarkQueueRunning = false;
                    workTcs.TrySetResult();
                }
            }
        }).ConfigureAwait(true);

        await workTcs.Task.ConfigureAwait(true);
    }

    private async Task RunUndownloadTestQueueAsync(object? flashSender = null)
    {
        _svc.Diagnostics.Write("Testing", "Test Undownload clicked");
        var ct = _testCts?.Token ?? CancellationToken.None;

        try
        {
        await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Checking Ollama API…")).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var startup = await EnsureOllamaApiReadyAsync(
            ct, "Undownload test requires Ollama", timeoutSec: 90).ConfigureAwait(true);
        if (!startup.Success)
        {
            _svc.Diagnostics.Write("Ollama", $"Test Undownload aborted: {startup.Message}");
            await UiDispatcher.InvokeAsync(() =>
            {
                AppendTestLog($"ABORT: {startup.Message}");
                TestStatusLabel.Text = "Undownload test aborted — Ollama API not reachable.";
                FinishTestOperation(flashSender, success: false, cancelled: false);
            }).ConfigureAwait(true);
            return;
        }

        await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Building undownload test queue…"))
            .ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var candidates = (await _svc.Registry.GetUndownloadTestQueueAsync(ct).ConfigureAwait(true)).ToList();
        if (candidates.Count == 0)
        {
            var cancelled = ct.IsCancellationRequested;
            await UiDispatcher.InvokeAsync(() =>
            {
                TestStatusLabel.Text = cancelled
                    ? "Test stopped"
                    : "No undownloaded models need testing.";
                FinishTestOperation(flashSender, success: false, cancelled);
            }).ConfigureAwait(true);
            return;
        }

        _svc.Diagnostics.Write("Testing",
            $"Undownload queue built ({candidates.Count} model(s)) before AI prioritization.");

        await UiDispatcher.InvokeAsync(() => AppendTestSpinUpStatus("Prioritizing queue with AI…")).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var categories = await GetCategoryMapAsync().ConfigureAwait(true);
        var queueItems = candidates
            .Select(c => new BenchmarkQueueAdvisorService.UndownloadQueueItem(
                c.LibraryName,
                c.PullTag,
                c.FileSizeBytes,
                c.HasKnownFileSize))
            .ToList();

        EnterAiActivity();
        BenchmarkQueueAdvisorService.PrioritizedUndownloadQueue queue;
        try
        {
            queue = await _svc.QueueAdvisor.PrioritizeUndownloadAsync(queueItems, categories, ct)
                .ConfigureAwait(true);
        }
        finally
        {
            ExitAiActivity();
        }

        ct.ThrowIfCancellationRequested();
        candidates = queue.Candidates
            .Select(item => new UndownloadTestCandidate
            {
                LibraryName = item.LibraryName,
                PullTag = item.PullTag,
                FileSizeBytes = item.FileSizeBytes,
                HasKnownFileSize = item.HasKnownFileSize
            })
            .ToList();

        var unknownCount = candidates.Count(c => !c.HasKnownFileSize);
        _svc.Diagnostics.Write("Testing",
            $"AI prioritized undownload queue ({candidates.Count} model(s), {unknownCount} unknown size last): " +
            $"{string.Join(" -> ", candidates.Select(c => c.LibraryName))}");

        _undownloadCts?.Cancel();
        _undownloadCts = new CancellationTokenSource();
        var workCt = _undownloadCts.Token;
        var workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTestWork = workTcs.Task;

        await UiDispatcher.InvokeAsync(() =>
        {
            _testingHighlight.ClearTestedUndownloadMarks();
            AppendTestLog(
                $"--- Undownload test queue ({candidates.Count} model(s), smallest file size first) ---");
            AppendTestLog(
                $"Order: {string.Join(" -> ", candidates.Select(c => c.LibraryName))}");
            if (unknownCount > 0)
            {
                AppendTestLog(
                    $"Unknown catalog file size queued last ({unknownCount} model(s)).");
            }

            var queueLabel = unknownCount > 0
                ? $"AI ordered undownload queue ({unknownCount} unknown size last): {string.Join(" -> ", candidates.Select(c => c.LibraryName))}"
                : $"AI ordered undownload queue: {string.Join(" -> ", candidates.Select(c => c.LibraryName))}";
            TestStatusLabel.Text = queueLabel;
        }).ConfigureAwait(true);
        _svc.ActivityLog.Write("AI", queue.Rationale);

        await _svc.WorkQueue.EnqueueAsync(async _ =>
        {
            var cancelled = false;
            try
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (workCt.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    var pullTag = candidate.PullTag;
                    var fileSizeLabel = candidate.HasKnownFileSize
                        ? ModelSizeFormatter.FormatBytes(candidate.FileSizeBytes)
                        : "unknown";

                    await UiDispatcher.InvokeAsync(() =>
                    {
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

                        var aiSummarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: workCt)
                            .ConfigureAwait(false);
                        await UiDispatcher.InvokeAsync(() =>
                            ResetTestProgressUi(
                                pullTag,
                                i,
                                candidates.Count,
                                new[] { "CPU", "APU", "GPU", "Hybrid" },
                                aiSummarizer,
                                includeDownloadRow: true))
                            .ConfigureAwait(false);

                        var pullStartup = await EnsureOllamaApiReadyAsync(
                            workCt, $"Download requires Ollama ({pullTag})", timeoutSec: 60).ConfigureAwait(false);
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

                        await _svc.ApiClient.PullAsync(pullTag, pullProgress, workCt).ConfigureAwait(false);
                        _svc.Profiles.ClearCache();

                        if (!await _svc.ApiClient.IsModelInstalledAsync(pullTag, workCt).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"Download finished but {pullTag} was not found in the local Ollama model list.");
                        }

                        await UiDispatcher.InvokeAsync(() =>
                        {
                            if (_testModeProgress.TryGetValue("Download", out var row))
                            {
                                _testProgressAnimator.SetAuthoritative(row.Bar, 100);
                                _testProgressAnimator.FreezeBar(row.Bar, 100);
                                _testProgressAnimator.SetAuthoritative(TestOverallProgress, 100);
                                row.Status.Text = "Complete";
                                row.Status.Foreground = (Brush)FindResource("Brush.Active");
                            }

                            AppendTestLog($"Download verified: {pullTag} is installed locally.");
                            AppendTestLog("Step 2/3: Running 4-mode benchmark on downloaded model…");
                        }).ConfigureAwait(false);

                        await RunBenchmarkQueueCoreAsync(
                            new[] { pullTag },
                            flashSender: null,
                            externalCt: workCt,
                            modelIndexOffset: i,
                            totalModels: candidates.Count,
                            holdCatalogHighlight: true,
                            skipDownloadProgressRow: true)
                            .ConfigureAwait(false);

                        if (workCt.IsCancellationRequested)
                        {
                            cancelled = true;
                        }

                        await UiDispatcher.InvokeAsync(() =>
                            AppendTestLog($"Step 3/3: Removing local copy {pullTag} (keeping benchmark data)…"))
                            .ConfigureAwait(false);

                        await _svc.ModelSessions.UninstallModelAsync(pullTag, workCt).ConfigureAwait(false);
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
                            SetCatalogTestingHighlight(candidate.LibraryName, on: false);
                            _testingHighlight.MarkTestedUndownload(candidate.LibraryName);
                            await RefreshModelsUiAsync().ConfigureAwait(true);
                            await RefreshCatalogUiAsync().ConfigureAwait(true);
                            await RefreshTestResultsUiAsync().ConfigureAwait(true);
                            _testingHighlight.ReapplyActive();
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
                                SetCatalogTestingHighlight(candidate.LibraryName, on: false);
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
        catch (OperationCanceledException)
        {
            _svc.Diagnostics.Write("Testing", "Test Undownload cancelled during pre-queue");
            await UiDispatcher.InvokeAsync(() =>
            {
                if (_acceptProgressUpdates)
                {
                    ApplyTestStoppedProgressUi();
                }

                FinishTestOperation(flashSender, success: false, cancelled: true);
            }).ConfigureAwait(true);
        }
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
        var benchmarkModes = new[] { "CPU", "APU", "GPU", "Hybrid" };
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

                var benchmarkSettings = await ResolveBenchmarkSettingsForModelAsync(model, ct)
                    .ConfigureAwait(false);
                var numCtx = benchmarkSettings.NumCtx;
                var numPredict = benchmarkSettings.NumPredict;
                var numParallelByMode = benchmarkSettings.NumParallelByMode;

                await UiDispatcher.InvokeAsync(() =>
                {
                    TestStatusLabel.Text = isEmbed
                        ? $"Embedding benchmark {model} ({globalIndex + 1}/{totalModels}) — /api/embed latency test…"
                        : $"Benchmarking {model} ({globalIndex + 1}/{totalModels}) — num_ctx={numCtx}, num_predict={numPredict}, {BenchmarkParallelSettings.FormatParallelSummary(numParallelByMode)}…";
                }).ConfigureAwait(false);

                var log = new Progress<string>(AppendTestLog);

                var session = _progressSession;
                var progress = new Progress<BenchmarkProgressUpdate>(update =>
                {
                    if (session != _progressSession)
                    {
                        return;
                    }

                    UiDispatcher.InvokeAsync(() =>
                    {
                        if (session != _progressSession || !_acceptProgressUpdates)
                        {
                            return;
                        }

                        ApplyBenchmarkProgressUpdate(update);
                    });
                });

                await _svc.BenchmarkRunner.RunAsync(
                    model,
                    numPredict: numPredict,
                    numCtx: numCtx,
                    numParallelByMode: numParallelByMode,
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
                    EnterAiActivity();
                    try
                    {
                        await _svc.BenchmarkInsights.InterpretProfileAsync(summary, ct).ConfigureAwait(false);
                        await _svc.BenchmarkInsights.DiagnoseFailuresAsync(summary, ct).ConfigureAwait(false);
                    }
                    catch (Exception aiEx) when (aiEx is not OperationCanceledException)
                    {
                        await UiDispatcher.InvokeAsync(() =>
                            AppendTestLog(
                                $"AI insight warning ({model}): {OllamaOutputSanitizer.FormatException(aiEx)}"))
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ExitAiActivity();
                    }
                }

                await UiDispatcher.InvokeAsync(async () => await RefreshTestResultsUiAsync().ConfigureAwait(true))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var connectionLost = OllamaConnectionHelper.IsConnectionError(ex);
                var userMessage = OllamaConnectionHelper.FormatUserMessage(ex);
                await UiDispatcher.InvokeAsync(() =>
                {
                    AppendTestLog($"FAIL ({model}): {userMessage}");
                    TestStatusLabel.Text = connectionLost
                        ? $"Benchmark hit Ollama connection loss on {model} — attempting recovery and continuing queue…"
                        : $"Benchmark error on {model}: {userMessage} — continuing queue…";
                }).ConfigureAwait(false);

                if (connectionLost)
                {
                    try
                    {
                        var recovery = await _svc.ModeService.Processes
                            .EnsureApiReadyAsync(90, ct, autoStart: true)
                            .ConfigureAwait(false);
                        _svc.ApiClient.InvalidateCaches();
                        await UiDispatcher.InvokeAsync(() =>
                        {
                            if (recovery.Success)
                            {
                                AppendTestLog("Recovery: Ollama API is ready again; continuing benchmark queue.");
                            }
                            else
                            {
                                AppendTestLog($"Recovery warning: {recovery.Message}");
                            }
                        }).ConfigureAwait(false);
                    }
                    catch (Exception recoverEx)
                    {
                        await UiDispatcher.InvokeAsync(() =>
                            AppendTestLog(
                                $"Recovery warning: {OllamaOutputSanitizer.FormatException(recoverEx)}"))
                            .ConfigureAwait(false);
                    }
                }
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
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ChatHistory.Text += msg;
                    if (flashSender is not null)
                    {
                        EndTaskFlashIdle(flashSender);
                    }
                }).ConfigureAwait(false);
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
            try
            {
                var insightIndex = DataGridColumnHelper.IndexOfStarColumn(grid, "Insight");
                if (insightIndex < 0)
                {
                    insightIndex = 0;
                }

                DataGridColumnHelper.AutoFitColumns(grid, insightIndex);
            }
            catch (Exception ex)
            {
                _svc.Diagnostics.Write("TestResults", $"Column auto-fit failed: {ex.Message}");
            }

            return;
        }

        if (ReferenceEquals(grid, ModelsGrid))
        {
            var modelsStar = DataGridColumnHelper.IndexOfStarColumn(grid, "Description");
            if (modelsStar < 0)
            {
                modelsStar = 0;
            }

            DataGridColumnHelper.AutoFitColumns(grid, modelsStar);
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
        var downloadActive = _catalogDescriptionMode == CatalogDescriptionDisplayMode.Download
            ? (Style)FindResource("CatalogDescriptionModeActive")
            : (Style)FindResource("ToolkitButton");
        var aiActive = _catalogDescriptionMode == CatalogDescriptionDisplayMode.Ai
            ? (Style)FindResource("CatalogDescriptionModeActive")
            : (Style)FindResource("ToolkitButton");

        DownloadDescriptionsModeBtn.Style = downloadActive;
        AiDescriptionsModeBtn.Style = aiActive;
        ModelsOfficialDescriptionsBtn.Style = downloadActive;
        ModelsAiDescriptionsBtn.Style = aiActive;
    }

    private string ResolveLaunchDisplayDescription(
        string libraryName,
        IReadOnlyDictionary<string, LibraryCatalogEntry> catalogEntries,
        ModelDescriptionStoreDocument descriptionDoc)
    {
        if (!catalogEntries.TryGetValue(libraryName, out var entry))
        {
            return string.Empty;
        }

        var downloadDescription = string.IsNullOrWhiteSpace(entry.Description)
            ? string.Empty
            : entry.Description.Trim();
        var aiDescription = descriptionDoc.Models.TryGetValue(libraryName, out var aiEntry)
            && !string.IsNullOrWhiteSpace(aiEntry.ListDescription)
            ? aiEntry.ListDescription!.Trim()
            : string.Empty;

        return _catalogDescriptionMode == CatalogDescriptionDisplayMode.Ai
            ? string.IsNullOrWhiteSpace(aiDescription) ? "(not summarized)" : aiDescription
            : downloadDescription;
    }

    private async void DownloadDescriptionsMode_Click(object sender, RoutedEventArgs e)
    {
        if (IsCatalogToolbarLocked)
        {
            CatalogStatusLabel.Text = "Wait for the active catalog operation to finish.";
            return;
        }

        try
        {
            _catalogDescriptionMode = CatalogDescriptionDisplayMode.Download;
            ApplyCatalogDescriptionModeUi();
            await RefreshCatalogUiAsync().ConfigureAwait(true);
            await RefreshModelsUiAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Catalog", $"Description mode switch failed: {ex.Message}");
            CatalogStatusLabel.Text = ex.Message;
        }
    }

    private async void AiDescriptionsMode_Click(object sender, RoutedEventArgs e)
    {
        if (IsCatalogToolbarLocked)
        {
            CatalogStatusLabel.Text = "Wait for the active catalog operation to finish.";
            return;
        }

        try
        {
            _catalogDescriptionMode = CatalogDescriptionDisplayMode.Ai;
            ApplyCatalogDescriptionModeUi();
            await RefreshCatalogUiAsync().ConfigureAwait(true);
            await RefreshModelsUiAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Catalog", $"Description mode switch failed: {ex.Message}");
            CatalogStatusLabel.Text = ex.Message;
        }
    }

    private async void RefreshDescriptions_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        if (!TryBeginCatalogToolbarOperation(
                CatalogToolbarOperationType.RefreshDescriptions, sender, out var opToken, out var generation))
        {
            EndTaskFlashIdle(sender);
            CatalogStatusLabel.Text = "Another catalog operation is already running.";
            return;
        }

        _catalogDescriptionMode = CatalogDescriptionDisplayMode.Ai;
        ApplyCatalogDescriptionModeUi();
        _catalogBulkUiCounter = 0;
        CatalogStatusLabel.Text = "Refreshing descriptions from ollama.com...";

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = _catalogOps.CreateLinkedTokenSource(workCt)!;
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

                await BindFullCatalogGridAsync("Refreshing descriptions", sortAlphabetically: true, ct)
                    .ConfigureAwait(false);

                var progress = new Progress<string>(msg =>
                    _ = UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg));
                var itemProgress = new Progress<DescriptionRefreshItemProgress>(p =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    _ = UiDispatcher.InvokeAsync(() => HandleDescriptionRefreshProgress(p));
                });

                var count = await _svc.Descriptions.RefreshAllListDescriptionsAsync(
                    entries,
                    forceRegenerate: true,
                    progress,
                    itemProgress,
                    ct).ConfigureAwait(false);

                _svc.ActivityLog.Write("AI", $"Refreshed {count} catalog description(s) from web + AI.");

                var sizeProgress = new Progress<string>(msg =>
                    _ = UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg));

                await _svc.CatalogStore.EnrichFileSizesAsync(sizeProgress, cancellationToken: ct)
                    .ConfigureAwait(false);

                _svc.CatalogStore.ClearCache();
                await ApplyCatalogFileSizesToRowsAsync(ct).ConfigureAwait(false);

                await UiDispatcher.InvokeAsync(() =>
                {
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
                _svc.Diagnostics.Write("Catalog", $"RefreshDescriptions complete ({count} descriptions).");
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = "Description refresh cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
                _svc.Diagnostics.Write("Catalog", "RefreshDescriptions cancelled.");
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                _svc.Diagnostics.Write("Catalog", $"RefreshDescriptions failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
                await EndCatalogToolbarOperationScopeAsync(generation).ConfigureAwait(false);
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
        _testingHighlight.ReapplyActive();
    }

    private async Task BindFullCatalogGridAsync(
        string statusPrefix,
        bool sortAlphabetically = false,
        CancellationToken cancellationToken = default)
    {
        var rows = (await _svc.Registry.GetCatalogRowsAsync(
            search: null,
            categoryFilter: "All",
            descriptionMode: _catalogDescriptionMode,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        if (sortAlphabetically)
        {
            rows = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        await UiDispatcher.InvokeAsync(() =>
        {
            _suppressCatalogUiEvents = true;
            try
            {
                CategoryFilterCombo.SelectedIndex = 0;
                CatalogSearchBox.Text = string.Empty;
                BindCatalogRows(rows);
                CatalogStatusLabel.Text = $"{statusPrefix} for {rows.Count} model(s)...";
            }
            finally
            {
                _suppressCatalogUiEvents = false;
            }
        }).ConfigureAwait(false);
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
        if (!_catalogOps.IsToolbarBusy
            || !_catalogRowByName.TryGetValue(progress.ModelName, out var row))
        {
            return;
        }

        if (progress.Phase == CatalogRefreshPhase.Started)
        {
            _catalogRowAnimator.BeginRow(row);
            _catalogBulkUiCounter++;
            if (_catalogBulkUiCounter == 1 || _catalogBulkUiCounter % 25 == 0)
            {
                CatalogGrid.SelectedItem = row;
                CatalogGrid.ScrollIntoView(row);
            }
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
        if (!_catalogOps.IsToolbarBusy
            || !_catalogRowByName.TryGetValue(progress.ModelName, out var row))
        {
            return;
        }

        if (progress.Phase == DescriptionRefreshPhase.Started)
        {
            _catalogRowAnimator.BeginRow(row);
            _catalogBulkUiCounter++;
            if (_catalogBulkUiCounter == 1 || _catalogBulkUiCounter % 25 == 0)
            {
                CatalogGrid.SelectedItem = row;
                CatalogGrid.ScrollIntoView(row);
            }
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
        TestSummarizerBtn.IsEnabled = enabled;
        CategorizeAllBtn.IsEnabled = enabled;
        RecategorizeAllBtn.IsEnabled = enabled;
        GenerateAiRecommendationsBtn.IsEnabled = enabled;
        RefreshAiRecommendationsBtn.IsEnabled = enabled;
        ClearAiRecommendationsBtn.IsEnabled = enabled;
        AiUninstallRecommendedBtn.IsEnabled = enabled;
        AiLaunchRecommendedBtn.IsEnabled = enabled;
        AiTestRecommendedBtn.IsEnabled = enabled;
        AiDownloadRecommendedBtn.IsEnabled = enabled;
        AiOpenInCatalogBtn.IsEnabled = enabled;
        CancelAiRecommendationsBtn.IsEnabled = enabled;
        SummarizerCombo.IsEnabled = enabled;
    }

    private void SetSummarizerRefreshControlsEnabled(bool enabled)
    {
        RefreshSummarizerListBtn.IsEnabled = enabled;
        SummarizerCombo.IsEnabled = enabled;
    }

    private void SetAiGenerateBusy(bool busy)
    {
        GenerateAiRecommendationsBtn.IsEnabled = !busy;
        RefreshSummarizerListBtn.IsEnabled = !busy;
        SummarizerCombo.IsEnabled = !busy;
    }

    private async Task LoadCatalogTabAsync()
    {
        if (IsCatalogToolbarLocked && !_catalogDownloadInProgress)
        {
            return;
        }

        await RefreshCategoryFilterComboAsync().ConfigureAwait(true);
        await RefreshCatalogUiAsync().ConfigureAwait(true);
    }

    private void CancelCatalogFileSizeEnrichment() => _svc.CatalogStore.CancelFileSizeEnrichment();

    private async Task CancelActiveCatalogOperationsAsync()
    {
        var flashSender = _catalogOps.ActiveFlashSender;
        _catalogOps.Cancel();
        _catalogOps.ForceReset();
        _catalogDownloadCts?.Cancel();
        CancelCatalogFileSizeEnrichment();
        _svc.Diagnostics.Write("Catalog", "STOP requested for active catalog operation.");

        await UiDispatcher.InvokeAsync(() =>
        {
            _catalogRowAnimator.Stop();
            CatalogRowRefreshAnimator.ResetAll(_catalogRows);
            HideCatalogDownloadProgress();
            if (flashSender is not null)
            {
                EndTaskFlashIdle(flashSender);
            }

            _catalogToolbarOperations = 0;
            UpdateCatalogStopButtonUi();
        }).ConfigureAwait(false);
    }

    private async void CatalogStop_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCatalogStopActive)
        {
            return;
        }

        _catalogDownloadCts?.Cancel();
        _catalogFileSizesCts?.Cancel();
        CancelCatalogFileSizeEnrichment();
        await CancelActiveCatalogOperationsAsync().ConfigureAwait(true);
        CatalogStatusLabel.Text = "Stopping catalog operation...";
    }

    private void ApplyCatalogFilterInMemory()
    {
        var search = CatalogSearchBox.Text?.Trim() ?? string.Empty;
        var category = CategoryFilterCombo.SelectedItem as string ?? "All";
        var rows = _catalogRows.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(r =>
                r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.DisplayDescription.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.BestMode.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.BestTps.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows.Where(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = rows.ToList();
        CatalogGrid.ItemsSource = filtered;
        CatalogStatusLabel.Text = _catalogDownloadInProgress
            ? $"Downloading — showing {filtered.Count} filtered model(s)."
            : $"Showing {filtered.Count} catalog model(s).";
        ScheduleFitGridColumns(CatalogGrid);
    }

    private async Task RefreshCatalogUiAsync(bool force = false)
    {
        if (!force && _catalogDownloadInProgress)
        {
            ApplyCatalogFilterInMemory();
            return;
        }

        if (!force && IsCatalogToolbarLocked)
        {
            return;
        }

        try
        {
            var search = await UiDispatcher.InvokeAsync(() => CatalogSearchBox.Text).ConfigureAwait(false);
            var category = await UiDispatcher.InvokeAsync(() => CategoryFilterCombo.SelectedItem as string)
                .ConfigureAwait(false);
            var rows = (await _svc.Registry.GetCatalogRowsAsync(search, category, _catalogDescriptionMode)
                .ConfigureAwait(false)).ToList();
            if (NaturalLanguageSearchService.LooksNaturalLanguage(search) && rows.Count > 1)
            {
                var candidates = rows.Select(r => new NlSearchCandidate(r.Name, r.Category, r.ParameterSize, r.DisplayDescription))
                    .ToList();
                EnterAiActivity();
                IReadOnlyList<string> ranked;
                try
                {
                    ranked = await _svc.NlSearch.RankModelsAsync(search, candidates).ConfigureAwait(false);
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
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        CatalogStatusLabel.Text = $"NL-ranked {ranked.Count} model(s); showing {rows.Count}.";
                        BindCatalogRows(rows);
                    }).ConfigureAwait(false);
                    return;
                }
            }

            await UiDispatcher.InvokeAsync(() =>
            {
                BindCatalogRows(rows);
                CatalogStatusLabel.Text = $"Showing {rows.Count} catalog model(s).";
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Catalog", $"RefreshCatalogUi failed: {ex.Message}");
            await UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = ex.Message).ConfigureAwait(false);
        }
    }

    private void CatalogSearchBox_KeyUp(object sender, KeyEventArgs e)
    {
        _catalogSearchTimer.Stop();
        _catalogSearchTimer.Start();
    }

    private async void CategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatalogUiEvents)
        {
            return;
        }

        try
        {
            if (_catalogDownloadInProgress)
            {
                ApplyCatalogFilterInMemory();
            }
            else if (!IsCatalogToolbarLocked)
            {
                await RefreshCatalogUiAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Catalog", $"Category filter failed: {ex.Message}");
            CatalogStatusLabel.Text = ex.Message;
        }
    }

    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        if (!TryBeginCatalogToolbarOperation(
                CatalogToolbarOperationType.RefreshCatalog, sender, out _, out var generation))
        {
            EndTaskFlashIdle(sender);
            CatalogStatusLabel.Text = "Another catalog operation is already running.";
            return;
        }

        _catalogBulkUiCounter = 0;
        CatalogStatusLabel.Text = "Refreshing from ollama.com...";
        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = _catalogOps.CreateLinkedTokenSource(workCt)!;
            var ct = linked.Token;
            try
            {
                await _svc.CatalogStore.RefreshFromWebAsync(cancellationToken: ct).ConfigureAwait(false);
                _svc.CatalogStore.ClearCache();

                await BindFullCatalogGridAsync("Refreshing catalog", cancellationToken: ct).ConfigureAwait(false);

                var progress = new Progress<string>(msg =>
                    _ = UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg));

                var installed = await _svc.Registry.GetInstalledModelNamesAsync(ct).ConfigureAwait(false);
                await _svc.CatalogStore.ProcessCatalogEntriesUiPassAsync(
                    installed,
                    progress,
                    (item, _) =>
                    {
                        UiDispatcher.InvokeAsync(() => HandleCatalogRefreshProgress(item));
                        return Task.CompletedTask;
                    },
                    ct).ConfigureAwait(false);

                var sizeProgress = new Progress<string>(msg =>
                    _ = UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg));

                await _svc.CatalogStore.EnrichFileSizesAsync(sizeProgress, cancellationToken: ct)
                    .ConfigureAwait(false);

                _svc.CatalogStore.ClearCache();
                await ApplyCatalogFileSizesToRowsAsync(ct).ConfigureAwait(false);

                await UiDispatcher.InvokeAsync(async () =>
                {
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                    _catalogRowAnimator.Stop();
                    ScrollCatalogGridToTop();
                    CatalogStatusLabel.Text = "Catalog refreshed.";
                    EndTaskFlashSuccess(sender, "Refreshed", 10, FinishCatalogRefreshHoldover);
                }).ConfigureAwait(false);
                _svc.Diagnostics.Write("Catalog", "RefreshCatalog complete.");
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = "Catalog refresh cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
                _svc.Diagnostics.Write("Catalog", "RefreshCatalog cancelled.");
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                _svc.Diagnostics.Write("Catalog", $"RefreshCatalog failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await EndCatalogToolbarOperationScopeAsync(generation).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void GetFileSizes_Click(object sender, RoutedEventArgs e)
    {
        if (TaskButton(sender) is not { } flashButton
            || !_flashButtons.TryBegin(flashButton, FlashColorScheme.YellowBlack))
        {
            return;
        }

        if (_catalogFileSizesInProgress)
        {
            EndTaskFlashIdle(sender);
            CatalogStatusLabel.Text = "Get File Sizes is already running.";
            return;
        }

        _catalogFileSizesCts?.Cancel();
        _catalogFileSizesCts = new CancellationTokenSource();
        var ct = _catalogFileSizesCts.Token;
        _catalogFileSizesInProgress = true;
        UpdateCatalogStopButtonUi();
        CatalogStatusLabel.Text = "Probing file sizes from Ollama pull metadata…";

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, ct);
            var token = linked.Token;
            try
            {
                var startup = await EnsureOllamaApiReadyAsync(token, "Get File Sizes requires Ollama API", timeoutSec: 90)
                    .ConfigureAwait(false);
                if (!startup.Success)
                {
                    throw new InvalidOperationException(startup.Message);
                }

                var progress = new Progress<string>(msg =>
                    _ = UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = msg));
                var count = await _svc.CatalogStore.ProbeMissingFileSizesAsync(_svc.ApiClient, progress, token)
                    .ConfigureAwait(false);
                _svc.CatalogStore.ClearCache();

                await UiDispatcher.InvokeAsync(async () =>
                {
                    await ApplyCatalogFileSizesToRowsAsync(token).ConfigureAwait(true);
                    await RefreshCatalogUiAsync(force: true).ConfigureAwait(true);
                    CatalogStatusLabel.Text = count > 0
                        ? $"Updated file sizes for {count} catalog model(s)."
                        : "No missing file sizes found to probe.";
                    EndTaskFlashSuccess(sender, count > 0 ? "Sized" : "Done", 10);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    CatalogStatusLabel.Text = "Get File Sizes cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _svc.Diagnostics.Write("Catalog", $"Get File Sizes failed: {ex.Message}");
                var msg = await ExplainErrorAsync(ex.Message, token).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _catalogFileSizesInProgress = false;
                    UpdateCatalogStopButtonUi();
                }).ConfigureAwait(false);
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

        if (!TryBeginCatalogToolbarOperation(
                CatalogToolbarOperationType.ClearCatalog, sender, out _, out var generation))
        {
            EndTaskFlashIdle(sender);
            CatalogStatusLabel.Text = "Another catalog operation is already running.";
            return;
        }

        CatalogStatusLabel.Text = "Clearing catalog...";

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = _catalogOps.CreateLinkedTokenSource(workCt)!;
            var ct = linked.Token;
            try
            {
                await _svc.CatalogStore.ResetStoreAsync(ct).ConfigureAwait(false);
                await _svc.Descriptions.ResetStoreAsync(ct).ConfigureAwait(false);
                await _svc.CategoryStore.ResetStoreAsync(ct).ConfigureAwait(false);
                _svc.CatalogStore.ClearCache();
                _svc.Descriptions.ClearCache();
                _svc.CategoryStore.ClearCache();

                await UiDispatcher.InvokeAsync(async () =>
                {
                    _catalogRowAnimator.Stop();
                    CatalogRowRefreshAnimator.ResetAll(_catalogRows);
                    _catalogRows.Clear();
                    _catalogRowByName = new Dictionary<string, CatalogRowViewModel>(StringComparer.OrdinalIgnoreCase);
                    InitCategoryFilter();
                    CatalogStatusLabel.Text =
                        "Catalog cleared — cached library, AI descriptions, and categories removed. Use Refresh Catalog to reload.";
                    CategoryStatusLabel.Text = "Catalog categorization: 0/0 classified";
                    _svc.ActivityLog.Write("Task",
                        "Catalog cleared: library cache, AI descriptions (model-descriptions.json), and usage categories (model-usage-categories.json).");
                    EndTaskFlashIdle(sender);
                    await RefreshCategoryStatusAsync().ConfigureAwait(true);
                }).ConfigureAwait(false);
                _svc.Diagnostics.Write("Catalog",
                    "ClearCatalog complete — library catalog, AI descriptions, and category data removed.");
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    CatalogStatusLabel.Text = "Clear catalog cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
                _svc.Diagnostics.Write("Catalog", "ClearCatalog cancelled.");
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                _svc.Diagnostics.Write("Catalog", $"ClearCatalog failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await EndCatalogToolbarOperationScopeAsync(generation).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void CategorizeAll_Click(object sender, RoutedEventArgs e) =>
        await RunClassificationAsync(
            recategorize: true,
            fromAiSettings: MainTabs.SelectedItem == AiSettingsTab,
            sender: sender,
            resetCategoriesFirst: true).ConfigureAwait(true);

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
            sender: sender,
            resetCategoriesFirst: true).ConfigureAwait(true);
    }

    private async Task RunClassificationAsync(
        bool recategorize,
        bool fromAiSettings,
        object? sender = null,
        bool resetCategoriesFirst = false)
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

        if (!TryBeginCatalogToolbarOperation(
                CatalogToolbarOperationType.Categorize, sender!, out _, out var generation))
        {
            if (sender is not null)
            {
                EndTaskFlashIdle(sender);
            }

            var busyMessage = "Another catalog operation is already running.";
            if (fromAiSettings)
            {
                SetAiSettingsActionStatus(busyMessage);
                SetAiSettingsButtonsEnabled(true);
            }
            else
            {
                CatalogStatusLabel.Text = busyMessage;
            }

            return;
        }

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = _catalogOps.CreateLinkedTokenSource(workCt)!;
            var ct = linked.Token;
            EnterAiActivity();
            int? classifiedCount = null;
            try
            {
                if (resetCategoriesFirst)
                {
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        if (fromAiSettings)
                        {
                            SetAiSettingsActionStatus("Clearing existing categories...");
                        }
                        else
                        {
                            CatalogStatusLabel.Text = "Clearing existing categories...";
                        }
                    }).ConfigureAwait(false);

                    await _svc.Classification.ResetAllCategoriesAsync(ct).ConfigureAwait(false);
                    await UiDispatcher.InvokeAsync(async () =>
                    {
                        await RefreshCategoryFilterComboAsync().ConfigureAwait(true);
                        await RefreshCatalogUiAsync(force: true).ConfigureAwait(true);
                    }).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(summarizer))
                {
                    var modeMsg = $"Applying summarizer best mode for {summarizer}...";
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        if (fromAiSettings)
                        {
                            SetAiSettingsActionStatus(modeMsg);
                        }
                        else
                        {
                            CatalogStatusLabel.Text = modeMsg;
                        }
                    }).ConfigureAwait(false);
                    await _svc.SummarizerInference.ApplySummarizerBestModeAsync(summarizer, ct)
                        .ConfigureAwait(false);
                    await SyncComputeModeDisplayAsync().ConfigureAwait(false);
                }

                var progress = new Progress<string>(msg =>
                {
                    UiDispatcher.InvokeFireAndForget(() =>
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
                _svc.Diagnostics.Write("Catalog", $"Categorize complete ({count} model(s)).");
                classifiedCount = count;
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
                _svc.Diagnostics.Write("Catalog", "Categorize cancelled.");
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                _svc.Diagnostics.Write("Catalog", $"Categorize failed: {ex.Message}");
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
                await EndCatalogToolbarOperationScopeAsync(generation).ConfigureAwait(false);

                if (fromAiSettings)
                {
                    await UiDispatcher.InvokeAsync(() => SetAiSettingsButtonsEnabled(true)).ConfigureAwait(false);
                }
            }

            if (classifiedCount is int classifiedTotal)
            {
                await UiDispatcher.InvokeAsync(async () =>
                {
                    _svc.CategoryStore.ClearCache();
                    _svc.CatalogStore.ClearCache();

                    var doneMessage = classifiedTotal == 0
                        ? "No models in catalog to classify."
                        : $"Done — AI classified {classifiedTotal} catalog model(s).";
                    if (fromAiSettings)
                    {
                        SetAiSettingsActionStatus(doneMessage);
                    }
                    else
                    {
                        CatalogStatusLabel.Text = doneMessage;
                    }

                    await RefreshCategoryFilterComboAsync().ConfigureAwait(true);
                    await RefreshCatalogUiAsync(force: true).ConfigureAwait(true);
                    await RefreshCategoryStatusAsync().ConfigureAwait(true);
                    if (sender is not null)
                    {
                        EndTaskFlashSuccess(sender, "Categorized", 10);
                    }
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private void CatalogGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCatalogToolbarLocked)
        {
            return;
        }

        if (e.ClickCount == 2
            && FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject) is { Column: { Header: var header } }
            && header is string headerText
            && (headerText.Equals("Fastest Mode", StringComparison.OrdinalIgnoreCase)
                || headerText.Equals("Best Metric", StringComparison.OrdinalIgnoreCase))
            && CatalogGrid.SelectedItem is CatalogRowViewModel catalogRow)
        {
            NavigateToTestResultRow(catalogRow.Name);
            e.Handled = true;
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
            _svc.ApiClient.InvalidateCaches();
            var warmed = await _svc.ApiClient.GetTagsAsync(
                timeoutSec: 15, forceRefresh: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            _svc.Diagnostics.Write("Ollama", $"API ready; warmed tags cache ({warmed.Count} model(s))");
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
            _svc.Diagnostics.Write("Ollama", $"API not ready: {startup.Message}");
            return (false, startup.Message);
        }

        _svc.Diagnostics.Write("Ollama", "API ready");
        return (true, startup.Message);
    }

    private void BeginCatalogDownloadProgressUi(string model, string status)
    {
        CatalogDownloadProgressPanel.Visibility = Visibility.Visible;
        CatalogDownloadProgress.Value = 0;
        CatalogDownloadProgressLabel.Text = $"{model} — {status}";
        CatalogStatusLabel.Text = CatalogDownloadProgressLabel.Text;
        UpdateCatalogDownloadButtonUi();
    }

    private void ShowCatalogDownloadProgress(string model, ModelPullProgress update)
    {
        CatalogDownloadProgressPanel.Visibility = Visibility.Visible;
        if (update.Percent is int percent)
        {
            CatalogDownloadProgress.Value = percent;
        }

        var detail = FormatDownloadProgressStatus(update);
        CatalogDownloadProgressLabel.Text = $"{model} — {detail}";
        CatalogStatusLabel.Text = CatalogDownloadProgressLabel.Text;
    }

    private void HideCatalogDownloadProgress()
    {
        CatalogDownloadProgressPanel.Visibility = Visibility.Collapsed;
        CatalogDownloadProgress.Value = 0;
        CatalogDownloadProgressLabel.Text = string.Empty;
        UpdateCatalogDownloadButtonUi();
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

    private void CatalogStopDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCatalogDownloadActive)
        {
            return;
        }

        _catalogDownloadCts?.Cancel();
        _svc.Diagnostics.Write("Catalog", "STOP download requested.");
        CatalogStatusLabel.Text = "Stopping download…";
    }

    private async void DownloadCatalogModel_Click(object sender, RoutedEventArgs e)
    {
        var rows = CatalogGrid.SelectedItems.Cast<CatalogRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("Select a catalog model first.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryBlockDownloadIfTestActive())
        {
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

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, ct);
            var token = linked.Token;
            _catalogDownloadInProgress = true;
            var downloaded = 0;

            await UiDispatcher.InvokeAsync(UpdateCatalogDownloadButtonUi).ConfigureAwait(false);

            try
            {
                var startup = await EnsureOllamaApiReadyAsync(token, "Cannot download — Ollama API not ready", timeoutSec: 90)
                    .ConfigureAwait(false);
                if (!startup.Success)
                {
                    throw new InvalidOperationException(startup.Message);
                }

                for (var i = 0; i < rows.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = rows[i];
                    var resolution = !string.IsNullOrWhiteSpace(row.DefaultPullTag)
                        ? new CatalogPullResolution
                        {
                            PullTag = row.DefaultPullTag,
                            Resolved = true,
                            IsCloudOnly = row.IsCloudOnly
                        }
                        : await _svc.CatalogStore.ResolvePullTagAsync(row.Name, token).ConfigureAwait(false);

                    if (!resolution.Resolved)
                    {
                        var missingTagMessage =
                            $"{row.Name} has no downloadable tag on ollama.com. Try Refresh Catalog.";
                        _svc.Diagnostics.Write("Catalog", $"Download skipped: {missingTagMessage}");
                        await UiDispatcher.InvokeAsync(() => CatalogStatusLabel.Text = missingTagMessage)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var pullTag = resolution.PullTag;
                    if (resolution.IsCloudOnly)
                    {
                        var proceed = await UiDispatcher.InvokeAsync(() =>
                            MessageBox.Show(
                                $"{pullTag} is a cloud model (runs via Ollama cloud, not a local weight download). Continue?",
                                "Cloud model",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question) == MessageBoxResult.Yes).ConfigureAwait(false);
                        if (!proceed)
                        {
                            continue;
                        }
                    }

                    _svc.Diagnostics.Write("Catalog", $"Download started: {pullTag}");
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        BeginCatalogDownloadRow(row);
                        BeginCatalogDownloadProgressUi(
                            pullTag,
                            rows.Count > 1
                                ? $"Preparing download {i + 1}/{rows.Count}…"
                                : "Preparing download…");
                    }).ConfigureAwait(false);

                    await UiDispatcher.InvokeAsync(() =>
                        BeginCatalogDownloadProgressUi(pullTag, "Downloading…")).ConfigureAwait(false);

                    var progress = new Progress<ModelPullProgress>(update =>
                    {
                        _ = UiDispatcher.InvokeAsync(() =>
                        {
                            ShowCatalogDownloadProgress(pullTag, update);
                            _svc.ActivityLog.Write("Download", update.Status);
                        });
                    });

                    await _svc.ApiClient.PullAsync(pullTag, progress, token).ConfigureAwait(false);
                    _svc.ApiClient.InvalidateCaches();
                    _svc.Profiles.ClearCache();

                    var installed = await _svc.ApiClient.IsModelInstalledAsync(pullTag, token).ConfigureAwait(false);
                    if (!installed)
                    {
                        throw new InvalidOperationException(
                            $"Download finished but {pullTag} was not found in the local Ollama model list.");
                    }

                    downloaded++;
                    _svc.ActivityLog.Write("Task", $"Downloaded {pullTag}.");
                    await UiDispatcher.InvokeAsync(async () =>
                    {
                        EndCatalogDownloadRow(row, installed: true);
                        if (i == rows.Count - 1)
                        {
                            HideCatalogDownloadProgress();
                        }

                        CatalogStatusLabel.Text = rows.Count > 1
                            ? $"Downloaded {downloaded}/{rows.Count} model(s). Last: {pullTag}."
                            : $"Downloaded {pullTag}.";
                        await RefreshModelsUiAsync().ConfigureAwait(true);
                    }).ConfigureAwait(false);
                }

                await UiDispatcher.InvokeAsync(() =>
                {
                    HideCatalogDownloadProgress();
                    if (downloaded > 0)
                    {
                        EndTaskFlashSuccess(sender, "Downloaded", 10);
                    }
                    else
                    {
                        EndTaskFlashIdle(sender);
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    foreach (var row in rows)
                    {
                        EndCatalogDownloadRow(row, row.Installed);
                    }

                    HideCatalogDownloadProgress();
                    CatalogStatusLabel.Text = "Download cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _svc.Diagnostics.Write("Catalog", $"Download failed: {ex.Message}");
                var msg = await ExplainErrorAsync(ex.Message, token).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    foreach (var row in rows)
                    {
                        EndCatalogDownloadRow(row, row.Installed);
                    }

                    HideCatalogDownloadProgress();
                    CatalogStatusLabel.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                _catalogDownloadInProgress = false;
                await UiDispatcher.InvokeAsync(UpdateCatalogDownloadButtonUi).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void UninstallCatalogModel_Click(object sender, RoutedEventArgs e)
    {
        try
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
                    var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        CatalogStatusLabel.Text = msg;
                        EndTaskFlashIdle(sender);
                    }).ConfigureAwait(false);
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Catalog", $"Uninstall failed: {ex.Message}");
            CatalogStatusLabel.Text = ex.Message;
        }
    }

    private async Task<string> FormatInsightColumnAsync(string? insight, TestResultRowViewModel? row = null)
    {
        if (row is not null)
        {
            var failedModes = new List<string>();
            if (row.CpuFailed)
            {
                failedModes.Add("CPU");
            }

            if (row.ApuFailed)
            {
                failedModes.Add("APU");
            }

            if (row.GpuFailed)
            {
                failedModes.Add("GPU");
            }

            if (row.HybridFailed)
            {
                failedModes.Add("Hybrid");
            }

            if (failedModes.Count > 0 && string.IsNullOrWhiteSpace(insight))
            {
                return $"{failedModes.Count}/4 failed — {string.Join(", ", failedModes)}";
            }
        }

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
        _svc.Profiles.ClearCache();
        var rows = await _svc.Registry.GetTestResultRowsAsync().ConfigureAwait(true);
        var enriched = new List<TestResultRowViewModel>();
        foreach (var row in rows)
        {
            var insight = await _svc.BenchmarkInsights.GetInsightAsync(row.Model).ConfigureAwait(true);
            var insightText = await FormatInsightColumnAsync(insight, row).ConfigureAwait(true);
            enriched.Add(new TestResultRowViewModel
            {
                Model = row.Model,
                Category = row.Category ?? string.Empty,
                BenchmarkKind = string.IsNullOrWhiteSpace(row.BenchmarkKind)
                    ? BenchmarkKinds.Generate
                    : row.BenchmarkKind,
                BestMode = row.BestMode ?? string.Empty,
                BestTps = row.BestTps,
                BestEmbedMs = row.BestEmbedMs,
                CpuResult = row.CpuResult ?? "-",
                ApuResult = row.ApuResult ?? "-",
                GpuResult = row.GpuResult ?? "-",
                HybridResult = row.HybridResult ?? "-",
                CpuFailed = row.CpuFailed,
                ApuFailed = row.ApuFailed,
                GpuFailed = row.GpuFailed,
                HybridFailed = row.HybridFailed,
                IsInstalledLocally = row.IsInstalledLocally,
                StatusDisplay = row.StatusDisplay ?? string.Empty,
                IsAllModesFailed = row.IsAllModesFailed,
                Insight = insightText ?? string.Empty,
                LastTested = row.LastTested ?? string.Empty,
                ReportPath = row.ReportPath
            });
        }

        _testResultsAllRows = enriched;
        await RefreshTestResultsCategoryFilterComboAsync().ConfigureAwait(true);
        ApplyTestResultsFilter();
    }

    private async Task RefreshTestResultsCategoryFilterComboAsync()
    {
        var present = _testResultsAllRows
            .Select(r => r.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(CategoryNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(CategoryNormalizer.GetSortOrder)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<string> { "All" };
        items.AddRange(present);

        await UiDispatcher.InvokeAsync(() =>
        {
            var previous = TestResultsCategoryFilterCombo.SelectedItem as string;
            _suppressTestResultsUiEvents = true;
            try
            {
                TestResultsCategoryFilterCombo.ItemsSource = items;
                if (!string.IsNullOrWhiteSpace(previous)
                    && items.Contains(previous, StringComparer.OrdinalIgnoreCase))
                {
                    TestResultsCategoryFilterCombo.SelectedItem = previous;
                }
                else
                {
                    TestResultsCategoryFilterCombo.SelectedIndex = 0;
                }
            }
            finally
            {
                _suppressTestResultsUiEvents = false;
            }
        }).ConfigureAwait(true);
    }

    private void ApplyTestResultsFilter()
    {
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Invoke(ApplyTestResultsFilterCore);
            return;
        }

        ApplyTestResultsFilterCore();
    }

    private static bool TestResultFieldMatches(string? value, string search) =>
        (value ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase);

    private void ApplyTestResultsFilterCore()
    {
        var selectedModel = TestResultsGrid.SelectedItem is TestResultRowViewModel selected
            ? selected.Model
            : null;
        var search = TestResultsSearchBox.Text?.Trim() ?? string.Empty;
        var category = TestResultsCategoryFilterCombo.SelectedItem as string ?? "All";

        var filtered = _testResultsAllRows.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(r =>
                TestResultFieldMatches(r.Model, search)
                || TestResultFieldMatches(r.Category, search)
                || TestResultFieldMatches(r.Insight, search)
                || TestResultFieldMatches(r.BestMode, search)
                || TestResultFieldMatches(r.CpuResult, search)
                || TestResultFieldMatches(r.ApuResult, search)
                || TestResultFieldMatches(r.GpuResult, search)
                || TestResultFieldMatches(r.HybridResult, search)
                || TestResultFieldMatches(r.StatusDisplay, search));
        }

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(r =>
                (r.Category ?? string.Empty).Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered.ToList();
        _suppressTestResultsSelectionEvents = true;
        try
        {
            TestResultsGrid.ItemsSource = rows;
            if (!string.IsNullOrEmpty(selectedModel))
            {
                var match = rows.FirstOrDefault(r =>
                    r.Model.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    TestResultsGrid.SelectedItem = match;
                }
            }
        }
        finally
        {
            _suppressTestResultsSelectionEvents = false;
        }

        SafeUpdateTestResultsActionButtons();
        ScheduleFitGridColumns(TestResultsGrid);
    }

    private void SafeUpdateTestResultsActionButtons()
    {
        try
        {
            UpdateTestResultsActionButtons();
        }
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("TestResults", $"Action button update failed: {ex.Message}");
        }
    }

    private void UpdateTestResultsActionButtons()
    {
        if (TestResultsGrid.SelectedItem is not TestResultRowViewModel row)
        {
            DownloadFromResultsBtn.IsEnabled = false;
            LaunchFromResultsBtn.IsEnabled = false;
            ClearSelectedTestResultBtn.IsEnabled = false;
            return;
        }

        ClearSelectedTestResultBtn.IsEnabled = true;
        var downloadBlocked = _testResultsDownloadInProgress
            || IsBenchmarkQueueRunning()
            || IsCatalogDownloadActive;
        if (row.IsInstalledLocally)
        {
            DownloadFromResultsBtn.IsEnabled = false;
            LaunchFromResultsBtn.IsEnabled = !row.IsAllModesFailed
                && !string.IsNullOrWhiteSpace(row.BestMode)
                && !row.BestMode.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                && !downloadBlocked;
        }
        else
        {
            LaunchFromResultsBtn.IsEnabled = false;
            DownloadFromResultsBtn.IsEnabled = !downloadBlocked && !IsBenchmarkQueueRunning();
        }
    }

    private void TestResultsSearchBox_KeyUp(object sender, KeyEventArgs e)
    {
        _testResultsSearchTimer.Stop();
        _testResultsSearchTimer.Start();
    }

    private void TestResultsCategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTestResultsUiEvents)
        {
            return;
        }

        ApplyTestResultsFilter();
    }

    private void NavigateToTestResultRow(string libraryOrModelName)
    {
        MainTabs.SelectedItem = TestResultsTab;
        var match = _testResultsAllRows.FirstOrDefault(r =>
            r.Model.Equals(libraryOrModelName, StringComparison.OrdinalIgnoreCase)
            || r.Model.StartsWith($"{libraryOrModelName}:", StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        TestResultsSearchBox.Text = libraryOrModelName.Split(':')[0];
        ApplyTestResultsFilter();
        TestResultsGrid.SelectedItem = TestResultsGrid.Items
            .Cast<object>()
            .OfType<TestResultRowViewModel>()
            .FirstOrDefault(r => r.Model.Equals(match.Model, StringComparison.OrdinalIgnoreCase));
        if (TestResultsGrid.SelectedItem is TestResultRowViewModel selected)
        {
            TestResultsGrid.ScrollIntoView(selected);
            SafeUpdateTestResultsActionButtons();
        }
    }

    private async void DownloadFromResults_Click(object sender, RoutedEventArgs e)
    {
        if (TestResultsGrid.SelectedItem is not TestResultRowViewModel row)
        {
            MessageBox.Show("Select a test result row first.", "Download Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.IsInstalledLocally)
        {
            MessageBox.Show($"{row.Model} is already installed locally.", "Download Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryBlockDownloadIfTestActive())
        {
            return;
        }

        if (_testResultsDownloadInProgress)
        {
            MessageBox.Show("Another Test Results download is already in progress.", "Download Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsCatalogDownloadActive)
        {
            MessageBox.Show(
                "A Model Library download is in progress. Wait or click Stop Download before downloading from Test Results.",
                "Download Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        var libraryName = row.Model.Split(':')[0];
        _testResultsDownloadInProgress = true;
        SafeUpdateTestResultsActionButtons();

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                var startup = await EnsureOllamaApiReadyAsync(ct, "Cannot download — Ollama API not ready", timeoutSec: 90)
                    .ConfigureAwait(false);
                if (!startup.Success)
                {
                    throw new InvalidOperationException(startup.Message);
                }

                var resolution = await _svc.CatalogStore.ResolvePullTagAsync(libraryName, ct).ConfigureAwait(false);
                if (!resolution.Resolved)
                {
                    throw new InvalidOperationException(
                        $"{libraryName} has no downloadable tag on ollama.com. Try Refresh Catalog.");
                }

                var pullTag = resolution.PullTag;
                var progress = new Progress<ModelPullProgress>(update =>
                    _ = UiDispatcher.InvokeAsync(() => TestResultDetail.Text = $"Downloading {pullTag}: {update.Status}"));

                await _svc.ApiClient.PullAsync(pullTag, progress, ct).ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                _svc.Profiles.ClearCache();

                await UiDispatcher.InvokeAsync(async () =>
                {
                    TestResultDetail.Text = $"Downloaded {pullTag}.";
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    await RefreshTestResultsUiAsync().ConfigureAwait(true);
                    await RefreshCatalogUiAsync(force: true).ConfigureAwait(true);
                    EndTaskFlashSuccess(sender, "Downloaded", 10);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    TestResultDetail.Text = "Download cancelled.";
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    TestResultDetail.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _testResultsDownloadInProgress = false;
                    SafeUpdateTestResultsActionButtons();
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void ClearSelectedTestResult_Click(object sender, RoutedEventArgs e)
    {
        if (TestResultsGrid.SelectedItem is not TestResultRowViewModel row)
        {
            MessageBox.Show("Select a test result row first.", "Clear Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message =
            $"Clear all test data for '{row.Model}'? Benchmark results, AI insights, and report files "
            + "for this model will be removed and cannot be restored.";

        if (!ToolkitConfirmDialog.ShowAccept(this, message, "Clear Test Data"))
        {
            return;
        }

        if (!BeginTaskFlash(sender))
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
                await RefreshCatalogUiAsync(force: true).ConfigureAwait(true);
                EndTaskFlashSuccess(sender, "Cleared", 8);
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
            EndTaskFlashIdle(sender);
        }
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

    private void TestResultsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { } gridRow
            || gridRow.Item is not TestResultRowViewModel item)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        TestResultsGrid.SelectedItem = item;
    }

    private void TestResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTestResultsSelectionEvents)
        {
            return;
        }

        if (TestResultsGrid.SelectedItem is not TestResultRowViewModel row)
        {
            _testResultsDetailCts?.Cancel();
            TestResultDetail.Text = string.Empty;
            SafeUpdateTestResultsActionButtons();
            return;
        }

        SafeUpdateTestResultsActionButtons();
        TestResultDetail.Text = string.IsNullOrWhiteSpace(row.Insight)
            ? $"No AI insight for {row.Model} yet."
            : row.Insight;

        _ = LoadTestResultDetailAsync(row.Model);
    }

    private async Task LoadTestResultDetailAsync(string model)
    {
        _testResultsDetailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _testResultsDetailCts = cts;
        var token = cts.Token;

        try
        {
            var entry = await _svc.BenchmarkInsights.GetEntryAsync(model, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (TestResultsGrid.SelectedItem is not TestResultRowViewModel selected
                || !selected.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (entry is null)
            {
                var placeholder = await FormatInsightColumnAsync(null).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                TestResultDetail.Text = string.IsNullOrWhiteSpace(placeholder)
                    ? $"No AI insight for {model} yet."
                    : placeholder;
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.Interpretation)
                && entry.FailureDiagnosis is not { Count: > 0 })
            {
                TestResultDetail.Text = await FormatInsightColumnAsync(entry.Interpretation).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(TestResultDetail.Text))
                {
                    TestResultDetail.Text = $"No AI insight for {model} yet.";
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

            if (!token.IsCancellationRequested)
            {
                TestResultDetail.Text = detail.ToString().TrimEnd();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _svc.Diagnostics.Write("TestResults", $"Detail load failed: {ex}");
                TestResultDetail.Text = ex.Message;
            }
        }
    }

    private async void LaunchFromResults_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBlockWorkloadIfDownloadActive("launching a model"))
        {
            return;
        }

        var model = GetSelectedTestResultModel();
        if (model is null)
        {
            if (TestResultsGrid.SelectedItem is null)
            {
                TestResultDetail.Text = "Select a model row in Test Results, then click Launch Selected.";
                MessageBox.Show("No model selected.", "Launch Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        if (TestResultsGrid.SelectedItem is TestResultRowViewModel selectedRow
            && (selectedRow.IsAllModesFailed
                || string.Equals(selectedRow.BestMode, "FAILED", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"Model '{model.Model}' failed all benchmark modes. Retest before launching.", "Launch Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (model.NeedsRetest || string.IsNullOrEmpty(model.BestMode))
        {
            MessageBox.Show($"Model '{model.Model}' has no benchmark profile. Run a test first.", "Launch Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Enum.TryParse<ComputeMode>(model.BestMode, out var mode))
        {
            MessageBox.Show($"Model '{model.Model}' has an unsupported best mode.", "Launch Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
                await ApplyLaunchParallelAndModeAsync(model, mode, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    EndTaskFlashSuccess(sender, "Launched");
                    TestResultDetail.Text = $"Launched {model.Model} in {model.BestMode} mode.";
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    ModelRunStatus.Text = msg;
                    TestResultDetail.Text = msg;
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private ModelProfileSummary? GetSelectedTestResultModel()
    {
        TestResultRowViewModel? row = TestResultsGrid.SelectedItem as TestResultRowViewModel;
        if (row is null && TestResultsGrid.Items.Count > 0 && TestResultsGrid.SelectedIndex < 0)
        {
            TestResultsGrid.SelectedIndex = 0;
            row = TestResultsGrid.SelectedItem as TestResultRowViewModel;
        }

        if (row is null)
        {
            return null;
        }

        if (!row.IsInstalledLocally)
        {
            TestResultDetail.Text =
                $"'{row.Model}' is not installed locally. Download it from Model Library before launching.";
            MessageBox.Show(
                $"Model '{row.Model}' is not installed locally. Download it from the Model Library first.",
                "Launch Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        if (ModelsGrid.ItemsSource is IEnumerable<ModelLaunchRowViewModel> modelRows)
        {
            var match = modelRows.FirstOrDefault(m =>
                m.Model.Equals(row.Model, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.ToSummary();
            }
        }

        TestResultDetail.Text = $"Installed profile for '{row.Model}' was not found. Try Refresh on Models & Launch.";
        MessageBox.Show(
            $"Could not find an installed profile for '{row.Model}'.",
            "Launch Selected",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return null;
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
                await ApplyLaunchParallelAndModeAsync(model, mode, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() => ModelRunStatus.Text = msg).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task ApplyLaunchParallelAndModeAsync(
        ModelProfileSummary model,
        ComputeMode mode,
        CancellationToken cancellationToken)
    {
        var parallelByMode = await ResolveLaunchParallelByModeAsync(model.Model, cancellationToken)
            .ConfigureAwait(false);
        var parallel = BenchmarkParallelSettings.GetParallel(mode, parallelByMode);
        _svc.ServerTuning.ApplyParallelForMode(mode, parallelByMode);
        await _svc.ModeService.ApplyModeWithRestartAsync(mode, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _svc.ApiClient.InvalidateCaches();
        await _svc.ModelSessions.SwitchToModelAsync(model.Model, warmLoad: true, cancellationToken)
            .ConfigureAwait(false);
        await UiDispatcher.InvokeAsync(() =>
        {
            ModelRunStatus.Text =
                $"{model.Model} | {model.BestMode} ({model.BestMetricDisplay}) | parallel={parallel} | Ollama restarted, model loaded";
            ChatHistory.Text = string.Empty;
            _chatMessages.Clear();
            UpdateFooterComputeModeLabel();
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, int>?> ResolveLaunchParallelByModeAsync(
        string model,
        CancellationToken cancellationToken)
    {
        var store = await _svc.Profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (store.Models.TryGetValue(model, out var profile)
            && profile.NumParallelByMode is { Count: > 0 })
        {
            return profile.NumParallelByMode;
        }

        var cached = await _svc.BenchmarkSettingsAdvisor.GetCachedAsync(model, cancellationToken)
            .ConfigureAwait(false);
        return cached?.NumParallelByMode;
    }

    private sealed record AiSettingsRefreshState(
        bool OllamaReady,
        SummarizerUiState SummarizerState,
        int Classified,
        int Total,
        AiLlmRecommendationsDocument? Recommendations);

    private void BindSummarizerComboImmediate()
    {
        var settings = _svc.AiSettings.LoadAsync().GetAwaiter().GetResult();
        var choices = SummarizerModelResolver.BuildDefaultChoices(settings.PreferredSummarizerModel);
        ApplySummarizerComboBinding(choices, settings.PreferredSummarizerModel);
        AiSettingsStatus.Text = "Loading summarizer list...";
    }

    private void ApplySummarizerComboBinding(SummarizerModelChoices choices, string? preferredModel)
    {
        var selected = ResolveSummarizerComboSelection(choices, preferredModel);
        var models = choices.Models.ToList();
        if (selected is not null
            && !models.Any(m => m.Equals(selected, StringComparison.OrdinalIgnoreCase)))
        {
            models.Insert(0, selected);
        }

        _suppressSummarizerComboSave = true;
        try
        {
            if (!SummarizerComboListsEqual(SummarizerCombo.ItemsSource as IList<string>, models))
            {
                SummarizerCombo.ItemsSource = models;
            }

            if (selected is not null && !Equals(SummarizerCombo.SelectedItem, selected))
            {
                SummarizerCombo.SelectedItem = selected;
            }
        }
        finally
        {
            _suppressSummarizerComboSave = false;
        }
    }

    private static string? ResolveSummarizerComboSelection(
        SummarizerModelChoices choices,
        string? preferredModel)
    {
        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            var saved = choices.Models.FirstOrDefault(m =>
                m.Equals(preferredModel, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                return saved;
            }

            return preferredModel;
        }

        return choices.Models.FirstOrDefault(m => choices.InstalledNames.Contains(m))
               ?? choices.Models.FirstOrDefault();
    }

    private static bool SummarizerComboListsEqual(IList<string>? current, IReadOnlyList<string> next)
    {
        if (current is null)
        {
            return false;
        }

        if (current.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i], next[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task RefreshAiSettingsUiAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _aiSettingsRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = Interlocked.Increment(ref _aiSettingsRefreshGeneration);
        try
        {
            var state = await FetchAiSettingsStateAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            if (generation != _aiSettingsRefreshGeneration)
            {
                return;
            }

            await ApplyAiSettingsStateToUiAsync(state).ConfigureAwait(false);
        }
        finally
        {
            _aiSettingsRefreshGate.Release();
        }
    }

    private async Task<AiSettingsRefreshState> FetchAiSettingsStateAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var ready = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(false);
        var summarizerState = await _svc.Summarizer.FetchUiStateAsync(forceRefresh, cancellationToken)
            .ConfigureAwait(false);
        var entries = await _svc.CatalogStore.GetEntriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var (classified, total) = await _svc.CategoryStore.GetStatusAsync(entries.Count, cancellationToken)
            .ConfigureAwait(false);
        var recommendations = await _svc.AiLlmRecommendations.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new AiSettingsRefreshState(ready, summarizerState, classified, total, recommendations);
    }

    private async Task ApplyAiSettingsStateToUiAsync(AiSettingsRefreshState state)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            var summarizerState = state.SummarizerState;
            var choices = summarizerState.Choices;
            var settings = summarizerState.Settings;
            var summarizer = summarizerState.ActiveSummarizer;
            var installedCount = choices.Models.Count(m => choices.InstalledNames.Contains(m));
            var preferredInstalled = !string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel)
                                     && choices.InstalledNames.Contains(settings.PreferredSummarizerModel);

            AiSettingsStatus.Text = state.OllamaReady || summarizerState.HasInstalledTags
                ? summarizer is not null
                    ? $"AI Active — summarizer: {summarizer} ({installedCount} installed choice(s))"
                    : !string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel) && !preferredInstalled
                        ? $"AI Inactive — preferred summarizer {settings.PreferredSummarizerModel} not installed (pull from Model Library)"
                        : choices.Models.Count > 0
                            ? $"AI Inactive — pick a summarizer ({installedCount}/{choices.Models.Count} installed)"
                            : "AI Inactive — no summarizer models available"
                : "AI Inactive — Ollama API not reachable";

            ApplySummarizerComboBinding(choices, settings.PreferredSummarizerModel);
            CategoryStatusLabel.Text = $"Catalog categorization: {state.Classified}/{state.Total} classified";
            BindAiRecommendationsDocument(state.Recommendations);
        }).ConfigureAwait(false);
    }

    private async Task RefreshCategoryStatusAsync()
    {
        var entries = await _svc.CatalogStore.GetEntriesAsync().ConfigureAwait(true);
        var (classified, total) = await _svc.CategoryStore.GetStatusAsync(entries.Count).ConfigureAwait(true);
        CategoryStatusLabel.Text = $"Catalog categorization: {classified}/{total} classified";
    }

    private void SetAiRecommendationsActionStatus(string text) =>
        AiRecommendationsActionStatus.Text = text;

    private async Task BindAiRecommendationsFromCacheAsync()
    {
        var doc = await _svc.AiLlmRecommendations.LoadAsync().ConfigureAwait(true);
        BindAiRecommendationsDocument(doc);
    }

    private void BindAiRecommendationsDocument(AiLlmRecommendationsDocument? doc)
    {
        if (doc is null || (doc.Installed.Count == 0 && doc.Uninstalled.Count == 0))
        {
            InstalledRecommendedGrid.ItemsSource = null;
            UninstalledRecommendedGrid.ItemsSource = null;
            AiRecommendationsStatusLabel.Text =
                "No recommendation lists generated yet. Click Generate Recommendations first.";
            return;
        }

        var installedRows = doc.Installed
            .Select(e => AiRecommendedLlmRowViewModel.FromEntry(e, installed: true))
            .ToList();
        var uninstalledRows = doc.Uninstalled
            .Select(e => AiRecommendedLlmRowViewModel.FromEntry(e, installed: false))
            .ToList();
        InstalledRecommendedGrid.ItemsSource = installedRows;
        UninstalledRecommendedGrid.ItemsSource = uninstalledRows;
        if (installedRows.Count > 0)
        {
            InstalledRecommendedGrid.SelectedIndex = 0;
        }

        if (uninstalledRows.Count > 0 && InstalledRecommendedGrid.SelectedIndex < 0)
        {
            UninstalledRecommendedGrid.SelectedIndex = 0;
        }

        AiRecommendationsStatusLabel.Text =
            $"Last updated {doc.GeneratedAt} — {doc.Installed.Count} installed, {doc.Uninstalled.Count} uninstalled"
            + (string.IsNullOrWhiteSpace(doc.SummaryModel) ? string.Empty : $" (via {doc.SummaryModel})");
    }

    private async void GenerateAiRecommendations_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginTaskFlashWithFeedback(sender, SetAiRecommendationsActionStatus))
        {
            return;
        }

        _generateRecommendationsFlashSender = sender;
        SetAiRecommendationsActionStatus("Starting recommendation pipeline...");
        SetAiGenerateBusy(true);
        _aiRecommendationsCts?.Cancel();
        _aiRecommendationsCts?.Dispose();
        _aiRecommendationsCts = new CancellationTokenSource();

        await _svc.WorkQueue.EnqueueAsync(async _ =>
        {
            var ct = _aiRecommendationsCts!.Token;
            EnterAiActivity();
            try
            {
                if (!await ValidateAiRecommendationsPreflightAsync(ct).ConfigureAwait(false))
                {
                    await FinishGenerateRecommendationsAsync(cancelled: false, failed: true).ConfigureAwait(false);
                    return;
                }

                var summarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summarizer))
                {
                    SetAiRecommendationsActionStatusOnUi(
                        $"Applying summarizer best mode for {summarizer}...");
                    await _svc.SummarizerInference.ApplySummarizerBestModeAsync(summarizer, ct)
                        .ConfigureAwait(false);
                    UpdateFooterComputeModeLabel();
                }

                var entries = await _svc.CatalogStore.GetEntriesAsync(cancellationToken: ct)
                    .ConfigureAwait(false);
                var (classified, total) = await _svc.CategoryStore.GetStatusAsync(entries.Count, ct)
                    .ConfigureAwait(false);

                if (classified < total)
                {
                    _svc.Diagnostics.Write("AI",
                        $"Generate recommendations: categorizing catalog ({classified}/{total} classified).");
                    SetAiRecommendationsActionStatusOnUi(
                        $"Catalog incomplete — categorizing {total - classified} remaining model(s)...");
                    if (!await RunClassificationCoreAsync(recategorize: false, ct, useRecommendationsStatus: true)
                            .ConfigureAwait(false))
                    {
                        await FinishGenerateRecommendationsAsync(cancelled: false, failed: true).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    _svc.Diagnostics.Write("AI",
                        "Generate recommendations: catalog fully classified — skipping full recategorize.");
                    SetAiRecommendationsActionStatusOnUi("Using existing catalog categories...");
                }

                ct.ThrowIfCancellationRequested();
                SetAiRecommendationsActionStatusOnUi("Generating AI recommendation lists...");
                var intent = await UiDispatcher.InvokeAsync(() => AiRecommendationsIntentBox.Text?.Trim())
                    .ConfigureAwait(false);
                var summaries = await _svc.Profiles.GetAllSummariesAsync(ct).ConfigureAwait(false);
                var categories = await GetCategoryMapAsync().ConfigureAwait(false);
                var tags = await _svc.ApiClient.GetTagsAsync(cancellationToken: ct).ConfigureAwait(false);
                var installedNames = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var doc = await _svc.AiLlmRecommendations.GenerateAsync(
                    intent,
                    summaries,
                    entries,
                    categories,
                    installedNames,
                    ct).ConfigureAwait(false);

                _svc.ActivityLog.Write("AI",
                    $"LLM recommendations: {doc.Installed.Count} installed, {doc.Uninstalled.Count} uninstalled.");
                _svc.Diagnostics.Write("AI",
                    $"Generate recommendations complete: {doc.Installed.Count} installed, {doc.Uninstalled.Count} uninstalled.");
                await UiDispatcher.InvokeAsync(async () =>
                {
                    BindAiRecommendationsDocument(doc);
                    SetAiRecommendationsActionStatus(
                        $"Done — {doc.Installed.Count} installed and {doc.Uninstalled.Count} uninstalled recommendations.");
                    await RefreshCategoryStatusAsync().ConfigureAwait(true);
                    if (_generateRecommendationsFlashSender is not null)
                    {
                        EndTaskFlashSuccess(_generateRecommendationsFlashSender, "Generated", 10);
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _svc.Diagnostics.Write("AI", "Generate recommendations cancelled.");
                await FinishGenerateRecommendationsAsync(cancelled: true, failed: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                _svc.Diagnostics.Write("AI", $"Generate recommendations failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() => SetAiRecommendationsActionStatus(msg)).ConfigureAwait(false);
                await FinishGenerateRecommendationsAsync(cancelled: false, failed: true).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
                await UiDispatcher.InvokeAsync(() =>
                {
                    SetAiGenerateBusy(false);
                    _generateRecommendationsFlashSender = null;
                }).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async Task FinishGenerateRecommendationsAsync(bool cancelled, bool failed)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            if (cancelled)
            {
                SetAiRecommendationsActionStatus("Recommendation generation cancelled.");
            }

            if (failed)
            {
                SetAiGenerateBusy(false);
            }

            if (_generateRecommendationsFlashSender is not null)
            {
                EndTaskFlashIdle(_generateRecommendationsFlashSender);
            }
        }).ConfigureAwait(false);
    }

    private async Task<bool> ValidateAiRecommendationsPreflightAsync(CancellationToken cancellationToken)
    {
        var settings = await _svc.AiSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.ToolkitAiEnabled)
        {
            SetAiRecommendationsActionStatusOnUi("Enable Toolkit AI in AI Features to generate recommendations.");
            return false;
        }

        if (!await _svc.AiSettings.IsFeatureEnabledAsync(AiFeatureKeys.ModelPickerAdvisor, cancellationToken)
                .ConfigureAwait(false))
        {
            SetAiRecommendationsActionStatusOnUi("Enable Model picker advisor in AI Features.");
            return false;
        }

        if (!await _svc.AiSettings.IsFeatureEnabledAsync(AiFeatureKeys.CatalogCategorization, cancellationToken)
                .ConfigureAwait(false))
        {
            SetAiRecommendationsActionStatusOnUi("Enable Catalog categorization in AI Features.");
            return false;
        }

        var ollamaReady = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(false);
        var summarizer = await _svc.Summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!ollamaReady || string.IsNullOrWhiteSpace(summarizer))
        {
            SetAiRecommendationsActionStatusOnUi(
                "Recommendations require Ollama running with a summarizer model installed.");
            return false;
        }

        return true;
    }

    private void SetAiRecommendationsActionStatusOnUi(string text) =>
        UiDispatcher.InvokeAsync(() => SetAiRecommendationsActionStatus(text));

    private async Task<bool> RunClassificationCoreAsync(
        bool recategorize,
        CancellationToken cancellationToken,
        bool useRecommendationsStatus = false)
    {
        var progress = new Progress<string>(msg =>
        {
            UiDispatcher.InvokeAsync(() =>
            {
                if (useRecommendationsStatus)
                {
                    SetAiRecommendationsActionStatus(msg);
                }
                else
                {
                    SetAiSettingsActionStatus(msg);
                }
            });
        });

        try
        {
            var count = await _svc.Classification.ClassifyAllAsync(recategorize, progress, cancellationToken)
                .ConfigureAwait(false);
            _svc.CategoryStore.ClearCache();
            _svc.CatalogStore.ClearCache();
            _svc.ActivityLog.Write("AI", $"Classification complete ({count} model(s) updated).");
            await UiDispatcher.InvokeAsync(async () =>
            {
                await RefreshCategoryStatusAsync().ConfigureAwait(true);
                if (!useRecommendationsStatus)
                {
                    await RefreshCatalogUiAsync().ConfigureAwait(true);
                }
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = await ExplainErrorAsync(ex.Message, cancellationToken).ConfigureAwait(false);
            SetAiRecommendationsActionStatusOnUi(msg);
            return false;
        }
    }

    private async void RefreshAiRecommendations_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        SetAiSettingsButtonsEnabled(false);
        try
        {
            await BindAiRecommendationsFromCacheAsync().ConfigureAwait(true);
            SetAiRecommendationsActionStatus("Recommendation lists refreshed from cache.");
            EndTaskFlashSuccess(sender, "Refreshed");
        }
        catch (Exception ex)
        {
            SetAiRecommendationsActionStatus($"Refresh failed: {ex.Message}");
            EndTaskFlashIdle(sender);
        }
        finally
        {
            SetAiSettingsButtonsEnabled(true);
        }
    }

    private async void ClearAiRecommendations_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginTaskFlash(sender))
        {
            return;
        }

        try
        {
            await _svc.AiLlmRecommendations.ClearAsync().ConfigureAwait(true);
            BindAiRecommendationsDocument(null);
            SetAiRecommendationsActionStatus("Recommendation lists cleared.");
            EndTaskFlashSuccess(sender, "Cleared");
        }
        catch (Exception ex)
        {
            SetAiRecommendationsActionStatus($"Clear failed: {ex.Message}");
            EndTaskFlashIdle(sender);
        }
    }

    private void CancelAiRecommendations_Click(object sender, RoutedEventArgs e)
    {
        _aiRecommendationsCts?.Cancel();
        _catalogOps.Cancel();
        if (_generateRecommendationsFlashSender is not null)
        {
            EndTaskFlashIdle(_generateRecommendationsFlashSender);
            _generateRecommendationsFlashSender = null;
        }

        SetAiRecommendationsActionStatus("Cancelling...");
        SetAiGenerateBusy(false);
        UpdateFooterComputeModeLabel();
    }

    private async void AiDownloadRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (UninstalledRecommendedGrid.SelectedItem is not AiRecommendedLlmRowViewModel row)
        {
            SetAiRecommendationsActionStatus("Select an uninstalled recommended model first.");
            return;
        }

        if (!TryBeginTaskFlashWithFeedback(sender, SetAiRecommendationsActionStatus))
        {
            return;
        }

        SetAiSettingsButtonsEnabled(false);
        await DownloadModelTagFromAiSettingsAsync(row.PullTag, sender).ConfigureAwait(true);
    }

    private async void AiUninstallRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledRecommendedGrid.SelectedItem is not AiRecommendedLlmRowViewModel row)
        {
            SetAiRecommendationsActionStatus("Select an installed recommended model first.");
            return;
        }

        if (!ToolkitConfirmDialog.ShowAccept(
                this,
                $"Remove {row.PullTag} from this PC? Benchmark data in the toolkit will be kept.",
                "Uninstall LLM"))
        {
            return;
        }

        if (!TryBeginTaskFlashWithFeedback(sender, SetAiRecommendationsActionStatus))
        {
            return;
        }

        SetAiSettingsButtonsEnabled(false);
        var pullTag = row.PullTag;
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModelSessions.UninstallModelAsync(pullTag, ct).ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                _svc.Profiles.ClearCache();
                _svc.AiLlmRecommendations.ClearCache();
                await RefreshModelsUiAsync().ConfigureAwait(false);
                await RefreshAiSettingsUiAsync(cancellationToken: ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    SetAiRecommendationsActionStatus($"Uninstalled {pullTag}.");
                    EndTaskFlashSuccess(sender, "Removed");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    SetAiRecommendationsActionStatus(msg);
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() => SetAiSettingsButtonsEnabled(true)).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void AiLaunchRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBlockWorkloadIfDownloadActive("launching a model"))
        {
            return;
        }

        if (InstalledRecommendedGrid.SelectedItem is not AiRecommendedLlmRowViewModel row)
        {
            SetAiRecommendationsActionStatus("Select an installed recommended model first.");
            return;
        }

        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        var match = summaries.FirstOrDefault(s =>
            s.Model.Equals(row.Model, StringComparison.OrdinalIgnoreCase)
            || s.Model.StartsWith($"{row.Model.Split(':')[0]}:", StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            SetAiRecommendationsActionStatus($"No profile found for {row.Model}.");
            return;
        }

        if (!BeginTaskFlash(sender))
        {
            return;
        }

        MainTabs.SelectedItem = ModelRunTab;
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

    private void AiTestRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledRecommendedGrid.SelectedItem is not AiRecommendedLlmRowViewModel row)
        {
            SetAiRecommendationsActionStatus("Select an installed recommended model first.");
            return;
        }

        MainTabs.SelectedItem = TestingSuiteTab;
        for (var i = 0; i < TestModelCombo.Items.Count; i++)
        {
            var item = TestModelCombo.Items[i]?.ToString();
            if (item is not null
                && (item.Equals(row.Model, StringComparison.OrdinalIgnoreCase)
                    || item.StartsWith($"{row.Model.Split(':')[0]}:", StringComparison.OrdinalIgnoreCase)))
            {
                TestModelCombo.SelectedIndex = i;
                SetAiRecommendationsActionStatus($"Selected {item} in Testing Suite.");
                return;
            }
        }

        SetAiRecommendationsActionStatus($"{row.Model} was not found in the Testing Suite model list.");
    }

    private void AiOpenInCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (UninstalledRecommendedGrid.SelectedItem is not AiRecommendedLlmRowViewModel row)
        {
            SetAiRecommendationsActionStatus("Select an uninstalled recommended model first.");
            return;
        }

        MainTabs.SelectedItem = ModelLibraryTab;
        CatalogSearchBox.Text = row.Model.Split(':')[0];
        SetAiRecommendationsActionStatus($"Opened Model Library for {row.Model}.");
    }

    private async Task DownloadModelTagFromAiSettingsAsync(string pullTag, object? flashSender)
    {
        SetAiRecommendationsActionStatus($"Downloading {pullTag}...");
        _aiRecommendationsCts?.Cancel();
        _aiRecommendationsCts?.Dispose();
        _aiRecommendationsCts = new CancellationTokenSource();
        var ct = _aiRecommendationsCts.Token;

        await _svc.WorkQueue.EnqueueAsync(async workCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workCt, ct);
            var token = linked.Token;
            await UiDispatcher.InvokeAsync(() =>
            {
                AiSettingsDownloadProgressPanel.Visibility = Visibility.Visible;
                AiSettingsDownloadProgress.Value = 0;
                AiSettingsDownloadProgressLabel.Text = "Preparing...";
            }).ConfigureAwait(false);

            try
            {
                var startup = await EnsureOllamaApiReadyAsync(token, $"Cannot download {pullTag}", timeoutSec: 90)
                    .ConfigureAwait(false);
                if (!startup.Success)
                {
                    throw new InvalidOperationException(startup.Message);
                }

                var progress = new Progress<ModelPullProgress>(update =>
                {
                    UiDispatcher.InvokeAsync(() =>
                    {
                        if (update.Percent is int percent)
                        {
                            AiSettingsDownloadProgress.Value = percent;
                        }

                        AiSettingsDownloadProgressLabel.Text = update.Percent is int pct
                            ? $"{pullTag} — {update.Status} ({pct}%)"
                            : $"{pullTag} — {update.Status}";
                        SetAiRecommendationsActionStatus($"Downloading {pullTag}: {update.Status}");
                    });
                });

                await _svc.ApiClient.PullAsync(pullTag, progress, token).ConfigureAwait(false);
                _svc.ApiClient.InvalidateCaches();
                _svc.Profiles.ClearCache();
                _svc.AiLlmRecommendations.ClearCache();
                await UiDispatcher.InvokeAsync(async () =>
                {
                    AiSettingsDownloadProgressPanel.Visibility = Visibility.Collapsed;
                    SetAiRecommendationsActionStatus($"Downloaded {pullTag}. Regenerate recommendations to refresh lists.");
                    await RefreshModelsUiAsync().ConfigureAwait(true);
                    await RefreshAiSettingsUiAsync().ConfigureAwait(true);
                    if (flashSender is not null)
                    {
                        EndTaskFlashSuccess(flashSender, "Downloaded", 10);
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    AiSettingsDownloadProgressPanel.Visibility = Visibility.Collapsed;
                    SetAiRecommendationsActionStatus($"Download cancelled for {pullTag}.");
                    if (flashSender is not null)
                    {
                        EndTaskFlashIdle(flashSender);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await ExplainErrorAsync(ex.Message, token).ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    AiSettingsDownloadProgressPanel.Visibility = Visibility.Collapsed;
                    SetAiRecommendationsActionStatus(msg);
                    if (flashSender is not null)
                    {
                        EndTaskFlashIdle(flashSender);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() => SetAiSettingsButtonsEnabled(true)).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private async void SummarizerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSummarizerComboSave
            || !_summarizerDropdownOpen
            || SummarizerCombo.SelectedItem is not string model)
        {
            return;
        }

        var settings = await _svc.AiSettings.LoadAsync().ConfigureAwait(false);
        settings.PreferredSummarizerModel = model;
        await _svc.AiSettings.SaveAsync(settings).ConfigureAwait(false);
        await UpdateAiStatusAsync().ConfigureAwait(false);
    }

    private async void RefreshSummarizerList_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginTaskFlashWithFeedback(sender, SetAiSettingsActionStatus))
        {
            return;
        }

        SetAiSettingsActionStatus("Refreshing summarizer list...");
        SetSummarizerRefreshControlsEnabled(false);
        var started = DateTime.UtcNow;
        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                _svc.ApiClient.InvalidateCaches();
                await RefreshAiSettingsUiAsync(forceRefresh: true, ct).ConfigureAwait(false);
                var elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                await UiDispatcher.InvokeAsync(() =>
                {
                    var count = SummarizerCombo.Items.Count;
                    SetAiSettingsActionStatus(count == 0
                        ? $"Summarizer list empty after {elapsed}s — start Ollama, then refresh again."
                        : $"Summarizer list refreshed — {count} choice(s) ({elapsed}s).");
                    EndTaskFlashSuccess(sender, "Refreshed");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _svc.ActivityLog.Write("Error", ex.Message);
                _svc.Diagnostics.Write("AI", $"Summarizer list refresh failed: {ex.Message}");
                await UiDispatcher.InvokeAsync(() =>
                {
                    SetAiSettingsActionStatus($"Refresh failed: {ex.Message}");
                    EndTaskFlashIdle(sender);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() => SetSummarizerRefreshControlsEnabled(true))
                    .ConfigureAwait(false);
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
                SetAiSettingsActionStatus($"Applying summarizer best mode for {model}...");
                await _svc.SummarizerInference.ApplySummarizerBestModeAsync(model, ct)
                    .ConfigureAwait(false);
                await UiDispatcher.InvokeAsync(UpdateFooterComputeModeLabel).ConfigureAwait(false);
                SetAiSettingsActionStatus($"Testing summarizer ({model})...");
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
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
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
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
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
        if (!TryBlockWorkloadIfDownloadActive("using Ask AI"))
        {
            return;
        }

        var intent = PromptForIntent("What do you want to do?");
        if (string.IsNullOrWhiteSpace(intent))
        {
            return;
        }

        if (sender is not null && !BeginTaskFlash(sender))
        {
            return;
        }

        var summaries = (ModelsGrid.ItemsSource as IEnumerable<ModelLaunchRowViewModel>)?
            .Select(r => r.ToSummary()).ToList()
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
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
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
        var selected = ModelsGrid.SelectedItems.Cast<ModelLaunchRowViewModel>().ToList();
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
            .Select(s => (s.Model, (string?)s.Category, (ModelProfileSummary?)s.ToSummary()))
            .ToList();
        await RunCompareManyAsync(models, sender).ConfigureAwait(true);
    }

    private async void CompareCatalog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryBlockWorkloadIfDownloadActive("comparing models"))
            {
                return;
            }

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

            var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(false);
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
        catch (Exception ex)
        {
            _svc.Diagnostics.Write("Catalog", $"Compare failed: {ex.Message}");
            CatalogStatusLabel.Text = ex.Message;
            EndTaskFlashIdle(sender);
        }
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
                var msg = await ExplainErrorAsync(ex.Message, ct).ConfigureAwait(false);
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

            EnterAiActivity();
            LogAnomaliesDocument doc;
            try
            {
                doc = await _svc.LogAnomalies.ScanAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ExitAiActivity();
            }
            if (doc.Anomalies.Count > 0)
            {
                await UiDispatcher.InvokeAsync(() =>
                    AnomalySummary.Text = $"{doc.Anomalies.Count} anomaly pattern(s) detected — see AI Activity.")
                    .ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }
}