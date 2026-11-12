using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Trader.Core.Assistant;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Core.TradingSystem;

namespace Trader.Tests;

public class PreTradeCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 14, 0, 0, TimeSpan.Zero);
    private static readonly AccountProfile Account = new(10_000, 1, "EUR");

    private static PreTradeInputs Inputs(
        PreTradeRequest? request = null,
        TradingRules? rules = null,
        IReadOnlyList<JournalTradeDto>? trades = null,
        IReadOnlyList<MarketEvent>? events = null,
        IReadOnlyList<OpenExposure>? exposure = null,
        TradingSetupDto? setup = null,
        int pending = 0,
        SystemPhase phase = SystemPhase.Full) =>
        new(
            request ?? new PreTradeRequest("AAPL", TradeDirection.Buy, 100, 98, 104, 50, 1, null),
            rules ?? new TradingRules { RequireSetup = false },
            true,
            1,
            Account,
            phase,
            AssetClass.Stock,
            trades ?? [],
            pending,
            setup,
            events ?? [],
            exposure ?? [],
            Now,
            TimeZoneInfo.Utc);

    private static JournalTradeDto Trade(long id, DateTimeOffset entry, TradeStatus status = TradeStatus.Open, double? pnl = null, DateTimeOffset? exit = null, string symbol = "MSFT") =>
        new(id, symbol, symbol, TradeDirection.Buy, entry, 100, 10, 98, 104, 1, 0, "XTB", string.Empty, null, status, exit, pnl is null ? null : 100, 20, pnl, pnl / 20, null, null);

    [Fact]
    public void Evaluate_ValidTrade_PassesEverything()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs());

        check.Blocked.Should().BeFalse();
        check.NeedsJustification.Should().BeFalse();
        check.Items.Should().OnlyContain(i => i.Status != CheckStatus.Fail);
        check.RiskPercent.Should().BeApproximately(1, 1e-9);
        check.RewardRisk.Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void Evaluate_NoStop_IsBlocked()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(new PreTradeRequest("AAPL", TradeDirection.Buy, 100, null, 104, 50, 1, null)));

        check.Blocked.Should().BeTrue();
        check.Items.Should().Contain(i => i.Code == "stop" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_StopOnTheWrongSide_IsBlocked()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(new PreTradeRequest("AAPL", TradeDirection.Buy, 100, 102, 104, 50, 1, null)));

        check.Blocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_TooMuchRisk_NeedsJustificationButIsNotBlocked()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(new PreTradeRequest("AAPL", TradeDirection.Buy, 100, 98, 104, 200, 1, null)));

        check.Blocked.Should().BeFalse();
        check.NeedsJustification.Should().BeTrue();
        check.Items.Should().Contain(i => i.Code == "risk" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_TooMuchRiskWhenBreaksAreNotAllowed_IsBlocked()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(
            new PreTradeRequest("AAPL", TradeDirection.Buy, 100, 98, 104, 200, 1, null),
            new TradingRules { RequireSetup = false, AllowJustifiedBreaks = false }));

        check.Blocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_LowRewardRisk_FailsTheRule()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(new PreTradeRequest("AAPL", TradeDirection.Buy, 100, 98, 101, 50, 1, null)));

        check.Items.Should().Contain(i => i.Code == "rr" && i.Status == CheckStatus.Fail);
        check.NeedsJustification.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_SmallRealPhase_ReducesTheAllowedRisk()
    {
        PreTradeInputs inputs = Inputs(phase: SystemPhase.SmallReal);

        PreTradeCheck check = PreTradeChecker.Evaluate(inputs);

        check.RiskFactor.Should().Be(0.25);
        check.Items.Should().Contain(i => i.Code == "risk" && i.Status == CheckStatus.Fail);
        check.SuggestedQuantity.Should().Be(12);
    }

    [Fact]
    public void Evaluate_DailyTradeLimitReached_IsBlocked()
    {
        List<JournalTradeDto> trades = [Trade(1, Now.AddHours(-3)), Trade(2, Now.AddHours(-2)), Trade(3, Now.AddHours(-1))];

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(trades: trades, rules: new TradingRules { RequireSetup = false, MaxTradesPerDay = 3 }));

        check.Blocked.Should().BeTrue();
        check.Items.Should().Contain(i => i.Code == "trades-today" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_DailyLossLimitReached_IsBlocked()
    {
        JournalTradeDto loser = Trade(1, Now.AddHours(-4), TradeStatus.Closed, -250, Now.AddHours(-1));

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(trades: [loser], rules: new TradingRules { RequireSetup = false, MaxDailyLossPercent = 2 }));

        check.Blocked.Should().BeTrue();
        check.Items.Should().Contain(i => i.Code == "daily-loss" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_ConsecutiveLosses_ForceACooldown()
    {
        List<JournalTradeDto> trades =
        [
            Trade(1, Now.AddHours(-30), TradeStatus.Closed, -10, Now.AddHours(-29)),
            Trade(2, Now.AddHours(-20), TradeStatus.Closed, -10, Now.AddHours(-19)),
            Trade(3, Now.AddHours(-10), TradeStatus.Closed, -10, Now.AddHours(-9))
        ];

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(trades: trades, rules: new TradingRules { RequireSetup = false, MaxConsecutiveLosses = 3, CooldownHours = 24, MaxDailyLossPercent = 0 }));

        check.Blocked.Should().BeTrue();
        check.Items.Should().Contain(i => i.Code == "cooldown" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_AWinInTheStreak_ResetsTheCooldown()
    {
        List<JournalTradeDto> trades =
        [
            Trade(1, Now.AddHours(-30), TradeStatus.Closed, -10, Now.AddHours(-29)),
            Trade(2, Now.AddHours(-20), TradeStatus.Closed, -10, Now.AddHours(-19)),
            Trade(3, Now.AddHours(-10), TradeStatus.Closed, 40, Now.AddHours(-9))
        ];

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(trades: trades, rules: new TradingRules { RequireSetup = false, MaxConsecutiveLosses = 2, MaxDailyLossPercent = 0 }));

        check.Items.Should().Contain(i => i.Code == "cooldown" && i.Status == CheckStatus.Pass);
    }

    [Fact]
    public void Evaluate_PendingReviewsOverTheLimit_IsBlocked()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(pending: 3, rules: new TradingRules { RequireSetup = false, MaxPendingReviews = 3 }));

        check.Blocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_EarningsInsideTheBlackout_FailsTheRule()
    {
        MarketEvent earnings = new("Resultados de AAPL", Now.AddHours(20), true);

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(events: [earnings]));

        check.Items.Should().Contain(i => i.Code == "earnings" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_HighImpactMacroSoon_FailsTheRule()
    {
        MarketEvent macro = new("Decisão de taxas da Fed", Now.AddHours(5), false);

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(events: [macro]));

        check.Items.Should().Contain(i => i.Code == "macro" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_CorrelatedOpenPosition_FailsTheRule()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(exposure: [new OpenExposure("MSFT", TradeDirection.Buy, 0.85)]));

        check.Items.Should().Contain(i => i.Code == "correlation" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_NegativelyCorrelatedOppositePosition_IsTheSameBet()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(exposure: [new OpenExposure("TLT", TradeDirection.Sell, -0.8)]));

        check.Items.Should().Contain(i => i.Code == "correlation" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_UncorrelatedPosition_Passes()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(exposure: [new OpenExposure("GLD", TradeDirection.Buy, 0.1)]));

        check.Items.Should().Contain(i => i.Code == "correlation" && i.Status == CheckStatus.Pass);
    }

    [Fact]
    public void Evaluate_SetupRequiredButMissing_FailsTheRule()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(rules: new TradingRules { RequireSetup = true }));

        check.Items.Should().Contain(i => i.Code == "setup" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_OutsideTradingHours_FailsTheRule()
    {
        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(rules: new TradingRules { RequireSetup = false, TradingStartHour = 15, TradingEndHour = 20 }));

        check.Items.Should().Contain(i => i.Code == "hours" && i.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Evaluate_ManualChecklistIncludesTheSetupItems()
    {
        TradingSetupDto setup = new(1, "Pullback", string.Empty, string.Empty, string.Empty, ["Volume a confirmar"], null, new SetupStrategy(), true, null);

        PreTradeCheck check = PreTradeChecker.Evaluate(Inputs(setup: setup, rules: new TradingRules { RequireSetup = true, Checklist = ["Estou calmo"] }));

        check.ManualItems.Should().BeEquivalentTo(["Estou calmo", "Volume a confirmar"]);
    }

    [Fact]
    public void SuggestedQuantity_UsesTheRiskPercentAndTheStopDistance()
    {
        PreTradeChecker.SuggestedQuantity(10_000, 1, 1, 100, 98, 1).Should().Be(50);
        PreTradeChecker.SuggestedQuantity(10_000, 1, 1, 100, null, 1).Should().Be(0);
    }
}

public class SystemStatisticsTests
{
    private static SystemTrade Closed(long id, double r, int breaks = 0, string setup = "A", DateTimeOffset? entry = null, bool withRecord = true)
    {
        DateTimeOffset when = entry ?? new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero).AddDays(id);
        JournalTradeDto trade = new(id, "AAPL", "Apple", TradeDirection.Buy, when, 100, 10, 98, 104, 1, 0, "XTB", string.Empty, null, TradeStatus.Closed, when.AddHours(5), 100, 20, r * 20, r, null, null);
        TradeRecord? record = withRecord
            ? new TradeRecord { TradeId = id, PlanVersion = 1, SetupName = setup, Phase = SystemPhase.Paper, RuleBreaks = breaks }
            : null;

        return new SystemTrade(trade, record);
    }

    [Fact]
    public void Group_ComputesWinRateAverageAndConfidenceInterval()
    {
        PerformanceGroup group = SystemStatistics.Group("x", [1, -1, 2, -1, 1, 1, -1, 2, 1, 1]);

        group.Count.Should().Be(10);
        group.WinRate.Should().BeApproximately(0.7, 1e-9);
        group.AverageR.Should().BeApproximately(0.6, 1e-9);
        group.CiLow.Should().NotBeNull();
        group.CiLow.Should().BeLessThan(0.6);
        group.CiHigh.Should().BeGreaterThan(0.6);
        group.ProfitFactor.Should().BeApproximately(9.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Group_Empty_HasNoStatistics()
    {
        PerformanceGroup group = SystemStatistics.Group("x", []);

        group.Count.Should().Be(0);
        group.AverageR.Should().BeNull();
    }

    [Fact]
    public void TradesNeeded_IsNullWithoutEdge_AndShrinksAsSamplesGrow()
    {
        SystemStatistics.TradesNeeded([-1, 1, -1, 1, -1, 1]).Should().BeNull();
        SystemStatistics.TradesNeeded([1, 1.2, 0.8, 1.1, 0.9, 1.0, 1.05]).Should().Be(0);
    }

    [Fact]
    public void Performance_SeparatesInRulesOutOfRulesAndOutsideTheSystem()
    {
        List<SystemTrade> trades =
        [
            Closed(1, 1.5), Closed(2, 1.0), Closed(3, -1.0),
            Closed(4, -1.0, breaks: 1), Closed(5, -1.0, breaks: 2),
            Closed(6, 2.0, withRecord: false)
        ];

        SystemPerformance performance = SystemStatistics.Performance(trades, TimeZoneInfo.Utc);

        performance.InRules.Count.Should().Be(3);
        performance.InRules.AverageR.Should().BeApproximately(0.5, 1e-9);
        performance.OutOfRules.Count.Should().Be(2);
        performance.OutOfRules.AverageR.Should().BeApproximately(-1, 1e-9);
        performance.OutsideSystem.Count.Should().Be(1);
        performance.All.Count.Should().Be(6);
        performance.BySetup.Should().ContainSingle(g => g.Label == "A" && g.Count == 5);
        performance.Curve.Should().HaveCount(6);
        performance.MaxDrawdownR.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Performance_Verdict_BlamesBrokenRulesWhenTheyCostMoney()
    {
        List<SystemTrade> trades = Enumerable.Range(1, 12).Select(i => Closed(i, i % 3 == 0 ? -1 : 1.5)).ToList();
        trades.AddRange(Enumerable.Range(20, 4).Select(i => Closed(i, -1, breaks: 1)));

        SystemPerformance performance = SystemStatistics.Performance(trades, TimeZoneInfo.Utc);

        performance.Verdict.Should().Contain("fora das regras");
        performance.Verdict.Should().Contain("custar");
    }

    [Fact]
    public void Performance_FewTrades_RefusesToConclude()
    {
        SystemPerformance performance = SystemStatistics.Performance([Closed(1, 1), Closed(2, -1)], TimeZoneInfo.Utc);

        performance.Verdict.Should().Contain("cedo");
    }

    [Fact]
    public void Progress_PromotesOnlyWithEnoughTradesPositiveRAndAdherence()
    {
        TradingRules rules = new() { PaperTradesToPromote = 5, MinAdherencePercent = 80 };
        List<SystemTrade> good = Enumerable.Range(1, 5).Select(i => Closed(i, i == 1 ? -1 : 1.2)).ToList();
        List<SystemTrade> sloppy = Enumerable.Range(1, 5).Select(i => Closed(i, 1.2, breaks: i <= 3 ? 1 : 0)).ToList();
        List<SystemTrade> few = Enumerable.Range(1, 3).Select(i => Closed(i, 1.2)).ToList();

        SystemStatistics.Progress(SystemPhase.Paper, DateTimeOffset.MinValue, rules, good).CanPromote.Should().BeTrue();
        SystemStatistics.Progress(SystemPhase.Paper, DateTimeOffset.MinValue, rules, sloppy).CanPromote.Should().BeFalse();
        SystemStatistics.Progress(SystemPhase.Paper, DateTimeOffset.MinValue, rules, few).CanPromote.Should().BeFalse();
    }

    [Fact]
    public void Progress_RealPhaseWithABadStreak_DemotesAndNeverPromotesFromFull()
    {
        TradingRules rules = new() { DemotionWindow = 5, DemotionAverageR = -0.3 };
        List<SystemTrade> trades = Enumerable.Range(1, 5).Select(i =>
        {
            SystemTrade t = Closed(i, -0.8);
            t.Record!.Phase = SystemPhase.SmallReal;
            return t;
        }).ToList();

        SystemStatistics.Progress(SystemPhase.SmallReal, DateTimeOffset.MinValue, rules, trades).Demoted.Should().BeTrue();
        SystemStatistics.Progress(SystemPhase.Full, DateTimeOffset.MinValue, rules, []).CanPromote.Should().BeFalse();
    }

    [Fact]
    public void Week_CountsAdherenceAndPendingReviews()
    {
        DateOnly monday = new(2026, 11, 9);
        DateTimeOffset when = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        List<SystemTrade> trades = [Closed(1, 1, entry: when), Closed(2, -1, breaks: 1, entry: when), Closed(3, 1, entry: when.AddDays(-20))];

        WeekSummary week = SystemStatistics.Week(monday, trades, TimeZoneInfo.Utc);

        week.Opened.Should().Be(2);
        week.InRules.Should().Be(1);
        week.OutOfRules.Should().Be(1);
        week.AdherencePercent.Should().Be(50);
        week.PendingReviews.Should().Be(2);
    }

    [Fact]
    public void Validator_RejectsNonsenseRules()
    {
        TradingRulesValidator.Validate(new TradingRules()).Should().BeEmpty();
        TradingRulesValidator.Validate(new TradingRules { RiskPercent = 50 }).Should().NotBeEmpty();
        TradingRulesValidator.Validate(new TradingRules { SmallRiskFactor = 0 }).Should().NotBeEmpty();
        TradingRulesValidator.Validate(new TradingRules { TradingStartHour = 30 }).Should().NotBeEmpty();
    }
}

public class SetupBacktesterTests
{
    private static List<Candle> Candles(int count, Func<int, double> close)
    {
        DateTimeOffset start = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return Enumerable.Range(0, count).Select(i =>
        {
            double c = close(i);
            double previous = i == 0 ? c : close(i - 1);
            return new Candle(start.AddDays(i), previous, Math.Max(c, previous) + 0.5, Math.Min(c, previous) - 0.5, c, 1000);
        }).ToList();
    }

    [Fact]
    public void Run_NoRule_FindsNothing()
    {
        SetupBacktester.Run("X", Candles(400, i => 100 + i * 0.1), new SetupStrategy()).Should().BeEmpty();
    }

    [Fact]
    public void Run_BreakoutInAnUptrend_TakesLongTradesWithPositiveResult()
    {
        List<Candle> candles = Candles(500, i => 100 + i * 0.8);

        IReadOnlyList<SetupBacktestTrade> trades = SetupBacktester.Run("X", candles, new SetupStrategy { Entry = SetupEntryRule.Breakout, Lookback = 20, StopAtr = 1.5, TargetR = 2, MaxBars = 15 });

        trades.Should().NotBeEmpty();
        trades.Should().OnlyContain(t => t.Direction == TradeDirection.Buy);
        trades.Average(t => t.R).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Run_Costs_AreSubtractedFromEveryTrade()
    {
        List<Candle> candles = Candles(500, i => 100 + i * 0.8);
        SetupStrategy strategy = new() { Entry = SetupEntryRule.Breakout };

        double free = SetupBacktester.Run("X", candles, strategy, 0).Average(t => t.R);
        double costly = SetupBacktester.Run("X", candles, strategy, 0.2).Average(t => t.R);

        (free - costly).Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void Run_ShortHistory_ReturnsNothing()
    {
        SetupBacktester.Run("X", Candles(100, i => 100 + i), new SetupStrategy { Entry = SetupEntryRule.Breakout }).Should().BeEmpty();
    }

    [Fact]
    public void Summarize_FewTrades_RefusesToConclude()
    {
        SetupBacktestResult result = SetupBacktester.Summarize([new SetupBacktestTrade("X", TradeDirection.Buy, DateTimeOffset.UtcNow, 100, 104, 2)], 1);

        result.Verdict.Should().Contain("pequena");
    }

    [Fact]
    public void Excursion_MeasuresTheWorstAndBestMoveInR()
    {
        List<Candle> candles =
        [
            new(DateTimeOffset.UtcNow, 100, 103, 99.5, 102, 1),
            new(DateTimeOffset.UtcNow.AddHours(1), 102, 106, 101, 105, 1)
        ];

        (double mae, double mfe) = TradeExcursion.Compute(TradeDirection.Buy, 100, 98, candles)!.Value;

        mae.Should().BeApproximately(-0.25, 1e-9);
        mfe.Should().BeApproximately(3, 1e-9);
    }

    [Fact]
    public void Management_AlertsOnceAtEachPlanLevel()
    {
        TradeRecord record = new() { BreakEvenAtR = 1, PartialAtR = 2, MaxHoldingDays = 5 };
        DateTimeOffset entry = DateTimeOffset.UtcNow.AddDays(-1);

        TradeManagement.Check(TradeDirection.Buy, 100, 98, 100.5, entry, DateTimeOffset.UtcNow, record).Should().BeNull();
        TradeManagement.Check(TradeDirection.Buy, 100, 98, 102.5, entry, DateTimeOffset.UtcNow, record).Should().Be(ManagementAction.BreakEven);
        record.BreakEvenAlerted = true;
        TradeManagement.Check(TradeDirection.Buy, 100, 98, 104.5, entry, DateTimeOffset.UtcNow, record).Should().Be(ManagementAction.Partial);
        record.PartialAlerted = true;
        TradeManagement.Check(TradeDirection.Buy, 100, 98, 104.5, entry.AddDays(-10), DateTimeOffset.UtcNow, record).Should().Be(ManagementAction.TimeExit);
        TradeManagement.Check(TradeDirection.Sell, 100, 102, 97.5, entry, DateTimeOffset.UtcNow, new TradeRecord { BreakEvenAtR = 1 }).Should().Be(ManagementAction.BreakEven);
    }
}

public class AssistantIntentParserTests
{
    private static readonly IReadOnlyList<InstrumentName> Instruments =
    [
        new("AAPL", "Apple Inc."),
        new("NVDA", "NVIDIA Corporation"),
        new("GC=F", "Gold Futures"),
        new("BTC-USD", "Bitcoin USD"),
        new("^GSPC", "S&P 500"),
        new("EURUSD=X", "EUR/USD"),
        new("A", "Agilent Technologies")
    ];

    [Theory]
    [InlineData("vou comprar NVDA", "NVDA", TradeDirection.Buy, true)]
    [InlineData("quero vender AAPL agora", "AAPL", TradeDirection.Sell, true)]
    [InlineData("vou comprar $nvda", "NVDA", TradeDirection.Buy, true)]
    [InlineData("o que mexe hoje no ouro?", "GC=F", null, false)]
    [InlineData("vou entrar long em bitcoin", "BTC-USD", TradeDirection.Buy, true)]
    [InlineData("o que achas da Apple?", "AAPL", null, false)]
    [InlineData("quero comprar o euro dólar", "EURUSD=X", TradeDirection.Buy, true)]
    [InlineData("vou shortar o S&P 500", "^GSPC", TradeDirection.Sell, true)]
    public void Parse_UnderstandsSymbolsDirectionAndTradeIntent(string text, string? symbol, TradeDirection? direction, bool wantsTrade)
    {
        AssistantIntent intent = AssistantIntentParser.Parse(text, Instruments);

        intent.Symbol.Should().Be(symbol);
        intent.Direction.Should().Be(direction);
        intent.WantsTrade.Should().Be(wantsTrade);
    }

    [Fact]
    public void Parse_UsesTheContextSymbolWhenNothingIsMentioned()
    {
        AssistantIntentParser.Parse("e qual é o stop?", Instruments, "NVDA").Symbol.Should().Be("NVDA");
    }

    [Fact]
    public void Parse_CommonUppercaseWordsAreNotSymbols()
    {
        AssistantIntentParser.Parse("o RSI está alto e o CPI sai amanhã", Instruments).Symbol.Should().BeNull();
    }

    [Fact]
    public void Parse_BothDirectionsMentioned_IsAmbiguous()
    {
        AssistantIntentParser.Parse("devo comprar ou vender NVDA?", Instruments).Direction.Should().BeNull();
    }
}

public class TradingSystemIntegrationTests
{
    private const string Password = "Str0ng!Passw0rd";

    private sealed record Tokens(string AccessToken);

    private static async Task<HttpClient> SignIn(TraderFactory factory, string email)
    {
        HttpClient client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", new { email, password = Password })).EnsureSuccessStatusCode();
        Tokens tokens = (await (await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password })).Content.ReadFromJsonAsync<Tokens>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static SystemTradeRequest Request(double? stop = 98, double quantity = 40, IReadOnlyList<string>? checks = null, string? justification = null) => new(
        new OpenTradeRequest("AAPL", TradeDirection.Buy, DateTimeOffset.UtcNow.AddMinutes(-5), 100, quantity, stop, 106, 1, 0, "XTB", string.Empty, null),
        null,
        checks,
        justification,
        null,
        null);

    [Fact]
    public async Task System_WithoutToken_ReturnsUnauthorized()
    {
        await using TraderFactory factory = new();

        foreach (string path in new[] { "/api/system-trading/status", "/api/system-trading/plan", "/api/assistant/status" })
        {
            (await factory.CreateClient().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Plan_FirstSaveCreatesVersionOne_AndLaterChangesAfterTradesAreLocked()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");

        TradingPlanDto empty = (await client.GetFromJsonAsync<TradingPlanDto>("/api/system-trading/plan"))!;
        empty.Exists.Should().BeFalse();

        TradingRules rules = new() { RequireSetup = false, MaxTradesPerDay = 5, Checklist = [] };
        (await client.PutAsJsonAsync("/api/system-trading/plan", new SavePlanRequest(rules, "Princípios", "Primeira versão", null))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        TradingPlanDto saved = (await client.GetFromJsonAsync<TradingPlanDto>("/api/system-trading/plan"))!;
        saved.Version.Should().Be(1);
        saved.Rules.MaxTradesPerDay.Should().Be(5);

        (await client.PostAsJsonAsync("/api/system-trading/trades", Request())).StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage locked = await client.PutAsJsonAsync("/api/system-trading/plan", new SavePlanRequest(rules with { MaxTradesPerDay = 9 }, "x", "mudei a meio do dia", null));
        locked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await locked.Content.ReadAsStringAsync()).Should().Contain("revisão semanal");

        (await client.PostAsJsonAsync("/api/system-trading/reviews/weekly", new SaveWeeklyReviewRequest("a", "b", "c", "mudar"))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/system-trading/plan", new SavePlanRequest(rules with { MaxTradesPerDay = 9 }, "x", "depois da revisão", null))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        TradingPlanDto next = (await client.GetFromJsonAsync<TradingPlanDto>("/api/system-trading/plan"))!;
        next.Version.Should().Be(2);
        next.History.Should().HaveCount(2);
    }

    [Fact]
    public async Task OpenSystemTrade_RecordsThePhaseAndRulesAndBlocksWithoutAStop()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");
        (await client.PutAsJsonAsync("/api/system-trading/plan", new SavePlanRequest(new TradingRules { RequireSetup = false, Checklist = ["Estou calmo"] }, string.Empty, "início", null))).EnsureSuccessStatusCode();

        HttpResponseMessage noChecklist = await client.PostAsJsonAsync("/api/system-trading/trades", Request());
        noChecklist.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage noStop = await client.PostAsJsonAsync("/api/system-trading/trades", Request(stop: null, checks: ["Estou calmo"]));
        noStop.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        SystemTradeResult blocked = (await noStop.Content.ReadFromJsonAsync<SystemTradeResult>())!;
        blocked.Check!.Blocked.Should().BeTrue();

        HttpResponseMessage ok = await client.PostAsJsonAsync("/api/system-trading/trades", Request(checks: ["Estou calmo"]));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        SystemTradeResult result = (await ok.Content.ReadFromJsonAsync<SystemTradeResult>())!;
        result.Trade!.Platform.Should().Be("Papel");
        result.Trade.InitialStopLoss.Should().Be(98);

        TradeRecordDto record = (await client.GetFromJsonAsync<TradeRecordDto>($"/api/system-trading/records/{result.Trade.Id}"))!;
        record.Phase.Should().Be(SystemPhase.Paper);
        record.PlanVersion.Should().Be(1);
        record.RuleBreaks.Should().Be(0);
    }

    [Fact]
    public async Task OpenSystemTrade_BreakingARule_RequiresAJustificationAndCountsAsABreak()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");
        (await client.PutAsJsonAsync("/api/system-trading/plan", new SavePlanRequest(new TradingRules { RequireSetup = false, Checklist = [] }, string.Empty, "início", null))).EnsureSuccessStatusCode();

        HttpResponseMessage without = await client.PostAsJsonAsync("/api/system-trading/trades", Request(quantity: 400));
        without.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage with = await client.PostAsJsonAsync("/api/system-trading/trades", Request(quantity: 400, justification: "Convicção alta neste ativo"));
        with.StatusCode.Should().Be(HttpStatusCode.OK);
        SystemTradeResult result = (await with.Content.ReadFromJsonAsync<SystemTradeResult>())!;

        TradeRecordDto record = (await client.GetFromJsonAsync<TradeRecordDto>($"/api/system-trading/records/{result.Trade!.Id}"))!;
        record.RuleBreaks.Should().BeGreaterThan(0);
        record.Justification.Should().Contain("Convicção");
    }

    [Fact]
    public async Task Review_ClosedTradeMustBeReviewed_AndTheBacklogBlocksNewTrades()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");
        TradingRules rules = new() { RequireSetup = false, Checklist = [], MaxPendingReviews = 1, MaxTradesPerDay = 0, MaxDailyLossPercent = 0, MaxWeeklyLossPercent = 0 };
        (await client.PutAsJsonAsync("/api/system-trading/plan", new SavePlanRequest(rules, string.Empty, "início", null))).EnsureSuccessStatusCode();

        SystemTradeResult opened = (await (await client.PostAsJsonAsync("/api/system-trading/trades", Request())).Content.ReadFromJsonAsync<SystemTradeResult>())!;
        (await client.PostAsJsonAsync($"/api/journal/trades/{opened.Trade!.Id}/close", new CloseTradeRequest(DateTimeOffset.UtcNow, 104, 0, null))).EnsureSuccessStatusCode();

        IReadOnlyList<PendingReview> pending = (await client.GetFromJsonAsync<IReadOnlyList<PendingReview>>("/api/system-trading/reviews/pending"))!;
        pending.Should().ContainSingle(p => p.TradeId == opened.Trade.Id);

        (await client.PostAsJsonAsync("/api/system-trading/trades", Request())).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync($"/api/system-trading/records/{opened.Trade.Id}/review", new TradeReviewRequest(true, true, "bem", "nada", "paciência"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<IReadOnlyList<PendingReview>>("/api/system-trading/reviews/pending"))!.Should().BeEmpty();
        (await client.PostAsJsonAsync("/api/system-trading/trades", Request())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Levels_MovingTheStopKeepsTheInitialRiskForR()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");
        JournalTradeDto trade = (await (await client.PostAsJsonAsync("/api/journal/trades", new
        {
            symbol = "AAPL", direction = 0, entryTime = DateTimeOffset.UtcNow.AddHours(-1), entryPrice = 100, quantity = 10, stopLoss = 98, takeProfit = 110, fxRate = 1, fees = 0, platform = "XTB", notes = string.Empty
        })).Content.ReadFromJsonAsync<JournalTradeDto>())!;

        HttpResponseMessage moved = await client.PutAsJsonAsync($"/api/journal/trades/{trade.Id}/levels", new TradeLevelsRequest(100, 110));
        moved.StatusCode.Should().Be(HttpStatusCode.OK);

        JournalTradeDto closed = (await (await client.PostAsJsonAsync($"/api/journal/trades/{trade.Id}/close", new CloseTradeRequest(DateTimeOffset.UtcNow, 104, 0, null))).Content.ReadFromJsonAsync<JournalTradeDto>())!;

        closed.StopLoss.Should().Be(100);
        closed.InitialStopLoss.Should().Be(98);
        closed.RMultiple.Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public async Task Setups_CanBeCreatedListedAndDeactivatedWhenUsed()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");

        SaveSetupRequest request = new("Pullback", "desc", "entra", "sai", ["Volume ok"], null, new SetupStrategy { Entry = SetupEntryRule.TrendPullback }, true);
        (await client.PostAsJsonAsync("/api/system-trading/setups", request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/system-trading/setups", request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyList<TradingSetupDto> setups = (await client.GetFromJsonAsync<IReadOnlyList<TradingSetupDto>>("/api/system-trading/setups"))!;
        setups.Should().ContainSingle(s => s.Name == "Pullback" && s.Checklist.Count == 1);

        (await client.DeleteAsync($"/api/system-trading/setups/{setups[0].Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<IReadOnlyList<TradingSetupDto>>("/api/system-trading/setups"))!.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_ReportsTheNextStepsForANewUser()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");

        SystemStatus status = (await client.GetFromJsonAsync<SystemStatus>("/api/system-trading/status"))!;

        status.PlanExists.Should().BeFalse();
        status.CanTrade.Should().BeTrue();
        status.Phase.Phase.Should().Be(SystemPhase.Paper);
        status.NextSteps.Should().Contain(s => s.Contains("plano"));
    }

    [Fact]
    public async Task Assistant_WithoutAModel_StillReturnsTheStatusAndAPlainAnswer()
    {
        await using TraderFactory factory = new();
        HttpClient client = await SignIn(factory, "owner@example.com");

        AssistantStatus status = (await client.GetFromJsonAsync<AssistantStatus>("/api/assistant/status"))!;
        status.ModelEnabled.Should().BeFalse();

        HttpResponseMessage reply = await client.PostAsJsonAsync("/api/assistant/ask", new AssistantRequest("como estou esta semana?", null, null, null));
        reply.StatusCode.Should().Be(HttpStatusCode.OK);
        AssistantReply body = (await reply.Content.ReadFromJsonAsync<AssistantReply>())!;
        body.UsedModel.Should().BeFalse();
        body.Text.Should().NotBeNullOrWhiteSpace();
    }
}
