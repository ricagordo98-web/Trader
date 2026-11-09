using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Trader.Core.Abstractions;
using Trader.Core.Backtesting;
using Trader.Core.Intraday;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Suggestions;

public sealed class ModelReliabilityService(TraderDbContext dbContext, IMarketDataProvider provider, IMemoryCache cache, IIntradaySettings intradaySettings, IScoringSettings scoringSettings) : IModelReliabilityService
{
    private const int StudyDays = 90;
    private const int MaxSymbols = 30;
    private const int MinArticlesPerSymbol = 2;
    private const int Concurrency = 3;
    private static readonly TimeSpan StudyCacheDuration = TimeSpan.FromHours(1);

    public async Task<ModelReliabilityReport> GetReportAsync(SuggestionHorizon? horizon, CancellationToken cancellationToken)
    {
        return ModelReliability.CalculateWithShadow(await LoadClosedAsync(horizon, cancellationToken, includeProjection: true));
    }

    public async Task<WeightLearning> GetWeightLearningAsync(SuggestionHorizon horizon, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, double> current = horizon == SuggestionHorizon.Intraday
            ? SignalWeightMap.Read(await intradaySettings.GetAsync(cancellationToken))
            : SignalWeightMap.Read(await scoringSettings.GetAsync(cancellationToken));

        return WeightLearner.Learn(horizon, await LoadClosedAsync(horizon, cancellationToken), current);
    }

    public async Task<WeightLearning> ApplyLearnedWeightsAsync(SuggestionHorizon horizon, CancellationToken cancellationToken)
    {
        WeightLearning learning = await GetWeightLearningAsync(horizon, cancellationToken);

        if (!learning.Validated)
        {
            return learning;
        }

        Dictionary<string, double> proposed = learning.Weights.ToDictionary(w => w.Signal, w => w.ProposedWeight);

        if (horizon == SuggestionHorizon.Intraday)
        {
            IntradayOptions options = await intradaySettings.GetAsync(cancellationToken);
            SignalWeightMap.Apply(options, proposed);
            await intradaySettings.SaveAsync(options, cancellationToken);
        }
        else
        {
            ScoringOptions options = await scoringSettings.GetAsync(cancellationToken);
            SignalWeightMap.Apply(options, proposed);
            await scoringSettings.SaveAsync(options, cancellationToken);
        }

        return learning;
    }

    public async Task<MetaModelFit> GetMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken) =>
        MetaModelTrainer.Train(horizon, await LoadClosedAsync(horizon, cancellationToken));

    public async Task<MetaModelFit> ApplyMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken)
    {
        MetaModelFit fit = await GetMetaModelAsync(horizon, cancellationToken);

        if (fit.Model is null)
        {
            return fit;
        }

        string key = MetaModelStore.SettingKey(horizon);
        string json = System.Text.Json.JsonSerializer.Serialize(fit.Model, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        AppSetting? setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (setting is null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = key, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(MetaModelStore.CacheKey(horizon));
        return fit;
    }

    public async Task DisableMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken)
    {
        string key = MetaModelStore.SettingKey(horizon);
        AppSetting? setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (setting is not null)
        {
            dbContext.AppSettings.Remove(setting);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        cache.Remove(MetaModelStore.CacheKey(horizon));
    }

    private async Task<List<SuggestionDto>> LoadClosedAsync(SuggestionHorizon? horizon, CancellationToken cancellationToken, bool includeProjection = false)
    {
        IQueryable<Suggestion> query = dbContext.Suggestions.AsNoTracking()
            .Where(s => s.Status != SuggestionStatus.Open && (includeProjection || s.Variant != SuggestionVariants.Projection));

        if (horizon is not null)
        {
            query = query.Where(s => s.Horizon == horizon);
        }

        List<Suggestion> closed = await query.ToListAsync(cancellationToken);
        Dictionary<string, AssetClass> classes = await dbContext.Instruments.AsNoTracking().ToDictionaryAsync(i => i.Symbol, i => i.AssetClass, cancellationToken);
        CostModel costs = new();

        return closed
            .Select(SuggestionService.ToDto)
            .Select(dto => classes.TryGetValue(dto.Symbol, out AssetClass assetClass)
                ? dto with { Class = assetClass, CostR = SuggestionCosts.CostR(dto, assetClass, costs) }
                : dto)
            .ToList();
    }

    public async Task<SentimentStudyResult> GetSentimentStudyAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue("sentiment-study", out SentimentStudyResult? cached) && cached is not null)
        {
            return cached;
        }

        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-StudyDays);

        var rows = await (
            from link in dbContext.Set<ArticleSymbol>().AsNoTracking()
            join article in dbContext.Articles.AsNoTracking() on link.ArticleId equals article.Id
            where article.Sentiment != null && article.PublishedAt >= since
            orderby article.PublishedAt descending
            select new { link.Symbol, article.PublishedAt, Sentiment = article.Sentiment!.Value })
            .ToListAsync(cancellationToken);

        List<string> symbols = rows
            .GroupBy(r => r.Symbol)
            .Where(g => g.Count() >= MinArticlesPerSymbol)
            .OrderByDescending(g => g.Count())
            .Take(MaxSymbols)
            .Select(g => g.Key)
            .ToList();

        Dictionary<string, IReadOnlyList<Candle>> daily = [];
        using SemaphoreSlim gate = new(Concurrency);

        await Task.WhenAll(symbols.Select(async symbol =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                ChartSeries series = await provider.GetChartAsync(symbol, "1d", "6mo", cancellationToken);

                lock (daily)
                {
                    daily[symbol] = series.Candles;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                gate.Release();
            }
        }));

        List<SentimentObservation> observations = rows
            .Where(r => daily.ContainsKey(r.Symbol))
            .Select(r => new SentimentObservation(
                r.Symbol,
                r.Sentiment,
                SentimentStudy.ForwardReturn(daily[r.Symbol], r.PublishedAt, 1),
                SentimentStudy.ForwardReturn(daily[r.Symbol], r.PublishedAt, 3)))
            .ToList();

        SentimentStudyResult result = SentimentStudy.Evaluate(observations);
        cache.Set("sentiment-study", result, StudyCacheDuration);
        return result;
    }
}
