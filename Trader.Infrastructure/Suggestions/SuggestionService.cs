using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Alerts;
using Trader.Core.Analysis;
using Trader.Core.Backtesting;
using Trader.Core.Calendar;
using Trader.Core.Intraday;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Infrastructure.Explain;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Suggestions;

public sealed class SuggestionService(
    TraderDbContext dbContext,
    IMarketDataService marketData,
    IScoringSettings scoringSettings,
    IAlertService alerts,
    IntradayOptions intradayOptions,
    ICalendarService calendar,
    IIntradaySettings intradaySettings,
    IMemoryCache cache,
    ILogger<SuggestionService> logger) : ISuggestionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan IntradayCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan CalibrationCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DailyTrendCacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan SwingNewsWindow = TimeSpan.FromDays(30);

    public async Task<TradeContext?> GetContextAsync(string symbol, CancellationToken cancellationToken)
    {
        Instrument? instrument = await dbContext.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Symbol == symbol, cancellationToken);

        if (instrument is null)
        {
            return null;
        }

        ScoringOptions scoring = await scoringSettings.GetAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<Candle> candles = await marketData.GetCandlesAsync(symbol, "1d", "1y", cancellationToken);
        IndicatorSeries series = IndicatorCalculator.ComputeSeries(symbol, instrument.Name, candles);

        if (series.Snapshots.LastOrDefault() is not { } market)
        {
            return null;
        }

        PriceLookup? benchmark = await GetBenchmarkAsync(instrument, [], cancellationToken);
        IReadOnlyDictionary<string, int> earnings = await GetEarningsAsync(now, cancellationToken);

        market = market with
        {
            RelativeStrength = benchmark is null ? null : ContextMetrics.RelativeStrength(series.Candles, series.Candles.Count - 1, benchmark),
            DaysToEarnings = earnings.TryGetValue(symbol, out int days) ? days : null,
            Vix = await GetVixAsync(cancellationToken)
        };

        SentimentSnapshot sentiment = await BuildSentimentAsync(symbol, now, scoring, SwingNewsWindow, cancellationToken);

        return new TradeContext(instrument.Name, instrument.AssetClass, market, sentiment, scoring);
    }

    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        await EvaluateOpenAsync(SuggestionHorizon.Swing, cancellationToken);
        return await GenerateAsync(await scoringSettings.GetAsync(cancellationToken), cancellationToken);
    }

    public async Task<int> RefreshIntradayAsync(IReadOnlyList<IntradayContext> contexts, CancellationToken cancellationToken)
    {
        await EvaluateOpenAsync(SuggestionHorizon.Intraday, cancellationToken);

        IntradayEngine engine = new(intradayOptions);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cooldownStart = now - IntradayCooldown;

        HashSet<string> blocked = (await dbContext.Suggestions
            .Where(s => !s.IsShadow && s.Variant == "A" && s.Horizon == SuggestionHorizon.Intraday && (s.Status == SuggestionStatus.Open || s.CreatedAt >= cooldownStart))
            .Select(s => s.Symbol)
            .ToListAsync(cancellationToken)).ToHashSet();

        HashSet<(string Symbol, TradeDirection Direction)> openShadows = (await dbContext.Suggestions
            .Where(s => s.IsShadow && s.Variant == "A" && s.Horizon == SuggestionHorizon.Intraday && (s.Status == SuggestionStatus.Open || s.CreatedAt >= cooldownStart))
            .Select(s => new { s.Symbol, s.Direction })
            .ToListAsync(cancellationToken)).Select(s => (s.Symbol, s.Direction)).ToHashSet();

        int slots = intradayOptions.MaxOpenSuggestions
            - await dbContext.Suggestions.CountAsync(x => !x.IsShadow && x.Variant == "A" && x.Horizon == SuggestionHorizon.Intraday && x.Status == SuggestionStatus.Open, cancellationToken);

        IReadOnlyList<IntradayContext> enriched = await EnrichAsync(contexts, now, cancellationToken);

        ScoringOptions newsScoring = new() { SentimentHalfLifeHours = intradayOptions.NewsHalfLifeHours };
        List<(SuggestionDraft Draft, IntradayContext Context)> drafts = [];
        IReadOnlySet<string> weakCells = intradayOptions.AutoDisableWeakCells
            ? await GetWeakCellsAsync(intradayOptions.WeakCellMinSamples, cancellationToken)
            : new HashSet<string>();

        foreach (IntradayContext context in enriched.Where(c => Math.Abs(c.RecentMove) >= intradayOptions.MinEntryMove))
        {
            SentimentSnapshot sentiment = await BuildSentimentAsync(context.Symbol, now, newsScoring, TimeSpan.FromHours(intradayOptions.NewsWindowHours), cancellationToken);
            SuggestionDraft? draft = engine.Assess(context, sentiment);

            if (draft is not null)
            {
                drafts.Add((GateWeakCell(draft, weakCells, WeakCells.Key(SuggestionHorizon.Intraday, context.AssetClass), WeakCells.HourKey(SuggestionHorizon.Intraday, context.AssetClass, now.UtcDateTime.Hour)), context));
            }
        }

        List<Suggestion> added = slots <= 0
            ? []
            : drafts
                .Where(d => !d.Draft.IsShadow && !blocked.Contains(d.Draft.Symbol))
                .OrderByDescending(d => d.Draft.Confidence)
                .Take(Math.Min(slots, intradayOptions.MaxNewSuggestionsPerCycle))
                .Select(d => ToEntity(d.Draft, now, now.AddHours(intradayOptions.ExpiryHours), SuggestionHorizon.Intraday))
                .ToList();

        HashSet<string> addedSymbols = added.Select(a => a.Symbol).ToHashSet();
        List<Suggestion> shadows = drafts
            .Where(d => d.Draft.IsShadow
                && !blocked.Contains(d.Draft.Symbol)
                && !addedSymbols.Contains(d.Draft.Symbol)
                && !openShadows.Contains((d.Draft.Symbol, d.Draft.Direction)))
            .OrderByDescending(d => d.Draft.Confidence)
            .Take(intradayOptions.RecordShadow ? intradayOptions.MaxShadowPerCycle : 0)
            .Select(d => ToShadowEntity(d.Draft, now, now.AddHours(intradayOptions.ExpiryHours), SuggestionHorizon.Intraday, null))
            .ToList();

        List<Suggestion> variantShadows = await AssessVariantBAsync(enriched, now, cooldownStart, cancellationToken);

        dbContext.Suggestions.AddRange(added);
        dbContext.Suggestions.AddRange(shadows);
        dbContext.Suggestions.AddRange(variantShadows);
        await dbContext.SaveChangesAsync(cancellationToken);
        await alerts.RaiseAsync(AlertFactory.ForNewSuggestions(added, now), cancellationToken);

        if (shadows.Count > 0)
        {
            logger.LogInformation("Intraday cycle recorded {Count} shadow suggestions", shadows.Count);
        }

        if (added.Count > 0)
        {
            logger.LogInformation("Intraday cycle created {Count} suggestions", added.Count);
        }

        return added.Count;
    }

    private async Task<List<Suggestion>> AssessVariantBAsync(IReadOnlyList<IntradayContext> contexts, DateTimeOffset now, DateTimeOffset cooldownStart, CancellationToken cancellationToken)
    {
        VariantSettings? variant = await intradaySettings.GetVariantBAsync(cancellationToken);

        if (variant is null || !variant.Enabled)
        {
            return [];
        }

        IntradayOptions options = variant.Options;
        IntradayEngine engine = new(options);
        ScoringOptions newsScoring = new() { SentimentHalfLifeHours = options.NewsHalfLifeHours };

        HashSet<string> blocked = (await dbContext.Suggestions
            .Where(s => s.Variant == "B" && s.Horizon == SuggestionHorizon.Intraday && (s.Status == SuggestionStatus.Open || s.CreatedAt >= cooldownStart))
            .Select(s => s.Symbol)
            .ToListAsync(cancellationToken)).ToHashSet();

        List<SuggestionDraft> drafts = [];

        foreach (IntradayContext context in contexts.Where(c => !blocked.Contains(c.Symbol) && Math.Abs(c.RecentMove) >= options.MinEntryMove))
        {
            SentimentSnapshot sentiment = await BuildSentimentAsync(context.Symbol, now, newsScoring, TimeSpan.FromHours(options.NewsWindowHours), cancellationToken);

            if (engine.Evaluate(context, sentiment) is { } draft)
            {
                drafts.Add(draft);
            }
        }

        return drafts
            .OrderByDescending(d => d.Confidence)
            .Take(options.MaxNewSuggestionsPerCycle)
            .Select(d =>
            {
                Suggestion entity = ToEntity(d, now, now.AddHours(options.ExpiryHours), SuggestionHorizon.Intraday);
                entity.IsShadow = true;
                entity.Variant = "B";
                entity.ShadowReason = "variante B (teste)";
                return entity;
            })
            .ToList();
    }

    public async Task<IReadOnlyList<SuggestionDto>> GetAsync(string? symbol, bool? open, int limit, SuggestionHorizon? horizon, CancellationToken cancellationToken, bool includeShadow = false)
    {
        IQueryable<Suggestion> query = dbContext.Suggestions.AsNoTracking();

        if (!includeShadow)
        {
            query = query.Where(s => !s.IsShadow);
        }

        if (horizon is not null)
        {
            query = query.Where(s => s.Horizon == horizon);
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            query = query.Where(s => s.Symbol == symbol);
        }

        query = open switch
        {
            true => query.Where(s => s.Status == SuggestionStatus.Open).OrderByDescending(s => s.Confidence),
            false => query.Where(s => s.Status != SuggestionStatus.Open).OrderByDescending(s => s.ClosedAt),
            _ => query.OrderByDescending(s => s.CreatedAt)
        };

        List<Suggestion> rows = await query.Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        Dictionary<string, AssetClass> classes = await dbContext.Instruments.AsNoTracking().ToDictionaryAsync(i => i.Symbol, i => i.AssetClass, cancellationToken);
        Dictionary<(SuggestionHorizon, AssetClass?), ConfidenceCalibration?> calibrations = [];

        foreach (Suggestion row in rows)
        {
            AssetClass? rowClass = classes.TryGetValue(row.Symbol, out AssetClass found) ? found : null;

            if (!calibrations.ContainsKey((row.Horizon, rowClass)))
            {
                calibrations[(row.Horizon, rowClass)] = await GetCalibrationAsync(row.Horizon, rowClass, cancellationToken);
            }
        }

        Dictionary<SuggestionHorizon, MetaModel?> metaModels = [];

        foreach (SuggestionHorizon rowHorizon in rows.Select(r => r.Horizon).Distinct())
        {
            metaModels[rowHorizon] = await GetMetaModelAsync(rowHorizon, cancellationToken);
        }

        return rows
            .Select(r =>
            {
                AssetClass? rowClass = classes.TryGetValue(r.Symbol, out AssetClass found) ? found : null;
                SuggestionDto dto = ToDto(r) with { Class = rowClass };

                if (metaModels[r.Horizon] is { } meta)
                {
                    return dto with { CalibratedConfidence = meta.Predict(dto.Signals, dto.Direction), CalibrationSamples = meta.Samples };
                }

                return calibrations[(r.Horizon, rowClass)] is { } calibration
                    ? dto with { CalibratedConfidence = calibration.Apply(r.Confidence), CalibrationSamples = calibration.Samples }
                    : dto;
            })
            .ToList();
    }

    public async Task<TrackRecordDto> GetTrackRecordAsync(SuggestionHorizon? horizon, CancellationToken cancellationToken)
    {
        IQueryable<Suggestion> all = dbContext.Suggestions.AsNoTracking().Where(s => !s.IsShadow);

        if (horizon is not null)
        {
            all = all.Where(s => s.Horizon == horizon);
        }

        List<Suggestion> closed = await all
            .Where(s => s.Status != SuggestionStatus.Open)
            .ToListAsync(cancellationToken);

        int openCount = await all.CountAsync(s => s.Status == SuggestionStatus.Open, cancellationToken);

        return TrackRecordCalculator.Calculate(closed.Select(ToDto).ToList(), openCount);
    }

    private async Task<IReadOnlyDictionary<string, double>> GetSourceWeightsAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue("source-weights", out IReadOnlyDictionary<string, double>? cached) && cached is not null)
        {
            return cached;
        }

        var closed = await dbContext.Suggestions
            .AsNoTracking()
            .Where(s => s.Status != SuggestionStatus.Open && s.RMultiple != null)
            .Select(s => new { s.EvidenceJson, R = s.RMultiple!.Value })
            .ToListAsync(cancellationToken);

        List<(IReadOnlyList<string> Sources, bool Win)> outcomes = [];

        foreach (var row in closed)
        {
            SuggestionEvidence? evidence = JsonSerializer.Deserialize<SuggestionEvidence>(row.EvidenceJson, JsonOptions);
            outcomes.Add((evidence?.Articles.Select(a => a.Source).ToList() ?? [], row.R > 0));
        }

        IReadOnlyDictionary<string, double> weights = SourceWeightCalculator.Calculate(outcomes);
        cache.Set("source-weights", weights, CalibrationCacheDuration);

        List<(IReadOnlyList<string> Sources, bool Win)> categoryOutcomes = [];

        foreach (var row in closed)
        {
            SuggestionEvidence? evidence = JsonSerializer.Deserialize<SuggestionEvidence>(row.EvidenceJson, JsonOptions);
            categoryOutcomes.Add((evidence?.Articles.Select(a => SentimentAggregator.CategoryKey(a.Title)).ToList() ?? [], row.R > 0));
        }

        cache.Set("category-weights", SourceWeightCalculator.Calculate(categoryOutcomes), CalibrationCacheDuration);
        return weights;
    }

    private async Task<ConfidenceCalibration?> GetCalibrationAsync(SuggestionHorizon horizon, AssetClass? assetClass, CancellationToken cancellationToken)
    {
        string key = $"calibration:{horizon}:{assetClass}";

        if (cache.TryGetValue(key, out ConfidenceCalibration? cached))
        {
            return cached;
        }

        List<Suggestion> closed = await dbContext.Suggestions
            .AsNoTracking()
            .Where(s => s.Horizon == horizon && s.Status != SuggestionStatus.Open && s.RMultiple != null)
            .ToListAsync(cancellationToken);

        Dictionary<string, AssetClass> classes = await dbContext.Instruments.AsNoTracking().ToDictionaryAsync(i => i.Symbol, i => i.AssetClass, cancellationToken);
        CostModel costs = new();

        List<(double Confidence, bool Win)> outcomes = closed
            .Where(s => assetClass is null || (classes.TryGetValue(s.Symbol, out AssetClass c) && c == assetClass))
            .Select(s =>
            {
                SuggestionDto dto = ToDto(s);
                double cost = classes.TryGetValue(s.Symbol, out AssetClass c) ? SuggestionCosts.CostR(dto, c, costs) : 0;
                return (s.Confidence, s.RMultiple!.Value - cost > 0);
            })
            .ToList();

        ConfidenceCalibration? calibration = ConfidenceCalibrator.Fit(outcomes);

        if (calibration is null && assetClass is not null)
        {
            calibration = await GetCalibrationAsync(horizon, null, cancellationToken);
        }

        cache.Set(key, calibration, CalibrationCacheDuration);
        return calibration;
    }

    private async Task EvaluateOpenAsync(SuggestionHorizon horizon, CancellationToken cancellationToken)
    {
        List<Suggestion> open = await dbContext.Suggestions
            .Where(s => s.Status == SuggestionStatus.Open && s.Horizon == horizon)
            .ToListAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<Alert> raised = [];

        foreach (Suggestion suggestion in open)
        {
            try
            {
                IReadOnlyList<Candle> candles = horizon == SuggestionHorizon.Intraday
                    ? await marketData.GetCandlesAsync(suggestion.Symbol, "5m", "5d", cancellationToken)
                    : await marketData.GetCandlesAsync(suggestion.Symbol, "60m", "1mo", cancellationToken);
                OutcomeResult? outcome = OutcomeResolver.Resolve(suggestion, candles, now, intradayOptions.ToExitPolicy());

                if (outcome is null)
                {
                    continue;
                }

                suggestion.Status = outcome.Status;
                suggestion.ExitPrice = outcome.ExitPrice;
                suggestion.ClosedAt = outcome.ClosedAt;
                suggestion.RMultiple = outcome.RMultiple;
                suggestion.MfeR = outcome.MfeR;
                suggestion.MaeR = outcome.MaeR;
                suggestion.SmartR = outcome.SmartR;

                if (!suggestion.IsShadow && AlertFactory.ForOutcome(suggestion, now) is { } alert)
                {
                    raised.Add(alert);
                }

                logger.LogInformation("Suggestion {Id} {Symbol} closed as {Status} ({R:0.00}R)", suggestion.Id, suggestion.Symbol, outcome.Status, outcome.RMultiple);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Could not evaluate {Symbol}: {Message}", suggestion.Symbol, ex.Message);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await alerts.RaiseAsync(raised, cancellationToken);
    }

    private async Task<int> GenerateAsync(ScoringOptions scoring, CancellationToken cancellationToken)
    {
        SuggestionEngine engine = new(scoring);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        HashSet<string> openSymbols = (await dbContext.Suggestions
            .Where(s => !s.IsShadow && s.Status == SuggestionStatus.Open && s.Horizon == SuggestionHorizon.Swing)
            .Select(s => s.Symbol)
            .ToListAsync(cancellationToken)).ToHashSet();

        HashSet<(string Symbol, TradeDirection Direction)> openShadows = (await dbContext.Suggestions
            .Where(s => s.IsShadow && s.Status == SuggestionStatus.Open && s.Horizon == SuggestionHorizon.Swing)
            .Select(s => new { s.Symbol, s.Direction })
            .ToListAsync(cancellationToken)).Select(s => (s.Symbol, s.Direction)).ToHashSet();
        int shadowsRecorded = 0;

        List<Instrument> instruments = await dbContext.Instruments.AsNoTracking().OrderBy(i => i.SortOrder).ToListAsync(cancellationToken);
        int created = 0;
        List<Suggestion> added = [];

        IReadOnlySet<string> weakCells = scoring.AutoDisableWeakCells
            ? await GetWeakCellsAsync(scoring.WeakCellMinSamples, cancellationToken)
            : new HashSet<string>();
        double? vix = await GetVixAsync(cancellationToken);
        IReadOnlyDictionary<string, int> earnings = await GetEarningsAsync(now, cancellationToken);
        Dictionary<string, PriceLookup?> benchmarks = [];

        foreach (Instrument instrument in instruments.Where(i => !openSymbols.Contains(i.Symbol)))
        {
            try
            {
                IReadOnlyList<Candle> candles = await marketData.GetCandlesAsync(instrument.Symbol, "1d", "1y", cancellationToken);
                IndicatorSeries series = IndicatorCalculator.ComputeSeries(instrument.Symbol, instrument.Name, candles);
                MarketSnapshot? market = series.Snapshots.LastOrDefault();

                if (market is null)
                {
                    continue;
                }

                PriceLookup? benchmark = await GetBenchmarkAsync(instrument, benchmarks, cancellationToken);
                market = market with
                {
                    RelativeStrength = benchmark is null ? null : ContextMetrics.RelativeStrength(series.Candles, series.Candles.Count - 1, benchmark),
                    DaysToEarnings = earnings.TryGetValue(instrument.Symbol, out int days) ? days : null,
                    Vix = vix
                };

                SentimentSnapshot sentiment = await BuildSentimentAsync(instrument.Symbol, now, scoring, SwingNewsWindow, cancellationToken);
                SuggestionDraft? draft = engine.Assess(market, sentiment);

                if (draft is null)
                {
                    continue;
                }

                draft = GateWeakCell(draft, weakCells, WeakCells.Key(SuggestionHorizon.Swing, instrument.AssetClass));

                if (draft.IsShadow)
                {
                    if (!openShadows.Contains((draft.Symbol, draft.Direction)))
                    {
                        dbContext.Suggestions.Add(ToShadowEntity(draft, now, now.AddDays(scoring.ExpiryDays), SuggestionHorizon.Swing, null));
                        openShadows.Add((draft.Symbol, draft.Direction));
                        shadowsRecorded++;
                    }

                    continue;
                }

                Suggestion entity = ToEntity(draft, now, now.AddDays(scoring.ExpiryDays), SuggestionHorizon.Swing);
                dbContext.Suggestions.Add(entity);
                added.Add(entity);
                created++;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Could not score {Symbol}: {Message}", instrument.Symbol, ex.Message);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await alerts.RaiseAsync(AlertFactory.ForNewSuggestions(added, now), cancellationToken);
        logger.LogInformation("Suggestion cycle created {Count} suggestions and recorded {Shadows} shadow ones", created, shadowsRecorded);
        return created;
    }

    private static SuggestionDraft GateWeakCell(SuggestionDraft draft, IReadOnlySet<string> weakCells, params string[] keys) =>
        !draft.IsShadow && keys.Any(weakCells.Contains)
            ? draft with { Blockers = ["célula com resultado negativo comprovado (tipo de ativo ou hora)"] }
            : draft;

    private async Task<IReadOnlySet<string>> GetWeakCellsAsync(int minIndependentCases, CancellationToken cancellationToken)
    {
        string key = $"weak-cells:{minIndependentCases}";

        if (cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null)
        {
            return cached;
        }

        List<Suggestion> closed = await dbContext.Suggestions
            .AsNoTracking()
            .Where(s => s.Variant == "A" && s.Status != SuggestionStatus.Open && s.RMultiple != null)
            .ToListAsync(cancellationToken);

        Dictionary<string, AssetClass> classes = await dbContext.Instruments.AsNoTracking().ToDictionaryAsync(i => i.Symbol, i => i.AssetClass, cancellationToken);
        CostModel costs = new();
        List<CellOutcome> outcomes = [];

        foreach (Suggestion suggestion in closed.Where(s => classes.ContainsKey(s.Symbol)))
        {
            AssetClass assetClass = classes[suggestion.Symbol];
            double netR = suggestion.RMultiple!.Value - SuggestionCosts.CostR(ToDto(suggestion), assetClass, costs);
            DateOnly day = DateOnly.FromDateTime(suggestion.CreatedAt.UtcDateTime);

            outcomes.Add(new CellOutcome(WeakCells.Key(suggestion.Horizon, assetClass), day, netR));

            if (suggestion.Horizon == SuggestionHorizon.Intraday)
            {
                outcomes.Add(new CellOutcome(WeakCells.HourKey(suggestion.Horizon, assetClass, suggestion.CreatedAt.UtcDateTime.Hour), day, netR));
            }
        }

        IReadOnlySet<string> weak = WeakCells.Find(outcomes, minIndependentCases);
        cache.Set(key, weak, CalibrationCacheDuration);
        return weak;
    }

    private async Task<double?> GetVixAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<Candle>? candles = await marketData.GetCandlesAsync("^VIX", "1d", "1mo", cancellationToken);
            return candles is { Count: > 0 } ? candles[^1].Close : null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not load VIX: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> GetEarningsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            CalendarFeed? feed = await calendar.GetAsync(14, cancellationToken);
            DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);

            return (feed?.Events ?? [])
                .Where(e => e.Kind == CalendarKind.Earnings && e.Symbol is not null && e.Date >= today)
                .GroupBy(e => e.Symbol!)
                .ToDictionary(g => g.Key, g => g.Min(e => e.Date.DayNumber) - today.DayNumber);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not load earnings dates: {Message}", ex.Message);
            return new Dictionary<string, int>();
        }
    }

    private async Task<PriceLookup?> GetBenchmarkAsync(Instrument instrument, Dictionary<string, PriceLookup?> loaded, CancellationToken cancellationToken)
    {
        if (BenchmarkSelector.For(instrument.Symbol, instrument.AssetClass) is not { } benchmark)
        {
            return null;
        }

        if (loaded.TryGetValue(benchmark.Symbol, out PriceLookup? existing))
        {
            return existing;
        }

        try
        {
            IReadOnlyList<Candle>? candles = await marketData.GetCandlesAsync(benchmark.Symbol, "1d", "1y", cancellationToken);
            loaded[benchmark.Symbol] = candles is { Count: > 0 } ? new PriceLookup(candles) : null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not load benchmark {Symbol}: {Message}", benchmark.Symbol, ex.Message);
            loaded[benchmark.Symbol] = null;
        }

        return loaded[benchmark.Symbol];
    }

    private async Task<double?> GetDailyTrendAsync(string symbol, CancellationToken cancellationToken)
    {
        string key = $"daily-trend:{symbol}";

        if (cache.TryGetValue(key, out double? cached))
        {
            return cached;
        }

        double? trend = null;

        try
        {
            IReadOnlyList<Candle>? candles = await marketData.GetCandlesAsync(symbol, "1d", "6mo", cancellationToken);

            if (candles is { Count: > 0 } && IndicatorCalculator.Compute(symbol, symbol, candles) is { } snapshot)
            {
                trend = ContextMetrics.DailyTrend(snapshot);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not load daily trend for {Symbol}: {Message}", symbol, ex.Message);
        }

        cache.Set(key, trend, DailyTrendCacheDuration);
        return trend;
    }

    private async Task<MetaModel?> GetMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken)
    {
        string key = MetaModelStore.CacheKey(horizon);

        if (cache.TryGetValue(key, out MetaModel? cached))
        {
            return cached;
        }

        AppSetting? setting = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == MetaModelStore.SettingKey(horizon), cancellationToken);
        MetaModel? model = setting is null ? null : JsonSerializer.Deserialize<MetaModel>(setting.Value, JsonOptions);
        cache.Set(key, model, CalibrationCacheDuration);
        return model;
    }

    private async Task<SentimentSnapshot> BuildSentimentAsync(string symbol, DateTimeOffset now, ScoringOptions scoring, TimeSpan window, CancellationToken cancellationToken)
    {
        DateTimeOffset since = now - window;

        var rows = await (
            from articleSymbol in dbContext.Set<ArticleSymbol>().AsNoTracking()
            join article in dbContext.Articles.AsNoTracking() on articleSymbol.ArticleId equals article.Id
            where articleSymbol.Symbol == symbol && article.PublishedAt >= since && article.Sentiment != null
            select new { article.Title, article.Url, article.Source, article.SourceType, Sentiment = article.Sentiment!.Value, article.PublishedAt })
            .ToListAsync(cancellationToken);

        return SentimentAggregator.Build(
            rows.Select(r => new ArticleSentimentRow(r.Title, r.Url, r.Source, r.SourceType, r.Sentiment, r.PublishedAt)).ToList(),
            now,
            scoring,
            await GetSourceWeightsAsync(cancellationToken),
            cache.TryGetValue("category-weights", out IReadOnlyDictionary<string, double>? categoryWeights) ? categoryWeights : null);
    }

    private static Suggestion ToEntity(SuggestionDraft draft, DateTimeOffset now, DateTimeOffset expiresAt, SuggestionHorizon horizon) => new()
    {
        Symbol = draft.Symbol,
        Name = draft.Name,
        Direction = draft.Direction,
        Confidence = draft.Confidence,
        Score = draft.Score,
        Entry = draft.Entry,
        StopLoss = draft.StopLoss,
        TakeProfit = draft.TakeProfit,
        RiskReward = draft.RiskReward,
        Explanation = draft.Explanation,
        EvidenceJson = JsonSerializer.Serialize(new SuggestionEvidence(draft.Signals, draft.Articles), JsonOptions),
        CreatedAt = now,
        ExpiresAt = expiresAt,
        Status = SuggestionStatus.Open,
        Horizon = horizon
    };

    private static Suggestion ToShadowEntity(SuggestionDraft draft, DateTimeOffset now, DateTimeOffset expiresAt, SuggestionHorizon horizon, string? extraReason)
    {
        Suggestion entity = ToEntity(draft, now, expiresAt, horizon);
        entity.IsShadow = true;
        entity.ShadowReason = string.Join("; ", draft.Reasons.Append(extraReason).OfType<string>());
        return entity;
    }

    private async Task<IReadOnlyList<IntradayContext>> EnrichAsync(IReadOnlyList<IntradayContext> contexts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Dictionary<string, double?> changes = [];
        double? vix = null;

        try
        {
            foreach (InstrumentQuote quote in await marketData.GetQuotesAsync(null, cancellationToken) ?? [])
            {
                changes[quote.Symbol] = quote.ChangePercent;

                if (quote.Symbol == "^VIX")
                {
                    vix = quote.Price;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not load benchmark moves: {Message}", ex.Message);
        }

        string? macro = null;

        if (intradayOptions.AvoidMacroHours > 0)
        {
            try
            {
                CalendarFeed? feed = await calendar.GetAsync(2, cancellationToken);
                TimeSpan window = TimeSpan.FromHours(intradayOptions.AvoidMacroHours);

                macro = (feed?.Events ?? [])
                    .Where(e => e.Kind == CalendarKind.Macro && MacroKnowledge.Find(e.Title)?.Importance == EventImportance.High)
                    .Select(e => (Event: e, Time: CalendarTimes.TimeFor(e)))
                    .Where(x => x.Time is { } time && Math.Abs((time - now).TotalHours) <= window.TotalHours)
                    .Select(x => x.Event.Title)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not check macro events: {Message}", ex.Message);
            }
        }

        List<IntradayContext> enriched = [];

        foreach (IntradayContext c in contexts)
        {
            double? dailyTrend = Math.Abs(c.RecentMove) >= intradayOptions.MinEntryMove ? await GetDailyTrendAsync(c.Symbol, cancellationToken) : null;

            enriched.Add(c with
            {
                MarketChangePercent = BenchmarkSelector.For(c.Symbol, c.AssetClass) is { } benchmark && changes.TryGetValue(benchmark.Symbol, out double? change) ? change : null,
                MacroEvent = macro,
                Vix = vix,
                DailyTrend = dailyTrend
            });
        }

        return enriched;
    }

    internal static SuggestionDto ToDto(Suggestion s)
    {
        SuggestionEvidence evidence = JsonSerializer.Deserialize<SuggestionEvidence>(s.EvidenceJson, JsonOptions)
            ?? new SuggestionEvidence([], []);

        return new SuggestionDto(
            s.Id,
            s.Symbol,
            s.Name,
            s.Direction,
            s.Confidence,
            s.Entry,
            s.StopLoss,
            s.TakeProfit,
            s.RiskReward,
            s.Explanation,
            s.CreatedAt,
            s.ExpiresAt,
            s.Status,
            s.ClosedAt,
            s.ExitPrice,
            s.RMultiple,
            evidence.Signals,
            evidence.Articles,
            s.Horizon,
            IsShadow: s.IsShadow,
            ShadowReason: s.ShadowReason,
            MfeR: s.MfeR,
            MaeR: s.MaeR,
            SmartR: s.SmartR,
            Variant: s.Variant);
    }
}
