using System.Globalization;
using Trader.Core.Models;

namespace Trader.Core.Projection;

public static class ProjectionEngine
{
    public const int StepMinutes = 5;
    public const int MinBars = 200;
    public const int DefaultSimulations = 2000;

    private const int BucketMinutes = 30;
    private const int BucketsPerDay = 24 * 60 / BucketMinutes;
    private const int MinBucketSamples = 20;
    private const int MomentumBars = 12;
    private const int SessionFallbackBars = 78;
    private const double Lambda = 0.94;
    private const double MomentumWeight = 0.03;
    private const double ReversionPerStep = 0.005;
    private const double MaxDriftInSigmas = 0.1;
    private const double GapFactor = 3;

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static ProjectionResult? Project(IReadOnlyList<Candle> candles, int horizonMinutes, int simulations = DefaultSimulations)
    {
        if (candles.Count < MinBars || horizonMinutes < StepMinutes || candles.Any(c => c.Close <= 0 || !double.IsFinite(c.Close)))
        {
            return null;
        }

        int steps = horizonMinutes / StepMinutes;
        Candle last = candles[^1];
        double[] seasonal = SeasonalProfile(candles);
        Residuals residuals = FitVolatility(candles, seasonal);

        if (residuals.Standardized.Length < MinBars / 2 || residuals.Sigma <= 0)
        {
            return null;
        }

        (double vwap, double momentumPerStep) = DriftInputs(candles, residuals.Sigma);
        Random random = new(HashCode.Combine(last.Time.UtcTicks, steps, candles.Count));
        double[][] closes = Simulate(last, steps, simulations, residuals, seasonal, vwap, momentumPerStep, random);

        return Summarize(last, horizonMinutes, steps, closes, residuals, seasonal, vwap, momentumPerStep, candles);
    }

    private static double[][] Simulate(
        Candle last,
        int steps,
        int simulations,
        Residuals residuals,
        double[] seasonal,
        double vwap,
        double momentumPerStep,
        Random random)
    {
        double[][] closes = new double[steps][];

        for (int s = 0; s < steps; s++)
        {
            closes[s] = new double[simulations];
        }

        double[] stepSigma = new double[steps];

        for (int s = 0; s < steps; s++)
        {
            stepSigma[s] = HorizonSigma(residuals, s + 1) * seasonal[Bucket(last.Time.AddMinutes((s + 1) * StepMinutes))];
        }

        double logVwap = Math.Log(vwap);
        double cap = MaxDriftInSigmas * residuals.Sigma;

        for (int p = 0; p < simulations; p++)
        {
            double logPrice = Math.Log(last.Close);

            for (int s = 0; s < steps; s++)
            {
                double drift = Math.Clamp(momentumPerStep - ReversionPerStep * (logPrice - logVwap), -cap, cap);
                double epsilon = residuals.Standardized[random.Next(residuals.Standardized.Length)];
                logPrice += drift + stepSigma[s] * epsilon;
                closes[s][p] = Math.Exp(logPrice);
            }
        }

        return closes;
    }

    private static ProjectionResult Summarize(
        Candle last,
        int horizonMinutes,
        int steps,
        double[][] closes,
        Residuals residuals,
        double[] seasonal,
        double vwap,
        double momentumPerStep,
        IReadOnlyList<Candle> candles)
    {
        List<ProjectionBand> bands = [];
        List<ProjectedBar> bars = [];
        double previousClose = last.Close;

        for (int s = 0; s < steps; s++)
        {
            double[] sorted = closes[s].Order().ToArray();
            DateTimeOffset time = last.Time.AddMinutes((s + 1) * StepMinutes);
            double median = Percentile(sorted, 0.5);
            bands.Add(new ProjectionBand(time, Percentile(sorted, 0.10), Percentile(sorted, 0.25), median, Percentile(sorted, 0.75), Percentile(sorted, 0.90)));

            double wickSize = residuals.WickRatio * HorizonSigma(residuals, 1) * seasonal[Bucket(time)] * previousClose;
            double high = Math.Max(previousClose, median) + wickSize;
            double low = Math.Max(Math.Min(previousClose, median) - wickSize, double.Epsilon);
            bars.Add(new ProjectedBar(time, previousClose, high, low, median));
            previousClose = median;
        }

        double[] ending = closes[^1];
        double up = ending.Count(c => c > last.Close) / (double)ending.Length;
        double[] logs = ending.Select(c => Math.Log(c / last.Close)).ToArray();
        double mean = logs.Average();
        double sigma = Math.Sqrt(logs.Sum(l => (l - mean) * (l - mean)) / logs.Length);
        double median50 = bands[^1].P50;

        return new ProjectionResult(
            StepMinutes,
            horizonMinutes,
            last.Close,
            last.Time,
            bars,
            bands,
            up,
            (median50 / last.Close - 1) * 100,
            sigma * 100,
            Drivers(last, steps, residuals, seasonal, vwap, momentumPerStep, sigma, candles),
            BuildPlan(last, closes, residuals, seasonal, up, sigma));
    }

    private const double PlanBandSigmas = 0.674;

    private static ProjectionPlan? BuildPlan(Candle last, double[][] closes, Residuals residuals, double[] seasonal, double probabilityUp, double horizonSigma)
    {
        TradeDirection direction = probabilityUp >= 0.5 ? TradeDirection.Buy : TradeDirection.Sell;
        int sign = direction == TradeDirection.Buy ? 1 : -1;
        double distance = PlanBandSigmas * horizonSigma;
        double stop = last.Close * Math.Exp(-sign * distance);
        double target = last.Close * Math.Exp(sign * distance);
        double risk = Math.Abs(last.Close - stop);
        double reward = Math.Abs(target - last.Close);

        if (risk <= last.Close * 1e-6 || reward <= 0)
        {
            return null;
        }

        double rr = reward / risk;
        int steps = closes.Length;
        int simulations = closes[0].Length;
        double[] wick = new double[steps];

        for (int s = 0; s < steps; s++)
        {
            wick[s] = residuals.WickRatio * HorizonSigma(residuals, s + 1) * seasonal[Bucket(last.Time.AddMinutes((s + 1) * StepMinutes))];
        }

        int targetHits = 0;
        int stopHits = 0;
        int wins = 0;
        double totalR = 0;

        for (int p = 0; p < simulations; p++)
        {
            double previous = last.Close;
            double result = 0;
            bool resolved = false;

            for (int s = 0; s < steps && !resolved; s++)
            {
                double close = closes[s][p];
                double high = Math.Max(previous, close) * (1 + wick[s]);
                double low = Math.Min(previous, close) * (1 - wick[s]);
                bool hitStop = sign == 1 ? low <= stop : high >= stop;
                bool hitTarget = sign == 1 ? high >= target : low <= target;

                if (hitStop)
                {
                    (result, resolved) = (-1, true);
                    stopHits++;
                }
                else if (hitTarget)
                {
                    (result, resolved) = (rr, true);
                    targetHits++;
                }

                previous = close;
            }

            if (!resolved)
            {
                result = sign * (previous - last.Close) / risk;
            }

            totalR += result;
            wins += result > 0 ? 1 : 0;
        }

        return new ProjectionPlan(
            direction,
            last.Close,
            stop,
            target,
            rr,
            targetHits / (double)simulations,
            stopHits / (double)simulations,
            (simulations - targetHits - stopHits) / (double)simulations,
            wins / (double)simulations,
            totalR / simulations);
    }

    private static List<ProjectionDriver> Drivers(
        Candle last,
        int steps,
        Residuals residuals,
        double[] seasonal,
        double vwap,
        double momentumPerStep,
        double horizonSigma,
        IReadOnlyList<Candle> candles)
    {
        double upcoming = Enumerable.Range(1, steps).Average(s => seasonal[Bucket(last.Time.AddMinutes(s * StepMinutes))]);
        double reversionEffect = -ReversionPerStep * Math.Log(last.Close / vwap) * steps * 100;
        double momentumEffect = momentumPerStep * steps * 100;
        double sigmaNow = residuals.Sigma * 100;
        double cap = MaxDriftInSigmas * residuals.Sigma;

        return
        [
            new ProjectionDriver(
                "Volatilidade",
                $"Cada vela de {StepMinutes} min mexe cerca de ±{sigmaNow.ToString("0.###", Pt)}% (média móvel exponencial dos retornos recentes, λ={Lambda.ToString("0.##", Pt)}). Ao longo do horizonte isto dá ±{(horizonSigma * 100).ToString("0.##", Pt)}% a 1 desvio-padrão (a banda de 80% cobre cerca de ±{(horizonSigma * 100 * 1.28).ToString("0.##", Pt)}%).",
                null),
            new ProjectionDriver(
                "Hora do dia",
                $"A volatilidade muda ao longo da sessão (mais forte na abertura e no fecho). A janela projetada tem em média {upcoming.ToString("0.##", Pt)}× a volatilidade média do dia, medida nas últimas {Days(candles)} sessões.",
                null),
            new ProjectionDriver(
                "Momentum",
                $"Retorno das últimas {MomentumBars} velas ({(Math.Log(last.Close / candles[^(MomentumBars + 1)].Close) * 100).ToString("+0.##;-0.##;0", Pt)}%), com peso pequeno porque o momentum intradiário é fraco.",
                momentumEffect),
            new ProjectionDriver(
                "Reversão à VWAP",
                $"O preço está {(Math.Log(last.Close / vwap) * 100).ToString("+0.##;-0.##;0", Pt)}% face à VWAP da sessão ({vwap.ToString("0.#####", Pt)}). Há uma força suave a puxar o preço para lá, que enfraquece à medida que ele se aproxima.",
                reversionEffect),
            new ProjectionDriver(
                "Caudas gordas",
                $"Os choques são reamostrados dos retornos reais do ativo (simulação histórica filtrada), por isso movimentos grandes são mais frequentes do que numa curva normal (curtose {residuals.Kurtosis.ToString("0.#", Pt)}; 3 seria normal). A deriva está limitada a ±{(cap * 100).ToString("0.###", Pt)}% por vela para a direção nunca dominar o ruído.",
                null)
        ];
    }

    private static int Days(IReadOnlyList<Candle> candles) =>
        Math.Max(1, candles.Select(c => c.Time.UtcDateTime.Date).Distinct().Count());

    private static double HorizonSigma(Residuals residuals, int stepsAhead)
    {
        double variance = 1 + (residuals.CurrentVariance - 1) * Math.Pow(Lambda, stepsAhead);
        return residuals.BaseSigma * Math.Sqrt(Math.Max(variance, 0.05));
    }

    private static (double Vwap, double MomentumPerStep) DriftInputs(IReadOnlyList<Candle> candles, double sigma)
    {
        int start = candles.Count - 1;

        while (start > 0 && (candles[start].Time - candles[start - 1].Time).TotalMinutes <= StepMinutes * GapFactor)
        {
            start--;
        }

        if (candles.Count - start > SessionFallbackBars * 6)
        {
            start = candles.Count - SessionFallbackBars;
        }

        double weighted = 0;
        double volume = 0;
        double plain = 0;

        for (int i = start; i < candles.Count; i++)
        {
            double typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3;
            weighted += typical * candles[i].Volume;
            volume += candles[i].Volume;
            plain += typical;
        }

        double vwap = volume > 0 ? weighted / volume : plain / (candles.Count - start);
        bool contiguous = (candles[^1].Time - candles[^(MomentumBars + 1)].Time).TotalMinutes <= MomentumBars * StepMinutes * GapFactor;
        double recent = contiguous ? Math.Log(candles[^1].Close / candles[^(MomentumBars + 1)].Close) : 0;
        double z = Math.Clamp(recent / (sigma * Math.Sqrt(MomentumBars)), -2, 2);

        return (vwap, MomentumWeight * sigma * z);
    }

    private static Residuals FitVolatility(IReadOnlyList<Candle> candles, double[] seasonal)
    {
        List<double> deseasonalized = [];
        List<double> standardized = [];
        List<double> upperWicks = [];
        List<double> lowerWicks = [];
        double variance = 0;
        int warm = 0;
        double warmSum = 0;

        for (int i = 1; i < candles.Count; i++)
        {
            if ((candles[i].Time - candles[i - 1].Time).TotalMinutes > StepMinutes * GapFactor)
            {
                continue;
            }

            double z = Math.Log(candles[i].Close / candles[i - 1].Close) / seasonal[Bucket(candles[i].Time)];

            if (warm < 50)
            {
                warmSum += z * z;
                warm++;
                variance = warmSum / warm;
                deseasonalized.Add(z);
                continue;
            }

            if (variance > 0)
            {
                double s = Math.Sqrt(variance);
                standardized.Add(z / s);
                double body = candles[i - 1].Close;
                double scale = s * seasonal[Bucket(candles[i].Time)] * body;

                if (scale > 0)
                {
                    upperWicks.Add((candles[i].High - Math.Max(candles[i].Open, candles[i].Close)) / scale);
                    lowerWicks.Add((Math.Min(candles[i].Open, candles[i].Close) - candles[i].Low) / scale);
                }
            }

            variance = Lambda * variance + (1 - Lambda) * z * z;
            deseasonalized.Add(z);
        }

        double[] unit = Normalize(standardized);
        double longRun = deseasonalized.Count > 0 ? deseasonalized.Sum(z => z * z) / deseasonalized.Count : 0;
        double baseSigma = Math.Sqrt(longRun);
        double wick = upperWicks.Count > 0 ? (Median(upperWicks) + Median(lowerWicks)) / 2 : 0;
        double kurtosis = unit.Length > 3 ? unit.Sum(u => Math.Pow(u, 4)) / unit.Length : 3;

        return new Residuals(unit, Math.Sqrt(variance), baseSigma, longRun > 0 ? variance / longRun : 1, Math.Max(wick, 0), kurtosis);
    }

    private static double[] Normalize(List<double> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        double mean = values.Average();
        double deviation = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);

        return deviation > 0 ? values.Select(v => (v - mean) / deviation).ToArray() : [];
    }

    private static double[] SeasonalProfile(IReadOnlyList<Candle> candles)
    {
        double[] sums = new double[BucketsPerDay];
        int[] counts = new int[BucketsPerDay];
        double total = 0;
        int all = 0;

        for (int i = 1; i < candles.Count; i++)
        {
            if ((candles[i].Time - candles[i - 1].Time).TotalMinutes > StepMinutes * GapFactor)
            {
                continue;
            }

            double r = Math.Log(candles[i].Close / candles[i - 1].Close);
            int b = Bucket(candles[i].Time);
            sums[b] += r * r;
            counts[b]++;
            total += r * r;
            all++;
        }

        double overall = all > 0 && total > 0 ? Math.Sqrt(total / all) : 1;
        double[] raw = new double[BucketsPerDay];

        for (int b = 0; b < BucketsPerDay; b++)
        {
            raw[b] = counts[b] >= MinBucketSamples ? Math.Sqrt(sums[b] / counts[b]) / overall : 1;
        }

        double[] smooth = new double[BucketsPerDay];

        for (int b = 0; b < BucketsPerDay; b++)
        {
            double value = 0.5 * raw[b] + 0.25 * raw[(b + BucketsPerDay - 1) % BucketsPerDay] + 0.25 * raw[(b + 1) % BucketsPerDay];
            smooth[b] = Math.Clamp(value, 0.3, 3);
        }

        return smooth;
    }

    private static int Bucket(DateTimeOffset time) => (time.UtcDateTime.Hour * 60 + time.UtcDateTime.Minute) / BucketMinutes;

    private static double Percentile(double[] sorted, double p)
    {
        double position = p * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Median(List<double> values) => Percentile(values.Order().ToArray(), 0.5);

    private sealed record Residuals(double[] Standardized, double Sigma, double BaseSigma, double CurrentVariance, double WickRatio, double Kurtosis);
}
