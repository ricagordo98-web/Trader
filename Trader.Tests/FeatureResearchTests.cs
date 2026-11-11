using FluentAssertions;
using Trader.Core.Backtesting;
using Trader.Core.Models;

namespace Trader.Tests;

public class FeatureResearchTests
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string> { ["signal"] = "Sinal", ["noise"] = "Ruído" };

    private static List<ResearchSample> Samples(bool predictive, int perYear = 400, int years = 8)
    {
        Random random = new(11);
        List<ResearchSample> samples = [];

        for (int year = 0; year < years; year++)
        {
            for (int i = 0; i < perYear; i++)
            {
                double signal = random.NextDouble() * 2 - 1;
                double noise = random.NextDouble() * 2 - 1;
                double netR = (predictive ? 0.4 * signal : 0) + (random.NextDouble() * 2 - 1);
                samples.Add(new ResearchSample("AAPL", AssetClass.Stock, new DateTimeOffset(2018 + year, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i % 300), TradeDirection.Buy, netR,
                    new Dictionary<string, double> { ["signal"] = signal, ["noise"] = noise }));
            }
        }

        return samples;
    }

    [Fact]
    public void Analyze_FeatureThatPredictsTheResult_IsFlaggedAsPossible()
    {
        FeatureResearch research = FeatureEdgeAnalyzer.Analyze(Samples(predictive: true), Labels);

        FeatureEdge signal = research.Edges.Single(e => e.Feature == "signal");
        signal.Verdict.Should().Be(FeatureEdgeAnalyzer.VerdictPossible);
        signal.Correlation.Should().BeGreaterThan(0.1);
        signal.TopNetR.Should().BeGreaterThan(signal.BottomNetR);
        signal.ConsistentYears.Should().Be(signal.Years);
        research.Edges[0].Feature.Should().Be("signal");
    }

    [Fact]
    public void Analyze_PureNoise_IsNeverFlaggedAsPossible()
    {
        FeatureResearch research = FeatureEdgeAnalyzer.Analyze(Samples(predictive: false), Labels);

        research.Edges.Should().OnlyContain(e => e.Verdict != FeatureEdgeAnalyzer.VerdictPossible);
        research.Edges.Should().OnlyContain(e => Math.Abs(e.Correlation) < 0.05);
    }

    [Fact]
    public void Analyze_TooFewSamples_ReportsNoEdges() =>
        FeatureEdgeAnalyzer.Analyze(Samples(true, perYear: 10, years: 3), Labels).Edges.Should().BeEmpty();

    [Fact]
    public void Spearman_PerfectAndInverseMonotoneRelations_AreMinusOneAndOne()
    {
        double[] x = [1, 2, 3, 4, 5, 6];

        FeatureEdgeAnalyzer.Spearman(x, [10, 20, 30, 40, 50, 60]).Should().BeApproximately(1, 1e-9);
        FeatureEdgeAnalyzer.Spearman(x, [60, 50, 40, 30, 20, 10]).Should().BeApproximately(-1, 1e-9);
        FeatureEdgeAnalyzer.Spearman(x, [5, 5, 5, 5, 5, 5]).Should().Be(0);
    }

    [Fact]
    public void ResearchFeatures_AlignsDirectionalFeaturesWithTheTradeSide()
    {
        Trader.Core.Scoring.MarketSnapshot snapshot = new("X", "X", 100, 2, 104, 100, 90, 60, 0.5, 0.8, 95, 110);

        IReadOnlyDictionary<string, double> buy = ResearchFeatures.From(snapshot, TradeDirection.Buy, 0.3);
        IReadOnlyDictionary<string, double> sell = ResearchFeatures.From(snapshot, TradeDirection.Sell, 0.3);

        buy["trend_fast"].Should().Be(2);
        sell["trend_fast"].Should().Be(-2);
        buy["stop_room"].Should().Be(2.5);
        sell["stop_room"].Should().Be(5);
        buy["volatility"].Should().Be(sell["volatility"]);
        buy.Should().NotContainKey("adx");
    }
}

public class BacktestRobustnessTests
{
    private static BacktestTrade Trade(int year, AssetClass assetClass, double gross, double net) => new(
        "X", assetClass, TradeDirection.Buy, new DateTimeOffset(year, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(year, 3, 5, 0, 0, 0, TimeSpan.Zero),
        100, 101, Trader.Core.Models.SuggestionStatus.HitTakeProfit, 4, gross, net, 1);

    [Fact]
    public void Calculate_RescalesOnlyTheCostPartOfEachTrade()
    {
        List<BacktestTrade> trades = [Trade(2020, AssetClass.Stock, 1.0, 0.9), Trade(2021, AssetClass.Stock, -1.0, -1.1)];

        BacktestRobustness result = BacktestRobustnessCalculator.Calculate(trades);

        result.CostScenarios.Single(s => s.Multiplier == 1).Stats.AverageNetR.Should().BeApproximately(-0.1, 1e-9);
        result.CostScenarios.Single(s => s.Multiplier == 2).Stats.AverageNetR.Should().BeApproximately(-0.2, 1e-9);
        result.CostScenarios.Single(s => s.Multiplier == 0.5).Stats.AverageNetR.Should().BeApproximately(-0.05, 1e-9);
        result.CostScenarios.Single(s => s.Multiplier == 1).Stats.AverageGrossR.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void Calculate_GroupsByEntryYearAndDropsCryptoInTheSeparateResult()
    {
        List<BacktestTrade> trades = [Trade(2020, AssetClass.Stock, 1, 0.9), Trade(2020, AssetClass.Crypto, 2, 1.5), Trade(2021, AssetClass.Etf, 0.5, 0.4)];

        BacktestRobustness result = BacktestRobustnessCalculator.Calculate(trades);

        result.ByYear.Select(y => y.Name).Should().Equal("2020", "2021");
        result.ByYear[0].Stats.Trades.Should().Be(2);
        result.WithoutCrypto.Trades.Should().Be(2);
        result.WithoutCrypto.AverageNetR.Should().BeApproximately(0.65, 1e-9);
    }

    [Fact]
    public void Calculate_NoTrades_ReturnsEmptyResults()
    {
        BacktestRobustness result = BacktestRobustnessCalculator.Calculate([]);

        result.ByYear.Should().BeEmpty();
        result.WithoutCrypto.Trades.Should().Be(0);
        result.CostScenarios.Should().HaveCount(4);
    }
}
