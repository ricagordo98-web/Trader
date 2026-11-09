using FluentAssertions;
using Trader.Core.Models;
using Trader.Core.Projection;

namespace Trader.Tests;

public class ProjectionAccuracyTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-09T15:00:00Z");

    private static RegisterProjectionRequest Request(Func<RegisterProjectionRequest, RegisterProjectionRequest>? change = null)
    {
        RegisterProjectionRequest request = new("AAPL", 120, Origin, 100, 100.2, 99, 99.6, 100.8, 101.4, 0.8, TradeDirection.Buy, 99.5, 100.5, null);
        return change?.Invoke(request) ?? request;
    }

    private static ProjectionRecord Record(double? actual = null, bool resolved = false) => new()
    {
        Id = 1,
        Symbol = "AAPL",
        Name = "Apple",
        CreatedAt = Origin,
        OriginTime = Origin,
        TargetTime = Origin.AddMinutes(120),
        HorizonMinutes = 120,
        LastPrice = 100,
        ProjectedMedian = 100,
        P10 = 99,
        P25 = 99.6,
        P75 = 100.4,
        P90 = 101,
        OneSigmaPercent = 1,
        PlanDirection = TradeDirection.Buy,
        PlanStop = 99.3,
        PlanTarget = 100.7,
        ActualPrice = actual,
        ResolvedAt = resolved ? Origin.AddMinutes(130) : null
    };

    [Fact]
    public void Validate_ValidRequestBeforeTheHorizon_HasNoErrors() =>
        ProjectionAccuracy.Validate(Request(), Origin.AddMinutes(10)).Should().BeEmpty();

    [Fact]
    public void Validate_HorizonAlreadyPassed_IsRejected() =>
        ProjectionAccuracy.Validate(Request(), Origin.AddMinutes(121)).Should().ContainSingle(e => e.Contains("já passou"));

    [Fact]
    public void Validate_BandsOutOfOrder_IsRejected() =>
        ProjectionAccuracy.Validate(Request(r => r with { P10 = 101.5 }), Origin).Should().Contain(e => e.Contains("fora de ordem"));

    [Fact]
    public void Validate_IncompletePlan_IsRejected() =>
        ProjectionAccuracy.Validate(Request(r => r with { PlanStop = null }), Origin).Should().Contain(e => e.Contains("incompleto"));

    [Fact]
    public void Resolve_BeforeTheTargetTime_StaysPending() =>
        ProjectionAccuracy.Resolve(Record(), [], Origin.AddMinutes(60)).Should().Be((null, false));

    [Fact]
    public void Resolve_UsesTheLastCandleAtOrBeforeTheTargetTime()
    {
        List<Candle> candles =
        [
            new(Origin.AddMinutes(115), 100, 100, 100, 100.1, 1),
            new(Origin.AddMinutes(120), 100, 100, 100, 100.5, 1),
            new(Origin.AddMinutes(125), 100, 100, 100, 101.5, 1)
        ];

        ProjectionAccuracy.Resolve(Record(), candles, Origin.AddMinutes(130)).Should().Be((100.5, true));
    }

    [Fact]
    public void Resolve_NoCandleNearTheTarget_WaitsThenGivesUp()
    {
        List<Candle> candles = [new(Origin.AddMinutes(60), 100, 100, 100, 100, 1)];

        ProjectionAccuracy.Resolve(Record(), candles, Origin.AddMinutes(130)).Should().Be((null, false));
        ProjectionAccuracy.Resolve(Record(), candles, Origin.AddDays(2)).Should().Be((null, true));
    }

    [Fact]
    public void ToDto_ComputesSignedErrorBandAndDistanceToTheTarget()
    {
        ProjectionRecordDto dto = ProjectionAccuracy.ToDto(Record(actual: 100.9, resolved: true));

        dto.State.Should().Be(ProjectionAccuracy.Resolved);
        dto.ErrorAbsolute.Should().BeApproximately(0.9, 1e-9);
        dto.ErrorPercent.Should().BeApproximately(0.9, 1e-9);
        dto.ErrorSigmas.Should().BeApproximately(0.9, 0.01);
        dto.Band.Should().Be("80");
        dto.TargetDistance.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void ToDto_BelowTheProjection_GivesNegativeErrorAndOutsideTheBands()
    {
        ProjectionRecordDto dto = ProjectionAccuracy.ToDto(Record(actual: 98.5, resolved: true));

        dto.ErrorAbsolute.Should().BeNegative();
        dto.Band.Should().Be("out");
    }

    [Fact]
    public void ToDto_PendingAndUnavailable_HaveNoError()
    {
        ProjectionAccuracy.ToDto(Record()).State.Should().Be(ProjectionAccuracy.Pending);
        ProjectionAccuracy.ToDto(Record(resolved: true)).State.Should().Be(ProjectionAccuracy.Unavailable);
        ProjectionAccuracy.ToDto(Record()).ErrorPercent.Should().BeNull();
    }

    [Fact]
    public void Summarize_AveragesTheSignedAndAbsoluteErrorsAndBandHits()
    {
        List<ProjectionRecordDto> items =
        [
            ProjectionAccuracy.ToDto(Record(actual: 100.2, resolved: true)),
            ProjectionAccuracy.ToDto(Record(actual: 99.8, resolved: true)),
            ProjectionAccuracy.ToDto(Record(actual: 100.9, resolved: true)),
            ProjectionAccuracy.ToDto(Record(actual: 97, resolved: true)),
            ProjectionAccuracy.ToDto(Record())
        ];

        ProjectionAccuracySummary summary = ProjectionAccuracy.Summarize(items)!;

        summary.Count.Should().Be(4);
        summary.Within50Share.Should().Be(0.5);
        summary.Within80Share.Should().Be(0.75);
        summary.MeanAbsoluteErrorPercent.Should().BeGreaterThan(Math.Abs(summary.BiasPercent) - 1e-9);
        ProjectionAccuracy.Summarize([ProjectionAccuracy.ToDto(Record())]).Should().BeNull();
    }
}
