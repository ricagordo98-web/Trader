using Trader.Core.Models;

namespace Trader.Core.Rotation;

public sealed record RotationStats(
    int Months,
    double Cagr,
    double Volatility,
    double Sharpe,
    double MaxDrawdown,
    double PositiveMonthsShare,
    double AverageTurnover);

public sealed record RotationHolding(string Symbol, double Weight);

public sealed record RotationStrategyResult(
    string Key,
    string Name,
    string Description,
    bool IsBenchmark,
    RotationStats Stats,
    double FirstHalfSharpe,
    double SecondHalfSharpe,
    double CagrWithHighCosts,
    double? ExcessCagr,
    double? ExcessTStat,
    string Verdict,
    IReadOnlyDictionary<int, double> YearlyReturns,
    IReadOnlyList<RotationHolding> CurrentHoldings);

public sealed record RotationResult(
    DateTimeOffset GeneratedAt,
    DateTimeOffset From,
    DateTimeOffset To,
    int Months,
    double CostPercentPerSide,
    IReadOnlyList<string> Universe,
    IReadOnlyList<RotationStrategyResult> Strategies,
    IReadOnlyList<string> Notes);

public sealed record RotationInput(string Symbol, IReadOnlyList<Candle> Candles);

public static class RotationVerdicts
{
    public const string Better = "better";
    public const string LessRisk = "less-risk";
    public const string NoEdge = "no-edge";
    public const string Benchmark = "benchmark";
}
