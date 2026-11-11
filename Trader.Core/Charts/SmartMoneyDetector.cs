using Trader.Core.Models;

namespace Trader.Core.Charts;

public sealed record SmartMoneyItem(string Group, string Label, bool Bullish, DateTimeOffset From, DateTimeOffset To, double High, double Low, bool ExtendRight);

public static class SmartMoneyGroups
{
    public const string FairValueGaps = "fvg";
    public const string OrderBlocks = "order-blocks";
    public const string Liquidity = "liquidity";
    public const string Structure = "structure";
    public const string PremiumDiscount = "premium-discount";
}

public static class SmartMoneyDetector
{
    public const int SwingStrength = 3;
    public const int RangeLookback = 14;
    public const double MinGapRanges = 0.25;
    public const int MaxPerGroup = 6;
    public const double OteLow = 0.62;
    public const double OteHigh = 0.79;

    private sealed record Swing(int Index, double Price, bool IsHigh);

    public static IReadOnlyList<SmartMoneyItem> Detect(IReadOnlyList<Candle> candles)
    {
        List<Candle> bars = candles.Where(c => c.Close > 0).OrderBy(c => c.Time).ToList();

        if (bars.Count < 2 * SwingStrength + 3)
        {
            return [];
        }

        List<Swing> swings = FindSwings(bars);
        List<SmartMoneyItem> items = [];
        items.AddRange(FairValueGaps(bars));
        items.AddRange(Liquidity(bars, swings));
        items.AddRange(Structure(bars, swings, out List<SmartMoneyItem> blocks));
        items.AddRange(blocks);
        items.AddRange(PremiumDiscount(bars, swings));
        return items;
    }

    private static List<Swing> FindSwings(List<Candle> bars)
    {
        List<Swing> swings = [];

        for (int i = SwingStrength; i < bars.Count - SwingStrength; i++)
        {
            bool isHigh = true;
            bool isLow = true;

            for (int k = 1; k <= SwingStrength; k++)
            {
                isHigh &= bars[i].High > bars[i - k].High && bars[i].High > bars[i + k].High;
                isLow &= bars[i].Low < bars[i - k].Low && bars[i].Low < bars[i + k].Low;
            }

            if (isHigh)
            {
                swings.Add(new Swing(i, bars[i].High, true));
            }

            if (isLow)
            {
                swings.Add(new Swing(i, bars[i].Low, false));
            }
        }

        return swings;
    }

    private static double AverageRange(List<Candle> bars, int end)
    {
        int start = Math.Max(0, end - RangeLookback + 1);
        return bars.Skip(start).Take(end - start + 1).Average(c => c.High - c.Low);
    }

    private static IEnumerable<SmartMoneyItem> FairValueGaps(List<Candle> bars)
    {
        List<SmartMoneyItem> gaps = [];

        for (int i = 2; i < bars.Count; i++)
        {
            double minimum = MinGapRanges * AverageRange(bars, i);
            bool bullish = bars[i].Low - bars[i - 2].High >= minimum && bars[i].Low > bars[i - 2].High;
            bool bearish = bars[i - 2].Low - bars[i].High >= minimum && bars[i - 2].Low > bars[i].High;

            if (!bullish && !bearish)
            {
                continue;
            }

            double high = bullish ? bars[i].Low : bars[i - 2].Low;
            double low = bullish ? bars[i - 2].High : bars[i].High;
            bool filled = false;

            for (int j = i + 1; j < bars.Count && !filled; j++)
            {
                filled = bullish ? bars[j].Close < low : bars[j].Close > high;
            }

            if (!filled)
            {
                gaps.Add(new SmartMoneyItem(SmartMoneyGroups.FairValueGaps, bullish ? "FVG alta" : "FVG baixa", bullish, bars[i - 1].Time, bars[^1].Time, high, low, true));
            }
        }

        return gaps.TakeLast(MaxPerGroup);
    }

    private static IEnumerable<SmartMoneyItem> Liquidity(List<Candle> bars, List<Swing> swings)
    {
        List<SmartMoneyItem> pools = [];

        foreach (Swing swing in swings)
        {
            bool taken = false;

            for (int j = swing.Index + SwingStrength + 1; j < bars.Count && !taken; j++)
            {
                taken = swing.IsHigh ? bars[j].High > swing.Price : bars[j].Low < swing.Price;
            }

            if (!taken)
            {
                pools.Add(new SmartMoneyItem(SmartMoneyGroups.Liquidity, swing.IsHigh ? "Liquidez acima" : "Liquidez abaixo", !swing.IsHigh, bars[swing.Index].Time, bars[^1].Time, swing.Price, swing.Price, true));
            }
        }

        return pools.Where(p => p.Bullish).TakeLast(MaxPerGroup).Concat(pools.Where(p => !p.Bullish).TakeLast(MaxPerGroup));
    }

    private static IEnumerable<SmartMoneyItem> Structure(List<Candle> bars, List<Swing> swings, out List<SmartMoneyItem> orderBlocks)
    {
        List<SmartMoneyItem> breaks = [];
        List<SmartMoneyItem> blocks = [];
        Swing? lastHigh = null;
        Swing? lastLow = null;
        int trend = 0;
        int nextSwing = 0;

        for (int i = 0; i < bars.Count; i++)
        {
            while (nextSwing < swings.Count && swings[nextSwing].Index + SwingStrength <= i)
            {
                Swing confirmed = swings[nextSwing++];

                if (confirmed.IsHigh)
                {
                    lastHigh = confirmed;
                }
                else
                {
                    lastLow = confirmed;
                }
            }

            if (lastHigh is not null && bars[i].Close > lastHigh.Price)
            {
                breaks.Add(new SmartMoneyItem(SmartMoneyGroups.Structure, trend < 0 ? "MSS alta" : "BOS alta", true, bars[lastHigh.Index].Time, bars[i].Time, lastHigh.Price, lastHigh.Price, false));
                AddOrderBlock(bars, blocks, lastHigh.Index, i, true);
                trend = 1;
                lastHigh = null;
            }
            else if (lastLow is not null && bars[i].Close < lastLow.Price)
            {
                breaks.Add(new SmartMoneyItem(SmartMoneyGroups.Structure, trend > 0 ? "MSS baixa" : "BOS baixa", false, bars[lastLow.Index].Time, bars[i].Time, lastLow.Price, lastLow.Price, false));
                AddOrderBlock(bars, blocks, lastLow.Index, i, false);
                trend = -1;
                lastLow = null;
            }
        }

        orderBlocks = blocks.TakeLast(MaxPerGroup).ToList();
        return breaks.TakeLast(MaxPerGroup);
    }

    private static void AddOrderBlock(List<Candle> bars, List<SmartMoneyItem> blocks, int swingIndex, int breakIndex, bool bullish)
    {
        for (int k = breakIndex - 1; k >= swingIndex; k--)
        {
            bool opposite = bullish ? bars[k].Close < bars[k].Open : bars[k].Close > bars[k].Open;

            if (!opposite)
            {
                continue;
            }

            bool mitigated = false;

            for (int j = breakIndex + 1; j < bars.Count && !mitigated; j++)
            {
                mitigated = bullish ? bars[j].Close < bars[k].Low : bars[j].Close > bars[k].High;
            }

            if (!mitigated)
            {
                blocks.Add(new SmartMoneyItem(SmartMoneyGroups.OrderBlocks, bullish ? "OB alta" : "OB baixa", bullish, bars[k].Time, bars[^1].Time, bars[k].High, bars[k].Low, true));
            }

            return;
        }
    }

    private static IEnumerable<SmartMoneyItem> PremiumDiscount(List<Candle> bars, List<Swing> swings)
    {
        Swing? high = swings.LastOrDefault(s => s.IsHigh);
        Swing? low = swings.LastOrDefault(s => !s.IsHigh);

        if (high is null || low is null || high.Price <= low.Price)
        {
            yield break;
        }

        DateTimeOffset from = bars[Math.Min(high.Index, low.Index)].Time;
        DateTimeOffset to = bars[^1].Time;
        double range = high.Price - low.Price;
        bool bullishLeg = high.Index > low.Index;

        yield return new SmartMoneyItem(SmartMoneyGroups.PremiumDiscount, "Equilíbrio 50%", bullishLeg, from, to, low.Price + range / 2, low.Price + range / 2, true);

        double oteHigh = bullishLeg ? high.Price - OteLow * range : low.Price + OteHigh * range;
        double oteLow = bullishLeg ? high.Price - OteHigh * range : low.Price + OteLow * range;
        yield return new SmartMoneyItem(SmartMoneyGroups.PremiumDiscount, bullishLeg ? "OTE compra" : "OTE venda", bullishLeg, from, to, oteHigh, oteLow, true);
    }
}
