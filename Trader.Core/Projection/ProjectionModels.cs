using Trader.Core.Models;

namespace Trader.Core.Projection;

public sealed record ProjectedBar(DateTimeOffset Time, double Open, double High, double Low, double Close);

public sealed record ProjectionBand(DateTimeOffset Time, double P10, double P25, double P50, double P75, double P90);

public sealed record ProjectionDriver(string Name, string Detail, double? EffectPercent);

public sealed record ProjectionCalibration(int Origins, double Coverage80, double Coverage50, double? DirectionHit, int DirectionSamples);

public sealed record ProjectionPlan(
    TradeDirection Direction,
    double Entry,
    double StopLoss,
    double TakeProfit,
    double RiskReward,
    double ProbabilityTarget,
    double ProbabilityStop,
    double ProbabilityExpire,
    double ProbabilityWin,
    double ExpectedR);

public sealed record ProjectionResult(
    int StepMinutes,
    int HorizonMinutes,
    double LastPrice,
    DateTimeOffset LastTime,
    IReadOnlyList<ProjectedBar> Bars,
    IReadOnlyList<ProjectionBand> Bands,
    double ProbabilityUp,
    double ExpectedMovePercent,
    double OneSigmaPercent,
    IReadOnlyList<ProjectionDriver> Drivers,
    ProjectionPlan? Plan = null);

public sealed record PriceProjection(
    string Symbol,
    string Name,
    string Currency,
    string Interval,
    IReadOnlyList<Candle> History,
    ProjectionResult Result,
    ProjectionCalibration? Calibration,
    bool MarketOpen,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt,
    long? TrackedSuggestionId = null,
    bool AlreadyTracked = false);
