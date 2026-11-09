using System.Globalization;
using Trader.Core.Models;
using Trader.Core.Scoring;

namespace Trader.Core.Analysis;

public static class TradeAnalyzer
{
    private const int BaseScore = 50;
    private const double ModelAgreementFloor = 0.1;
    private const double SentimentThreshold = 0.15;
    private const int MinHistorySamples = 20;

    public static TradeAnalysis Analyze(
        TradeAnalysisRequest request,
        TradeContext context,
        AnalysisHistory? history,
        IReadOnlyList<string> upcomingEvents,
        DateTimeOffset now)
    {
        MarketSnapshot market = context.Market;
        ScoringOptions options = context.Scoring;
        int sign = request.Direction == TradeDirection.Buy ? 1 : -1;
        double risk = Math.Abs(request.Entry - request.StopLoss);
        double reward = Math.Abs(request.TakeProfit - request.Entry);
        double riskReward = risk > 0 ? reward / risk : 0;

        SuggestionEngine engine = new(options);
        (IReadOnlyList<SignalResult> signals, double modelScore) = engine.ScoreSignals(market, context.Sentiment);
        (double suggestedStop, double suggestedTarget) = engine.ComputeLevels(market, request.Direction);
        double suggestedRisk = Math.Abs(market.Price - suggestedStop);
        SuggestedLevels levels = new(market.Price, suggestedStop, suggestedTarget, suggestedRisk > 0 ? Math.Abs(suggestedTarget - market.Price) / suggestedRisk : 0);
        TradeDirection? modelDirection = Math.Abs(modelScore) < ModelAgreementFloor ? null : modelScore > 0 ? TradeDirection.Buy : TradeDirection.Sell;

        List<AnalysisFinding> findings = [];
        AddRiskReward(findings, riskReward, options);
        AddStopDistance(findings, risk, market);
        AddStopStructure(findings, request, market, sign);
        AddTargetReach(findings, reward, request, market, options, sign);
        AddEntryVersusPrice(findings, request, market, sign);
        AddModel(findings, modelScore, modelDirection, request.Direction, sign);
        AddContext(findings, market, options, sign);
        AddSentiment(findings, context.Sentiment, options, sign);
        AddHistory(findings, history, request.Direction);

        if (upcomingEvents.Count > 0)
        {
            findings.Add(new AnalysisFinding(
                "Calendário",
                FindingKind.Warning,
                "Evento macro importante nas próximas 48 horas",
                $"{string.Join("; ", upcomingEvents)}. Estes eventos costumam provocar movimentos bruscos que apanham stops.",
                -4));
        }

        int score = Math.Clamp(BaseScore + findings.Sum(f => f.Points), 0, 100);

        if (riskReward < 1)
        {
            score = Math.Min(score, 45);
        }

        (string code, string verdict) = Verdict(score);

        return new TradeAnalysis(
            request.Symbol,
            context.Name,
            request.Direction,
            market.Price,
            market.Atr,
            request.Entry,
            request.StopLoss,
            request.TakeProfit,
            riskReward,
            score,
            code,
            verdict,
            Summarize(findings),
            findings.OrderBy(f => f.Points).ToList(),
            new ModelView(modelDirection, modelScore, signals, levels),
            context.Sentiment,
            history,
            upcomingEvents,
            now);
    }

    private static void AddRiskReward(List<AnalysisFinding> findings, double riskReward, ScoringOptions options)
    {
        double breakEven = 1 / (1 + riskReward) * 100;
        string detail = $"Ganhas {Number(riskReward, "0.##")}× o que arriscas. Com este rácio precisas de acertar mais de {Number(breakEven, "0")}% das vezes só para empatar, antes de custos. O motor exige pelo menos {Number(options.MinRiskReward, "0.#")}.";

        (FindingKind kind, string title, int points) = riskReward switch
        {
            >= 2 => (FindingKind.Positive, $"Bom rácio risco/retorno ({Number(riskReward, "0.##")}:1)", 12),
            _ when riskReward >= options.MinRiskReward => (FindingKind.Positive, $"Rácio risco/retorno aceitável ({Number(riskReward, "0.##")}:1)", 8),
            >= 1.2 => (FindingKind.Neutral, $"Rácio risco/retorno curto ({Number(riskReward, "0.##")}:1)", 0),
            >= 1 => (FindingKind.Warning, $"Rácio risco/retorno fraco ({Number(riskReward, "0.##")}:1)", -8),
            _ => (FindingKind.Negative, $"Arriscas mais do que podes ganhar ({Number(riskReward, "0.##")}:1)", -18)
        };

        findings.Add(new AnalysisFinding("Risco/retorno", kind, title, detail, points));
    }

    private static void AddStopDistance(List<AnalysisFinding> findings, double risk, MarketSnapshot market)
    {
        double atrs = risk / market.Atr;
        string detail = $"O stop está a {Number(atrs, "0.##")} ATR do preço de entrada (ATR diário {Price(market.Atr)}, a amplitude média de um dia).";

        (FindingKind kind, string title, int points) = atrs switch
        {
            < 0.75 => (FindingKind.Negative, "Stop muito curto", -12),
            < 1.25 => (FindingKind.Warning, "Stop apertado", -5),
            <= 3 => (FindingKind.Positive, "Stop com folga razoável", 6),
            _ => (FindingKind.Warning, "Stop muito largo", -5)
        };

        string extra = atrs switch
        {
            < 0.75 => " O ruído normal de um dia pode apanhá-lo sem a tese estar errada.",
            < 1.25 => " Um dia mais agitado do que o normal pode bastar para o apanhar.",
            <= 3 => string.Empty,
            _ => " Se falhar, a perda é grande em relação à volatilidade normal do ativo."
        };

        findings.Add(new AnalysisFinding("Stop loss", kind, title, detail + extra, points));
    }

    private static void AddStopStructure(List<AnalysisFinding> findings, TradeAnalysisRequest request, MarketSnapshot market, int sign)
    {
        double swing = sign > 0 ? market.SwingLow : market.SwingHigh;
        string side = sign > 0 ? "mínimo" : "máximo";
        bool beyond = sign > 0 ? request.StopLoss <= swing : request.StopLoss >= swing;

        findings.Add(beyond
            ? new AnalysisFinding("Stop loss", FindingKind.Positive, $"Stop para lá do último {side} recente", $"O {side} recente está em {Price(swing)} e o teu stop fica para lá dele: só é apanhado se a estrutura recente quebrar.", 6)
            : new AnalysisFinding("Stop loss", FindingKind.Warning, $"Stop antes do último {side} recente", $"O {side} recente está em {Price(swing)}. O teu stop fica aquém desse nível, por isso um movimento normal até ele já te tira da trade.", -6));
    }

    private static void AddTargetReach(List<AnalysisFinding> findings, double reward, TradeAnalysisRequest request, MarketSnapshot market, ScoringOptions options, int sign)
    {
        double atrs = reward / market.Atr;
        double typical = Math.Sqrt(options.ExpiryDays);
        string detail = $"O alvo está a {Number(atrs, "0.##")} ATR. Em {options.ExpiryDays} dias o preço costuma percorrer cerca de {Number(typical, "0.#")} ATR.";

        (FindingKind kind, string title, int points) = atrs switch
        {
            _ when atrs > 2 * typical => (FindingKind.Negative, "Alvo muito difícil de atingir", -10),
            _ when atrs > 1.2 * typical => (FindingKind.Warning, "Alvo ambicioso", -4),
            < 1 => (FindingKind.Warning, "Alvo muito curto", -3),
            _ => (FindingKind.Positive, "Alvo ao alcance", 4)
        };

        if (kind == FindingKind.Warning && atrs < 1)
        {
            detail += " Com spread e comissões, ganhos tão pequenos perdem muito valor.";
        }

        double resistance = sign > 0 ? market.SwingHigh : market.SwingLow;
        bool blocked = sign > 0 ? market.Price < resistance && resistance < request.TakeProfit : market.Price > resistance && resistance > request.TakeProfit;

        if (blocked)
        {
            detail += $" No caminho há o {(sign > 0 ? "máximo" : "mínimo")} recente em {Price(resistance)}, onde o preço já recuou antes.";
            points -= 3;
        }

        findings.Add(new AnalysisFinding("Take profit", kind, title, detail, points));
    }

    private static void AddEntryVersusPrice(List<AnalysisFinding> findings, TradeAnalysisRequest request, MarketSnapshot market, int sign)
    {
        double price = market.Price;
        bool stopBreached = sign > 0 ? price <= request.StopLoss : price >= request.StopLoss;
        bool targetReached = sign > 0 ? price >= request.TakeProfit : price <= request.TakeProfit;

        if (stopBreached)
        {
            findings.Add(new AnalysisFinding("Entrada", FindingKind.Negative, "O preço atual já passou o teu stop", $"O preço está em {Price(price)}, para lá do stop em {Price(request.StopLoss)}. Esta trade já estaria fechada.", -30));
            return;
        }

        if (targetReached)
        {
            findings.Add(new AnalysisFinding("Entrada", FindingKind.Negative, "O preço atual já atingiu o teu alvo", $"O preço está em {Price(price)}, para lá do alvo em {Price(request.TakeProfit)}. Entrar agora seria chegar depois do movimento.", -20));
            return;
        }

        double gap = Math.Abs(request.Entry - price) / market.Atr;

        if (gap < 0.25)
        {
            return;
        }

        double nowRisk = Math.Abs(price - request.StopLoss);
        double nowRiskReward = nowRisk > 0 ? Math.Abs(request.TakeProfit - price) / nowRisk : 0;
        string where = (request.Entry - price) * sign > 0 ? "pior" : "melhor";
        string detail = $"O preço atual é {Price(price)} e a tua entrada é {Price(request.Entry)} ({Number(gap, "0.##")} ATR de diferença, {where} do que entrar agora). Se entrasses ao preço atual com o mesmo stop e alvo, o rácio seria {Number(nowRiskReward, "0.##")}:1.";

        findings.Add(gap > 1
            ? new AnalysisFinding("Entrada", FindingKind.Warning, "Entrada longe do preço atual", detail + " A ordem só enche se o preço lá chegar.", -6)
            : new AnalysisFinding("Entrada", FindingKind.Neutral, "Entrada diferente do preço atual", detail, 0));
    }

    private static void AddModel(List<AnalysisFinding> findings, double modelScore, TradeDirection? modelDirection, TradeDirection direction, int sign)
    {
        string verb = direction == TradeDirection.Buy ? "compra" : "venda";

        if (modelDirection is null)
        {
            findings.Add(new AnalysisFinding("Modelo", FindingKind.Neutral, "O modelo não vê direção clara", $"Os sinais técnicos e de sentimento quase se anulam (pontuação {Number(modelScore, "+0.00;-0.00")}).", 0));
            return;
        }

        int weight = Math.Min(15, (int)Math.Round(Math.Abs(modelScore) * 30));
        bool aligned = modelDirection == direction;

        findings.Add(aligned
            ? new AnalysisFinding("Modelo", FindingKind.Positive, $"O modelo concorda com a {verb}", $"Pontuação do modelo {Number(modelScore, "+0.00;-0.00")}, na mesma direção. O peso é limitado porque o histórico mostra que este modelo ainda não separa bem as boas das más sugestões.", weight)
            : new AnalysisFinding("Modelo", FindingKind.Negative, $"O modelo vê o contrário da {verb}", $"Pontuação do modelo {Number(modelScore, "+0.00;-0.00")}, na direção oposta à tua. O peso é limitado porque o modelo ainda não tem vantagem demonstrada.", -weight));
    }

    private static void AddContext(List<AnalysisFinding> findings, MarketSnapshot market, ScoringOptions options, int sign)
    {
        if (market.Adx is { } adx)
        {
            if (options.MinAdx > 0 && adx < options.MinAdx)
            {
                findings.Add(new AnalysisFinding("Contexto", FindingKind.Warning, "Mercado lateral", $"ADX {Number(adx, "0")} (abaixo de {Number(options.MinAdx, "0")}): sem tendência clara, os movimentos tendem a voltar para trás.", -6));
            }
            else if (adx >= 25)
            {
                findings.Add(new AnalysisFinding("Contexto", FindingKind.Positive, "Tendência forte", $"ADX {Number(adx, "0")}: o mercado está a mover-se com direção.", 4));
            }
        }

        if (market.WeeklyTrend is { } weekly && Math.Abs(weekly) >= 0.5)
        {
            findings.Add(Math.Sign(weekly) == sign
                ? new AnalysisFinding("Contexto", FindingKind.Positive, "A favor da tendência semanal", "A tendência do gráfico semanal vai no mesmo sentido da tua trade.", 6)
                : new AnalysisFinding("Contexto", FindingKind.Negative, "Contra a tendência semanal", "A tendência do gráfico semanal vai no sentido oposto ao da tua trade.", -10));
        }

        if ((sign > 0 && market.Rsi >= 70) || (sign < 0 && market.Rsi <= 30))
        {
            findings.Add(new AnalysisFinding("Contexto", FindingKind.Warning, sign > 0 ? "Ativo sobrecomprado" : "Ativo sobrevendido", $"RSI {Number(market.Rsi, "0")}: o preço já andou muito na direção em que queres entrar.", -6));
        }

        double stretch = (market.Price - market.Ema20) * sign / market.Atr;

        if (stretch > 2.5)
        {
            findings.Add(new AnalysisFinding("Contexto", FindingKind.Warning, "A perseguir o movimento", $"O preço está a {Number(stretch, "0.#")} ATR da média de 20 dias, na direção da tua trade. Entradas esticadas costumam apanhar o fim do movimento.", -6));
        }

        if (options.MaxVix > 0 && market.Vix is { } vix && vix >= options.MaxVix)
        {
            findings.Add(new AnalysisFinding("Contexto", FindingKind.Warning, "Volatilidade de mercado elevada", $"VIX em {Number(vix, "0.#")}: o mercado está nervoso e os movimentos são menos previsíveis.", -6));
        }

        if (options.BlockEarningsWithinDays > 0 && market.DaysToEarnings is { } days && days >= 0 && days <= options.BlockEarningsWithinDays)
        {
            findings.Add(new AnalysisFinding("Contexto", FindingKind.Negative, "Resultados da empresa à porta", $"A empresa divulga resultados em {days} dias, o que pode fazer o preço saltar por cima do stop.", -10));
        }
    }

    private static void AddSentiment(List<AnalysisFinding> findings, SentimentSnapshot sentiment, ScoringOptions options, int sign)
    {
        if (sentiment.ArticleCount < options.MinSentimentArticles)
        {
            findings.Add(new AnalysisFinding("Sentimento", FindingKind.Neutral, "Poucas notícias para medir o sentimento", $"Só há {sentiment.ArticleCount} artigos guardados sobre este ativo nos últimos 30 dias (mínimo {options.MinSentimentArticles}). Esta análise não conta com sentimento.", 0));
            return;
        }

        double aligned = sentiment.WeightedSentiment * sign;
        string basis = $"Sentimento {Number(sentiment.WeightedSentiment, "+0.00;-0.00")} em {sentiment.ArticleCount} artigos.";

        if (aligned > SentimentThreshold)
        {
            findings.Add(new AnalysisFinding("Sentimento", FindingKind.Positive, "Notícias a favor", $"{basis} Vai no mesmo sentido da tua trade.", 8));
        }
        else if (aligned < -SentimentThreshold)
        {
            findings.Add(new AnalysisFinding("Sentimento", FindingKind.Negative, "Notícias contra", $"{basis} Vai no sentido oposto ao da tua trade.", -8));
        }
        else
        {
            findings.Add(new AnalysisFinding("Sentimento", FindingKind.Neutral, "Sentimento neutro", $"{basis} Não puxa para nenhum lado.", 0));
        }
    }

    private static void AddHistory(List<AnalysisFinding> findings, AnalysisHistory? history, TradeDirection direction)
    {
        if (history is null || history.Samples < MinHistorySamples)
        {
            findings.Add(new AnalysisFinding("Histórico", FindingKind.Neutral, "Sem histórico suficiente", $"Só há {history?.Samples ?? 0} sugestões fechadas deste tipo ({(direction == TradeDirection.Buy ? "compras" : "vendas")} em {history?.Scope ?? "este tipo de ativo"}); precisa de pelo menos {MinHistorySamples} para ser fiável.", 0));
            return;
        }

        string detail = $"{history.Samples} sugestões fechadas ({history.Scope}): {Number(history.PositiveShare * 100, "0")}% terminaram com ganho, R médio {Number(history.AverageR, "+0.00;-0.00")}.";

        findings.Add(history.AverageR switch
        {
            < -0.05 => new AnalysisFinding("Histórico", FindingKind.Warning, "Este tipo de trade tem perdido", detail, -5),
            > 0.1 => new AnalysisFinding("Histórico", FindingKind.Positive, "Este tipo de trade tem ganho", detail, 4),
            _ => new AnalysisFinding("Histórico", FindingKind.Neutral, "Histórico sem vantagem clara", detail, 0)
        });
    }

    private static (string Code, string Label) Verdict(int score) => score switch
    {
        >= 70 => ("good", "Boa"),
        >= 55 => ("acceptable", "Aceitável"),
        >= 40 => ("weak", "Fraca"),
        _ => ("bad", "Má")
    };

    private static string Summarize(IReadOnlyList<AnalysisFinding> findings)
    {
        List<string> strong = findings.Where(f => f.Points > 0).OrderByDescending(f => f.Points).Take(2).Select(f => f.Title.ToLowerInvariant()).ToList();
        List<string> weak = findings.Where(f => f.Points < 0).OrderBy(f => f.Points).Take(2).Select(f => f.Title.ToLowerInvariant()).ToList();

        string good = strong.Count > 0 ? $"A favor: {string.Join(", ", strong)}." : "Nada de especial a favor.";
        string bad = weak.Count > 0 ? $"Contra: {string.Join(", ", weak)}." : "Nada de grave contra.";

        return $"{good} {bad}";
    }

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string Price(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}
