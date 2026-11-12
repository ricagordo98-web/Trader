using System.Globalization;
using System.Text;
using Trader.Core.Analysis;
using Trader.Core.Models;
using Trader.Core.TradingSystem;

namespace Trader.Core.Assistant;

public static class DossierText
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static string Price(double value) => value.ToString(value >= 100 ? "#,0.00" : value >= 1 ? "#,0.0000" : "0.######", Pt);

    public static string Summary(TradeDossier d)
    {
        List<string> parts = [];
        string side = d.Direction == TradeDirection.Buy ? "compra" : "venda";

        string trend = string.Join(", ", d.Timeframes.Select(t => $"{t.Label} {t.Trend}"));
        parts.Add($"{d.Symbol} a {Price(d.Price)} {d.Currency}{(d.ChangePercent is { } change ? $" ({change.ToString("+0.0;-0.0", Pt)}% hoje)" : string.Empty)}. Tendência: {trend}.");

        if (d.Analysis is { } analysis)
        {
            parts.Add($"Análise da {side}: {analysis.Score}/100, {analysis.Verdict.ToLowerInvariant()}.");
        }

        if (d.Model.OpenSuggestion is { } suggestion)
        {
            parts.Add($"O modelo tem uma sugestão de {(suggestion.Direction == TradeDirection.Buy ? "compra" : "venda")} aberta.");
        }

        if (d.Events.Count > 0)
        {
            parts.Add($"Atenção: {string.Join("; ", d.Events)}.");
        }

        if (d.Check is { } check)
        {
            int fails = check.Items.Count(i => i.Status == CheckStatus.Fail);
            parts.Add(check.Blocked
                ? "O teu sistema bloqueia esta trade agora."
                : fails == 0 ? "Cumpre as regras verificáveis do teu plano." : $"Falha {fails} regra(s) do teu plano.");
        }

        return string.Join(" ", parts);
    }

    public static string Facts(TradeDossier d)
    {
        StringBuilder sb = new();
        sb.AppendLine($"ATIVO: {d.Symbol} ({d.Name}), {d.AssetClass}, moeda {d.Currency}.");
        sb.AppendLine($"PREÇO: {Price(d.Price)}{(d.ChangePercent is { } c ? $", variação hoje {c.ToString("+0.00;-0.00", Pt)}%" : string.Empty)}{(d.PriceAsOf is { } asOf ? $", às {asOf:yyyy-MM-dd HH:mm} UTC" : string.Empty)}.");
        sb.AppendLine($"DIREÇÃO EM ANÁLISE: {(d.Direction == TradeDirection.Buy ? "compra" : "venda")}{(d.DirectionFromUser ? " (indicada pelo utilizador)" : " (assumida)")}.");

        foreach (TimeframeView tf in d.Timeframes)
        {
            sb.AppendLine($"TENDÊNCIA {tf.Label}: {tf.Trend}. {tf.Detail}");
        }

        sb.AppendLine($"VOLATILIDADE: ATR diário {Price(d.Atr)} ({(d.Price > 0 ? d.Atr / d.Price * 100 : 0).ToString("0.0", Pt)}% do preço).");
        sb.AppendLine($"NÍVEIS: suporte recente {Price(d.SwingLow)}, resistência recente {Price(d.SwingHigh)}{(d.YearLow is { } yl && d.YearHigh is { } yh ? $", mínimo de 52 semanas {Price(yl)}, máximo {Price(yh)}" : string.Empty)}.");

        if (d.Analysis is { } a)
        {
            sb.AppendLine($"ANÁLISE DA TRADE: pontuação {a.Score}/100 ({a.Verdict}). {a.Summary}");

            foreach (AnalysisFinding finding in a.Findings.Take(8))
            {
                sb.AppendLine($"- [{finding.Category}] {finding.Title}: {finding.Detail}");
            }

            sb.AppendLine($"SENTIMENTO DAS NOTÍCIAS: {a.Sentiment.WeightedSentiment.ToString("+0.00;-0.00", Pt)} em {a.Sentiment.ArticleCount} artigos{(a.Sentiment.BuzzZScore is { } buzz ? $", buzz z={buzz.ToString("0.0", Pt)}" : string.Empty)}.");
            sb.AppendLine($"MODELO: {(a.Model.Direction switch { TradeDirection.Buy => "inclina-se para comprar", TradeDirection.Sell => "inclina-se para vender", _ => "sem direção clara" })} (score {a.Model.Score.ToString("+0.00;-0.00", Pt)}).");
        }

        if (d.Model.OpenSuggestion is { } s)
        {
            sb.AppendLine($"SUGESTÃO ABERTA DO MODELO: {(s.Direction == TradeDirection.Buy ? "compra" : "venda")} a {Price(s.Entry)}, stop {Price(s.StopLoss)}, alvo {Price(s.TakeProfit)}, criada {s.CreatedAt:yyyy-MM-dd}.");
        }

        if (d.Model.ClosedOnSymbol > 0)
        {
            sb.AppendLine($"HISTÓRICO DO MODELO NESTE ATIVO: {d.Model.ClosedOnSymbol} sugestões fechadas, {(d.Model.WinRateOnSymbol ?? 0).ToString("P0", Pt)} ganhas, R médio {(d.Model.AverageROnSymbol ?? 0).ToString("+0.00;-0.00", Pt)}. O modelo global ainda não provou margem positiva.");
        }

        if (d.News.Count > 0)
        {
            sb.AppendLine("NOTÍCIAS RECENTES:");

            foreach (DossierNews news in d.News)
            {
                sb.AppendLine($"- {news.PublishedAt:yyyy-MM-dd} {news.Source}: {news.Title}{(news.Sentiment is { } sentiment ? $" (sentimento {sentiment.ToString("+0.00;-0.00", Pt)})" : string.Empty)}");
            }
        }
        else
        {
            sb.AppendLine("NOTÍCIAS RECENTES: nenhuma recolhida para este ativo.");
        }

        sb.AppendLine(d.Events.Count > 0 ? $"EVENTOS PRÓXIMOS: {string.Join("; ", d.Events)}." : "EVENTOS PRÓXIMOS: nenhum evento importante identificado.");

        if (d.Exposure.Count > 0)
        {
            sb.AppendLine($"POSIÇÕES ABERTAS DO UTILIZADOR: {string.Join(", ", d.Exposure.Select(e => $"{e.Symbol} {(e.Direction == TradeDirection.Buy ? "compra" : "venda")}{(e.Correlation is { } corr ? $" (correlação {corr.ToString("0.00", Pt)})" : string.Empty)}"))}.");
        }

        sb.AppendLine($"PROPOSTA DE TRADE: entrada {Price(d.Draft.Entry)}, stop {Price(d.Draft.StopLoss)}, alvo {Price(d.Draft.TakeProfit)}, R:R {d.Draft.RewardRisk.ToString("0.0", Pt)}, quantidade {d.Draft.Quantity.ToString("0.####", Pt)}.");

        if (d.Check is { } check)
        {
            sb.AppendLine($"REGRAS DO SISTEMA DO UTILIZADOR: {check.Verdict}");

            foreach (CheckItem item in check.Items.Where(i => i.Status != CheckStatus.Pass))
            {
                sb.AppendLine($"- {(item.Status == CheckStatus.Fail ? "FALHA" : "?")} {item.Label}: {item.Detail}");
            }
        }

        return sb.ToString();
    }
}

public static class AssistantPrompt
{
    public static string System(DateTimeOffset now, bool webSearch) =>
        $"""
        És o assistente de trading pessoal do Ricardo, dentro da app Trader. Hoje é {now:yyyy-MM-dd HH:mm} UTC.
        O teu trabalho é recolher e resumir informação sobre a trade que ele está a pensar fazer, e lembrá-lo das regras do sistema dele.
        Responde em português de Portugal, a tratar por tu, de forma direta e curta (no máximo uns 8 a 12 parágrafos curtos ou listas).
        Escreve em texto simples, sem markdown (sem **, # nem tabelas); para listas usa linhas que começam por "- ".
        Usa os FACTOS fornecidos pela app como fonte principal e cita números exatamente como aparecem. Nunca inventes preços, datas ou notícias.
        {(webSearch ? "Podes usar a pesquisa web para notícias de última hora ou contexto que falte; diz sempre de onde vem a informação." : "Não tens acesso à internet: se faltar informação, diz que não a tens.")}
        Separa claramente o que está a favor e o que está contra a trade. Se as regras do sistema falham ou bloqueiam, diz isso primeiro.
        Não dês ordens de compra ou venda nem garantias: a decisão é dele e deve seguir o plano. Não é aconselhamento financeiro.
        """;

    public static string UserMessage(string question, string? facts) =>
        facts is null
            ? question
            : $"""
              FACTOS DA APP (atualizados agora):
              {facts}

              PERGUNTA: {question}
              """;
}
