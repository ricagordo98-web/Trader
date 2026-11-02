using Trader.Core.Agender;

namespace Trader.Core.Abstractions;

public interface IAgenderSync
{
    Task<AgenderSettings> GetSettingsAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> SaveSettingsAsync(string userId, SaveAgenderSettingsRequest request, CancellationToken cancellationToken);

    Task RemoveAsync(string userId, CancellationToken cancellationToken);

    AgenderStatus GetStatus(string userId);

    Task<AgenderStatus> SyncNowAsync(string userId, CancellationToken cancellationToken);

    Task SyncAllAsync(CancellationToken cancellationToken);
}
