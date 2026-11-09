using Trader.Core.Analysis;
using Trader.Core.Intraday;
using Trader.Core.Models;

namespace Trader.Core.Abstractions;

public interface ISuggestionService
{
    Task<IReadOnlyList<SuggestionDto>> GetAsync(string? symbol, bool? open, int limit, SuggestionHorizon? horizon, CancellationToken cancellationToken, bool includeShadow = false);

    Task<TrackRecordDto> GetTrackRecordAsync(SuggestionHorizon? horizon, CancellationToken cancellationToken);

    Task<TradeContext?> GetContextAsync(string symbol, CancellationToken cancellationToken);

    Task<int> RefreshAsync(CancellationToken cancellationToken);

    Task<int> RefreshIntradayAsync(IReadOnlyList<IntradayContext> contexts, CancellationToken cancellationToken);
}
