using Trader.Infrastructure.Agender;
using Trader.Infrastructure.Analysis;
using Trader.Infrastructure.Projection;
using Trader.Infrastructure.Alerts;
using Trader.Infrastructure.Backup;
using Trader.Infrastructure.Learning;
using Trader.Infrastructure.Rotation;
using Trader.Infrastructure.Calendar;
using Trader.Infrastructure.Charts;
using Trader.Infrastructure.Currency;
using Trader.Infrastructure.Explain;
using Trader.Infrastructure.Summary;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Scoring;
using Trader.Infrastructure.Backtesting;
using Trader.Core.Intraday;
using Trader.Infrastructure.Intraday;
using Trader.Infrastructure.Journal;
using Trader.Infrastructure.Simulator;
using Trader.Infrastructure.MarketData;
using Trader.Infrastructure.News;
using Trader.Infrastructure.News.Sources;
using Trader.Infrastructure.Persistence;
using Trader.Infrastructure.Suggestions;

namespace Trader.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Trader/1.0";

    public static IServiceCollection AddTraderInfrastructure(this IServiceCollection services, IConfiguration configuration, string finBertDirectory)
    {
        services.AddDbContext<TraderDbContext>(options => options.UseSqlite(configuration.GetConnectionString("Trader")));
        services.AddMemoryCache();

        services.AddHttpClient<IMarketDataProvider, YahooFinanceProvider>(client =>
        {
            client.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddScoped<IMarketDataService, MarketDataService>();
        services.AddSingleton<FinnhubTradeFeed>();
        services.AddSingleton<ILiveTradeFeed>(sp => sp.GetRequiredService<FinnhubTradeFeed>());
        services.AddHostedService(sp => sp.GetRequiredService<FinnhubTradeFeed>());
        services.AddScoped<IChartDrawingService, ChartDrawingService>();
        services.AddScoped<IChartInsights, ChartInsightsService>();
        services.AddScoped<IChartLayoutService, ChartLayoutService>();
        services.AddScoped<IDisplayCurrency, DisplayCurrencyService>();
        services.AddScoped<IPriceSettings, PriceSettingsService>();
        services.AddScoped<IUserPreferences, UserPreferencesService>();
        services.AddScoped<IModelReliabilityService, ModelReliabilityService>();
        services.AddScoped<IPaperTradingService, PaperTradingService>();
        services.AddScoped<IAlertRuleService, AlertRuleService>();

        services.Configure<SourcesOptions>(configuration.GetSection(SourcesOptions.SectionName));
        services.AddSingleton<ITickerExtractor, TickerExtractor>();
        services.AddSingleton<ISentimentAnalyzer>(sp => CreateSentimentAnalyzer(sp, finBertDirectory));
        services.AddScoped<ArticleIngestor>();
        services.AddScoped<INewsService, NewsService>();
        services.AddScoped<ICommunityService, CommunityService>();
        services.Configure<ScoringOptions>(configuration.GetSection(ScoringOptions.SectionName));
        services.AddScoped<ISuggestionService, SuggestionService>();
        services.AddScoped<IScoringSettings, ScoringSettings>();
        services.AddScoped<IIntradaySettings, IntradaySettings>();
        services.AddScoped<IBacktestService, BacktestService>();
        services.AddScoped<IRotationService, RotationService>();
        services.AddScoped<IIntradayBacktestService, IntradayBacktestService>();
        services.AddScoped<IJournalService, JournalService>();
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.SectionName));
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<IDatabaseImportService, DatabaseImportService>();
        services.Configure<LearningOptions>(configuration.GetSection(LearningOptions.SectionName));
        services.AddScoped<ILearningService, LearningService>();
        services.AddHostedService<BackupWorker>();
        services.AddHttpClient<ICalendarService, CalendarService>(client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddHostedService<CalendarAlertWorker>();
        services.Configure<SummaryOptions>(configuration.GetSection(SummaryOptions.SectionName));
        services.AddHttpClient<ISummaryNarrator, OllamaNarrator>(client => client.Timeout = TimeSpan.FromMinutes(3));
        services.AddScoped<IDailySummaryService, DailySummaryService>();
        services.AddHostedService<SummaryWorker>();
        services.AddScoped<IChartTemplateService, ChartTemplateService>();
        services.AddScoped<IAlertService, AlertService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AgenderSyncState>();
        services.AddHttpClient(AgenderSyncService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IAgenderSync, AgenderSyncService>();
        services.AddScoped<ITradeAnalysisService, TradeAnalysisService>();
        services.AddScoped<IProjectionService, ProjectionService>();
        services.AddScoped<IProjectionRecordService, ProjectionRecordService>();
        services.AddHostedService<AgenderSyncWorker>();
        services.AddHttpClient<IAlertPublisher, NtfyPublisher>(client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IInstrumentService, InstrumentService>();
        services.AddSingleton<ISourceStatusRegistry, SourceStatusRegistry>();
        services.AddSingleton<ISymbolProvider, SymbolProvider>();
        services.AddSingleton(new IntradayOptions());
        AddEvidence<FinnhubNewsEvidence>(services);
        AddEvidence<MarketauxEvidence>(services);
        AddEvidence<GoogleNewsEvidence>(services);
        AddEvidence<EarningsEvidence>(services);
        AddEvidence<EdgarEvidence>(services);
        services.AddScoped<IEvidenceSource, StoredArticleEvidence>();
        services.AddHttpClient<IMoveNarrator, OllamaMoveNarrator>(client => client.Timeout = TimeSpan.FromMinutes(3));
        services.AddScoped<IMoveExplainer, MoveExplainer>();
        services.AddSingleton<IRadarService, RadarService>();
        services.AddHostedService<RadarWorker>();
        services.AddSingleton<HaltMonitor>();
        services.AddHttpClient<ICompanyProfileService, CompanyProfileService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<BinanceFlowFeed>();
        services.AddSingleton<IOrderFlowService>(sp => sp.GetRequiredService<BinanceFlowFeed>());
        services.AddHostedService(sp => sp.GetRequiredService<BinanceFlowFeed>());
        services.AddHttpClient(nameof(HaltWorker), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHostedService<HaltWorker>();
        services.AddHostedService<SuggestionWorker>();
        services.AddHostedService<QuoteWarmupWorker>();
        services.AddHostedService<PriceWatchWorker>();

        AddConnector<RssConnector>(services, 20);
        AddConnector<GdeltConnector>(services, 40);
        AddConnector<StockTwitsConnector>(services, 20);
        AddConnector<FinnhubConnector>(services, 20);
        AddConnector<RedditConnector>(services, 20);
        AddConnector<EdgarConnector>(services, 20);
        AddConnector<MarketauxConnector>(services, 20);
        AddConnector<FredConnector>(services, 20);
        AddConnector<HackerNewsConnector>(services, 20);
        AddConnector<RedditFeedConnector>(services, 20);
        AddConnector<MastodonConnector>(services, 20);
        AddConnector<LemmyConnector>(services, 20);
        services.AddHttpClient<ApeWisdomClient>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<ISourceConnector, ApeWisdomConnector>();

        services.AddHostedService<IngestionWorker>();
        return services;
    }

    private static void AddEvidence<TSource>(IServiceCollection services)
        where TSource : class, IEvidenceSource
    {
        services.AddHttpClient<TSource>(client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        services.AddTransient<IEvidenceSource>(sp => sp.GetRequiredService<TSource>());
    }

    private static void AddConnector<TConnector>(IServiceCollection services, int timeoutSeconds)
        where TConnector : class, ISourceConnector
    {
        services.AddHttpClient<TConnector>(client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        services.AddSingleton<ISourceConnector>(sp => sp.GetRequiredService<TConnector>());
    }

    private static ISentimentAnalyzer CreateSentimentAnalyzer(IServiceProvider services, string finBertDirectory)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Sentiment");

        if (!File.Exists(Path.Combine(finBertDirectory, "model.onnx")) || !File.Exists(Path.Combine(finBertDirectory, "vocab.txt")))
        {
            logger.LogWarning("FinBERT model not found in {Directory}; articles will be stored without sentiment", finBertDirectory);
            return new NullSentimentAnalyzer();
        }

        return new FinBertSentimentAnalyzer(finBertDirectory);
    }
}
