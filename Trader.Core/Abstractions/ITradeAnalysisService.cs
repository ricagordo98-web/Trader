using Trader.Core.Analysis;

namespace Trader.Core.Abstractions;

public interface ITradeAnalysisService
{
    Task<TradeAnalysis?> AnalyzeAsync(TradeAnalysisRequest request, CancellationToken cancellationToken);
}
