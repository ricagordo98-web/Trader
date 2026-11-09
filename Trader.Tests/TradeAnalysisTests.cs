using FluentAssertions;
using Trader.Core.Analysis;
using Trader.Core.Models;
using Trader.Core.Scoring;

namespace Trader.Tests;

public class TradeAnalysisTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T10:00:00Z");

    private static MarketSnapshot Market(double price = 100, double atr = 2, double rsi = 50, double? adx = 25, double? weekly = null, double swingLow = 94, double swingHigh = 110) =>
        new("CL=F", "Crude Oil", price, atr, price, price - 1, price - 2, rsi, 0.1, 0.5, swingLow, swingHigh, adx, weekly);

    private static TradeContext Context(MarketSnapshot? market = null, SentimentSnapshot? sentiment = null) =>
        new("Crude Oil", AssetClass.Commodity, market ?? Market(), sentiment ?? new SentimentSnapshot(0, 0, null, []), new ScoringOptions());

    private static TradeAnalysis Analyze(TradeAnalysisRequest request, TradeContext? context = null, AnalysisHistory? history = null, IReadOnlyList<string>? events = null) =>
        TradeAnalyzer.Analyze(request, context ?? Context(), history, events ?? [], Now);

    [Theory]
    [InlineData(TradeDirection.Buy, 100, 95, 110, 0)]
    [InlineData(TradeDirection.Buy, 100, 105, 110, 1)]
    [InlineData(TradeDirection.Buy, 100, 95, 90, 1)]
    [InlineData(TradeDirection.Sell, 100, 105, 90, 0)]
    [InlineData(TradeDirection.Sell, 100, 95, 90, 1)]
    [InlineData(TradeDirection.Sell, 100, 105, 110, 1)]
    [InlineData(TradeDirection.Buy, 0, 95, 110, 1)]
    public void Validate_ChecksThePriceSidesForTheDirection(TradeDirection direction, double entry, double stop, double target, int errors) =>
        TradeAnalysisValidator.Validate(new TradeAnalysisRequest("CL=F", direction, entry, stop, target)).Should().HaveCount(errors);

    [Fact]
    public void Analyze_GoodSetup_ScoresHigherThanABadOne()
    {
        TradeAnalysis good = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 93, 112));
        TradeAnalysis bad = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 99.5, 100.4));

        good.Score.Should().BeGreaterThan(bad.Score);
        bad.Verdict.Should().BeOneOf("Fraca", "Má");
    }

    [Fact]
    public void Analyze_RiskingMoreThanTheReward_CapsTheVerdictAtWeak()
    {
        TradeAnalysis analysis = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 90, 104));

        analysis.RiskReward.Should().BeLessThan(1);
        analysis.Score.Should().BeLessThanOrEqualTo(45);
        analysis.Findings.Should().Contain(f => f.Category == "Risco/retorno" && f.Kind == FindingKind.Negative);
    }

    [Fact]
    public void Analyze_PriceAlreadyBeyondTheStop_IsFlaggedAsNegative()
    {
        TradeAnalysis analysis = Analyze(
            new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 98, 106),
            Context(Market(price: 97)));

        analysis.Findings.Should().Contain(f => f.Title.Contains("já passou o teu stop") && f.Points == -30);
    }

    [Fact]
    public void Analyze_EntryFarFromThePrice_ReportsTheRiskRewardAtTheCurrentPrice()
    {
        TradeAnalysis analysis = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 96, 93, 106), Context(Market(price: 100)));

        analysis.Findings.Should().Contain(f => f.Category == "Entrada" && f.Detail.Contains("rácio seria"));
    }

    [Fact]
    public void Analyze_FewArticles_DoesNotCountSentiment()
    {
        TradeAnalysis analysis = Analyze(
            new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 95, 110),
            Context(sentiment: new SentimentSnapshot(0.9, 1, null, [])));

        analysis.Findings.Single(f => f.Category == "Sentimento").Points.Should().Be(0);
    }

    [Theory]
    [InlineData(0.6, TradeDirection.Buy, FindingKind.Positive)]
    [InlineData(-0.6, TradeDirection.Buy, FindingKind.Negative)]
    [InlineData(-0.6, TradeDirection.Sell, FindingKind.Positive)]
    public void Analyze_Sentiment_IsJudgedAgainstTheDirectionOfTheTrade(double sentiment, TradeDirection direction, FindingKind expected)
    {
        bool buy = direction == TradeDirection.Buy;
        TradeAnalysisRequest request = new("CL=F", direction, 100, buy ? 95 : 105, buy ? 110 : 90);

        TradeAnalysis analysis = Analyze(request, Context(sentiment: new SentimentSnapshot(sentiment, 10, null, [])));

        analysis.Findings.Single(f => f.Category == "Sentimento").Kind.Should().Be(expected);
    }

    [Fact]
    public void Analyze_BuyAgainstWeeklyDowntrend_IsPenalized()
    {
        TradeAnalysis analysis = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 95, 110), Context(Market(weekly: -1)));

        analysis.Findings.Should().Contain(f => f.Title == "Contra a tendência semanal" && f.Points < 0);
    }

    [Fact]
    public void Analyze_UpcomingMacroEvent_AddsAWarning()
    {
        TradeAnalysis analysis = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 95, 110), events: ["Decisão de juros da Fed (FOMC)"]);

        analysis.UpcomingEvents.Should().ContainSingle();
        analysis.Findings.Should().Contain(f => f.Category == "Calendário" && f.Detail.Contains("FOMC"));
    }

    [Fact]
    public void Analyze_HistoryWithFewSamples_IsNotUsedAsEvidence()
    {
        TradeAnalysis analysis = Analyze(
            new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 95, 110),
            history: new AnalysisHistory(5, 0.8, 0.9, "matérias-primas"));

        analysis.Findings.Single(f => f.Category == "Histórico").Points.Should().Be(0);
    }

    [Fact]
    public void Analyze_SuggestsModelLevelsOnTheSameSideAsTheDirection()
    {
        TradeAnalysis sell = Analyze(new TradeAnalysisRequest("CL=F", TradeDirection.Sell, 100, 105, 90));

        sell.Model.Levels.StopLoss.Should().BeGreaterThan(sell.Price);
        sell.Model.Levels.TakeProfit.Should().BeLessThan(sell.Price);
    }

    [Fact]
    public void Analyze_ScoreStaysWithinBounds()
    {
        TradeAnalysis analysis = Analyze(
            new TradeAnalysisRequest("CL=F", TradeDirection.Buy, 100, 99.9, 100.1),
            Context(Market(price: 90, rsi: 85, adx: 5, weekly: -1)),
            events: ["a", "b"]);

        analysis.Score.Should().BeInRange(0, 100);
    }
}
