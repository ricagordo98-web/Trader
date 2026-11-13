using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Trader.Core.Abstractions;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Infrastructure.Persistence;
using Trader.Infrastructure.Suggestions;

namespace Trader.Tests;

public class ConfidenceCalibratorTests
{
    private static List<(double, bool)> Outcomes(double confidence, int count, double winRate) =>
        Enumerable.Range(0, count).Select(i => (confidence, (i < count * winRate))).ToList();

    [Fact]
    public void Fit_TooFewSamples_ReturnsNull() =>
        ConfidenceCalibrator.Fit(Outcomes(0.8, 29, 0.5)).Should().BeNull();

    [Fact]
    public void Fit_OverconfidentModel_PullsTheProbabilityDown()
    {
        List<(double, bool)> data = [.. Outcomes(0.62, 40, 0.5), .. Outcomes(0.85, 40, 0.55)];

        ConfidenceCalibration calibration = ConfidenceCalibrator.Fit(data)!;

        calibration.Samples.Should().Be(80);
        calibration.Apply(0.85).Should().BeApproximately(0.55, 0.06);
        calibration.Apply(0.85).Should().BeLessThan(0.85);
    }

    [Fact]
    public void Fit_ResultIsMonotoneEvenWhenHigherConfidenceDidWorse()
    {
        List<(double, bool)> data = [.. Outcomes(0.65, 40, 0.7), .. Outcomes(0.9, 40, 0.4)];

        ConfidenceCalibration calibration = ConfidenceCalibrator.Fit(data)!;

        calibration.Apply(0.9).Should().BeGreaterThanOrEqualTo(calibration.Apply(0.65) - 1e-9);
        calibration.Points.Select(p => p.Probability).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Apply_OutsideTheObservedRange_UsesTheNearestPointAndInterpolatesBetweenPoints()
    {
        ConfidenceCalibration calibration = new([new CalibrationPoint(0.6, 0.4), new CalibrationPoint(0.8, 0.6)], 100, 0.5);

        calibration.Apply(0.1).Should().Be(0.4);
        calibration.Apply(0.99).Should().Be(0.6);
        calibration.Apply(0.7).Should().BeApproximately(0.5, 1e-9);
    }
}

public class SuggestionCalibrationServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private TraderDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbContext = new TraderDbContext(new DbContextOptionsBuilder<TraderDbContext>().UseSqlite(_connection).Options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private SuggestionService Service() => new(
        _dbContext,
        Substitute.For<IMarketDataService>(),
        Substitute.For<IScoringSettings>(),
        Substitute.For<IAlertService>(),
        new Trader.Core.Intraday.IntradayOptions(),
        Substitute.For<ICalendarService>(),
        Substitute.For<IIntradaySettings>(),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<SuggestionService>.Instance);

    private static Suggestion Make(SuggestionStatus status, double? r, double confidence = 0.8) => new()
    {
        Symbol = "TEST",
        Name = "Test",
        Direction = TradeDirection.Buy,
        Confidence = confidence,
        Entry = 100,
        StopLoss = 98,
        TakeProfit = 104,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        ExpiresAt = DateTimeOffset.UtcNow,
        Status = status,
        RMultiple = r,
        Horizon = SuggestionHorizon.Swing
    };

    [Fact]
    public async Task GetAsync_WithEnoughClosedSuggestions_AddsTheHistoricalHitRate()
    {
        for (int i = 0; i < 40; i++)
        {
            _dbContext.Suggestions.Add(i % 2 == 0 ? Make(SuggestionStatus.HitTakeProfit, 2) : Make(SuggestionStatus.HitStopLoss, -1));
        }

        _dbContext.Suggestions.Add(Make(SuggestionStatus.Open, null));
        await _dbContext.SaveChangesAsync();

        IReadOnlyList<SuggestionDto> open = await Service().GetAsync(null, true, 10, null, CancellationToken.None);

        SuggestionDto dto = open.Should().ContainSingle().Subject;
        dto.CalibratedConfidence.Should().BeApproximately(0.5, 0.05);
        dto.CalibrationSamples.Should().Be(40);
    }

    [Fact]
    public async Task GetAsync_WithoutHistory_LeavesTheCalibrationEmpty()
    {
        _dbContext.Suggestions.Add(Make(SuggestionStatus.Open, null));
        await _dbContext.SaveChangesAsync();

        (await Service().GetAsync(null, true, 10, null, CancellationToken.None)).Single().CalibratedConfidence.Should().BeNull();
    }
}

public class ModelReliabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static SuggestionDto Closed(double r, double confidence = 0.7, SuggestionStatus? status = null, TradeDirection direction = TradeDirection.Buy, double sentiment = 0) =>
        new(1, "AAA", "AAA", direction, confidence, 100, 98, 104, 2, "x", Start, Start.AddDays(2), status ?? (r > 0 ? SuggestionStatus.HitTakeProfit : SuggestionStatus.HitStopLoss), Start.AddHours(10), 100, r,
            sentiment == 0 ? [] : [new SignalResult("sentiment", "Sentimento", sentiment, 1, "x")], []);

    [Fact]
    public void Calculate_WithFewSamples_SaysInsufficient()
    {
        ModelReliabilityReport report = ModelReliability.Calculate([Closed(2), Closed(-1)]);

        report.Verdict.Level.Should().Be(ReliabilityLevel.Insufficient);
        report.Closed.Should().Be(2);
    }

    [Fact]
    public void Calculate_WithClearEdge_IsModerateUntilSampleGrows()
    {
        List<SuggestionDto> trades = [.. Enumerable.Range(0, 40).Select(_ => Closed(2)), .. Enumerable.Range(0, 10).Select(_ => Closed(-1))];

        ModelReliabilityReport report = ModelReliability.Calculate(trades);

        report.Verdict.Level.Should().Be(ReliabilityLevel.Moderate);
        report.WinRate.Should().BeApproximately(0.8, 1e-9);
        report.AverageR.Should().BeApproximately(1.4, 1e-9);
        report.BreakEvenWinRate.Should().BeApproximately(1.0 / 3, 1e-9);
        report.TakeProfitHits.Should().Be(40);
        report.StopLossHits.Should().Be(10);
    }

    [Fact]
    public void Calculate_WithLosingModel_ReportsNoEdge()
    {
        List<SuggestionDto> trades = [.. Enumerable.Range(0, 20).Select(_ => Closed(1)), .. Enumerable.Range(0, 30).Select(_ => Closed(-1))];

        ModelReliability.Calculate(trades).Verdict.Level.Should().Be(ReliabilityLevel.NoEdge);
    }

    [Fact]
    public void Calculate_WithNoisyPositiveResult_IsUnproven()
    {
        List<SuggestionDto> trades = [.. Enumerable.Range(0, 16).Select(_ => Closed(1)), .. Enumerable.Range(0, 14).Select(_ => Closed(-1))];

        ModelReliability.Calculate(trades).Verdict.Level.Should().Be(ReliabilityLevel.Unproven);
    }

    [Fact]
    public void Wilson_GivesNarrowerIntervalsWithMoreData()
    {
        Interval small = ModelReliability.Wilson(6, 10);
        Interval large = ModelReliability.Wilson(600, 1000);

        (large.High - large.Low).Should().BeLessThan(small.High - small.Low);
        large.Low.Should().BeLessThan(0.6).And.BeGreaterThan(0.56);
    }

    [Fact]
    public void Calculate_SplitsBySentimentAgreement()
    {
        List<SuggestionDto> trades =
        [
            Closed(2, sentiment: 0.5),
            Closed(-1, sentiment: -0.5),
            Closed(2, sentiment: 0.4),
            Closed(-1, direction: TradeDirection.Sell, sentiment: 0.6)
        ];

        ModelReliabilityReport report = ModelReliability.Calculate(trades);

        report.BySentiment.Single(g => g.Name == "Sentimento a favor").Count.Should().Be(2);
        report.BySentiment.Single(g => g.Name == "Sentimento contra").Count.Should().Be(2);
    }

    [Fact]
    public void ForwardReturn_UsesTheCloseOnPublishDayAsBase()
    {
        List<Candle> daily =
        [
            new(Start.AddDays(0), 100, 100, 100, 100, 1),
            new(Start.AddDays(1), 110, 110, 110, 110, 1),
            new(Start.AddDays(2), 121, 121, 121, 121, 1)
        ];

        SentimentStudy.ForwardReturn(daily, Start.AddDays(1).AddHours(5), 1).Should().BeApproximately(0.1, 1e-9);
        SentimentStudy.ForwardReturn(daily, Start.AddDays(2), 1).Should().BeNull();
    }

    [Fact]
    public void SentimentStudy_MeasuresHitRateOnStrongSentimentOnly()
    {
        List<SentimentObservation> observations = [];
        observations.AddRange(Enumerable.Range(0, 40).Select(_ => new SentimentObservation("A", 0.6, 0.02, 0.03)));
        observations.AddRange(Enumerable.Range(0, 40).Select(_ => new SentimentObservation("A", -0.6, -0.01, -0.02)));
        observations.AddRange(Enumerable.Range(0, 20).Select(_ => new SentimentObservation("A", 0.1, -0.05, -0.05)));

        SentimentStudyResult result = SentimentStudy.Evaluate(observations);

        result.Horizons[0].Samples.Should().Be(80);
        result.Horizons[0].HitRate.Should().Be(1);
        result.Verdict.Level.Should().Be(ReliabilityLevel.Moderate);
    }
}

public class AdaptivePriceFormatTests
{
    [Theory]
    [InlineData(0.0000087, "0,000008700")]
    [InlineData(0.0123, "0,01230")]
    [InlineData(5.5, "5,500")]
    [InlineData(250.456, "250,46")]
    public void Adaptive_KeepsSignificantDigitsForTinyPrices(double value, string expected) =>
        PriceFormat.Adaptive(value, System.Globalization.CultureInfo.GetCultureInfo("pt-PT")).Should().Be(expected);
}

public class CalibrationFlatTests
{
    [Fact]
    public void IsFlat_SameProbabilityEverywhere_ReturnsTrue() =>
        new ConfidenceCalibration([new CalibrationPoint(0.6, 0.44), new CalibrationPoint(0.8, 0.45)], 100, 0.44).IsFlat.Should().BeTrue();

    [Fact]
    public void IsFlat_ProbabilityRisesWithConfidence_ReturnsFalse() =>
        new ConfidenceCalibration([new CalibrationPoint(0.6, 0.40), new CalibrationPoint(0.8, 0.60)], 100, 0.5).IsFlat.Should().BeFalse();
}
