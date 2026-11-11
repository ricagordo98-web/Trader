using Trader.Core.Models;

namespace Trader.Core.Backtesting;

public sealed record CostScenario(double Multiplier, BacktestStats Stats);

public sealed record BacktestRobustness(IReadOnlyList<BacktestGroup> ByYear, IReadOnlyList<CostScenario> CostScenarios, BacktestStats WithoutCrypto);

public static class BacktestRobustnessCalculator
{
    public static readonly IReadOnlyList<double> CostMultipliers = [0.5, 1, 1.5, 2];

    public static BacktestRobustness Calculate(IReadOnlyList<BacktestTrade> trades) => new(
        trades
            .GroupBy(t => t.EntryTime.Year)
            .OrderBy(g => g.Key)
            .Select(g => new BacktestGroup(g.Key.ToString(), BacktestStatistics.Calculate(g.ToList())))
            .ToList(),
        CostMultipliers.Select(m => new CostScenario(m, BacktestStatistics.Calculate(Rescale(trades, m)))).ToList(),
        BacktestStatistics.Calculate(trades.Where(t => t.AssetClass != AssetClass.Crypto).ToList()));

    private static List<BacktestTrade> Rescale(IReadOnlyList<BacktestTrade> trades, double multiplier) =>
        trades.Select(t => t with { NetR = t.GrossR - (t.GrossR - t.NetR) * multiplier }).ToList();
}
