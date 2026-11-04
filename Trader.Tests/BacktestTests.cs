using FluentAssertions;
using Trader.Core.Backtesting;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Infrastructure.Backtesting;
using Trader.Infrastructure.Suggestions;

namespace Trader.Tests;

public class BacktestStatisticsTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BacktestTrade Trade(double netR, int day) => new(
        "T", AssetClass.Stock, TradeDirection.Buy, Start.AddDays(day), Start.AddDays(day + 1), 100, 101,
        netR > 0 ? SuggestionStatus.HitTakeProfit : SuggestionStatus.HitStopLoss, 1, netR, netR, netR);

    [Fact]
    public void Calculate_KnownTrades_ReturnsExpectedStatistics()
    {
        BacktestStats stats = BacktestStatistics.Calculate([Trade(2, 1), Trade(-1, 2), Trade(-1, 3), Trade(1.5, 4)]);

        stats.Trades.Should().Be(4);
        stats.WinRate.Should().Be(0.5);
        stats.TotalNetR.Should().BeApproximately(1.5, 1e-9);
        stats.AverageNetR.Should().BeApproximately(0.375, 1e-9);
        stats.ProfitFactor.Should().BeApproximately(3.5 / 2, 1e-9);
        stats.MaxDrawdownR.Should().BeApproximately(2, 1e-9);
        stats.ConfidenceLow.Should().BeLessThan(stats.AverageNetR);
        stats.ConfidenceHigh.Should().BeGreaterThan(stats.AverageNetR);
    }

    [Fact]
    public void Calculate_NoTrades_ReturnsZeroedStats()
    {
        BacktestStats stats = BacktestStatistics.Calculate([]);

        stats.Trades.Should().Be(0);
        stats.ProfitFactor.Should().BeNull();
    }

    [Fact]
    public void Calculate_MoreTradesNarrowTheConfidenceInterval()
    {
        List<BacktestTrade> few = Enumerable.Range(0, 20).Select(i => Trade(i % 2 == 0 ? 1.5 : -1, i)).ToList();
        List<BacktestTrade> many = Enumerable.Range(0, 200).Select(i => Trade(i % 2 == 0 ? 1.5 : -1, i)).ToList();

        BacktestStats small = BacktestStatistics.Calculate(few);
        BacktestStats large = BacktestStatistics.Calculate(many);

        (large.ConfidenceHigh - large.ConfidenceLow).Should().BeLessThan(small.ConfidenceHigh - small.ConfidenceLow);
    }

    [Fact]
    public void EquityCurve_AccumulatesInExitOrder()
    {
        IReadOnlyList<CurvePoint> curve = BacktestStatistics.EquityCurve([Trade(1, 5), Trade(-2, 1), Trade(0.5, 3)]);

        curve.Select(c => c.CumulativeR).Should().Equal(-2, -1.5, -0.5);
    }

    [Fact]
    public void Evaluate_FewTrades_IsInsufficient()
    {
        BacktestStats few = BacktestStatistics.Calculate(Enumerable.Range(0, 10).Select(i => Trade(1, i)).ToList());

        BacktestStatistics.Evaluate(few, few, few, few).Code.Should().Be("insufficient");
    }

    [Fact]
    public void Evaluate_NegativeAverage_IsNoEdge()
    {
        BacktestStats losing = BacktestStatistics.Calculate(Enumerable.Range(0, 40).Select(i => Trade(i % 3 == 0 ? 1 : -1, i)).ToList());

        BacktestStatistics.Evaluate(losing, losing, losing, losing).Code.Should().Be("no-edge");
    }

    [Fact]
    public void Evaluate_ConsistentSignificantWinner_IsPromising()
    {
        List<BacktestTrade> winning = Enumerable.Range(0, 120).Select(i => Trade(i % 4 == 0 ? -1 : 1.2, i)).ToList();
        BacktestStats strategy = BacktestStatistics.Calculate(winning);
        BacktestStats firstHalf = BacktestStatistics.Calculate(winning.Take(60).ToList());
        BacktestStats secondHalf = BacktestStatistics.Calculate(winning.Skip(60).ToList());
        BacktestStats randomBaseline = BacktestStatistics.Calculate(Enumerable.Range(0, 120).Select(i => Trade(i % 2 == 0 ? 1.5 : -1, i)).ToList());

        BacktestStatistics.Evaluate(strategy, randomBaseline, firstHalf, secondHalf).Code.Should().Be("promising");
    }

    [Fact]
    public void Evaluate_PositiveButNotBetterThanBaseline_IsInconclusive()
    {
        List<BacktestTrade> trades = Enumerable.Range(0, 60).Select(i => Trade(i % 2 == 0 ? 1.5 : -1, i)).ToList();
        BacktestStats stats = BacktestStatistics.Calculate(trades);

        BacktestStatistics.Evaluate(stats, stats, stats, stats).Code.Should().Be("inconclusive");
    }
}

public class CostModelTests
{
    [Fact]
    public void CostInR_ScalesWithEntryOverRisk()
    {
        CostModel costs = new() { StockRoundTripPercent = 0.10, OvernightPercentPerDay = 0.02 };

        double costR = costs.CostInR(AssetClass.Stock, entry: 100, risk: 2, daysHeld: 3);

        costR.Should().BeApproximately((0.10 + 0.02 * 3) / 100 * 100 / 2, 1e-9);
    }

    [Fact]
    public void RoundTripPercent_UsesAssetClassSpecificSpread()
    {
        CostModel costs = new() { ForexRoundTripPercent = 0.01, CommodityRoundTripPercent = 0.07 };

        costs.RoundTripPercent(AssetClass.Forex).Should().Be(0.01);
        costs.RoundTripPercent(AssetClass.Commodity).Should().Be(0.07);
    }

    [Theory]
    [InlineData("1y", 0.1, false)]
    [InlineData("5y", 0.1, true)]
    [InlineData("5y", -1, false)]
    [InlineData("5y", 9, false)]
    public void Validator_Cases(string range, double stockCost, bool valid)
    {
        BacktestRequest request = new() { Range = range, Costs = new CostModel { StockRoundTripPercent = stockCost } };

        (BacktestRequestValidator.Validate(request).Count == 0).Should().Be(valid);
    }
}

public class BacktestRunnerTests
{
    private static List<Candle> SyntheticSeries(int seed, int days)
    {
        Random random = new(seed);
        double price = 100;
        DateTimeOffset start = new(2020, 1, 1, 14, 30, 0, TimeSpan.Zero);
        List<Candle> candles = [];

        for (int day = 0; day < days; day++)
        {
            double drift = Math.Sin(day / 40.0) * 0.6;
            double open = price;
            double close = Math.Max(5, open + drift + (random.NextDouble() - 0.5) * 3);
            double high = Math.Max(open, close) + random.NextDouble() * 1.2;
            double low = Math.Min(open, close) - random.NextDouble() * 1.2;
            candles.Add(new Candle(start.AddDays(day), open, high, low, close, 1000));
            price = close;
        }

        return candles;
    }

    private static List<SymbolHistory> Histories(int days) =>
        Enumerable.Range(1, 6)
            .Select(i => new SymbolHistory($"S{i}", $"Synthetic {i}", AssetClass.Stock, SyntheticSeries(i, days)))
            .ToList();

    [Fact]
    public void Run_SyntheticData_ProducesTradesWithConsistentAccounting()
    {
        BacktestResult result = BacktestRunner.Run(Histories(900), new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);

        result.Strategy.Trades.Should().BeGreaterThan(0);
        result.Baseline.Trades.Should().BeGreaterThanOrEqualTo(result.Strategy.Trades);
        result.Strategy.AverageNetR.Should().BeLessThanOrEqualTo(result.Strategy.AverageGrossR);
        result.BySymbol.Sum(g => g.Stats.Trades).Should().Be(result.Strategy.Trades);
        result.FirstHalf.Trades.Should().Be(result.Strategy.Trades / 2);
    }

    [Fact]
    public void Run_WithSentiment_OnlyTestsDaysCoveredByArticles()
    {
        List<SymbolHistory> histories = Histories(900);
        DateTimeOffset firstArticle = histories[0].Candles[500].Time;
        Dictionary<string, IReadOnlyList<ArticleSentimentRow>> rows = histories.ToDictionary(
            h => h.Symbol,
            h => (IReadOnlyList<ArticleSentimentRow>)Enumerable.Range(0, 300)
                .Select(d => new ArticleSentimentRow("t", $"https://x/{d}", "src", SourceType.News, d % 2 == 0 ? 0.8 : 0.6, firstArticle.AddDays(d)))
                .ToList());

        BacktestResult result = BacktestRunner.Run(histories, new ScoringOptions(), new BacktestRequest { UseSentiment = true }, DateTimeOffset.UtcNow, rows);

        result.UsedSentiment.Should().BeTrue();
        result.From.Should().BeOnOrAfter(firstArticle.AddDays(3));
        result.Notes[0].Should().Contain("reconstruído");
    }

    [Fact]
    public void Run_WithSentimentButNoArticles_TestsNothing()
    {
        BacktestResult result = BacktestRunner.Run(
            Histories(900),
            new ScoringOptions(),
            new BacktestRequest { UseSentiment = true },
            DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyList<ArticleSentimentRow>>());

        result.SymbolsTested.Should().Be(0);
        result.Strategy.Trades.Should().Be(0);
    }

    [Fact]
    public void Run_SymbolWithZeroCloseAtStart_KeepsBuyAndHoldFinite()
    {
        List<SymbolHistory> histories = Histories(900);
        List<Candle> candles = histories[0].Candles.ToList();
        candles[BacktestRunner.WarmupBars] = candles[BacktestRunner.WarmupBars] with { Close = 0 };
        histories[0] = histories[0] with { Candles = candles };

        BacktestResult result = BacktestRunner.Run(histories, new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);

        double.IsFinite(result.BuyAndHoldPercent).Should().BeTrue();
    }

    [Fact]
    public void Run_WithoutSentiment_KeepsTechnicalOnlyNote()
    {
        BacktestResult result = BacktestRunner.Run(Histories(900), new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);

        result.UsedSentiment.Should().BeFalse();
        result.Notes[0].Should().Contain("sinais técnicos");
    }

    [Fact]
    public void Run_HigherCosts_ReduceNetResultButNotGross()
    {
        BacktestResult cheap = BacktestRunner.Run(Histories(900), new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);
        BacktestResult expensive = BacktestRunner.Run(
            Histories(900),
            new ScoringOptions(),
            new BacktestRequest { Costs = new CostModel { StockRoundTripPercent = 0.8, OvernightPercentPerDay = 0.08 } },
            DateTimeOffset.UtcNow);

        expensive.Strategy.AverageGrossR.Should().BeApproximately(cheap.Strategy.AverageGrossR, 1e-9);
        expensive.Strategy.AverageNetR.Should().BeLessThan(cheap.Strategy.AverageNetR);
    }

    [Fact]
    public void Run_HasNoLookAhead_TruncatedHistoryReproducesEarlierTrades()
    {
        List<SymbolHistory> full = Histories(900);
        List<SymbolHistory> truncated = full
            .Select(h => h with { Candles = h.Candles.Take(650).ToList() })
            .ToList();
        DateTimeOffset cutoff = truncated[0].Candles[^1].Time;

        BacktestResult fullResult = BacktestRunner.Run(full, new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);
        BacktestResult truncatedResult = BacktestRunner.Run(truncated, new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);

        truncatedResult.Strategy.Trades.Should().BeGreaterThan(0);

        double fullTotalBeforeCutoff = fullResult.Curve.Where(p => p.Time <= cutoff).Select(p => p.CumulativeR).LastOrDefault();
        double truncatedTotal = truncatedResult.Curve.Select(p => p.CumulativeR).LastOrDefault();

        truncatedResult.Curve.Count.Should().Be(fullResult.Curve.Count(p => p.Time <= cutoff));
        truncatedTotal.Should().BeApproximately(fullTotalBeforeCutoff, 1e-9);
    }

    [Fact]
    public void Run_TooShortHistory_TestsNoSymbols()
    {
        BacktestResult result = BacktestRunner.Run(Histories(150), new ScoringOptions(), new BacktestRequest(), DateTimeOffset.UtcNow);

        result.SymbolsTested.Should().Be(0);
        result.Strategy.Trades.Should().Be(0);
        result.Verdict.Code.Should().Be("insufficient");
    }
}

public class IndicatorSeriesTests
{
    [Fact]
    public void ComputeSeries_LastSnapshot_MatchesCompute()
    {
        List<Candle> candles = Enumerable.Range(0, 260)
            .Select(i => new Candle(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i), 100 + i * 0.3, 102 + i * 0.3, 98 + i * 0.3, 100 + i * 0.3 + Math.Sin(i), 10))
            .ToList();

        MarketSnapshot? last = IndicatorCalculator.ComputeSeries("X", "X", candles).Snapshots[^1];
        MarketSnapshot? single = IndicatorCalculator.Compute("X", "X", candles);

        last.Should().NotBeNull();
        single.Should().BeEquivalentTo(last);
    }

    [Fact]
    public void ComputeSeries_SnapshotsOnlyUseCandlesUpToTheirIndex()
    {
        List<Candle> all = Enumerable.Range(0, 300)
            .Select(i => new Candle(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i), 100, 102 + Math.Cos(i), 98 - Math.Sin(i), 100 + Math.Sin(i / 5.0) * 4, 10))
            .ToList();

        IndicatorSeries full = IndicatorCalculator.ComputeSeries("X", "X", all);
        IndicatorSeries partial = IndicatorCalculator.ComputeSeries("X", "X", all.Take(250).ToList());

        full.Snapshots[240].Should().BeEquivalentTo(partial.Snapshots[240]);
    }
}
