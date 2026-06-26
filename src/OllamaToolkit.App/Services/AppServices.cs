using OllamaToolkit.AiAssist;
using OllamaToolkit.BenchmarkRunner;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.Core.EnvBackup;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCategory;
using OllamaToolkit.ModelRegistry;

namespace OllamaToolkit.App.Services;

public sealed class AppServices : IDisposable
{
    private MasterFlashClock? _flashClock;

    public AppServices()
    {
        ActivityHub = new AiActivityHub();
        ActivityLog = new ActivityLogService();
        Diagnostics = new ToolkitDiagnosticsService();
        WorkQueue = new BackgroundWorkQueue(ActivityLog, Diagnostics);
        ApiClient = new OllamaApiClient();
        var ollamaInference = new InferenceActivityCallbacks
        {
            OnStart = ActivityHub.EnterOllamaInference,
            OnEnd = ActivityHub.ExitOllamaInference
        };
        ApiClient.BenchmarkInferenceActivity = ollamaInference;
        ApiClient.ChatInferenceActivity = ollamaInference;
        OllamaCli = new OllamaCliService();
        ModelSessions = new OllamaModelSessionService(OllamaCli)
        {
            LoadActivity = ollamaInference
        };
        ModeDefinitions = new ModeDefinitionService();
        ModeService = new ModeService(ModeDefinitions, new EnvBackupService(), new OllamaProcessService());
        EnvBackup = new EnvBackupService();
        AiSettings = new AiSettingsService();
        Profiles = new ProfileStoreService(ApiClient);
        ReportImporter = new ReportImporter(Profiles);
        ServerTuning = new OllamaServerTuningService();
        BenchmarkRunner = new AutomatedBenchmarkService(ModeService, ServerTuning, ApiClient, Profiles, ModelSessions);
        PlainErrors = new PlainLanguageErrorService(AiSettings, ApiClient);
        CatalogStore = new LibraryCatalogStoreService();
        Descriptions = new DescriptionStoreService(AiSettings, ApiClient);
        CategoryStore = new UsageCategoryStoreService();
        Classification = new CatalogClassificationService(AiSettings, ApiClient, CategoryStore, CatalogStore);
        Registry = new ModelRegistryService(CatalogStore, Descriptions, CategoryStore, Profiles, ApiClient);
        Summarizer = new SummarizerModelResolver(AiSettings, ApiClient);
        BenchmarkInsights = new BenchmarkInsightService(AiSettings, ApiClient, Summarizer);
        LogAnomalies = new LogAnomalyService(AiSettings, ApiClient, Summarizer);
        NlSearch = new NaturalLanguageSearchService(AiSettings, ApiClient, Summarizer);
        ModelAdvisor = new ModelAdvisorService(AiSettings, ApiClient, Summarizer);
        AiLlmRecommendations = new AiLlmRecommendationService(AiSettings, ApiClient, Summarizer);
        BenchmarkSettingsAdvisor = new BenchmarkSettingsAdvisorService(
            AiSettings, ApiClient, Summarizer, ModeDefinitions.DeviceMap);
        QueueAdvisor = new BenchmarkQueueAdvisorService(AiSettings, ApiClient, Summarizer);
        ModelComparison = new ModelComparisonService(AiSettings, ApiClient, Summarizer);
    }

    public MasterFlashClock FlashClock =>
        _flashClock ??= new MasterFlashClock(
            System.Windows.Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
    public AiActivityHub ActivityHub { get; }
    public BackgroundWorkQueue WorkQueue { get; }
    public OllamaApiClient ApiClient { get; }
    public OllamaCliService OllamaCli { get; }
    public OllamaModelSessionService ModelSessions { get; }
    public ModeDefinitionService ModeDefinitions { get; }
    public ModeService ModeService { get; }
    public OllamaServerTuningService ServerTuning { get; }
    public EnvBackupService EnvBackup { get; }
    public AiSettingsService AiSettings { get; }
    public ProfileStoreService Profiles { get; }
    public ReportImporter ReportImporter { get; }
    public AutomatedBenchmarkService BenchmarkRunner { get; }
    public PlainLanguageErrorService PlainErrors { get; }
    public ActivityLogService ActivityLog { get; }
    public ToolkitDiagnosticsService Diagnostics { get; }
    public LibraryCatalogStoreService CatalogStore { get; }
    public DescriptionStoreService Descriptions { get; }
    public UsageCategoryStoreService CategoryStore { get; }
    public CatalogClassificationService Classification { get; }
    public ModelRegistryService Registry { get; }
    public SummarizerModelResolver Summarizer { get; }
    public BenchmarkInsightService BenchmarkInsights { get; }
    public LogAnomalyService LogAnomalies { get; }
    public NaturalLanguageSearchService NlSearch { get; }
    public ModelAdvisorService ModelAdvisor { get; }
    public AiLlmRecommendationService AiLlmRecommendations { get; }
    public BenchmarkSettingsAdvisorService BenchmarkSettingsAdvisor { get; }
    public BenchmarkQueueAdvisorService QueueAdvisor { get; }
    public ModelComparisonService ModelComparison { get; }

    public void Dispose() => ApiClient.Dispose();
}