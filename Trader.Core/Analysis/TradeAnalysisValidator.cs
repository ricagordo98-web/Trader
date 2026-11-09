using Trader.Core.Models;

namespace Trader.Core.Analysis;

public static class TradeAnalysisValidator
{
    public static IReadOnlyList<string> Validate(TradeAnalysisRequest request)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            errors.Add("Escolhe o ativo.");
        }

        if (!IsPositive(request.Entry) || !IsPositive(request.StopLoss) || !IsPositive(request.TakeProfit))
        {
            errors.Add("A entrada, o stop loss e o take profit têm de ser preços positivos.");
            return errors;
        }

        bool buy = request.Direction == TradeDirection.Buy;

        if (buy ? request.StopLoss >= request.Entry : request.StopLoss <= request.Entry)
        {
            errors.Add(buy ? "Numa compra o stop loss tem de ficar abaixo da entrada." : "Numa venda o stop loss tem de ficar acima da entrada.");
        }

        if (buy ? request.TakeProfit <= request.Entry : request.TakeProfit >= request.Entry)
        {
            errors.Add(buy ? "Numa compra o take profit tem de ficar acima da entrada." : "Numa venda o take profit tem de ficar abaixo da entrada.");
        }

        return errors;
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
