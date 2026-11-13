using System.Globalization;
using Trader.Core.Models;

namespace Trader.Core.Scoring;

public sealed class SuggestionEngine(ScoringOptions options)
{
    private const double AgreementFloor = 0.1;
    private const double BuzzFloor = 1.0;
    private const double BuzzCeiling = 3.0;

    public SuggestionDraft? Evaluate(MarketSnapshot market, SentimentSnapshot sentiment)
    {
        SuggestionDraft? draft = Assess(market, sentiment);
        return draft is { IsShadow: false } ? draft : null;
    }

    public (IReadOnlyList<SignalResult> Signals, double Score) ScoreSignals(MarketSnapshot market, SentimentSnapshot sentiment)
    {
        List<SignalResult> signals = BuildSignals(market, sentiment);
        double totalWeight = signals.Sum(s => s.Weight);

        return (signals, totalWeight > 0 ? signals.Sum(s => s.Weight * s.Value) / totalWeight : 0);
    }

    public SuggestionDraft? Assess(MarketSnapshot market, SentimentSnapshot sentiment)
    {
        if (market.Atr <= 0 || market.Price <= 0)
        {
            return null;
        }

        List<SignalResult> signals = BuildSignals(market, sentiment);

        if (signals.Count < options.MinAgreeingSignals)
        {
            return null;
        }

        double score = signals.Sum(s => s.Weight * s.Value) / signals.Sum(s => s.Weight);

        if (Math.Abs(score) < 1e-9)
        {
            return null;
        }

        TradeDirection direction = score > 0 ? TradeDirection.Buy : TradeDirection.Sell;
        int sign = score > 0 ? 1 : -1;
        List<SignalResult> agreeing = signals.Where(s => Math.Sign(s.Value) == sign && Math.Abs(s.Value) >= AgreementFloor).ToList();

        if (agreeing.Count < options.MinAgreeingSignals)
        {
            return null;
        }

        double confidence = Sigmoid(options.ConfidenceSteepness * Math.Abs(score)) * agreeing.Count / signals.Count;

        (double stopLoss, double takeProfit) = ComputeLevels(market, direction);
        double risk = Math.Abs(market.Price - stopLoss);
        double riskReward = risk > 0 ? Math.Abs(takeProfit - market.Price) / risk : 0;

        List<string> blockers = [];

        if (confidence < options.ConfidenceThreshold)
        {
            blockers.Add("certeza abaixo do mínimo");
        }

        if (riskReward < options.MinRiskReward)
        {
            blockers.Add("R:R abaixo do mínimo");
        }

        AddContextBlockers(blockers, market, sign);

        if (blockers.Count > 0 && (!options.RecordShadow || confidence < options.ShadowMinConfidence))
        {
            return null;
        }

        IReadOnlyList<EvidenceArticle> articles = sentiment.Articles
            .Where(a => Math.Sign(a.Sentiment) == sign)
            .OrderByDescending(a => a.Influence)
            .Take(5)
            .ToList();

        return new SuggestionDraft(
            market.Symbol,
            market.Name,
            direction,
            confidence,
            score,
            market.Price,
            stopLoss,
            takeProfit,
            riskReward,
            signals,
            articles,
            BuildExplanation(direction, agreeing, signals.Count),
            blockers);
    }

    private void AddContextBlockers(List<string> blockers, MarketSnapshot market, int sign)
    {
        if (options.MinAdx > 0 && market.Adx is { } adx && adx < options.MinAdx)
        {
            blockers.Add($"mercado lateral (ADX {Format(adx, "0")})");
        }

        if (options.RequireWeeklyAlignment && market.WeeklyTrend is { } weekly && Math.Abs(weekly) >= 0.5 && Math.Sign(weekly) != sign)
        {
            blockers.Add("contra a tendência semanal");
        }

        if (options.BlockEarningsWithinDays > 0 && market.DaysToEarnings is { } days && days >= 0 && days <= options.BlockEarningsWithinDays)
        {
            blockers.Add($"resultados em {days} dias");
        }

        if (options.MaxVix > 0 && market.Vix is { } vix && vix >= options.MaxVix)
        {
            blockers.Add($"VIX elevado ({Format(vix, "0.#")})");
        }
    }

    private List<SignalResult> BuildSignals(MarketSnapshot market, SentimentSnapshot sentiment)
    {
        List<SignalResult> signals = [];

        if (sentiment.ArticleCount >= options.MinSentimentArticles)
        {
            double value = Clamp(sentiment.WeightedSentiment / options.SentimentScale);
            signals.Add(new SignalResult(
                "sentiment",
                "Sentimento",
                value,
                options.SentimentWeight,
                $"sentimento {Format(sentiment.WeightedSentiment, "+0.00;-0.00")} em {sentiment.ArticleCount} artigos"));

            if (sentiment.BuzzZScore is { } z && z >= BuzzFloor && Math.Abs(value) >= AgreementFloor)
            {
                double buzz = Math.Sign(value) * Clamp(z / BuzzCeiling, 0, 1);
                signals.Add(new SignalResult(
                    "buzz",
                    "Volume de menções",
                    buzz,
                    options.BuzzWeight,
                    $"volume de menções {Format(z, "0.0")} desvios acima do normal"));
            }
        }

        if (market.RelativeStrength is { } relative && options.RelativeStrengthWeight > 0 && options.RelativeStrengthScale > 0)
        {
            signals.Add(new SignalResult(
                "relative-strength",
                "Força relativa",
                Clamp(relative / options.RelativeStrengthScale),
                options.RelativeStrengthWeight,
                $"{Format(relative, "+0.0;-0.0")} pontos face ao mercado nas últimas 4 semanas"));
        }

        signals.Add(BuildTrend(market));
        signals.Add(BuildMomentum(market));
        signals.Add(new SignalResult(
            "bollinger",
            "Bandas de Bollinger",
            Clamp((0.5 - market.PercentB) * 2),
            options.BollingerWeight,
            $"posição nas Bollinger %B {Format(market.PercentB, "0.00")}"));

        return signals;
    }

    private SignalResult BuildTrend(MarketSnapshot market)
    {
        List<double> parts =
        [
            Math.Sign(market.Price - market.Ema20),
            Math.Sign(market.Ema20 - market.Ema50)
        ];

        if (market.Ema200 is { } ema200)
        {
            parts.Add(Math.Sign(market.Ema50 - ema200));
        }

        double value = parts.Average();
        string detail = value switch
        {
            > 0 => "tendência de alta (preço acima das médias EMA20/50)",
            < 0 => "tendência de baixa (preço abaixo das médias EMA20/50)",
            _ => "tendência indefinida"
        };

        return new SignalResult("trend", "Tendência", value, options.TrendWeight, detail);
    }

    private SignalResult BuildMomentum(MarketSnapshot market)
    {
        double macd = Clamp(market.MacdHistogram / market.Atr * 5);
        double rsi = Clamp((50 - market.Rsi) / 25);
        double value = 0.6 * macd + 0.4 * rsi;

        string rsiTag = market.Rsi < 35 ? "sobrevenda" : market.Rsi > 65 ? "sobrecompra" : "neutro";
        string macdTag = market.MacdHistogram >= 0 ? "positivo" : "negativo";

        return new SignalResult(
            "momentum",
            "Momentum",
            value,
            options.MomentumWeight,
            $"RSI {Format(market.Rsi, "0")} ({rsiTag}) e MACD {macdTag}");
    }

    public const double WideStopAtr = 3;
    public const double WideTargetAtr = 6;

    public static (double StopLoss, double TakeProfit) ComputeWideLevels(MarketSnapshot market, TradeDirection direction)
    {
        int sign = direction == TradeDirection.Buy ? 1 : -1;
        return (market.Price - sign * WideStopAtr * market.Atr, market.Price + sign * WideTargetAtr * market.Atr);
    }

    public (double StopLoss, double TakeProfit) ComputeLevels(MarketSnapshot market, TradeDirection direction)
    {
        double atr = market.Atr;
        double entry = market.Price;
        int sign = direction == TradeDirection.Buy ? 1 : -1;

        double stopLoss = entry - sign * options.SlAtrMultiple * atr;
        double takeProfit = entry + sign * options.TpAtrMultiple * atr;

        double swingStop = direction == TradeDirection.Buy ? market.SwingLow : market.SwingHigh;
        double stopDistance = Math.Abs(entry - swingStop) / atr;
        bool swingOnProtectiveSide = direction == TradeDirection.Buy ? swingStop < entry : swingStop > entry;

        if (swingOnProtectiveSide && stopDistance is >= 1.0 and <= 2.0)
        {
            stopLoss = swingStop - sign * 0.1 * atr;
        }

        double swingTarget = direction == TradeDirection.Buy ? market.SwingHigh : market.SwingLow;
        double risk = Math.Abs(entry - stopLoss);
        double targetDistance = Math.Abs(swingTarget - entry);
        bool swingOnTargetSide = direction == TradeDirection.Buy ? swingTarget > entry : swingTarget < entry;

        if (swingOnTargetSide && targetDistance >= options.MinRiskReward * risk && targetDistance < Math.Abs(takeProfit - entry))
        {
            takeProfit = swingTarget;
        }

        return (stopLoss, takeProfit);
    }

    private static string BuildExplanation(TradeDirection direction, List<SignalResult> agreeing, int total)
    {
        string verb = direction == TradeDirection.Buy ? "Compra" : "Venda";
        string reasons = string.Join("; ", agreeing.OrderByDescending(s => s.Weight * Math.Abs(s.Value)).Select(s => s.Detail));

        return $"{verb} sugerida: {reasons}. {agreeing.Count} de {total} sinais concordam.";
    }

    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));

    private static double Clamp(double value, double min = -1, double max = 1) => Math.Min(max, Math.Max(min, value));

    private static string Format(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
