using Trader.Core.Learning;

namespace Trader.Core.Abstractions;

public interface ILearningService
{
    Task<IReadOnlyList<LearningItemDto>> ListAsync(CancellationToken cancellationToken);

    Task<(LearningItemDto? Item, IReadOnlyList<string> Errors)> AddLinkAsync(AddLearningLinkRequest request, CancellationToken cancellationToken);

    Task<(LearningItemDto? Item, IReadOnlyList<string> Errors)> AddPdfAsync(string title, string? category, string fileName, Stream content, CancellationToken cancellationToken);

    Task<(Stream Content, string FileName)?> OpenPdfAsync(long id, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}
