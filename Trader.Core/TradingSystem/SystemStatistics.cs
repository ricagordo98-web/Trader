using System.Globalization;
using Trader.Core.Journal;
using Trader.Core.Models;

namespace Trader.Core.TradingSystem;

public sealed record SystemTrade(JournalTradeDto Trade, TradeRecord? Record);

public static class SystemStatistics
{
    private const double Z = 1.96;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static PerformanceGroup Group(string label, IEnumerable<double> results)
    {
        List<double> r = results.ToList();

        if (r.Count == 0)
        {
            return new PerformanceGroup(label, 0, 0, null, 0, null, null, null);
        }

        double mean = r.Average();
        double gains = r.Where(x => x > 0).Sum();
        double losses = -r.Where(x => x < 0).Sum();
        double? low = null;
        double? high = null;

        if (r.Count >= 2)
        {
            double sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Count - 1));
            double margin = Z * sd / Math.Sqrt(r.Count);
            low = mean - margin;
            high = mean + margin;
        }

        return new PerformanceGroup(label, r.Count, r.Count(x => x > 0) / (double)r.Count, mean, r.Sum(), losses > 0 ? gains / losses : null, low, high);
    }

    public static int? TradesNeeded(IReadOnlyList<double> results)
    {
        if (results.Count < 5)
        {
            return null;
        }

        double mean = results.Average();
        double sd = Math.Sqrt(results.Sum(x => (x - mean) * (x - mean)) / (results.Count - 1));

        if (mean <= 0 || sd <= 0)
        {
            return null;
        }

        int needed = (int)Math.Ceiling(Math.Pow(Z * sd / mean, 2));
        return Math.Max(0, needed - results.Count);
    }

    public static SystemPerformance Performance(IReadOnlyList<SystemTrade> trades, TimeZoneInfo zone)
    {
        List<SystemTrade> closed = trades
            .Where(t => t.Trade.Status == TradeStatus.Closed && t.Trade.RMultiple is not null)
            .OrderBy(t => t.Trade.ExitTime)
            .ToList();

        static double R(SystemTrade t) => t.Trade.RMultiple!.Value;

        List<SystemTrade> inSystem = closed.Where(t => t.Record is not null).ToList();
        List<double> inRules = inSystem.Where(t => t.Record!.RuleBreaks == 0).Select(R).ToList();

        List<PerformanceGroup> bySetup = inSystem
            .GroupBy(t => string.IsNullOrEmpty(t.Record!.SetupName) ? "Sem setup" : t.Record.SetupName)
            .Select(g => Group(g.Key, g.Select(R)))
            .OrderByDescending(g => g.Count)
            .ToList();

        List<PerformanceGroup> byVersion = inSystem
            .GroupBy(t => t.Record!.PlanVersion)
            .OrderBy(g => g.Key)
            .Select(g => Group(g.Key == 0 ? "Regras por omissão" : $"Plano v{g.Key}", g.Select(R)))
            .ToList();

        List<PerformanceGroup> byPhase = inSystem
            .GroupBy(t => t.Record!.Phase)
            .OrderBy(g => g.Key)
            .Select(g => Group(PhaseLabel(g.Key), g.Select(R)))
            .ToList();

        string[] days = ["Domingo", "Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado"];
        List<PerformanceGroup> byWeekday = closed
            .GroupBy(t => TimeZoneInfo.ConvertTime(t.Trade.EntryTime, zone).DayOfWeek)
            .OrderBy(g => ((int)g.Key + 6) % 7)
            .Select(g => Group(days[(int)g.Key], g.Select(R)))
            .ToList();

        List<PerformanceGroup> byHour = closed
            .GroupBy(t => TimeZoneInfo.ConvertTime(t.Trade.EntryTime, zone).Hour)
            .OrderBy(g => g.Key)
            .Select(g => Group($"{g.Key:00}h", g.Select(R)))
            .ToList();

        List<SystemTrade> withExcursion = inSystem.Where(t => t.Record!.MaeR is not null && t.Record.MfeR is not null).ToList();
        ExcursionSummary excursion = new(
            withExcursion.Count,
            withExcursion.Count == 0 ? null : withExcursion.Average(t => t.Record!.MaeR!.Value),
            withExcursion.Count == 0 ? null : withExcursion.Average(t => t.Record!.MfeR!.Value),
            withExcursion.Count(t => R(t) < 0 && t.Record!.MfeR >= 1),
            withExcursion.Count(t => R(t) > 0 && t.Record!.MaeR <= -0.8));

        List<CurvePoint> curve = [];
        double cumulative = 0;
        double peak = 0;
        double drawdown = 0;

        foreach (SystemTrade trade in closed)
        {
            cumulative += R(trade);
            peak = Math.Max(peak, cumulative);
            drawdown = Math.Max(drawdown, peak - cumulative);
            curve.Add(new CurvePoint(trade.Trade.ExitTime!.Value, cumulative));
        }

        PerformanceGroup inRulesGroup = Group("Dentro das regras", inRules);
        PerformanceGroup outRulesGroup = Group("Fora das regras", inSystem.Where(t => t.Record!.RuleBreaks > 0).Select(R));

        return new SystemPerformance(
            Group("Todas", closed.Select(R)),
            inRulesGroup,
            outRulesGroup,
            Group("Fora do sistema", closed.Where(t => t.Record is null).Select(R)),
            bySetup,
            byVersion,
            byPhase,
            byWeekday,
            byHour,
            excursion,
            TradesNeeded(inRules),
            curve,
            drawdown,
            Verdict(inRulesGroup, outRulesGroup));
    }

    public static PhaseProgress Progress(SystemPhase phase, DateTimeOffset since, TradingRules rules, IReadOnlyList<SystemTrade> trades)
    {
        List<SystemTrade> inPhase = trades
            .Where(t => t.Record is { } record && record.Phase == phase && t.Trade.Status == TradeStatus.Closed && t.Trade.RMultiple is not null && t.Trade.EntryTime >= since)
            .OrderBy(t => t.Trade.ExitTime)
            .ToList();

        int needed = phase switch
        {
            SystemPhase.Paper => rules.PaperTradesToPromote,
            SystemPhase.SmallReal => rules.SmallTradesToPromote,
            _ => 0
        };

        double? average = inPhase.Count == 0 ? null : inPhase.Average(t => t.Trade.RMultiple!.Value);
        double? adherence = inPhase.Count == 0 ? null : inPhase.Count(t => t.Record!.RuleBreaks == 0) * 100.0 / inPhase.Count;

        bool demoted = phase != SystemPhase.Paper
            && rules.DemotionWindow > 0
            && inPhase.Count >= rules.DemotionWindow
            && inPhase.TakeLast(rules.DemotionWindow).Average(t => t.Trade.RMultiple!.Value) <= rules.DemotionAverageR;

        bool canPromote = phase != SystemPhase.Full
            && inPhase.Count >= needed
            && average > 0
            && adherence >= rules.MinAdherencePercent;

        string message = phase switch
        {
            _ when demoted => $"As últimas {rules.DemotionWindow} trades deram em média {inPhase.TakeLast(rules.DemotionWindow).Average(t => t.Trade.RMultiple!.Value).ToString("+0.00;-0.00", Pt)} R. Voltas uma fase atrás.",
            SystemPhase.Full => "Estás na fase de tamanho normal. Se o desempenho cair abaixo do limite, voltas a real pequeno.",
            _ when canPromote => $"Cumpriste os critérios: {inPhase.Count} trades, {average!.Value.ToString("+0.00;-0.00", Pt)} R em média e {adherence!.Value.ToString("0", Pt)}% dentro das regras. Podes avançar.",
            _ => $"{inPhase.Count} de {needed} trades nesta fase. Para avançar precisas também de R médio positivo e pelo menos {rules.MinAdherencePercent.ToString("0", Pt)}% das trades dentro das regras."
        };

        return new PhaseProgress(phase, since, inPhase.Count, needed, average, adherence, canPromote, demoted, message);
    }

    public static WeekSummary Week(DateOnly weekStart, IReadOnlyList<SystemTrade> trades, TimeZoneInfo zone)
    {
        DateOnly weekEnd = weekStart.AddDays(7);

        bool InWeek(DateTimeOffset? time) => time is { } t && CircuitBreakers.LocalDate(t, zone) is var d && d >= weekStart && d < weekEnd;

        List<SystemTrade> opened = trades.Where(t => InWeek(t.Trade.EntryTime)).ToList();
        List<SystemTrade> closed = trades.Where(t => t.Trade.Status == TradeStatus.Closed && InWeek(t.Trade.ExitTime)).ToList();
        int inRules = opened.Count(t => t.Record is { RuleBreaks: 0 });
        int outRules = opened.Count(t => t.Record is { RuleBreaks: > 0 });

        return new WeekSummary(
            weekStart,
            opened.Count,
            closed.Count,
            closed.Count(t => t.Trade.NetPnl > 0),
            closed.Sum(t => t.Trade.RMultiple ?? 0),
            closed.Sum(t => t.Trade.NetPnl ?? 0),
            inRules,
            outRules,
            inRules + outRules == 0 ? null : inRules * 100.0 / (inRules + outRules),
            closed.Count(t => t.Record is { ReviewedAt: null }),
            closed.Where(t => t.Record is { Lesson.Length: > 0 }).Select(t => $"{t.Trade.Symbol}: {t.Record!.Lesson}").ToList());
    }

    public static string PhaseLabel(SystemPhase phase) => phase switch
    {
        SystemPhase.Paper => "Papel",
        SystemPhase.SmallReal => "Real pequeno",
        _ => "Tamanho normal"
    };

    private static string Verdict(PerformanceGroup inRules, PerformanceGroup outRules)
    {
        if (inRules.Count < 10)
        {
            return $"Só {inRules.Count} trades fechadas dentro das regras. Ainda é cedo para tirar conclusões: continua a seguir o plano.";
        }

        string system = inRules.CiLow > 0
            ? "O sistema tem expectância positiva com significado estatístico."
            : inRules.CiHigh < 0
                ? "O sistema está a perder de forma consistente. Revê o plano na próxima revisão semanal."
                : "Ainda não dá para distinguir o sistema de sorte: o intervalo de confiança inclui zero.";

        string discipline = outRules.Count >= 3 && outRules.AverageR is { } outAvg && inRules.AverageR is { } inAvg
            ? outAvg < inAvg
                ? $" As trades fora das regras rendem menos ({outAvg.ToString("+0.00;-0.00", Pt)} R contra {inAvg.ToString("+0.00;-0.00", Pt)} R): quebrar regras está a custar-te dinheiro."
                : " As trades fora das regras não estão a render menos, mas a amostra é pequena; não mudes o plano por isso."
            : string.Empty;

        return system + discipline;
    }
}

public static class TradingRulesValidator
{
    public static IReadOnlyList<string> Validate(TradingRules rules)
    {
        List<string> errors = [];

        if (rules.RiskPercent is <= 0 or > 10)
        {
            errors.Add("O risco por trade deve estar entre 0 e 10% do capital.");
        }

        if (rules.MinRewardRisk is < 0 or > 10)
        {
            errors.Add("O R:R mínimo deve estar entre 0 e 10.");
        }

        if (rules.MaxTradesPerDay is < 0 or > 100 || rules.MaxOpenPositions is < 0 or > 100 || rules.MaxConsecutiveLosses is < 0 or > 50)
        {
            errors.Add("Os limites de trades e posições devem estar entre 0 e 100.");
        }

        if (rules.MaxDailyLossPercent is < 0 or > 50 || rules.MaxWeeklyLossPercent is < 0 or > 50)
        {
            errors.Add("Os limites de perda devem estar entre 0 e 50% do capital.");
        }

        if (rules.TradingStartHour is < 0 or > 23 || rules.TradingEndHour is < 0 or > 24)
        {
            errors.Add("O horário deve estar entre 0h e 24h.");
        }

        if (rules.SmallRiskFactor is <= 0 or > 1)
        {
            errors.Add("O fator de risco da fase real pequeno deve estar entre 0 e 1.");
        }

        if (rules.MaxCorrelation is < 0 or > 1)
        {
            errors.Add("A correlação máxima deve estar entre 0 e 1.");
        }

        if (rules.Checklist.Count > 20 || rules.Checklist.Any(c => c.Length > 200))
        {
            errors.Add("A checklist pode ter até 20 pontos de 200 caracteres.");
        }

        if (rules.CooldownHours is < 0 or > 720 || rules.EarningsBlackoutHours is < 0 or > 720 || rules.MacroBlackoutHours is < 0 or > 168 || rules.MaxHoldingDays is < 0 or > 365)
        {
            errors.Add("Há um intervalo de tempo fora dos limites.");
        }

        if (rules.PaperTradesToPromote is < 0 or > 1000 || rules.SmallTradesToPromote is < 0 or > 1000 || rules.DemotionWindow is < 0 or > 500 || rules.MinAdherencePercent is < 0 or > 100)
        {
            errors.Add("Os critérios de fase estão fora dos limites.");
        }

        return errors;
    }
}
