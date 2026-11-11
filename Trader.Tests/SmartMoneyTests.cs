using FluentAssertions;
using Trader.Core.Charts;
using Trader.Core.Models;

namespace Trader.Tests;

public class SmartMoneyTests
{
    private static Candle Bar(int index, double open, double high, double low, double close) =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index), open, high, low, close, 1000);

    private static List<Candle> Flat(int count, double price = 100) =>
        Enumerable.Range(0, count).Select(i => Bar(i, price, price + 1, price - 1, price)).ToList();

    [Fact]
    public void Detect_WithTooFewCandles_ReturnsNothing()
    {
        SmartMoneyDetector.Detect(Flat(5)).Should().BeEmpty();
    }

    [Fact]
    public void Detect_BullishGap_ReturnsOpenFairValueGap()
    {
        List<Candle> bars = Flat(10);
        bars.Add(Bar(10, 100, 101, 99, 100));
        bars.Add(Bar(11, 100, 110, 100, 109));
        bars.Add(Bar(12, 109, 112, 105, 111));
        bars.AddRange(Enumerable.Range(13, 5).Select(i => Bar(i, 111, 113, 107, 111)));

        SmartMoneyItem gap = SmartMoneyDetector.Detect(bars).Single(i => i.Group == SmartMoneyGroups.FairValueGaps);

        gap.Bullish.Should().BeTrue();
        gap.Low.Should().Be(101);
        gap.High.Should().Be(105);
        gap.Label.Should().Be("FVG alta");
    }

    [Fact]
    public void Detect_GapClosedByLaterCandle_IsNotReported()
    {
        List<Candle> bars = Flat(10);
        bars.Add(Bar(10, 100, 101, 99, 100));
        bars.Add(Bar(11, 100, 110, 100, 109));
        bars.Add(Bar(12, 109, 112, 105, 111));
        bars.Add(Bar(13, 111, 111, 98, 99));
        bars.AddRange(Enumerable.Range(14, 4).Select(i => Bar(i, 99, 106, 98, 99)));

        SmartMoneyDetector.Detect(bars).Should().NotContain(i => i.Group == SmartMoneyGroups.FairValueGaps);
    }

    [Fact]
    public void Detect_SwingHighNeverExceeded_IsLiquidityAbove()
    {
        List<Candle> bars = Flat(5);
        bars.Add(Bar(5, 100, 108, 99, 100));
        bars.AddRange(Enumerable.Range(6, 6).Select(i => Bar(i, 100, 101, 99, 100)));

        SmartMoneyItem pool = SmartMoneyDetector.Detect(bars).Single(i => i.Group == SmartMoneyGroups.Liquidity && i.Label == "Liquidez acima");

        pool.High.Should().Be(108);
        pool.Low.Should().Be(108);
    }

    [Fact]
    public void Detect_SwingHighTakenOut_IsNoLongerLiquidity()
    {
        List<Candle> bars = Flat(5);
        bars.Add(Bar(5, 100, 108, 99, 100));
        bars.AddRange(Enumerable.Range(6, 4).Select(i => Bar(i, 100, 101, 99, 100)));
        bars.Add(Bar(10, 100, 109, 99, 100));
        bars.AddRange(Enumerable.Range(11, 3).Select(i => Bar(i, 100, 101, 99, 100)));

        SmartMoneyDetector.Detect(bars).Where(i => i.Label == "Liquidez acima").Should().NotContain(i => i.High == 108);
    }

    [Fact]
    public void Detect_CloseAboveSwingHigh_ReportsBreakOfStructureAndOrderBlock()
    {
        List<Candle> bars = Flat(5);
        bars.Add(Bar(5, 100, 108, 99, 100));
        bars.AddRange(Enumerable.Range(6, 3).Select(i => Bar(i, 100, 101, 99, 100)));
        bars.Add(Bar(9, 100, 101, 97, 98));
        bars.Add(Bar(10, 98, 112, 98, 111));
        bars.AddRange(Enumerable.Range(11, 4).Select(i => Bar(i, 111, 113, 110, 112)));

        IReadOnlyList<SmartMoneyItem> items = SmartMoneyDetector.Detect(bars);

        items.Should().Contain(i => i.Group == SmartMoneyGroups.Structure && i.Label == "BOS alta" && i.High == 108);
        SmartMoneyItem block = items.Single(i => i.Group == SmartMoneyGroups.OrderBlocks);
        block.Bullish.Should().BeTrue();
        block.High.Should().Be(101);
        block.Low.Should().Be(97);
    }

    [Fact]
    public void Detect_BreakInOppositeDirection_IsMarketStructureShift()
    {
        List<Candle> bars = Flat(5);
        bars.Add(Bar(5, 100, 108, 99, 100));
        bars.Add(Bar(6, 100, 101, 99, 100));
        bars.Add(Bar(7, 100, 101, 95, 100));
        bars.Add(Bar(8, 100, 101, 99, 100));
        bars.Add(Bar(9, 100, 112, 99, 111));
        bars.AddRange(Enumerable.Range(10, 3).Select(i => Bar(i, 111, 113, 110, 112)));
        bars.Add(Bar(13, 112, 113, 90, 91));
        bars.AddRange(Enumerable.Range(14, 4).Select(i => Bar(i, 91, 92, 90.5, 91)));

        SmartMoneyDetector.Detect(bars).Should().Contain(i => i.Label == "MSS baixa");
    }

    [Fact]
    public void Detect_UpLegThenPullback_ReturnsEquilibriumAndBuyZone()
    {
        List<Candle> bars = [];
        double[] lows = [100, 100, 100, 96, 100, 100, 100];
        double[] highs = [101, 101, 101, 101, 101, 101, 101];

        for (int i = 0; i < 7; i++)
        {
            bars.Add(Bar(i, 100, highs[i], lows[i], 100));
        }

        bars.Add(Bar(7, 100, 110, 100, 109));
        bars.Add(Bar(8, 109, 120, 108, 118));
        bars.AddRange(Enumerable.Range(9, 3).Select(i => Bar(i, 118, 119, 117, 118)));
        bars.AddRange(Enumerable.Range(12, 4).Select(i => Bar(i, 118, 119, 112, 114)));

        IReadOnlyList<SmartMoneyItem> items = SmartMoneyDetector.Detect(bars);

        SmartMoneyItem equilibrium = items.Single(i => i.Label == "Equilíbrio 50%");
        equilibrium.High.Should().BeApproximately(108, 0.001);

        SmartMoneyItem ote = items.Single(i => i.Label == "OTE compra");
        ote.High.Should().BeApproximately(120 - 0.62 * 24, 0.001);
        ote.Low.Should().BeApproximately(120 - 0.79 * 24, 0.001);
    }
}
