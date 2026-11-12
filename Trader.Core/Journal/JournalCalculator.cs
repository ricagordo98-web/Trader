using Trader.Core.Models;

namespace Trader.Core.Journal;

public sealed record TradeOutcome(double? RiskAmount, double? NetPnl, double? RMultiple);

public static class JournalCalculator
{
    public static TradeOutcome Compute(JournalTrade trade)
    {
        double? riskAmount = (trade.InitialStopLoss ?? trade.StopLoss) is { } stopLoss
            ? Math.Abs(trade.EntryPrice - stopLoss) * trade.Quantity * trade.FxRate
            : null;

        if (trade.Status != TradeStatus.Closed || trade.ExitPrice is not { } exitPrice)
        {
            return new TradeOutcome(riskAmount, null, null);
        }

        int sign = trade.Direction == TradeDirection.Buy ? 1 : -1;
        double gross = sign * (exitPrice - trade.EntryPrice) * trade.Quantity * trade.FxRate;
        double net = gross - trade.Fees;

        return new TradeOutcome(riskAmount, net, riskAmount is > 0 ? net / riskAmount : null);
    }
}

public static class JournalValidator
{
    public const int MaxNotesLength = 2000;
    public const int MaxSetupLength = 40;
    public const int MaxTagsLength = 120;
    public const int MaxEmotionLength = 20;
    public const int MaxImageLength = 600_000;

    public static IReadOnlyList<string> ValidateDetails(TradeDetailsRequest request)
    {
        List<string> errors = [];

        if ((request.Setup?.Trim().Length ?? 0) > MaxSetupLength || (request.Tags?.Trim().Length ?? 0) > MaxTagsLength || (request.Emotion?.Trim().Length ?? 0) > MaxEmotionLength)
        {
            errors.Add("Setup, etiquetas ou emoção demasiado longos.");
        }

        if ((request.Notes?.Length ?? 0) > MaxNotesLength)
        {
            errors.Add("Notas demasiado longas.");
        }

        if (request.ChartImage is { } image && (image.Length > MaxImageLength || !image.StartsWith("data:image/", StringComparison.Ordinal)))
        {
            errors.Add("A imagem tem de ser um PNG/JPEG até cerca de 400 KB.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateOpen(OpenTradeRequest request)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(request.Symbol) || request.Symbol.Trim().Length > 32)
        {
            errors.Add("Indica o símbolo (até 32 caracteres).");
        }

        if (request.EntryPrice <= 0 || request.Quantity <= 0)
        {
            errors.Add("A entrada e a quantidade têm de ser positivas.");
        }

        if (request.FxRate <= 0)
        {
            errors.Add("A taxa de conversão tem de ser positiva.");
        }

        if (request.Fees < 0)
        {
            errors.Add("As comissões não podem ser negativas.");
        }

        if (request.EntryTime > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            errors.Add("A data de entrada não pode estar no futuro.");
        }

        if (request.StopLoss is { } stopLoss)
        {
            bool protective = request.Direction == TradeDirection.Buy ? stopLoss < request.EntryPrice : stopLoss > request.EntryPrice;

            if (stopLoss <= 0 || !protective)
            {
                errors.Add("O stop loss tem de estar do lado de perda da entrada.");
            }
        }

        if (request.TakeProfit is { } takeProfit)
        {
            bool profitable = request.Direction == TradeDirection.Buy ? takeProfit > request.EntryPrice : takeProfit < request.EntryPrice;

            if (takeProfit <= 0 || !profitable)
            {
                errors.Add("O take profit tem de estar do lado de ganho da entrada.");
            }
        }

        if ((request.Notes?.Length ?? 0) > MaxNotesLength || (request.Platform?.Length ?? 0) > 64)
        {
            errors.Add("Notas ou plataforma demasiado longas.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateClose(JournalTrade trade, CloseTradeRequest request)
    {
        List<string> errors = [];

        if (trade.Status == TradeStatus.Closed)
        {
            errors.Add("Esta trade já está fechada.");
        }

        if (request.ExitPrice <= 0)
        {
            errors.Add("O preço de saída tem de ser positivo.");
        }

        if (request.AdditionalFees < 0)
        {
            errors.Add("As comissões não podem ser negativas.");
        }

        if (request.ExitTime < trade.EntryTime)
        {
            errors.Add("A saída não pode ser anterior à entrada.");
        }

        if (request.ExitTime > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            errors.Add("A data de saída não pode estar no futuro.");
        }

        if ((request.Notes?.Length ?? 0) > MaxNotesLength)
        {
            errors.Add("Notas demasiado longas.");
        }

        return errors;
    }
}
