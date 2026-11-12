using System.Globalization;
using Trader.Core.Journal;
using Trader.Core.Models;

namespace Trader.Core.TradingSystem;

public sealed record MarketEvent(string Title, DateTimeOffset Time, bool IsEarnings);

public sealed record OpenExposure(string Symbol, TradeDirection Direction, double? Correlation);

public sealed record PreTradeInputs(
    PreTradeRequest Request,
    TradingRules Rules,
    bool PlanExists,
    int PlanVersion,
    AccountProfile Account,
    SystemPhase Phase,
    AssetClass? AssetClass,
    IReadOnlyList<JournalTradeDto> Trades,
    int PendingReviews,
    TradingSetupDto? Setup,
    IReadOnlyList<MarketEvent> Events,
    IReadOnlyList<OpenExposure> Exposure,
    DateTimeOffset Now,
    TimeZoneInfo Zone);

public sealed record BreakerState(
    int TradesToday,
    double DailyPnl,
    double DailyLossLimit,
    double WeeklyPnl,
    double WeeklyLossLimit,
    int OpenPositions,
    int LosingStreak,
    DateTimeOffset? CooldownUntil,
    IReadOnlyList<CheckItem> Items);

public static class CircuitBreakers
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static BreakerState Evaluate(TradingRules rules, AccountProfile account, IReadOnlyList<JournalTradeDto> trades, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        DateOnly today = DateOnly.FromDateTime(localNow);
        DateOnly weekStart = WeekStart(today);

        int tradesToday = trades.Count(t => LocalDate(t.EntryTime, zone) == today);
        double dailyPnl = trades.Where(t => t.Status == TradeStatus.Closed && t.ExitTime is { } exit && LocalDate(exit, zone) == today).Sum(t => t.NetPnl ?? 0);
        double weeklyPnl = trades.Where(t => t.Status == TradeStatus.Closed && t.ExitTime is { } exit && LocalDate(exit, zone) >= weekStart).Sum(t => t.NetPnl ?? 0);
        double dailyLimit = rules.MaxDailyLossPercent > 0 ? account.Capital * rules.MaxDailyLossPercent / 100 : 0;
        double weeklyLimit = rules.MaxWeeklyLossPercent > 0 ? account.Capital * rules.MaxWeeklyLossPercent / 100 : 0;
        int open = trades.Count(t => t.Status == TradeStatus.Open);

        List<JournalTradeDto> closed = trades
            .Where(t => t.Status == TradeStatus.Closed && t.ExitTime is not null && t.NetPnl is not null)
            .OrderByDescending(t => t.ExitTime)
            .ToList();
        int streak = closed.TakeWhile(t => t.NetPnl < 0).Count();
        DateTimeOffset? cooldownUntil = rules.MaxConsecutiveLosses > 0 && streak >= rules.MaxConsecutiveLosses && rules.CooldownHours > 0
            ? closed[0].ExitTime!.Value.AddHours(rules.CooldownHours)
            : null;

        List<CheckItem> items =
        [
            rules.MaxTradesPerDay > 0
                ? Item("trades-today", "Trades por dia", tradesToday < rules.MaxTradesPerDay, CheckSeverity.Hard,
                    $"{tradesToday} de {rules.MaxTradesPerDay} trades abertas hoje.")
                : Skip("trades-today", "Trades por dia", "Sem limite diário de trades."),
            dailyLimit > 0
                ? Item("daily-loss", "Perda diária", dailyPnl > -dailyLimit, CheckSeverity.Hard,
                    $"Resultado de hoje {Money(dailyPnl)}; o limite é -{Money(dailyLimit)}.")
                : Skip("daily-loss", "Perda diária", "Sem limite de perda diária."),
            weeklyLimit > 0
                ? Item("weekly-loss", "Perda semanal", weeklyPnl > -weeklyLimit, CheckSeverity.Hard,
                    $"Resultado da semana {Money(weeklyPnl)}; o limite é -{Money(weeklyLimit)}.")
                : Skip("weekly-loss", "Perda semanal", "Sem limite de perda semanal."),
            cooldownUntil is { } until && now < until
                ? new CheckItem("cooldown", "Pausa depois de perdas", CheckStatus.Fail, CheckSeverity.Hard,
                    $"{streak} perdas seguidas. Pausa obrigatória até {TimeZoneInfo.ConvertTime(until, zone):dd/MM HH:mm}.")
                : new CheckItem("cooldown", "Pausa depois de perdas", CheckStatus.Pass, CheckSeverity.Hard,
                    streak == 0 ? "Sem perdas seguidas." : $"{streak} perda(s) seguida(s); a pausa começa em {rules.MaxConsecutiveLosses}.")
        ];

        return new BreakerState(tradesToday, dailyPnl, dailyLimit, weeklyPnl, weeklyLimit, open, streak, cooldownUntil, items);
    }

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static DateOnly LocalDate(DateTimeOffset time, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime);

    internal static string Money(double value) => value.ToString("#,0.00", Pt);

    private static CheckItem Item(string code, string label, bool pass, CheckSeverity severity, string detail) =>
        new(code, label, pass ? CheckStatus.Pass : CheckStatus.Fail, severity, detail);

    private static CheckItem Skip(string code, string label, string detail) => new(code, label, CheckStatus.Pass, CheckSeverity.Info, detail);
}

public static class PreTradeChecker
{
    private const double RiskTolerance = 1.05;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static double RiskFactor(SystemPhase phase, TradingRules rules) => phase == SystemPhase.SmallReal ? rules.SmallRiskFactor : 1;

    public static double SuggestedQuantity(double capital, double riskPercent, double factor, double entry, double? stopLoss, double fxRate)
    {
        if (stopLoss is not { } stop || entry <= 0 || fxRate <= 0 || Math.Abs(entry - stop) <= 0)
        {
            return 0;
        }

        double units = capital * riskPercent / 100 * factor / (Math.Abs(entry - stop) * fxRate);
        return units >= 1 ? Math.Floor(units) : Math.Round(units, 4);
    }

    public static IReadOnlyList<string> ManualItems(TradingRules rules, TradingSetupDto? setup) =>
        rules.Checklist.Concat(setup?.Checklist ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();

    public static PreTradeCheck Evaluate(PreTradeInputs input)
    {
        PreTradeRequest request = input.Request;
        TradingRules rules = input.Rules;
        double factor = RiskFactor(input.Phase, rules);
        List<CheckItem> items = [];

        if (!input.PlanExists)
        {
            items.Add(new CheckItem("plan", "Plano de trading", CheckStatus.Unknown, CheckSeverity.Info,
                "Ainda não escreveste o teu plano; estou a usar as regras por omissão. Cria-o em Sistema → Plano."));
        }

        bool stopValid = request.StopLoss is { } sl && sl > 0
            && (request.Direction == TradeDirection.Buy ? sl < request.Entry : sl > request.Entry);
        items.Add(new CheckItem("stop", "Stop loss definido", stopValid ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Hard,
            stopValid ? "O stop está do lado certo da entrada." : "Sem stop loss válido não há trade: define onde sais se estiveres errado."));

        double? riskPercent = null;

        if (stopValid && request.Quantity > 0 && input.Account.Capital > 0)
        {
            double riskAmount = Math.Abs(request.Entry - request.StopLoss!.Value) * request.Quantity * request.FxRate;
            riskPercent = riskAmount / input.Account.Capital * 100;
            double allowed = rules.RiskPercent * factor;
            items.Add(new CheckItem("risk", "Risco por trade", riskPercent <= allowed * RiskTolerance ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                $"Arriscas {riskPercent.Value.ToString("0.##", Pt)}% do capital ({CircuitBreakers.Money(riskAmount)}); o máximo nesta fase é {allowed.ToString("0.##", Pt)}%."));
        }
        else
        {
            items.Add(new CheckItem("risk", "Risco por trade", CheckStatus.Unknown, CheckSeverity.Rule, "Indica a quantidade e o stop para calcular o risco."));
        }

        double? rewardRisk = null;

        if (stopValid && request.TakeProfit is { } tp && tp > 0)
        {
            double risk = Math.Abs(request.Entry - request.StopLoss!.Value);
            bool tpSide = request.Direction == TradeDirection.Buy ? tp > request.Entry : tp < request.Entry;
            rewardRisk = tpSide && risk > 0 ? Math.Abs(tp - request.Entry) / risk : 0;
            items.Add(new CheckItem("rr", "Risco/retorno mínimo", rewardRisk >= rules.MinRewardRisk ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                $"R:R {rewardRisk.Value.ToString("0.##", Pt)}; o mínimo do plano é {rules.MinRewardRisk.ToString("0.##", Pt)}."));
        }
        else
        {
            items.Add(new CheckItem("rr", "Risco/retorno mínimo", CheckStatus.Fail, CheckSeverity.Rule, "Define um take profit para saber se compensa o risco."));
        }

        if (rules.RequireSetup)
        {
            items.Add(new CheckItem("setup", "Setup do playbook", input.Setup is null ? CheckStatus.Fail : CheckStatus.Pass, CheckSeverity.Rule,
                input.Setup is null ? "Escolhe o setup do teu playbook que esta trade segue." : $"Setup: {input.Setup.Name}."));
        }

        if (rules.AllowedAssetClasses.Count > 0)
        {
            bool allowedClass = input.AssetClass is { } assetClass && rules.AllowedAssetClasses.Contains(assetClass);
            items.Add(new CheckItem("asset", "Mercado permitido", allowedClass ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                allowedClass ? "Este mercado está no teu plano." : "Este tipo de ativo não está nos mercados do teu plano."));
        }

        if (rules.TradingStartHour is { } start && rules.TradingEndHour is { } end)
        {
            int hour = TimeZoneInfo.ConvertTime(input.Now, input.Zone).Hour;
            bool inside = start <= end ? hour >= start && hour < end : hour >= start || hour < end;
            items.Add(new CheckItem("hours", "Horário de trading", inside ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                $"O teu horário é das {start}h às {end}h."));
        }

        items.AddRange(CircuitBreakers.Evaluate(rules, input.Account, input.Trades, input.Now, input.Zone).Items);

        int open = input.Trades.Count(t => t.Status == TradeStatus.Open);

        if (rules.MaxOpenPositions > 0)
        {
            items.Add(new CheckItem("open-positions", "Posições abertas", open < rules.MaxOpenPositions ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                $"{open} abertas; o máximo é {rules.MaxOpenPositions}."));
        }

        bool sameSymbol = input.Trades.Any(t => t.Status == TradeStatus.Open && string.Equals(t.Symbol, request.Symbol, StringComparison.OrdinalIgnoreCase));
        items.Add(new CheckItem("same-symbol", "Sem posição repetida", sameSymbol ? CheckStatus.Fail : CheckStatus.Pass, CheckSeverity.Rule,
            sameSymbol ? "Já tens uma trade aberta neste ativo." : "Não tens outra trade aberta neste ativo."));

        if (rules.MaxCorrelation > 0 && input.Exposure.Count > 0)
        {
            List<OpenExposure> correlated = input.Exposure
                .Where(e => e.Correlation is { } c && Math.Abs(c) >= rules.MaxCorrelation
                    && (c > 0 ? e.Direction == request.Direction : e.Direction != request.Direction))
                .ToList();
            bool ok = correlated.Count < Math.Max(1, rules.MaxCorrelatedPositions);
            items.Add(new CheckItem("correlation", "Exposição correlacionada", ok ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                correlated.Count == 0
                    ? "Nenhuma posição aberta anda junta com este ativo."
                    : $"Anda junto com: {string.Join(", ", correlated.Select(c => $"{c.Symbol} ({c.Correlation!.Value.ToString("0.00", Pt)})"))}. Na prática é a mesma aposta."));
        }

        if (rules.EarningsBlackoutHours > 0)
        {
            MarketEvent? earnings = input.Events.FirstOrDefault(e => e.IsEarnings && e.Time >= input.Now.AddHours(-12) && e.Time - input.Now <= TimeSpan.FromHours(rules.EarningsBlackoutHours));
            items.Add(new CheckItem("earnings", "Sem resultados por perto", earnings is null ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                earnings is null ? $"Sem resultados da empresa nas próximas {rules.EarningsBlackoutHours} h." : $"{earnings.Title} em {TimeZoneInfo.ConvertTime(earnings.Time, input.Zone):dd/MM}."));
        }

        if (rules.AvoidHighImpactMacro && rules.MacroBlackoutHours > 0)
        {
            List<MarketEvent> macro = input.Events.Where(e => !e.IsEarnings && e.Time >= input.Now && e.Time - input.Now <= TimeSpan.FromHours(rules.MacroBlackoutHours)).ToList();
            items.Add(new CheckItem("macro", "Sem macro forte por perto", macro.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Rule,
                macro.Count == 0 ? $"Sem eventos macro importantes nas próximas {rules.MacroBlackoutHours} h." : string.Join("; ", macro.Select(m => $"{m.Title} ({TimeZoneInfo.ConvertTime(m.Time, input.Zone):dd/MM HH:mm})"))));
        }

        if (rules.MaxPendingReviews > 0)
        {
            items.Add(new CheckItem("reviews", "Revisões em dia", input.PendingReviews < rules.MaxPendingReviews ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Hard,
                input.PendingReviews == 0 ? "Todas as trades fechadas estão revistas." : $"{input.PendingReviews} trade(s) fechada(s) por rever; o limite é {rules.MaxPendingReviews}."));
        }

        bool hardFail = items.Any(i => i.Status == CheckStatus.Fail && i.Severity == CheckSeverity.Hard);
        bool ruleFail = items.Any(i => i.Status == CheckStatus.Fail && i.Severity == CheckSeverity.Rule);
        bool blocked = hardFail || (ruleFail && !rules.AllowJustifiedBreaks);
        double suggested = SuggestedQuantity(input.Account.Capital, rules.RiskPercent, factor, request.Entry, request.StopLoss, request.FxRate);

        string verdict = (hardFail, ruleFail, blocked) switch
        {
            (true, _, _) => "Bloqueado: um disjuntor ou regra obrigatória falhou. Hoje não entras nesta trade.",
            (_, true, true) => "Bloqueado: o teu plano não permite quebrar regras.",
            (_, true, false) => "Falha pelo menos uma regra. Só podes avançar com justificação, e fica registada como quebra de regra.",
            _ => "Todas as regras verificáveis estão cumpridas. Confirma a checklist manual."
        };

        return new PreTradeCheck(
            items,
            ManualItems(rules, input.Setup),
            blocked,
            !blocked && ruleFail,
            riskPercent,
            rewardRisk,
            suggested,
            input.Phase,
            factor,
            input.PlanVersion,
            input.PlanExists,
            verdict);
    }
}
