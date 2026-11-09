using Trader.Core.Models;

namespace Trader.Core.Projection;

public sealed class ProjectionRecord
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset OriginTime { get; set; }
    public DateTimeOffset TargetTime { get; set; }
    public int HorizonMinutes { get; set; }
    public double LastPrice { get; set; }
    public double ProjectedMedian { get; set; }
    public double P10 { get; set; }
    public double P25 { get; set; }
    public double P75 { get; set; }
    public double P90 { get; set; }
    public double OneSigmaPercent { get; set; }
    public TradeDirection? PlanDirection { get; set; }
    public double? PlanStop { get; set; }
    public double? PlanTarget { get; set; }
    public long? SuggestionId { get; set; }
    public double? ActualPrice { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed record RegisterProjectionRequest(
    string Symbol,
    int HorizonMinutes,
    DateTimeOffset OriginTime,
    double LastPrice,
    double ProjectedMedian,
    double P10,
    double P25,
    double P75,
    double P90,
    double OneSigmaPercent,
    TradeDirection? PlanDirection,
    double? PlanStop,
    double? PlanTarget,
    long? SuggestionId);

public sealed record ProjectionRecordDto(
    long Id,
    string Symbol,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset TargetTime,
    int HorizonMinutes,
    double LastPrice,
    double ProjectedMedian,
    double P10,
    double P90,
    TradeDirection? PlanDirection,
    double? PlanTarget,
    double? PlanStop,
    long? SuggestionId,
    string State,
    double? ActualPrice,
    double? ErrorAbsolute,
    double? ErrorPercent,
    double? ErrorSigmas,
    string? Band,
    double? TargetDistance,
    double? TargetDistancePercent);

public sealed record ProjectionAccuracySummary(
    int Count,
    double BiasPercent,
    double MeanAbsoluteErrorPercent,
    double BiasSigmas,
    double Within50Share,
    double Within80Share);

public sealed record ProjectionRecordsView(IReadOnlyList<ProjectionRecordDto> Items, ProjectionAccuracySummary? Summary);
