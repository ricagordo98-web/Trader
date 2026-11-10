using FluentAssertions;
using Trader.Core.Models;
using Trader.Core.Projection;

namespace Trader.Tests;

public class ProjectionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T13:30:00Z");

    private static List<Candle> Series(int days = 15, double sigma = 0.001, int seed = 7, bool continuous = false, Func<DateTimeOffset, double>? extraDrift = null)
    {
        Random random = new(seed);
        List<Candle> candles = [];
        double price = 100;

        for (int day = 0; day < days; day++)
        {
            for (int bar = 0; bar < 78; bar++)
            {
                DateTimeOffset time = continuous
                    ? Start.AddMinutes((day * 78 + bar) * 5)
                    : Start.AddDays(day).AddMinutes(bar * 5);
                double edge = Math.Abs(bar - 39) / 39.0;
                double vol = sigma * (0.6 + 1.2 * edge);
                double open = price;
                price *= Math.Exp(vol * Normal(random) + (extraDrift?.Invoke(time) ?? 0));
                double wick = vol * Math.Abs(Normal(random)) * 0.3;
                candles.Add(new Candle(time, open, Math.Max(open, price) * (1 + wick), Math.Min(open, price) * (1 - wick), price, 1000));
            }
        }

        return candles;
    }

    private static double Normal(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    [Fact]
    public void Project_TooFewCandles_ReturnsNull() =>
        ProjectionEngine.Project(Series(1), 120).Should().BeNull();

    [Fact]
    public void Project_SameInput_GivesTheSameResult()
    {
        List<Candle> candles = Series();

        ProjectionResult first = ProjectionEngine.Project(candles, 120)!;
        ProjectionResult second = ProjectionEngine.Project(candles, 120)!;

        second.Bands.Should().BeEquivalentTo(first.Bands);
        second.ProbabilityUp.Should().Be(first.ProbabilityUp);
    }

    [Fact]
    public void Project_BandsAreOrderedAndWidenWithTime()
    {
        ProjectionResult result = ProjectionEngine.Project(Series(), 120)!;

        result.Bands.Should().HaveCount(24);
        result.Bands.Should().OnlyContain(b => b.P10 <= b.P25 && b.P25 <= b.P50 && b.P50 <= b.P75 && b.P75 <= b.P90);
        (result.Bands[^1].P90 - result.Bands[^1].P10).Should().BeGreaterThan(result.Bands[0].P90 - result.Bands[0].P10);
    }

    [Fact]
    public void Project_ProjectedBarsAreValidCandlesOnTheFiveMinuteGrid()
    {
        List<Candle> candles = Series();
        ProjectionResult result = ProjectionEngine.Project(candles, 120)!;

        result.Bars.Should().HaveCount(24);
        result.Bars[0].Open.Should().Be(candles[^1].Close);
        result.Bars[0].Time.Should().Be(candles[^1].Time.AddMinutes(5));
        result.Bars.Should().OnlyContain(b => b.High >= Math.Max(b.Open, b.Close) && b.Low <= Math.Min(b.Open, b.Close) && b.Low > 0);
        result.Bars.Zip(result.Bars.Skip(1)).Should().OnlyContain(p => p.Second.Open == p.First.Close);
    }

    [Fact]
    public void Project_MoreVolatileHistory_GivesWiderBands()
    {
        ProjectionResult calm = ProjectionEngine.Project(Series(sigma: 0.001), 120)!;
        ProjectionResult wild = ProjectionEngine.Project(Series(sigma: 0.003), 120)!;

        wild.OneSigmaPercent.Should().BeGreaterThan(calm.OneSigmaPercent * 2);
    }

    [Fact]
    public void Project_ContinuousMarket_StillProjects()
    {
        ProjectionResult? result = ProjectionEngine.Project(Series(continuous: true), 60);

        result.Should().NotBeNull();
        result!.Bars.Should().HaveCount(12);
    }

    [Fact]
    public void Project_ReturnsFiveDriversAndAProbabilityBetweenZeroAndOne()
    {
        ProjectionResult result = ProjectionEngine.Project(Series(), 120)!;

        result.Drivers.Should().HaveCount(7);
        result.ProbabilityUp.Should().BeInRange(0, 1);
    }

    [Fact]
    public void Project_AssetWithATimeOfDayHabit_PicksItUpAndDiffersFromOneWithout()
    {
        List<Candle> habit = Series(continuous: true, extraDrift: t => t.Hour == 15 ? 0.0006 : 0);
        List<Candle> plain = Series(continuous: true);

        ProjectionResult withHabit = ProjectionEngine.Project(habit, 120)!;
        ProjectionResult without = ProjectionEngine.Project(plain, 120)!;

        ProjectionDriver profile = withHabit.Drivers.Single(d => d.Name == "Hábitos deste ativo");
        profile.EffectPercent.Should().NotBe(0);
        Math.Abs(profile.EffectPercent!.Value).Should().BeGreaterThan(Math.Abs(without.Drivers.Single(d => d.Name == "Hábitos deste ativo").EffectPercent!.Value));
    }

    [Fact]
    public void Project_LearnedWeightZero_TurnsTheLearnedFactorsOff()
    {
        ProjectionResult result = ProjectionEngine.Project(Series(), 120, ProjectionEngine.DefaultSimulations, 0)!;

        result.LearnedWeight.Should().Be(0);
        result.Drivers.Where(d => d.Name is "Hábitos deste ativo" or "Situações parecidas").Should().OnlyContain(d => d.EffectPercent == 0 && d.Detail.StartsWith("Desligado"));
    }

    [Fact]
    public void Project_Bars_FollowATypicalPathWithRealisticZigZagInsteadOfASmoothMedian()
    {
        ProjectionResult result = ProjectionEngine.Project(Series(), 120)!;

        double barMoves = result.Bars.Sum(b => Math.Abs(b.Close - b.Open));
        double medianMoves = result.Bands.Zip(result.Bands.Skip(1), (a, b) => Math.Abs(b.P50 - a.P50)).Sum();

        barMoves.Should().BeGreaterThan(medianMoves);
        result.Bars[^1].Close.Should().BeInRange(result.Bands[^1].P25, result.Bands[^1].P75);
    }

    [Fact]
    public void LearnedFactorsHelp_PoorDirectionHitWithEnoughSamples_IsFalse()
    {
        ProjectionCalibrator.LearnedFactorsHelp(new ProjectionCalibration(30, 0.8, 0.5, 0.45, 20)).Should().BeFalse();
        ProjectionCalibrator.LearnedFactorsHelp(new ProjectionCalibration(30, 0.8, 0.5, 0.45, 5)).Should().BeTrue();
        ProjectionCalibrator.LearnedFactorsHelp(new ProjectionCalibration(30, 0.8, 0.5, 0.56, 20)).Should().BeTrue();
        ProjectionCalibrator.LearnedFactorsHelp(null).Should().BeTrue();
    }

    [Fact]
    public void Project_FlatPrices_ReturnsNull()
    {
        List<Candle> flat = Series().Select(c => c with { Open = 100, High = 100, Low = 100, Close = 100 }).ToList();

        ProjectionEngine.Project(flat, 120).Should().BeNull();
    }

    [Fact]
    public void Evaluate_OnARandomWalk_HasReasonableBandCoverage()
    {
        ProjectionCalibration? calibration = ProjectionCalibrator.Evaluate(Series(days: 25), 120);

        calibration.Should().NotBeNull();
        calibration!.Origins.Should().BeGreaterThan(10);
        calibration.Coverage80.Should().BeInRange(0.6, 0.97);
        calibration.Coverage50.Should().BeInRange(0.3, 0.75);
    }

    [Fact]
    public void Project_Plan_PutsTargetAndStopOnTheCorrectSidesWithConsistentProbabilities()
    {
        ProjectionResult result = ProjectionEngine.Project(Series(), 120)!;
        ProjectionPlan plan = result.Plan!;

        if (plan.Direction == TradeDirection.Buy)
        {
            plan.StopLoss.Should().BeLessThan(plan.Entry);
            plan.TakeProfit.Should().BeGreaterThan(plan.Entry);
        }
        else
        {
            plan.StopLoss.Should().BeGreaterThan(plan.Entry);
            plan.TakeProfit.Should().BeLessThan(plan.Entry);
        }

        (plan.ProbabilityTarget + plan.ProbabilityStop + plan.ProbabilityExpire).Should().BeApproximately(1, 1e-9);
        plan.ProbabilityWin.Should().BeInRange(plan.ProbabilityTarget, 1);
        plan.RiskReward.Should().BeApproximately(1, 0.05);
    }

    [Fact]
    public void Project_Plan_OnADriftlessWalkHasExpectedRNearZero()
    {
        ProjectionPlan plan = ProjectionEngine.Project(Series(), 120)!.Plan!;

        plan.ExpectedR.Should().BeInRange(-0.3, 0.3);
    }

    [Fact]
    public void CalculateWithShadow_KeepsProjectionsOutOfTheModelStatistics()
    {
        SuggestionDto Closed(string variant, double r) => new(
            1, "AAPL", "Apple", TradeDirection.Buy, 0.6, 100, 99, 102, 2, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, r > 0 ? SuggestionStatus.HitTakeProfit : SuggestionStatus.HitStopLoss,
            DateTimeOffset.UtcNow, 100, r, [], [], SuggestionHorizon.Intraday, IsShadow: variant == SuggestionVariants.Projection, Variant: variant);

        List<SuggestionDto> items = [Closed("A", 2), Closed("A", -1), Closed(SuggestionVariants.Projection, -1), Closed(SuggestionVariants.Projection, 1), Closed(SuggestionVariants.Projection, -1)];

        Trader.Core.Scoring.ModelReliabilityReport report = Trader.Core.Scoring.ModelReliability.CalculateWithShadow(items, TimeZoneInfo.Utc);

        report.Closed.Should().Be(2);
        report.Projection!.Count.Should().Be(3);
        report.Projection.TargetHits.Should().Be(1);
        report.Projection.StopHits.Should().Be(2);
        report.ByVariant!.Select(g => g.Name).Should().Contain("Projeção (alvos do gráfico)");
    }

    [Fact]
    public void Evaluate_NotEnoughHistory_ReturnsNull() =>
        ProjectionCalibrator.Evaluate(Series(days: 2), 120).Should().BeNull();
}
