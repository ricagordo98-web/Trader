using Trader.Core.Models;

namespace Trader.Core.Rotation;

public sealed class MonthlyPrices
{
    private readonly Dictionary<string, double[]> _prices;

    private MonthlyPrices(DateTimeOffset[] dates, Dictionary<string, double[]> prices, int startIndex)
    {
        Dates = dates;
        _prices = prices;
        StartIndex = startIndex;
    }

    public DateTimeOffset[] Dates { get; }

    public int Count => Dates.Length;

    public int StartIndex { get; }

    public bool Has(string symbol) => _prices.ContainsKey(symbol);

    public double Price(string symbol, int index) => _prices[symbol][index];

    public static MonthlyPrices? Build(IReadOnlyList<RotationInput> inputs, string benchmark, DateTimeOffset asOf)
    {
        RotationInput? reference = inputs.FirstOrDefault(i => i.Symbol == benchmark);

        if (reference is null || reference.Candles.Count == 0)
        {
            return null;
        }

        DateTimeOffset[] monthEnds = reference.Candles
            .OrderBy(c => c.Time)
            .GroupBy(c => (c.Time.UtcDateTime.Year, c.Time.UtcDateTime.Month))
            .Where(g => g.Key.Year < asOf.UtcDateTime.Year || (g.Key.Year == asOf.UtcDateTime.Year && g.Key.Month < asOf.UtcDateTime.Month))
            .Select(g => g.Max(c => c.Time))
            .ToArray();

        Dictionary<string, double[]> prices = [];
        int start = 0;

        foreach (RotationInput input in inputs.Where(i => i.Candles.Count > 0))
        {
            List<Candle> candles = input.Candles.OrderBy(c => c.Time).ToList();
            double[] series = new double[monthEnds.Length];
            int cursor = 0;
            int firstValid = -1;
            double last = double.NaN;

            for (int m = 0; m < monthEnds.Length; m++)
            {
                while (cursor < candles.Count && candles[cursor].Time <= monthEnds[m])
                {
                    last = candles[cursor].AdjustedClose ?? candles[cursor].Close;
                    cursor++;
                }

                series[m] = double.IsNaN(last) || last <= 0 ? double.NaN : last;

                if (firstValid < 0 && !double.IsNaN(series[m]))
                {
                    firstValid = m;
                }
            }

            if (firstValid >= 0)
            {
                prices[input.Symbol] = series;
                start = Math.Max(start, firstValid);
            }
        }

        return prices.ContainsKey(benchmark) ? new MonthlyPrices(monthEnds, prices, start) : null;
    }
}
