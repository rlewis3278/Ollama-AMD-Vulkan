using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OllamaToolkit.App.Services;
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
    private CancellationTokenSource? _chatCts;
    private CancellationTokenSource? _benchmarkCts;
    private string? _runModel;
    private readonly List<ChatMessage> _chatMessages = new();
    private readonly DispatcherTimer _activityTimer;
    private readonly DispatcherTimer _catalogSearchTimer;
    public MainWindow()
    {
        InitializeComponent();
        _modeCards["CPU"] = CpuCard;
        _modeCards["APU"] = ApuCard;
        _modeCards["GPU"] = GpuCard;
        _modeCards["Hybrid"] = HybridCard;

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _activityTimer.Tick += (_, _) => RefreshActivityLog();
        _catalogSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _catalogSearchTimer.Tick += async (_, _) =>
        {
            _catalogSearchTimer.Stop();
            await RefreshCatalogUiAsync().ConfigureAwait(true);
        };
        Loaded += OnLoadedAsync;
        Closed += (_, _) =>
        {
            _activityTimer.Stop();
            _catalogSearchTimer.Stop();
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
        VulkanLabel.Text = $"Vulkan: APU index {map.ApuVulkanIndex} ({map.ApuName}) | GPU index {map.GpuVulkanIndex} ({map.GpuName})";
        foreach (var def in _svc.ModeDefinitions.Definitions.Values)
        {
            if (_modeCards.TryGetValue(def.Mode.ToString(), out var card))
            {
                card.Content = $"{def.ShortLabel}\n{def.Description}";
            }
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
            var imported = await _svc.ReportImporter.ImportReportsAsync(cancellationToken: ct).ConfigureAwait(false);
            if (imported > 0)
            {
                _svc.ActivityLog.Write("Task", $"Imported {imported} benchmark report(s).");
            }

            await UiDispatcher.InvokeAsync(async () =>
            {
                await RefreshModesUiAsync().ConfigureAwait(true);
                await RefreshModelsUiAsync().ConfigureAwait(true);
                await UpdateAiStatusAsync().ConfigureAwait(true);
                await RefreshAiSettingsUiAsync().ConfigureAwait(true);
                RefreshActivityLog();
            }).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async Task RefreshModesUiAsync()
    {
        var detected = _svc.ModeService.DetectCurrentMode();
        var apiReady = await _svc.ApiClient.IsReadyCachedAsync().ConfigureAwait(true);
        ModeStatusLabel.Text = $"Current mode: {detected} | Ollama API: {(apiReady ? "ready" : "not reachable")}";

        foreach (var pair in _modeCards)
        {
            var active = pair.Key.Equals(detected, StringComparison.OrdinalIgnoreCase);
            pair.Value.Background = active
                ? new SolidColorBrush(Color.FromRgb(22, 58, 38))
                : (Brush)FindResource("Brush.Button");
        }

        var snapshot = _svc.EnvBackup.ReadUserSnapshot();
        EnvBox.Text = string.Join(Environment.NewLine,
            snapshot.OrderBy(k => k.Key).Select(e => $"{e.Key} = {e.Value ?? "(not set)"}"));
    }

    private async Task RefreshModelsUiAsync()
    {
        var summaries = await _svc.Profiles.GetAllSummariesAsync().ConfigureAwait(true);
        ModelsGrid.ItemsSource = summaries;
        TestModelCombo.ItemsSource = summaries.Select(s => s.Model).ToList();
        if (TestModelCombo.Items.Count > 0)
        {
            TestModelCombo.SelectedIndex = 0;
        }

        var untested = summaries.Where(s => s.NeedsRetest).Select(s => s.Model).ToList();
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

        btn.Background = new SolidColorBrush(Color.FromRgb(22, 58, 38));
        var restart = RestartCheck.IsChecked == true;

        await _svc.WorkQueue.EnqueueAsync(async ct =>
        {
            try
            {
                await _svc.ModeService.ApplyModeAsync(mode, restartOllama: restart, cancellationToken: ct)
                    .ConfigureAwait(false);
                _svc.ActivityLog.Write("Task", $"Applied mode {mode}.");
                await UiDispatcher.InvokeAsync(async () => await RefreshModesUiAsync().ConfigureAwait(true))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = await _svc.PlainErrors.ExplainAsync(ex.Message, ct).ConfigureAwait(false);
                _svc.ActivityLog.Write("Error", msg);
                await UiDispatcher.InvokeAsync(() => MessageBox.Show(msg, "Mode Apply", MessageBoxButton.OK, MessageBoxImage.Warning))
                    .ConfigureAwait(false);
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

    private async void TestUntested_Click(object sender, RoutedEventArgs e)
    {
        var untested = await _svc.Profiles.GetUntestedAsync().ConfigureAwait(true);
        var names = untested.Select(u => u.Model).ToList();
        if (names.Count == 0)
        {
            TestStatusLabel.Text = "No untested models.";
            return;
        }

        await RunBenchmarkQueueAsync(names).ConfigureAwait(true);
    }

    private async Task RunBenchmarkQueueAsync(IReadOnlyList<string> models)
    {
        _benchmarkCts?.Cancel();
        _benchmarkCts = new CancellationTokenSource();
        var ct = _benchmarkCts.Token;

        await _svc.WorkQueue.EnqueueAsync(async token =>
        {
            foreach (var model in models)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await UiDispatcher.InvokeAsync(() => TestStatusLabel.Text = $"Testing {model}...").ConfigureAwait(false);
                var progress = new Progress<string>(line =>
                {
                    UiDispatcher.InvokeAsync(() =>
                    {
                        TestLogBox.Text += line + Environment.NewLine;
                        TestLogBox.CaretIndex = TestLogBox.Text.Length;
                        TestLogBox.ScrollToEnd();
                    });
                });

                try
                {
                    await _svc.BenchmarkRunner.RunAsync(model, log: progress, cancellationToken: ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        TestLogBox.Text += $"FAIL: {ex.Message}{Environment.NewLine}";
                    }).ConfigureAwait(false);
                }
            }

            _svc.Profiles.ClearCache();
            var summaries = await _svc.Profiles.GetAllSummariesAsync(ct).ConfigureAwait(false);
            foreach (var summary in summaries.Where(s => models.Contains(s.Model)))
            {
                await _svc.BenchmarkInsights.InterpretProfileAsync(summary, ct).ConfigureAwait(false);
                await _svc.BenchmarkInsights.DiagnoseFailuresAsync(summary, ct).ConfigureAwait(false);
            }

            await UiDispatcher.InvokeAsync(async () =>
            {
                TestStatusLabel.Text = "Benchmark queue complete.";
                await RefreshModelsUiAsync().ConfigureAwait(true);
                await RefreshTestResultsUiAsync().ConfigureAwait(true);
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
                        var prefix = ChatHistory.Text;
                        var marker = "Assistant: ";
                        var idx = prefix.LastIndexOf(marker, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            ChatHistory.Text = prefix[..(idx + marker.Length)] + captured;
                        }
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
        var rows = await _svc.Registry.GetCatalogRowsAsync(search, category).ConfigureAwait(true);
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

    private async Task RefreshTestResultsUiAsync()
    {
        var rows = await _svc.Registry.GetTestResultRowsAsync().ConfigureAwait(true);
        var enriched = new List<TestResultRowViewModel>();
        foreach (var row in rows)
        {
            var insight = await _svc.BenchmarkInsights.GetInsightAsync(row.Model).ConfigureAwait(true);
            var shortInsight = insight is null
                ? string.Empty
                : (insight.Length > 60 ? insight[..57] + "..." : insight);
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

        var full = await _svc.BenchmarkInsights.GetInsightAsync(row.Model).ConfigureAwait(true);
        TestResultDetail.Text = string.IsNullOrWhiteSpace(full)
            ? $"No AI insight for {row.Model}."
            : full;
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