using Trader.Core.Models;

namespace Trader.Core.Scoring;

public sealed record WideStopComparison(
    int Pairs,
    double CurrentNetR,
    double WideNetR,
    double Difference,
    double DifferenceLow,
    double DifferenceHigh,
    string Hint);

public static class WideStopComparer
{
    public const int MinPairs = 30;
    public const double OverlapFactor = 3;

    private const double ConfidenceZ = 1.96;

    public static WideStopComparison? Compare(IReadOnlyList<SuggestionDto> closed)
    {
        Dictionary<(string, TradeDirection, DateTimeOffset), SuggestionDto> current = closed
            .Where(s => s.Variant == "A" && !s.IsShadow && s.Horizon == SuggestionHorizon.Swing && s.RMultiple is not null)
            .GroupBy(s => (s.Symbol, s.Direction, s.CreatedAt))
            .ToDictionary(g => g.Key, g => g.First());

        List<(double Current, double Wide)> pairs = closed
            .Where(s => s.Variant == SuggestionVariants.WideStop && s.RMultiple is not null)
            .Select(s => current.TryGetValue((s.Symbol, s.Direction, s.CreatedAt), out SuggestionDto? match) ? (Current: Net(match), Wide: Net(s)) : ((double, double)?)null)
            .OfType<(double, double)>()
            .ToList();

        if (pairs.Count == 0)
        {
            return null;
        }

        double[] difference = pairs.Select(p => p.Wide - p.Current).ToArray();
        double mean = difference.Average();
        double stdDev = difference.Length > 1 ? Math.Sqrt(difference.Sum(d => (d - mean) * (d - mean)) / (difference.Length - 1)) : 0;
        double margin = difference.Length > 1 ? ConfidenceZ * stdDev / Math.Sqrt(difference.Length / OverlapFactor) : 0;

        string hint = pairs.Count < MinPairs
            ? $"Só há {pairs.Count} pares fechados; são precisos {MinPairs} para tirar conclusões."
            : mean - margin > 0
                ? "O stop largo está a ganhar de forma clara. Pode justificar-se passar o motor para 3×/6× ATR."
                : mean + margin < 0
                    ? "O stop largo está a perder face ao atual. A hipótese do backtest não se confirma ao vivo."
                    : "Ainda sem diferença clara entre o stop atual e o largo.";

        return new WideStopComparison(pairs.Count, pairs.Average(p => p.Current), pairs.Average(p => p.Wide), mean, mean - margin, mean + margin, hint);
    }

    private static double Net(SuggestionDto s) => s.RMultiple!.Value - (s.CostR ?? 0);
}
