using Trader.Core.Projection;

namespace Trader.Core.Abstractions;

public interface IProjectionService
{
    Task<PriceProjection?> ProjectAsync(string symbol, int horizonMinutes, CancellationToken cancellationToken);
}
