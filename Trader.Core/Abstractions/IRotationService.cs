using Trader.Core.Rotation;

namespace Trader.Core.Abstractions;

public interface IRotationService
{
    Task<RotationResult> RunAsync(CancellationToken cancellationToken);

    Task<RotationResult?> GetLastAsync(CancellationToken cancellationToken);
}
