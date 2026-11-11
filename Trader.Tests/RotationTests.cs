using FluentAssertions;
using Trader.Core.Models;
using Trader.Core.Rotation;

namespace Trader.Tests;

public class RotationTests
{
    private static readonly DateTimeOffset Start = new(2005, 1, 3, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static List<Candle> Daily(double dailyDrift, double noise, int seed, int days = 20 * 365)
    {
        Random random = new(seed);
        List<Candle> candles = [];
        double price = 100;

        for (int d = 0; d < days; d++)
        {
            DateTimeOffset time = Start.AddDays(d);

            if (time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            price *= Math.Exp(dailyDrift + noise * (random.NextDouble() * 2 - 1));
            candles.Add(new Candle(time, price, price, price, price, 1000));
        }

        return candles;
    }

    private static List<RotationInput> Universe(string? trender = null, double trendDrift = 0.0006)
    {
        int seed = 1;

        return RotationEngine.Universe
            .Select(symbol => new RotationInput(symbol, Daily(symbol == trender ? trendDrift : 0.0001, 0.01, seed++)))
            .ToList();
    }

    [Fact]
    public void Run_NotEnoughHistory_ReturnsNull() =>
        RotationEngine.Run(RotationEngine.Universe.Select(s => new RotationInput(s, Daily(0.0001, 0.01, 3, days: 400))).ToList(), AsOf).Should().BeNull();

    [Fact]
    public void Run_MissingBenchmark_ReturnsNull() =>
        RotationEngine.Run(Universe().Where(i => i.Symbol != "SPY").ToList(), AsOf).Should().BeNull();

    [Fact]
    public void Run_ReturnsBenchmarksAndThreeStrategiesWithCoherentStats()
    {
        RotationResult result = RotationEngine.Run(Universe(), AsOf)!;

        result.Strategies.Select(s => s.Key).Should().Equal("spy", "equal-sectors", "sector-momentum", "global-momentum", "trend");
        result.Strategies.Where(s => s.IsBenchmark).Select(s => s.Key).Should().Equal("spy", "equal-sectors");
        result.Months.Should().BeGreaterThan(200);
        result.To.Should().BeBefore(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
        result.Strategies.Should().OnlyContain(s => s.Stats.MaxDrawdown >= 0 && s.Stats.MaxDrawdown <= 1 && s.Stats.PositiveMonthsShare > 0 && s.Stats.PositiveMonthsShare < 1);
        result.Strategies.Single(s => s.Key == "spy").Verdict.Should().Be(RotationVerdicts.Benchmark);
    }

    [Fact]
    public void Run_StrongTrenderInTheUniverse_IsHeldByMomentumAndBeatsTheEqualWeightedSectors()
    {
        RotationResult result = RotationEngine.Run(Universe("XLK", 0.0012), AsOf)!;

        RotationStrategyResult momentum = result.Strategies.Single(s => s.Key == "sector-momentum");
        RotationStrategyResult equal = result.Strategies.Single(s => s.Key == "equal-sectors");

        momentum.CurrentHoldings.Select(h => h.Symbol).Should().Contain("XLK");
        momentum.CurrentHoldings.Should().HaveCount(RotationEngine.TopCount);
        momentum.Stats.Cagr.Should().BeGreaterThan(equal.Stats.Cagr);
    }

    [Fact]
    public void Run_HigherCosts_NeverImproveTheResult()
    {
        RotationResult result = RotationEngine.Run(Universe("XLK", 0.0008), AsOf)!;

        foreach (RotationStrategyResult strategy in result.Strategies.Where(s => !s.IsBenchmark))
        {
            strategy.CagrWithHighCosts.Should().BeLessThanOrEqualTo(strategy.Stats.Cagr + 1e-12);
        }
    }

    [Fact]
    public void Run_GlobalMomentumInAFallingMarket_GoesToCash()
    {
        List<RotationInput> falling = RotationEngine.Universe.Select((symbol, i) => new RotationInput(symbol, Daily(-0.0004, 0.002, 20 + i))).ToList();

        RotationResult result = RotationEngine.Run(falling, AsOf)!;

        result.Strategies.Single(s => s.Key == "global-momentum").CurrentHoldings.Should().BeEmpty();
    }

    [Fact]
    public void Run_SameInput_GivesTheSameResult()
    {
        RotationEngine.Run(Universe("XLE", 0.0005), AsOf)!.Strategies.Select(s => s.Stats.Cagr)
            .Should().Equal(RotationEngine.Run(Universe("XLE", 0.0005), AsOf)!.Strategies.Select(s => s.Stats.Cagr));
    }

    [Fact]
    public void Stats_ConstantMonthlyReturn_HasKnownGrowthAndNoRisk()
    {
        double[] returns = Enumerable.Repeat(0.01, 24).ToArray();

        RotationStats stats = RotationEngine.Stats(returns, new double[24]);

        stats.Cagr.Should().BeApproximately(Math.Pow(1.01, 12) - 1, 1e-9);
        stats.Volatility.Should().BeApproximately(0, 1e-9);
        stats.MaxDrawdown.Should().Be(0);
        stats.PositiveMonthsShare.Should().Be(1);
    }

    [Fact]
    public void Stats_DropFollowedByRecovery_ReportsTheDrawdown()
    {
        RotationStats stats = RotationEngine.Stats([0.1, -0.2, 0.05], [0, 0, 0]);

        stats.MaxDrawdown.Should().BeApproximately(0.2, 1e-9);
    }
}
