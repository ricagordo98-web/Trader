using Trader.Core.Models;
using Trader.Core.Scoring;

namespace Trader.Core.Backtesting;

public sealed record ResearchSample(string Symbol, AssetClass AssetClass, DateTimeOffset Time, TradeDirection Direction, double NetR, IReadOnlyDictionary<string, double> Features);

public sealed record FeatureEdge(
    string Feature,
    string Label,
    int Samples,
    double Correlation,
    double ZScore,
    double TopNetR,
    double BottomNetR,
    int Years,
    int ConsistentYears,
    string Verdict);

public sealed record FeatureResearch(int Samples, double AverageNetR, IReadOnlyList<FeatureEdge> Edges, IReadOnlyList<string> Notes);

public static class FeatureEdgeAnalyzer
{
    public const int MinSamples = 200;
    public const int MinYearSamples = 100;
    public const int MinYears = 5;
    public const double OverlapFactor = 3;
    public const double PossibleZ = 3;
    public const double WeakZ = 2;
    public const double PossibleConsistency = 0.7;
    private const double QuantileShare = 0.2;

    public const string VerdictPossible = "possible";
    public const string VerdictWeak = "weak";
    public const string VerdictNone = "none";

    public static FeatureResearch Analyze(IReadOnlyList<ResearchSample> samples, IReadOnlyDictionary<string, string> labels)
    {
        List<FeatureEdge> edges = [];

        foreach ((string feature, string label) in labels)
        {
            List<(double Value, double NetR, int Year)> rows = samples
                .Where(s => s.Features.TryGetValue(feature, out double v) && double.IsFinite(v))
                .Select(s => (s.Features[feature], s.NetR, s.Time.Year))
                .ToList();

            if (rows.Count >= MinSamples)
            {
                edges.Add(Evaluate(feature, label, rows));
            }
        }

        return new FeatureResearch(
            samples.Count,
            samples.Count == 0 ? 0 : samples.Average(s => s.NetR),
            edges.OrderByDescending(e => Math.Abs(e.ZScore)).ToList(),
            [
                "As amostras são entradas hipotéticas, uma por ativo a cada poucos dias e nos dois sentidos, com os mesmos stops, alvos e custos do backtest. Cada característica está alinhada com o sentido da operação (positivo = a favor).",
                "As amostras vizinhas sobrepõem-se no tempo e entre ativos, por isso o z-score é dividido por um fator conservador. Com muitas características testadas, só 'possível' (z ≥ 3 e sinal igual em 70% dos anos) merece um teste fora da amostra.",
                "Uma característica com sinal não prova uma estratégia: o quintil de topo tem de dar R líquido positivo depois de custos."
            ]);
    }

    private static FeatureEdge Evaluate(string feature, string label, List<(double Value, double NetR, int Year)> rows)
    {
        double ic = Spearman(rows.Select(r => r.Value).ToArray(), rows.Select(r => r.NetR).ToArray());
        double effective = rows.Count / OverlapFactor;
        double z = Math.Abs(ic) < 1 ? ic * Math.Sqrt((effective - 2) / (1 - ic * ic)) : 0;

        List<(double Value, double NetR, int Year)> ordered = rows.OrderBy(r => r.Value).ToList();
        int bucket = Math.Max(1, (int)(ordered.Count * QuantileShare));
        double bottom = ordered.Take(bucket).Average(r => r.NetR);
        double top = ordered.TakeLast(bucket).Average(r => r.NetR);

        int years = 0;
        int positive = 0;
        int negative = 0;

        foreach (IGrouping<int, (double Value, double NetR, int Year)> year in rows.GroupBy(r => r.Year))
        {
            if (year.Count() < MinYearSamples)
            {
                continue;
            }

            double yearIc = Spearman(year.Select(r => r.Value).ToArray(), year.Select(r => r.NetR).ToArray());
            years++;
            positive += yearIc > 0 ? 1 : 0;
            negative += yearIc < 0 ? 1 : 0;
        }

        int consistent = Math.Max(positive, negative);
        bool sameDirection = (positive >= negative) == (ic >= 0);
        string verdict = VerdictNone;

        if (years >= MinYears && Math.Abs(z) >= PossibleZ && sameDirection && consistent >= PossibleConsistency * years)
        {
            verdict = VerdictPossible;
        }
        else if (Math.Abs(z) >= WeakZ)
        {
            verdict = VerdictWeak;
        }

        return new FeatureEdge(feature, label, rows.Count, ic, z, top, bottom, years, consistent, verdict);
    }

    public static double Spearman(double[] x, double[] y)
    {
        if (x.Length < 3 || x.Length != y.Length)
        {
            return 0;
        }

        double[] rx = Ranks(x);
        double[] ry = Ranks(y);
        double mx = rx.Average();
        double my = ry.Average();
        double covariance = 0;
        double vx = 0;
        double vy = 0;

        for (int i = 0; i < rx.Length; i++)
        {
            covariance += (rx[i] - mx) * (ry[i] - my);
            vx += (rx[i] - mx) * (rx[i] - mx);
            vy += (ry[i] - my) * (ry[i] - my);
        }

        return vx > 0 && vy > 0 ? covariance / Math.Sqrt(vx * vy) : 0;
    }

    private static double[] Ranks(double[] values)
    {
        int[] order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        double[] ranks = new double[values.Length];
        int start = 0;

        while (start < order.Length)
        {
            int end = start;

            while (end + 1 < order.Length && values[order[end + 1]] == values[order[start]])
            {
                end++;
            }

            double rank = (start + end) / 2.0 + 1;

            for (int k = start; k <= end; k++)
            {
                ranks[order[k]] = rank;
            }

            start = end + 1;
        }

        return ranks;
    }
}

public static class ResearchFeatures
{
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["trend_fast"] = "Tendência curta (EMA20 vs EMA50)",
        ["trend_long"] = "Preço vs EMA200",
        ["rsi"] = "RSI acima de 50",
        ["macd"] = "Histograma MACD",
        ["bollinger"] = "Posição nas bandas de Bollinger",
        ["weekly"] = "Tendência semanal",
        ["relative_strength"] = "Força relativa ao índice",
        ["stop_room"] = "Espaço até ao suporte/resistência",
        ["score"] = "Pontuação atual do modelo",
        ["adx"] = "Força da tendência (ADX)",
        ["volatility"] = "Volatilidade (ATR em % do preço)",
        ["vix"] = "VIX"
    };

    public static IReadOnlyDictionary<string, double> From(MarketSnapshot snapshot, TradeDirection direction, double score)
    {
        int sign = direction == TradeDirection.Buy ? 1 : -1;
        double atr = snapshot.Atr > 0 ? snapshot.Atr : double.NaN;
        Dictionary<string, double> features = new()
        {
            ["trend_fast"] = sign * (snapshot.Ema20 - snapshot.Ema50) / atr,
            ["rsi"] = sign * (snapshot.Rsi - 50),
            ["macd"] = sign * snapshot.MacdHistogram / atr,
            ["bollinger"] = sign * (snapshot.PercentB - 0.5),
            ["stop_room"] = (direction == TradeDirection.Buy ? snapshot.Price - snapshot.SwingLow : snapshot.SwingHigh - snapshot.Price) / atr,
            ["score"] = sign * score,
            ["volatility"] = snapshot.Atr / snapshot.Price * 100
        };

        if (snapshot.Ema200 is { } ema200)
        {
            features["trend_long"] = sign * (snapshot.Price - ema200) / atr;
        }

        if (snapshot.WeeklyTrend is { } weekly)
        {
            features["weekly"] = sign * weekly;
        }

        if (snapshot.RelativeStrength is { } strength)
        {
            features["relative_strength"] = sign * strength;
        }

        if (snapshot.Adx is { } adx)
        {
            features["adx"] = adx;
        }

        if (snapshot.Vix is { } vix)
        {
            features["vix"] = vix;
        }

        return features;
    }
}
