using FluentAssertions;
using Trader.Core.Models;
using Trader.Core.Scoring;

namespace Trader.Tests;

public class WideStopTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private static SuggestionDto Item(string variant, int index, double r, bool shadow) => new(
        index, "SYM" + index, "Name", TradeDirection.Buy, 0.6, 100, 98, 104, 2, string.Empty,
        Start.AddHours(index), Start.AddDays(5), SuggestionStatus.HitTakeProfit, Start.AddDays(1), 104, r, [], [],
        Horizon: SuggestionHorizon.Swing, IsShadow: shadow, CostR: 0.05, Variant: variant);

    [Fact]
    public void Compare_PairsSameEntry_ReturnsDifferenceInNetR()
    {
        List<SuggestionDto> items = [];

        for (int i = 0; i < 40; i++)
        {
            items.Add(Item("A", i, 0.0, shadow: false));
            items.Add(Item(SuggestionVariants.WideStop, i, 0.5, shadow: true));
        }

        WideStopComparison? result = WideStopComparer.Compare(items);

        result!.Pairs.Should().Be(40);
        result.Difference.Should().BeApproximately(0.5, 1e-9);
        result.Hint.Should().Contain("ganhar");
    }

    [Fact]
    public void Compare_NoMatchingTwin_ReturnsNull() =>
        WideStopComparer.Compare([Item("A", 1, 1, false), Item(SuggestionVariants.WideStop, 2, 1, true)]).Should().BeNull();

    [Fact]
    public void ComputeWideLevels_Buy_UsesThreeAndSixAtr()
    {
        MarketSnapshot market = new("X", "X", 100, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        (double stop, double target) = SuggestionEngine.ComputeWideLevels(market with { Price = 100, Atr = 2 }, TradeDirection.Buy);

        stop.Should().Be(94);
        target.Should().Be(112);
    }
}
