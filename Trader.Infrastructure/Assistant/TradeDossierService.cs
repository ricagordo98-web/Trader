using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Analysis;
using Trader.Core.Assistant;
using Trader.Core.Calendar;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Core.TradingSystem;

namespace Trader.Infrastructure.Assistant;

public sealed class TradeDossierService(
    ISuggestionService suggestions,
    ITradeAnalysisService analysisService,
    IMarketDataService marketData,
    INewsService news,
    ICalendarService calendar,
    IJournalService journal,
    ITradingSystemService system,
    TimeProvider timeProvider,
    ILogger<TradeDossierService> logger) : ITradeDossierService
{
    private const int NewsLimit = 6;
    private const int EventDays = 7;
    private static readonly TimeSpan NewsWindow = TimeSpan.FromDays(7);

    public async Task<TradeDossier?> BuildAsync(string userId, string symbol, TradeDirection? direction, CancellationToken cancellationToken)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        TradeContext? context = await suggestions.GetContextAsync(symbol, cancellationToken);

        if (context is null)
        {
            return null;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        MarketSnapshot market = context.Market;
        SuggestionEngine engine = new(context.Scoring);
        IReadOnlyList<SuggestionDto> open = await suggestions.GetAsync(symbol, true, 1, null, cancellationToken);
        IReadOnlyList<SuggestionDto> closed = await ClosedAsync(symbol, cancellationToken);

        TradeDirection chosen = direction ?? open.FirstOrDefault()?.Direction ?? ModelDirection(engine, market, context.Sentiment) ?? TradeDirection.Buy;
        (double stop, double target) = engine.ComputeLevels(market, chosen);
        TradeAnalysis? analysis = await analysisService.AnalyzeAsync(new TradeAnalysisRequest(symbol, chosen, market.Price, stop, target), cancellationToken);

        InstrumentQuote? quote = (await marketData.GetQuotesAsync(null, cancellationToken)).FirstOrDefault(q => q.Symbol == symbol);
        IReadOnlyList<Candle> daily = await CandlesAsync(symbol, "1d", "1y", cancellationToken);
        IReadOnlyList<Candle> hourly = await CandlesAsync(symbol, "60m", "1mo", cancellationToken);
        IReadOnlyList<TimeframeView> timeframes = Timeframes(daily, hourly);

        IReadOnlyList<DossierNews> articles = await NewsAsync(symbol, now, cancellationToken);
        IReadOnlyList<string> events = await EventsAsync(symbol, now, cancellationToken);
        IReadOnlyList<JournalTradeDto> trades = await journal.ListAsync(userId, null, cancellationToken);
        IReadOnlyList<OpenExposure> exposure = await system.ExposureAsync(symbol, trades, cancellationToken);

        double risk = Math.Abs(market.Price - stop);
        double rewardRisk = risk > 0 ? Math.Abs(target - market.Price) / risk : 0;
        AccountProfile account = await journal.GetAccountAsync(userId, cancellationToken);
        TradingPlanDto plan = await system.GetPlanAsync(userId, cancellationToken);
        double quantity = PreTradeChecker.SuggestedQuantity(account.Capital, plan.Rules.RiskPercent, 1, market.Price, stop, 1);

        PreTradeCheck check = await system.CheckAsync(userId, new PreTradeRequest(symbol, chosen, market.Price, stop, target, quantity, 1, null), cancellationToken);
        double? riskPercent = quantity > 0 && account.Capital > 0 ? risk * quantity / account.Capital * 100 : null;
        TradeDraft draft = new(symbol, chosen, market.Price, stop, target, quantity, 1, riskPercent, rewardRisk);

        DossierModel model = new(
            open.FirstOrDefault(),
            closed.Count,
            closed.Count == 0 ? null : closed.Count(s => s.RMultiple > 0) / (double)closed.Count,
            closed.Count == 0 ? null : closed.Average(s => s.RMultiple ?? 0));

        TradeDossier dossier = new(
            symbol,
            context.Name,
            context.AssetClass,
            quote?.Currency ?? string.Empty,
            market.Price,
            quote?.ChangePercent,
            quote?.AsOf,
            chosen,
            direction is not null,
            timeframes,
            market.Atr,
            market.SwingLow,
            market.SwingHigh,
            daily.Count > 0 ? daily.Max(c => c.High) : null,
            daily.Count > 0 ? daily.Min(c => c.Low) : null,
            analysis,
            articles,
            events,
            model,
            exposure,
            check,
            draft,
            string.Empty,
            now);

        return dossier with { Summary = DossierText.Summary(dossier) };
    }

    private static TradeDirection? ModelDirection(SuggestionEngine engine, MarketSnapshot market, SentimentSnapshot sentiment)
    {
        (_, double score) = engine.ScoreSignals(market, sentiment);
        return Math.Abs(score) < 0.1 ? null : score > 0 ? TradeDirection.Buy : TradeDirection.Sell;
    }

    private async Task<IReadOnlyList<SuggestionDto>> ClosedAsync(string symbol, CancellationToken cancellationToken)
    {
        IReadOnlyList<SuggestionDto> all = await suggestions.GetAsync(symbol, false, 200, SuggestionHorizon.Swing, cancellationToken);
        return all.Where(s => s.Status != SuggestionStatus.Open && s.RMultiple is not null && !s.IsShadow && s.Variant == "A").ToList();
    }

    private async Task<IReadOnlyList<Candle>> CandlesAsync(string symbol, string interval, string range, CancellationToken cancellationToken)
    {
        try
        {
            return await marketData.GetCandlesAsync(symbol, interval, range, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("No {Interval} candles for the dossier of {Symbol}: {Message}", interval, symbol, ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<DossierNews>> NewsAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ArticleDto> articles = await news.GetArticlesAsync(new NewsQuery(Symbol: symbol, Limit: 60), cancellationToken);

            return articles
                .Where(a => now - a.PublishedAt <= NewsWindow)
                .OrderByDescending(a => Math.Abs(a.Sentiment ?? 0) * 0.5 + (a.PublishedAt - now).TotalDays / 14 + 0.5)
                .Take(NewsLimit)
                .OrderByDescending(a => a.PublishedAt)
                .Select(a => new DossierNews(a.Title, a.Url, a.Source, a.PublishedAt, a.Sentiment))
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("No news for the dossier of {Symbol}: {Message}", symbol, ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> EventsAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            CalendarFeed feed = await calendar.GetAsync(EventDays, cancellationToken);
            List<string> events = [];

            foreach (CalendarEvent e in feed.Events.OrderBy(e => e.Date))
            {
                if (e.Kind == CalendarKind.Earnings && string.Equals(e.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                {
                    events.Add($"resultados de {symbol} a {e.Date:dd/MM}");
                }
                else if (e.Kind == CalendarKind.Macro && MacroKnowledge.Find(e.Title)?.Importance == EventImportance.High)
                {
                    events.Add($"{e.Title} a {e.Date:dd/MM}");
                }
            }

            return events.Distinct().Take(8).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("No calendar for the dossier of {Symbol}: {Message}", symbol, ex.Message);
            return [];
        }
    }

    private static IReadOnlyList<TimeframeView> Timeframes(IReadOnlyList<Candle> daily, IReadOnlyList<Candle> hourly)
    {
        List<TimeframeView> views = [];

        if (daily.Count >= 60)
        {
            List<double> weekly = daily
                .GroupBy(c => System.Globalization.ISOWeek.GetYear(c.Time.UtcDateTime) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(c.Time.UtcDateTime))
                .OrderBy(g => g.Key)
                .Select(g => g.Last().Close)
                .ToList();

            if (weekly.Count >= 30)
            {
                views.Add(View("Semanal", weekly, 10, 30, 1));
            }

            views.Add(View("Diário", daily.Select(c => c.Close).ToList(), 20, 50, daily.Count >= 200 ? 200 : 0));
        }

        if (hourly.Count >= 60)
        {
            views.Add(View("Horário", hourly.Select(c => c.Close).ToList(), 20, 50, 0));
        }

        return views;
    }

    private static TimeframeView View(string label, IReadOnlyList<double> closes, int fast, int slow, int longPeriod)
    {
        double[] emaFast = SimpleIndicators.Ema(closes, fast);
        double[] emaSlow = SimpleIndicators.Ema(closes, slow);
        double[] rsi = SimpleIndicators.Rsi(closes);
        int last = closes.Count - 1;
        double price = closes[last];
        string trend = price > emaFast[last] && emaFast[last] > emaSlow[last] ? "em alta"
            : price < emaFast[last] && emaFast[last] < emaSlow[last] ? "em baixa"
            : "lateral";
        int lookback = Math.Min(closes.Count - 1, label == "Horário" ? 24 : label == "Semanal" ? 4 : 20);
        double change = closes[last - lookback] > 0 ? (price / closes[last - lookback] - 1) * 100 : 0;
        string detail = $"Preço {DossierText.Price(price)} face à EMA{fast} {DossierText.Price(emaFast[last])} e EMA{slow} {DossierText.Price(emaSlow[last])}; RSI {rsi[last]:0}; {change:+0.0;-0.0}% nas últimas {lookback} barras.";

        if (longPeriod > 1 && closes.Count >= longPeriod)
        {
            double[] emaLong = SimpleIndicators.Ema(closes, longPeriod);
            detail += $" {(price > emaLong[last] ? "Acima" : "Abaixo")} da EMA{longPeriod} ({DossierText.Price(emaLong[last])}).";
        }

        return new TimeframeView(label, trend, rsi[last], change, detail);
    }
}
