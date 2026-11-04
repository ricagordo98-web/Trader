using Trader.Core.Backtesting;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Infrastructure.Explain;
using Trader.Infrastructure.Suggestions;

namespace Trader.Infrastructure.Backtesting;

public sealed record BacktestMarketContext(IReadOnlyDictionary<string, IReadOnlyList<Candle>> Benchmarks, IReadOnlyList<Candle> Vix);

public sealed record SymbolHistory(string Symbol, string Name, AssetClass AssetClass, IReadOnlyList<Candle> Candles);

public static class BacktestRunner
{
    public const int WarmupBars = 200;

    private static readonly SentimentSnapshot NoSentiment = new(0, 0, null, []);

    private const string TechnicalOnlyNote = "Só foram usados sinais técnicos (tendência, momentum e Bollinger). Não existe sentimento histórico, por isso o sinal mais importante do motor ao vivo não foi testado.";

    private static readonly IReadOnlyList<string> CommonNotes =
    [
        "As entradas são ao fecho do dia do sinal. SL e TP são verificados com velas diárias; se ambos forem tocados no mesmo dia assume-se que o SL veio primeiro (pessimista).",
        "Os custos são pressupostos editáveis (spread de ida e volta mais financiamento por dia), não os valores exatos da tua corretora.",
        "Os parâmetros do motor não foram otimizados neste histórico. Se os ajustares para melhorar este resultado, o resultado deixa de ser fiável (sobreajuste).",
        "A watchlist atual é feita de ativos que hoje já são grandes e conhecidos (NVDA, AAPL, etc.). Testar o passado só com os vencedores de hoje inflaciona o resultado (viés de sobrevivência), sobretudo nas compras de ações.",
        "Cada ativo tem no máximo uma operação aberta; entre ativos diferentes as operações podem sobrepor-se e o R total soma-as como se fossem independentes."
    ];

    public static BacktestResult Run(
        IReadOnlyList<SymbolHistory> histories,
        ScoringOptions scoring,
        BacktestRequest request,
        DateTimeOffset generatedAt,
        IReadOnlyDictionary<string, IReadOnlyList<ArticleSentimentRow>>? sentimentRows = null,
        BacktestMarketContext? marketContext = null)
    {
        bool useSentiment = request.UseSentiment && sentimentRows is not null;
        bool useContext = request.UseMarketContext && marketContext is not null;
        Dictionary<string, PriceLookup> benchmarkLookups = useContext
            ? marketContext!.Benchmarks.ToDictionary(b => b.Key, b => new PriceLookup(b.Value))
            : [];
        PriceLookup? vixLookup = useContext && marketContext!.Vix.Count > 0 ? new PriceLookup(marketContext.Vix) : null;
        DateTimeOffset sentimentFrom = DateTimeOffset.MaxValue;
        SuggestionEngine engine = new(scoring);
        List<BacktestTrade> strategy = [];
        List<BacktestTrade> baseline = [];
        List<double> buyAndHold = [];
        DateTimeOffset from = DateTimeOffset.MaxValue;
        DateTimeOffset to = DateTimeOffset.MinValue;
        int tested = 0;

        foreach (SymbolHistory history in histories)
        {
            IndicatorSeries series = IndicatorCalculator.ComputeSeries(history.Symbol, history.Name, history.Candles);
            IReadOnlyList<Candle> candles = series.Candles;

            if (candles.Count <= WarmupBars + 1)
            {
                continue;
            }

            IReadOnlyList<ArticleSentimentRow>? rows = null;
            DateTimeOffset firstDay = candles[WarmupBars].Time;

            if (useSentiment)
            {
                if (!sentimentRows!.TryGetValue(history.Symbol, out rows) || rows.Count == 0)
                {
                    continue;
                }

                DateTimeOffset start = rows.Min(r => r.PublishedAt).AddDays(SentimentWarmupDays);
                firstDay = start > firstDay ? start : firstDay;
                sentimentFrom = firstDay < sentimentFrom ? firstDay : sentimentFrom;
            }

            tested++;
            from = firstDay < from ? firstDay : from;
            to = candles[^1].Time > to ? candles[^1].Time : to;
            if (candles[WarmupBars].Close > 0)
            {
                buyAndHold.Add((candles[^1].Close / candles[WarmupBars].Close - 1) * 100);
            }

            for (int i = WarmupBars; i < candles.Count - 1; i++)
            {
                if (candles[i].Time < firstDay)
                {
                    continue;
                }

                MarketSnapshot? snapshot = series.Snapshots[i];

                if (snapshot is not null)
                {
                    snapshot = useContext
                        ? snapshot with
                        {
                            RelativeStrength = BenchmarkSelector.For(history.Symbol, history.AssetClass) is { } benchmark && benchmarkLookups.TryGetValue(benchmark.Symbol, out PriceLookup? lookup)
                                ? ContextMetrics.RelativeStrength(candles, i, lookup)
                                : null,
                            Vix = vixLookup?.CloseAtOrBefore(candles[i].Time)
                        }
                        : snapshot with { Adx = null, WeeklyTrend = null };
                }

                SentimentSnapshot sentiment = rows is null ? NoSentiment : SentimentAt(rows, candles[i].Time, scoring);
                SuggestionDraft? draft = snapshot is null ? null : engine.Evaluate(snapshot, sentiment);

                if (snapshot is null || draft is null)
                {
                    continue;
                }

                Simulated? main = Simulate(history, candles, i, draft.Direction, draft.StopLoss, draft.TakeProfit, scoring, request.Costs);

                if (main is null)
                {
                    break;
                }

                strategy.Add(main.Trade);

                foreach (TradeDirection direction in new[] { TradeDirection.Buy, TradeDirection.Sell })
                {
                    (double stopLoss, double takeProfit) = engine.ComputeLevels(snapshot, direction);
                    Simulated? alternative = Simulate(history, candles, i, direction, stopLoss, takeProfit, scoring, request.Costs);

                    if (alternative is not null)
                    {
                        baseline.Add(alternative.Trade);
                    }
                }

                i = main.ExitIndex;
            }
        }

        List<BacktestTrade> chronological = strategy.OrderBy(t => t.EntryTime).ToList();
        int half = chronological.Count / 2;

        BacktestStats strategyStats = BacktestStatistics.Calculate(strategy);
        BacktestStats baselineStats = BacktestStatistics.Calculate(baseline);
        BacktestStats firstHalf = BacktestStatistics.Calculate(chronological.Take(half).ToList());
        BacktestStats secondHalf = BacktestStatistics.Calculate(chronological.Skip(half).ToList());

        return new BacktestResult(
            request.Range,
            generatedAt,
            tested == 0 ? generatedAt : from,
            tested == 0 ? generatedAt : to,
            tested,
            request.Costs,
            strategyStats,
            baselineStats,
            firstHalf,
            secondHalf,
            buyAndHold.Count == 0 ? 0 : buyAndHold.Average(),
            Group(strategy, t => t.AssetClass.ToString()),
            Group(strategy, t => t.Direction.ToString()),
            Group(strategy, t => t.Symbol).OrderByDescending(g => g.Stats.TotalNetR).ToList(),
            BacktestStatistics.EquityCurve(strategy),
            BacktestStatistics.Evaluate(strategyStats, baselineStats, firstHalf, secondHalf),
            BuildNotes(useSentiment, sentimentFrom, to, useContext),
            useSentiment,
            useContext);
    }

    private const int SentimentWarmupDays = 3;
    private static readonly TimeSpan CloseOffset = TimeSpan.FromHours(21);

    private static SentimentSnapshot SentimentAt(IReadOnlyList<ArticleSentimentRow> rows, DateTimeOffset day, ScoringOptions scoring)
    {
        DateTimeOffset asOf = new DateTimeOffset(day.UtcDateTime.Date, TimeSpan.Zero) + CloseOffset;

        return SentimentAggregator.Build(rows.Where(r => r.PublishedAt <= asOf).ToList(), asOf, scoring);
    }

    private static IReadOnlyList<string> BuildNotes(bool useSentiment, DateTimeOffset sentimentFrom, DateTimeOffset to, bool useContext)
    {
        string contextNote = useContext
            ? "Foram aplicados os filtros de contexto do motor ao vivo: mercado lateral (ADX), tendência semanal, força relativa face ao mercado e VIX. Os resultados trimestrais não existem no histórico, por isso esse filtro não foi testado."
            : "Os filtros de contexto (ADX, tendência semanal, força relativa e VIX) estavam desligados neste teste.";

        return useSentiment
            ?
            [
                $"O sentimento foi reconstruído a partir das notícias que este servidor guardou, só para os dias entre {sentimentFrom:dd/MM/yyyy} e {to:dd/MM/yyyy}. É um histórico curto: poucas operações não provam nada, só indicam o rumo.",
                contextNote,
                .. CommonNotes
            ]
            : [TechnicalOnlyNote, contextNote, .. CommonNotes];
    }

    private static List<BacktestGroup> Group(IReadOnlyList<BacktestTrade> trades, Func<BacktestTrade, string> key) =>
        trades
            .GroupBy(key)
            .Select(g => new BacktestGroup(g.Key, BacktestStatistics.Calculate(g.ToList())))
            .OrderBy(g => g.Name)
            .ToList();

    private static Simulated? Simulate(
        SymbolHistory history,
        IReadOnlyList<Candle> candles,
        int index,
        TradeDirection direction,
        double stopLoss,
        double takeProfit,
        ScoringOptions scoring,
        CostModel costs)
    {
        double entry = candles[index].Close;
        double risk = Math.Abs(entry - stopLoss);

        if (risk <= 0)
        {
            return null;
        }

        DateTimeOffset createdAt = candles[index].Time;
        DateTimeOffset expiresAt = createdAt.AddDays(scoring.ExpiryDays);
        DateTimeOffset lastTime = candles[^1].Time;

        Suggestion suggestion = new()
        {
            Direction = direction,
            Entry = entry,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt
        };

        List<Candle> window = candles.Skip(index + 1).TakeWhile(c => c.Time <= expiresAt).ToList();
        OutcomeResult? outcome = OutcomeResolver.Resolve(suggestion, window, lastTime >= expiresAt ? expiresAt : lastTime);

        if (outcome is null)
        {
            return null;
        }

        int exitIndex = index;
        while (exitIndex + 1 < candles.Count && candles[exitIndex + 1].Time <= outcome.ClosedAt)
        {
            exitIndex++;
        }

        double days = Math.Max(1, Math.Ceiling((outcome.ClosedAt - createdAt).TotalDays));
        int sign = direction == TradeDirection.Buy ? 1 : -1;
        double grossPercent = sign * (outcome.ExitPrice - entry) / entry * 100;

        BacktestTrade trade = new(
            history.Symbol,
            history.AssetClass,
            direction,
            createdAt,
            outcome.ClosedAt,
            entry,
            outcome.ExitPrice,
            outcome.Status,
            days,
            outcome.RMultiple,
            outcome.RMultiple - costs.CostInR(history.AssetClass, entry, risk, days),
            grossPercent - costs.CostPercent(history.AssetClass, days));

        return new Simulated(trade, exitIndex);
    }

    private sealed record Simulated(BacktestTrade Trade, int ExitIndex);
}
