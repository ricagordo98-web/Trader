using Trader.Core.Models;

namespace Trader.Core.Scoring;

public sealed record Interval(double Low, double High);

public sealed record ReliabilityStat(string Name, int Count, int Wins, double WinRate, Interval WinRateCi, double AverageR);

public enum ReliabilityLevel
{
    Insufficient,
    NoEdge,
    Unproven,
    Moderate,
    High
}

public sealed record ReliabilityVerdict(ReliabilityLevel Level, string Title, IReadOnlyList<string> Reasons);

public sealed record ModelReliabilityReport(
    int Closed,
    int TakeProfitHits,
    int StopLossHits,
    int Expired,
    double WinRate,
    Interval WinRateCi,
    double BreakEvenWinRate,
    double AverageR,
    Interval AverageRCi,
    double? ProfitFactor,
    double AverageWinR,
    double AverageLossR,
    double AverageHoursToClose,
    double AverageHoursToTakeProfit,
    double AverageHoursToStopLoss,
    double BrierScore,
    double BaselineBrier,
    double CalibrationError,
    double AverageConfidence,
    IReadOnlyList<ReliabilityStat> ByHorizon,
    IReadOnlyList<ReliabilityStat> ByDirection,
    IReadOnlyList<ReliabilityStat> ByConfidence,
    IReadOnlyList<ReliabilityStat> BySentiment,
    ReliabilityVerdict Verdict,
    ShadowAnalysis? Shadow = null,
    IReadOnlyList<ReliabilityStat>? ByAssetClass = null,
    IReadOnlyList<ReliabilityStat>? ByHour = null,
    IReadOnlyList<ReliabilityStat>? ByVariant = null,
    ExcursionAnalysis? Excursions = null,
    SmartExitComparison? SmartExit = null,
    double AverageGrossR = 0,
    double AverageCostR = 0,
    int IndependentCases = 0,
    Interval? ClusteredAverageRCi = null,
    ProjectionPerformance? Projection = null);

public sealed record ProjectionPerformance(int Count, double PredictedWinRate, double ActualWinRate, int TargetHits, int StopHits, int Expired, double AverageR);

public sealed record ExcursionAnalysis(
    int Samples,
    double MedianMfeR,
    double MedianMaeR,
    double WinnersMaeP80,
    double StoppedReachedHalfTargetShare,
    double? SuggestedStopR,
    string Hint);

public sealed record SmartExitComparison(int Samples, double FixedAverageR, double SmartAverageR, double DifferenceR, string Hint);

public sealed record ShadowAnalysis(ReliabilityStat? Shown, ReliabilityStat? Hidden, IReadOnlyList<ReliabilityStat> ByReason);

public static class ModelReliability
{
    public const int MinSamples = 30;
    public const int GoodSamples = 150;

    private const double Z = 1.96;
    private const double SentimentFloor = 0.1;

    public static ModelReliabilityReport Calculate(IReadOnlyList<SuggestionDto> closed)
    {
        List<SuggestionDto> done = closed.Where(s => s.RMultiple is not null && s.ClosedAt is not null).ToList();

        if (done.Count == 0)
        {
            return Empty();
        }

        double[] rs = done.Select(s => R(s)).ToArray();
        List<bool> wins = done.Select(s => R(s) > 0).ToList();
        int winCount = wins.Count(w => w);
        double winRate = (double)winCount / done.Count;
        double averageR = rs.Average();

        List<SuggestionDto> winners = done.Where(s => R(s) > 0).ToList();
        List<SuggestionDto> losers = done.Where(s => R(s) <= 0).ToList();
        double grossProfit = winners.Sum(s => R(s));
        double grossLoss = Math.Abs(losers.Where(s => R(s) < 0).Sum(s => R(s)));
        double averageWin = winners.Count == 0 ? 0 : grossProfit / winners.Count;
        double averageLoss = losers.Count == 0 ? 0 : losers.Average(s => R(s));
        double breakEven = averageWin > 0 && averageLoss < 0 ? -averageLoss / (averageWin - averageLoss) : 0.5;

        double baseRate = winRate;
        double brier = done.Average(s => Math.Pow(s.Confidence - (R(s) > 0 ? 1 : 0), 2));
        double baselineBrier = baseRate * (1 - baseRate);

        double[] clusterMeans = done
            .GroupBy(s => (Day: s.CreatedAt.UtcDateTime.Date, s.Class, s.Direction))
            .Select(g => g.Average(s => R(s)))
            .ToArray();
        Interval clustered = clusterMeans.Length >= 2 ? MeanCi(clusterMeans) : MeanCi(rs);

        ReliabilityVerdict verdict = BuildVerdict(done.Count, winRate, breakEven, averageR, clustered, brier, baselineBrier, done.Average(s => s.Confidence), clusterMeans.Length);

        return new ModelReliabilityReport(
            done.Count,
            done.Count(s => s.Status == SuggestionStatus.HitTakeProfit),
            done.Count(s => s.Status == SuggestionStatus.HitStopLoss),
            done.Count(s => s.Status == SuggestionStatus.Expired),
            winRate,
            Wilson(winCount, done.Count),
            breakEven,
            averageR,
            MeanCi(rs),
            grossLoss > 0 ? grossProfit / grossLoss : null,
            averageWin,
            averageLoss,
            AverageHours(done),
            AverageHours(done.Where(s => s.Status == SuggestionStatus.HitTakeProfit)),
            AverageHours(done.Where(s => s.Status == SuggestionStatus.HitStopLoss)),
            brier,
            baselineBrier,
            CalibrationError(done),
            done.Average(s => s.Confidence),
            Group(done, s => s.Horizon == SuggestionHorizon.Intraday ? "Intraday" : "Swing"),
            Group(done, s => s.Direction == TradeDirection.Buy ? "Compra" : "Venda"),
            Group(done, s => ConfidenceBand(s.Confidence)).OrderBy(g => g.Name, StringComparer.Ordinal).ToList(),
            SentimentGroups(done),
            verdict,
            IndependentCases: clusterMeans.Length,
            ClusteredAverageRCi: clustered);
    }

    public static double R(SuggestionDto s) => s.RMultiple!.Value - (s.CostR ?? 0);

    public static ExcursionAnalysis? AnalyzeExcursions(IReadOnlyList<SuggestionDto> done)
    {
        List<SuggestionDto> items = done.Where(s => s.MfeR is not null && s.MaeR is not null).ToList();

        if (items.Count < 10)
        {
            return null;
        }

        List<double> winnersMae = items.Where(s => s.Status == SuggestionStatus.HitTakeProfit).Select(s => s.MaeR!.Value).OrderBy(v => v).ToList();
        List<SuggestionDto> stopped = items.Where(s => s.Status == SuggestionStatus.HitStopLoss).ToList();
        double stoppedShare = stopped.Count == 0
            ? 0
            : stopped.Count(s => s.Entry != s.StopLoss && s.MfeR >= 0.5 * Math.Abs(s.TakeProfit - s.Entry) / Math.Abs(s.Entry - s.StopLoss)) / (double)stopped.Count;
        double? p80 = winnersMae.Count >= 5 ? winnersMae[(int)Math.Min(winnersMae.Count - 1, Math.Floor(winnersMae.Count * 0.8))] : null;

        string hint = p80 is { } stopR
            ? p80 < 0.9
                ? $"As vencedoras raramente aguentam mais de {stopR:0.0} R contra elas: um stop mais curto que o atual poderia ser testado."
                : "As vencedoras chegam a aguentar quase o stop todo antes de ganhar: não apertes o stop."
            : "Poucas vencedoras ainda para sugerir um stop.";

        return new ExcursionAnalysis(items.Count, Median(items.Select(s => s.MfeR!.Value)), Median(items.Select(s => s.MaeR!.Value)), p80 ?? 0, stoppedShare, p80, hint);
    }

    public static SmartExitComparison? CompareSmartExit(IReadOnlyList<SuggestionDto> done)
    {
        List<SuggestionDto> items = done.Where(s => s.SmartR is not null && s.RMultiple is not null).ToList();

        if (items.Count < 10)
        {
            return null;
        }

        double fixedAverage = items.Average(s => s.RMultiple!.Value - (s.CostR ?? 0));
        double smartAverage = items.Average(s => s.SmartR!.Value - (s.CostR ?? 0));
        double difference = smartAverage - fixedAverage;

        return new SmartExitComparison(
            items.Count,
            fixedAverage,
            smartAverage,
            difference,
            difference > 0.05
                ? "Com stop em breakeven, saída parcial e trailing o resultado teria sido melhor: vale a pena ativar a saída inteligente."
                : difference < -0.05
                    ? "A saída inteligente teria dado pior resultado: o stop e o alvo fixos estão a funcionar melhor."
                    : "Sem diferença relevante entre saída fixa e inteligente.");
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static ModelReliabilityReport CalculateWithShadow(IReadOnlyList<SuggestionDto> allClosed, TimeZoneInfo? zone = null)
    {
        List<SuggestionDto> projections = allClosed.Where(s => s.Variant == SuggestionVariants.Projection && s.RMultiple is not null).ToList();
        List<SuggestionDto> closed = allClosed.Where(s => s.Variant != SuggestionVariants.Projection).ToList();
        List<SuggestionDto> shown = closed.Where(s => !s.IsShadow).ToList();
        List<SuggestionDto> hidden = closed.Where(s => s.IsShadow && s.RMultiple is not null).ToList();

        ReliabilityStat? Stat(string name, List<SuggestionDto> items) => items.Count == 0
            ? null
            : new ReliabilityStat(name, items.Count, items.Count(s => R(s) > 0), items.Count(s => R(s) > 0) / (double)items.Count, Wilson(items.Count(s => R(s) > 0), items.Count), items.Average(s => R(s)));

        List<ReliabilityStat> byReason = hidden
            .SelectMany(s => s.ShadowReason.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(reason => (Reason: reason, Item: s)))
            .GroupBy(x => x.Reason)
            .Select(g => Stat(g.Key, g.Select(x => x.Item).ToList())!)
            .OrderByDescending(g => g.Count)
            .ToList();

        zone ??= TimeZoneInfo.Local;
        List<SuggestionDto> shownDone = shown.Where(s => s.RMultiple is not null && s.ClosedAt is not null).ToList();
        List<SuggestionDto> everything = closed.Where(s => s.RMultiple is not null).ToList();

        return Calculate(shown) with
        {
            Shadow = new ShadowAnalysis(Stat("Mostradas", shown.Where(s => s.RMultiple is not null).ToList()), Stat("Só registo (sombra)", hidden), byReason),
            ByAssetClass = Group(shownDone, s => s.Class is { } c ? ClassLabel(c) : "Desconhecida"),
            ByHour = Group(shownDone, s => $"{TimeZoneInfo.ConvertTime(s.CreatedAt, zone).Hour:00}h").OrderBy(g => g.Name, StringComparer.Ordinal).ToList(),
            ByVariant = Group(everything.Where(s => s.Variant == "B" || !s.IsShadow).Concat(projections).ToList(), VariantLabel),
            Projection = ProjectionStats(projections),
            Excursions = AnalyzeExcursions(everything),
            SmartExit = CompareSmartExit(everything),
            AverageGrossR = shownDone.Count == 0 ? 0 : shownDone.Average(s => s.RMultiple!.Value),
            AverageCostR = shownDone.Count == 0 ? 0 : shownDone.Average(s => s.CostR ?? 0)
        };
    }

    private static string VariantLabel(SuggestionDto s) => s.Variant switch
    {
        "B" => "Variante B (teste)",
        SuggestionVariants.Projection => "Projeção (alvos do gráfico)",
        _ => "Variante A (em uso)"
    };

    private static ProjectionPerformance? ProjectionStats(IReadOnlyList<SuggestionDto> items) => items.Count == 0
        ? null
        : new ProjectionPerformance(
            items.Count,
            items.Average(s => s.Confidence),
            items.Count(s => R(s) > 0) / (double)items.Count,
            items.Count(s => s.Status == SuggestionStatus.HitTakeProfit),
            items.Count(s => s.Status == SuggestionStatus.HitStopLoss),
            items.Count(s => s.Status == SuggestionStatus.Expired),
            items.Average(s => R(s)));

    private static string ClassLabel(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Stock => "Ações",
        AssetClass.Etf => "ETFs",
        AssetClass.Commodity => "Mercadorias",
        AssetClass.Forex => "Forex",
        AssetClass.Crypto => "Cripto",
        AssetClass.Index => "Índices",
        _ => "Outros"
    };

    public static Interval Wilson(int successes, int total)
    {
        if (total == 0)
        {
            return new Interval(0, 1);
        }

        double p = (double)successes / total;
        double z2 = Z * Z;
        double denominator = 1 + z2 / total;
        double centre = (p + z2 / (2 * total)) / denominator;
        double margin = Z * Math.Sqrt(p * (1 - p) / total + z2 / (4.0 * total * total)) / denominator;

        return new Interval(Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }

    public static Interval MeanCi(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            double single = values.Count == 1 ? values[0] : 0;
            return new Interval(single, single);
        }

        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        double margin = Z * Math.Sqrt(variance / values.Count);

        return new Interval(mean - margin, mean + margin);
    }

    public static string ConfidenceBand(double confidence) => confidence switch
    {
        < 0.7 => "60-70%",
        < 0.8 => "70-80%",
        _ => "80%+"
    };

    private static double CalibrationError(IReadOnlyList<SuggestionDto> done) =>
        done
            .GroupBy(s => ConfidenceBand(s.Confidence))
            .Sum(g => g.Count() / (double)done.Count * Math.Abs(g.Average(s => s.Confidence) - g.Count(s => R(s) > 0) / (double)g.Count()));

    private static double AverageHours(IEnumerable<SuggestionDto> items)
    {
        List<double> hours = items.Where(s => s.ClosedAt is not null).Select(s => (s.ClosedAt!.Value - s.CreatedAt).TotalHours).ToList();
        return hours.Count == 0 ? 0 : hours.Average();
    }

    private static List<ReliabilityStat> Group(IReadOnlyList<SuggestionDto> done, Func<SuggestionDto, string> key) =>
        done
            .GroupBy(key)
            .Select(g => new ReliabilityStat(g.Key, g.Count(), g.Count(s => R(s) > 0), g.Count(s => R(s) > 0) / (double)g.Count(), Wilson(g.Count(s => R(s) > 0), g.Count()), g.Average(s => R(s))))
            .OrderByDescending(g => g.Count)
            .ToList();

    private static List<ReliabilityStat> SentimentGroups(IReadOnlyList<SuggestionDto> done)
    {
        List<SuggestionDto> withSentiment = done
            .Where(s => s.Signals.Any(x => x.Name == "sentiment" && Math.Abs(x.Value) >= SentimentFloor))
            .ToList();

        return Group(withSentiment, s =>
        {
            double value = s.Signals.First(x => x.Name == "sentiment").Value;
            return Math.Sign(value) == (s.Direction == TradeDirection.Buy ? 1 : -1) ? "Sentimento a favor" : "Sentimento contra";
        });
    }

    private static ReliabilityVerdict BuildVerdict(int count, double winRate, double breakEven, double averageR, Interval averageRCi, double brier, double baselineBrier, double averageConfidence, int independentCases)
    {
        List<string> reasons = [];

        if (count < MinSamples)
        {
            reasons.Add($"Só há {count} sugestões fechadas; são precisas pelo menos {MinSamples} para tirar conclusões e cerca de {GoodSamples} para confiar.");
            return new ReliabilityVerdict(ReliabilityLevel.Insufficient, "Dados insuficientes", reasons);
        }

        reasons.Add($"{count} sugestões fechadas, acerto de {winRate * 100:0}% (ponto de equilíbrio {breakEven * 100:0}%).");
        reasons.Add($"R médio {averageR:+0.00;-0.00} (intervalo de confiança 95%: {averageRCi.Low:+0.00;-0.00} a {averageRCi.High:+0.00;-0.00}).");

        if (independentCases < count)
        {
            reasons.Add($"As {count} sugestões equivalem a cerca de {independentCases} apostas independentes (as do mesmo dia, tipo de ativo e sentido contam como uma); o intervalo usa esse número.");
        }

        if (averageConfidence - winRate > 0.1)
        {
            reasons.Add($"A certeza indicada (média {averageConfidence * 100:0}%) está acima do acerto real ({winRate * 100:0}%): usa a certeza calibrada.");
        }

        if (brier >= baselineBrier)
        {
            reasons.Add("A certeza não distingue melhor as sugestões boas das más do que um palpite à média.");
        }

        if (averageR <= 0)
        {
            return new ReliabilityVerdict(ReliabilityLevel.NoEdge, "Sem vantagem demonstrada", reasons);
        }

        if (averageRCi.Low <= 0)
        {
            reasons.Add("O resultado é positivo, mas ainda pode ser sorte: o intervalo inclui zero.");
            return new ReliabilityVerdict(ReliabilityLevel.Unproven, "Positivo, mas não provado", reasons);
        }

        return count >= GoodSamples
            ? new ReliabilityVerdict(ReliabilityLevel.High, "Vantagem estatisticamente provável", reasons)
            : new ReliabilityVerdict(ReliabilityLevel.Moderate, "Vantagem provável, amostra ainda curta", reasons);
    }

    private static ModelReliabilityReport Empty() =>
        new(0, 0, 0, 0, 0, new Interval(0, 1), 0.5, 0, new Interval(0, 0), null, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], [],
            new ReliabilityVerdict(ReliabilityLevel.Insufficient, "Dados insuficientes", [$"Ainda não há sugestões fechadas. São precisas pelo menos {MinSamples}."]));
}

public sealed record SentimentObservation(string Symbol, double Sentiment, double? Return1Day, double? Return3Days);

public sealed record SentimentHorizonResult(string Label, int Samples, int Hits, double HitRate, Interval HitRateCi, double? Correlation, double AverageReturnPositive, double AverageReturnNegative);

public sealed record SentimentStudyResult(int Articles, int Symbols, double MinStrength, IReadOnlyList<SentimentHorizonResult> Horizons, ReliabilityVerdict Verdict);

public static class SentimentStudy
{
    public const double MinStrength = 0.3;
    public const int MinSamples = 30;

    public static SentimentStudyResult Evaluate(IReadOnlyList<SentimentObservation> observations)
    {
        List<SentimentObservation> strong = observations.Where(o => Math.Abs(o.Sentiment) >= MinStrength).ToList();

        List<SentimentHorizonResult> horizons =
        [
            Horizon("1 dia depois", strong, o => o.Return1Day),
            Horizon("3 dias depois", strong, o => o.Return3Days)
        ];

        SentimentHorizonResult main = horizons[0];
        List<string> reasons = [$"{strong.Count} notícias com sentimento forte (|s| ≥ {MinStrength:0.0}) em {strong.Select(o => o.Symbol).Distinct().Count()} ativos."];
        ReliabilityVerdict verdict;

        if (main.Samples < MinSamples)
        {
            reasons.Add($"Só {main.Samples} com preço posterior; são precisas {MinSamples}. Deixa a app recolher notícias durante mais dias.");
            verdict = new ReliabilityVerdict(ReliabilityLevel.Insufficient, "Dados insuficientes", reasons);
        }
        else
        {
            reasons.Add($"O sinal do sentimento coincidiu com o do preço em {main.HitRate * 100:0}% dos casos (IC 95%: {main.HitRateCi.Low * 100:0}-{main.HitRateCi.High * 100:0}%; 50% = sorte).");
            verdict = main.HitRateCi.Low > 0.5
                ? new ReliabilityVerdict(main.Samples >= 150 ? ReliabilityLevel.High : ReliabilityLevel.Moderate, "O sentimento antecipa o preço", reasons)
                : main.HitRateCi.High < 0.5
                    ? new ReliabilityVerdict(ReliabilityLevel.NoEdge, "O sentimento tende a falhar o sentido do preço", reasons)
                    : new ReliabilityVerdict(ReliabilityLevel.Unproven, "Sem relação provada com o preço", reasons);
        }

        return new SentimentStudyResult(observations.Count, observations.Select(o => o.Symbol).Distinct().Count(), MinStrength, horizons, verdict);
    }

    public static double? ForwardReturn(IReadOnlyList<Candle> daily, DateTimeOffset publishedAt, int daysAhead)
    {
        int index = -1;

        for (int i = 0; i < daily.Count; i++)
        {
            if (daily[i].Time.UtcDateTime.Date <= publishedAt.UtcDateTime.Date)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        if (index < 0 || index + daysAhead >= daily.Count || daily[index].Close <= 0)
        {
            return null;
        }

        return (daily[index + daysAhead].Close - daily[index].Close) / daily[index].Close;
    }

    private static SentimentHorizonResult Horizon(string label, IReadOnlyList<SentimentObservation> strong, Func<SentimentObservation, double?> returnOf)
    {
        List<(double Sentiment, double Return)> pairs = strong
            .Select(o => (o.Sentiment, Return: returnOf(o)))
            .Where(p => p.Return is not null && p.Return != 0)
            .Select(p => (p.Sentiment, p.Return!.Value))
            .ToList();

        int hits = pairs.Count(p => Math.Sign(p.Sentiment) == Math.Sign(p.Return));
        List<double> positive = pairs.Where(p => p.Sentiment > 0).Select(p => p.Return).ToList();
        List<double> negative = pairs.Where(p => p.Sentiment < 0).Select(p => p.Return).ToList();

        return new SentimentHorizonResult(
            label,
            pairs.Count,
            hits,
            pairs.Count == 0 ? 0 : (double)hits / pairs.Count,
            ModelReliability.Wilson(hits, pairs.Count),
            Correlation(pairs),
            positive.Count == 0 ? 0 : positive.Average(),
            negative.Count == 0 ? 0 : negative.Average());
    }

    private static double? Correlation(IReadOnlyList<(double Sentiment, double Return)> pairs)
    {
        if (pairs.Count < 3)
        {
            return null;
        }

        double meanX = pairs.Average(p => p.Sentiment);
        double meanY = pairs.Average(p => p.Return);
        double covariance = pairs.Sum(p => (p.Sentiment - meanX) * (p.Return - meanY));
        double varianceX = pairs.Sum(p => Math.Pow(p.Sentiment - meanX, 2));
        double varianceY = pairs.Sum(p => Math.Pow(p.Return - meanY, 2));

        return varianceX == 0 || varianceY == 0 ? null : covariance / Math.Sqrt(varianceX * varianceY);
    }
}
