using Trader.Core.Projection;

namespace Trader.Core.Abstractions;

public interface IProjectionRecordService
{
    Task<(ProjectionRecordDto? Record, IReadOnlyList<string> Errors)> RegisterAsync(RegisterProjectionRequest request, CancellationToken cancellationToken);

    Task<ProjectionRecordsView> ListAsync(CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}
