namespace Trader.Core.Backtesting;

public sealed record ExitVariant(string Name, string Label, double? StopAtr, double? TargetAtr, int ExpiryDays);

public sealed record ExitSample(DateTimeOffset Time, IReadOnlyList<double> NetR);

public sealed record ExitVariantResult(
    string Name,
    string Label,
    int Samples,
    double AverageNetR,
    double DifferenceVsCurrent,
    double DifferenceLow,
    double DifferenceHigh,
    int Years,
    int BetterYears,
    string Verdict);

public sealed record ExitStudy(int Samples, IReadOnlyList<ExitVariantResult> Variants, IReadOnlyList<string> Notes);

public static class ExitStudyAnalyzer
{
    public const int MinSamples = 1000;
    public const int MinYearSamples = 100;
    public const int MinYears = 5;
    public const double OverlapFactor = 3;
    public const double YearConsistency = 0.7;

    public const string VerdictBetter = "better";
    public const string VerdictNone = "none";
    public const string VerdictWorse = "worse";

    private const double ConfidenceZ = 1.96;
    private const double NoTarget = 100;

    public static readonly IReadOnlyList<ExitVariant> Variants =
    [
        new("current", "Atual (stop 2×ATR ou suporte, alvo 4×ATR ou resistência, 5 dias)", null, null, 5),
        new("atr-2-4", "Só ATR: stop 2×, alvo 4×, 5 dias", 2, 4, 5),
        new("atr-1.5-3", "Só ATR: stop 1,5×, alvo 3×, 5 dias", 1.5, 3, 5),
        new("atr-3-6", "Só ATR: stop 3×, alvo 6×, 5 dias", 3, 6, 5),
        new("atr-2-4-10d", "Só ATR: stop 2×, alvo 4×, 10 dias", 2, 4, 10),
        new("atr-3-6-10d", "Só ATR: stop 3×, alvo 6×, 10 dias", 3, 6, 10),
        new("time-exit", "Stop 2×ATR sem alvo, sai ao fim de 10 dias", 2, NoTarget, 10)
    ];

    public static ExitStudy Analyze(IReadOnlyList<ExitSample> all)
    {
        List<ExitSample> samples = all.Where(s => s.NetR.Count == Variants.Count && s.NetR.All(double.IsFinite)).ToList();
        List<string> notes =
        [
            "As variantes foram fixadas antes de ver os resultados e comparam-se entrada a entrada com a saída atual, nas mesmas entradas hipotéticas (um dia em cada dez, nos dois sentidos). A diferença é em R líquido, depois de custos.",
            "Só é 'Melhor' se a diferença tiver o intervalo de confiança inteiro acima de zero e for positiva em 70% dos anos. Mudar a saída não cria vantagem em entradas sem vantagem: só muda o peso dos custos e a variância."
        ];

        if (samples.Count < MinSamples)
        {
            notes.Insert(0, "Amostra insuficiente para comparar saídas.");
            return new ExitStudy(samples.Count, [], notes);
        }

        List<ExitVariantResult> results = [];

        for (int v = 1; v < Variants.Count; v++)
        {
            double[] diff = samples.Select(s => s.NetR[v] - s.NetR[0]).ToArray();
            double mean = diff.Average();
            double stdDev = Math.Sqrt(diff.Sum(d => (d - mean) * (d - mean)) / (diff.Length - 1));
            double margin = ConfidenceZ * stdDev / Math.Sqrt(diff.Length / OverlapFactor);

            int years = 0;
            int better = 0;

            foreach (IGrouping<int, ExitSample> year in samples.GroupBy(s => s.Time.Year))
            {
                if (year.Count() < MinYearSamples)
                {
                    continue;
                }

                years++;
                better += year.Average(s => s.NetR[v] - s.NetR[0]) > 0 ? 1 : 0;
            }

            string verdict = VerdictNone;

            if (mean - margin > 0 && years >= MinYears && better >= YearConsistency * years)
            {
                verdict = VerdictBetter;
            }
            else if (mean + margin < 0)
            {
                verdict = VerdictWorse;
            }

            results.Add(new ExitVariantResult(Variants[v].Name, Variants[v].Label, samples.Count, samples.Average(s => s.NetR[v]), mean, mean - margin, mean + margin, years, better, verdict));
        }

        results.Insert(0, new ExitVariantResult(Variants[0].Name, Variants[0].Label, samples.Count, samples.Average(s => s.NetR[0]), 0, 0, 0, 0, 0, VerdictNone));
        return new ExitStudy(samples.Count, results, notes);
    }
}
