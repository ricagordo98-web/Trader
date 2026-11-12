using System.Globalization;
using Trader.Core.Intraday;
using Trader.Core.Journal;
using Trader.Core.Models;

namespace Trader.Core.Alerts;

public static class AlertFactory
{
    private const int MaxIndividualSuggestionAlerts = 3;

    public static Alert ForNewSuggestion(Suggestion suggestion, DateTimeOffset now) => new()
    {
        Kind = AlertKind.NewSuggestion,
        Symbol = suggestion.Symbol,
        Title = $"Nova sugestão: {(suggestion.Direction == TradeDirection.Buy ? "COMPRA" : "VENDA")} {suggestion.Symbol}",
        Message = string.Create(
            CultureInfo.InvariantCulture,
            $"{suggestion.Confidence * 100:0}% de certeza · entrada {suggestion.Entry:0.#####} · SL {suggestion.StopLoss:0.#####} · TP {suggestion.TakeProfit:0.#####}"),
        SuggestionId = suggestion.Id,
        CreatedAt = now
    };

    public static IReadOnlyList<Alert> ForNewSuggestions(IReadOnlyList<Suggestion> suggestions, DateTimeOffset now)
    {
        if (suggestions.Count <= MaxIndividualSuggestionAlerts)
        {
            return suggestions.Select(s => ForNewSuggestion(s, now)).ToList();
        }

        string top = string.Join(
            ", ",
            suggestions.OrderByDescending(s => s.Confidence).Take(3).Select(s => $"{(s.Direction == TradeDirection.Buy ? "COMPRAR" : "VENDER")} {s.Symbol}"));

        return
        [
            new Alert
            {
                Kind = AlertKind.NewSuggestion,
                Symbol = "SUGESTÕES",
                Title = $"{suggestions.Count} novas sugestões",
                Message = $"Mais fortes: {top}",
                CreatedAt = now
            }
        ];
    }

    public static IReadOnlyList<Alert> ForRadarEvents(IReadOnlyList<RadarEvent> events, DateTimeOffset now)
    {
        if (events.Count <= MaxIndividualSuggestionAlerts)
        {
            return events.Select(e => new Alert
            {
                Kind = AlertKind.Radar,
                Symbol = e.Symbol,
                Title = $"Radar: {e.Symbol} {(e.Direction == TradeDirection.Buy ? "↑" : "↓")} {e.Detail}",
                Message = string.Create(CultureInfo.InvariantCulture, $"{e.Name} · preço {e.Price:0.#####}{(e.Why is { Length: > 0 } why ? " · " + why : string.Empty)}"),
                CreatedAt = now
            }).ToList();
        }

        string top = string.Join(", ", events.OrderByDescending(e => e.Strength).Take(4).Select(e => $"{e.Symbol} {(e.Direction == TradeDirection.Buy ? "↑" : "↓")}"));

        return
        [
            new Alert
            {
                Kind = AlertKind.Radar,
                Symbol = "RADAR",
                Title = $"Radar: {events.Count} movimentos anormais",
                Message = $"Mais fortes: {top}",
                CreatedAt = now
            }
        ];
    }

    public static Alert ForTradeLevel(JournalTrade trade, TradeLevel level, double price, DateTimeOffset now, string currency = "", double? marketPrice = null)
    {
        string label = level == TradeLevel.StopLoss ? "Stop loss" : "Take profit";
        TradeOutcome outcome = JournalCalculator.Compute(new JournalTrade
        {
            Direction = trade.Direction,
            EntryPrice = trade.EntryPrice,
            Quantity = trade.Quantity,
            StopLoss = trade.StopLoss,
            InitialStopLoss = trade.InitialStopLoss,
            FxRate = trade.FxRate,
            Fees = trade.Fees,
            Status = TradeStatus.Closed,
            ExitPrice = price
        });

        string result = outcome.NetPnl is { } pnl
            ? $" · {MoneyText(pnl, currency)}{(outcome.RMultiple is { } r ? $" ({r.ToString("+0.0;-0.0;0.0", PtCulture)} R)" : string.Empty)}"
            : string.Empty;

        return new Alert
        {
            UserId = trade.UserId,
            Kind = level == TradeLevel.StopLoss ? AlertKind.TradeStopLoss : AlertKind.TradeTakeProfit,
            Symbol = trade.Symbol,
            Title = $"{trade.Symbol} · {label}{result}",
            Message = string.Create(CultureInfo.InvariantCulture, $"{(trade.Direction == TradeDirection.Buy ? "Compra" : "Venda")} a {trade.EntryPrice:0.#####}, fechada na Carteira a {price:0.#####} (preço agora {marketPrice ?? price:0.#####}). Confirma que também fechou na corretora."),
            SuggestionId = trade.SuggestionId ?? 0,
            TradeId = trade.Id,
            CreatedAt = now
        };
    }

    private static readonly CultureInfo PtCulture = CultureInfo.GetCultureInfo("pt-PT");

    private static string MoneyText(double value, string currency)
    {
        string symbol = currency.ToUpperInvariant() switch
        {
            "EUR" => "€",
            "USD" => "$",
            "GBP" => "£",
            var other => other
        };

        return $"{value.ToString("+#,0.00;-#,0.00;0.00", PtCulture)} {symbol}".TrimEnd();
    }

    public static Alert ForPriceAlert(PriceAlert alert, double price, DateTimeOffset now) => new()
    {
        UserId = alert.UserId,
        Kind = AlertKind.PriceTarget,
        Symbol = alert.Symbol,
        Title = $"Alerta de preço: {alert.Symbol} {(alert.Condition == PriceCondition.Above ? "acima de" : "abaixo de")} {alert.Price.ToString("0.#####", CultureInfo.InvariantCulture)}",
        Message = string.Create(CultureInfo.InvariantCulture, $"Preço atual {price:0.#####}{(alert.Note.Length > 0 ? " · " + alert.Note : string.Empty)}"),
        CreatedAt = now
    };

    public static Alert? ForOutcome(Suggestion suggestion, DateTimeOffset now)
    {
        (AlertKind Kind, string Label)? outcome = suggestion.Status switch
        {
            SuggestionStatus.HitTakeProfit => (AlertKind.TakeProfit, "Take profit atingido"),
            SuggestionStatus.HitStopLoss => (AlertKind.StopLoss, "Stop loss atingido"),
            SuggestionStatus.Expired => (AlertKind.Expired, "Sugestão expirou"),
            _ => null
        };

        if (outcome is null)
        {
            return null;
        }

        return new Alert
        {
            Kind = outcome.Value.Kind,
            Symbol = suggestion.Symbol,
            Title = $"{outcome.Value.Label}: {suggestion.Symbol}",
            Message = string.Create(CultureInfo.InvariantCulture, $"Resultado {suggestion.RMultiple ?? 0:+0.00;-0.00;0.00} R · saída {suggestion.ExitPrice ?? 0:0.#####}"),
            SuggestionId = suggestion.Id,
            CreatedAt = now
        };
    }
}
