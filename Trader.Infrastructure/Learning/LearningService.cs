using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trader.Core.Abstractions;
using Trader.Core.Learning;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Learning;

public sealed class LearningOptions
{
    public const string SectionName = "Learning";

    public string Directory { get; set; } = "learning";
}

public sealed class LearningService(
    TraderDbContext dbContext,
    IOptions<LearningOptions> options,
    TimeProvider timeProvider,
    ILogger<LearningService> logger) : ILearningService
{
    private const string PdfExtension = ".pdf";
    private const int CopyBufferSize = 81920;

    private readonly string _directory = options.Value.Directory;

    public async Task<IReadOnlyList<LearningItemDto>> ListAsync(CancellationToken cancellationToken)
    {
        List<LearningItem> items = await dbContext.LearningItems.AsNoTracking().ToListAsync(cancellationToken);

        return items.OrderByDescending(i => i.CreatedAt).Select(ToDto).ToList();
    }

    public async Task<(LearningItemDto? Item, IReadOnlyList<string> Errors)> AddLinkAsync(AddLearningLinkRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> errors = LearningRules.ValidateLink(request);

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        LearningItem item = new()
        {
            Title = request.Title.Trim(),
            Kind = LearningKind.Link,
            Category = LearningRules.Clean(request.Category),
            Url = request.Url.Trim(),
            CreatedAt = timeProvider.GetUtcNow()
        };

        dbContext.LearningItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (ToDto(item), []);
    }

    public async Task<(LearningItemDto? Item, IReadOnlyList<string> Errors)> AddPdfAsync(string title, string? category, string fileName, Stream content, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> errors = LearningRules.ValidatePdfMetadata(title, category);

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        System.IO.Directory.CreateDirectory(_directory);
        string storedName = Guid.NewGuid().ToString("N") + PdfExtension;
        string path = Path.Combine(_directory, storedName);
        long size;

        try
        {
            size = await SavePdfAsync(content, path, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            DeleteFile(path);
            return (null, [ex.Message]);
        }

        LearningItem item = new()
        {
            Title = title.Trim(),
            Kind = LearningKind.Pdf,
            Category = LearningRules.Clean(category),
            FileName = Path.GetFileName(fileName),
            StoredName = storedName,
            SizeBytes = size,
            CreatedAt = timeProvider.GetUtcNow()
        };

        dbContext.LearningItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (ToDto(item), []);
    }

    public async Task<(Stream Content, string FileName)?> OpenPdfAsync(long id, CancellationToken cancellationToken)
    {
        LearningItem? item = await dbContext.LearningItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.Kind == LearningKind.Pdf, cancellationToken);

        if (item?.StoredName is null)
        {
            return null;
        }

        string path = Path.Combine(_directory, Path.GetFileName(item.StoredName));

        return File.Exists(path)
            ? (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true), item.FileName ?? item.StoredName)
            : null;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        LearningItem? item = await dbContext.LearningItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (item is null)
        {
            return false;
        }

        dbContext.LearningItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (item.StoredName is not null)
        {
            DeleteFile(Path.Combine(_directory, Path.GetFileName(item.StoredName)));
        }

        return true;
    }

    private static async Task<long> SavePdfAsync(Stream content, string path, CancellationToken cancellationToken)
    {
        await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        byte[] buffer = new byte[CopyBufferSize];
        long total = 0;
        bool first = true;
        int read;

        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (first)
            {
                first = false;

                if (!LearningRules.LooksLikePdf(buffer.AsSpan(0, read)))
                {
                    throw new InvalidDataException("O ficheiro não parece ser um PDF.");
                }
            }

            total += read;

            if (total > LearningRules.MaxPdfBytes)
            {
                throw new InvalidDataException($"O PDF excede o limite de {LearningRules.MaxPdfBytes / 1024 / 1024} MB.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total == 0 ? throw new InvalidDataException("O ficheiro está vazio.") : total;
    }

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Could not delete learning file {Path}: {Message}", path, ex.Message);
        }
    }

    private static LearningItemDto ToDto(LearningItem item) =>
        new(item.Id, item.Title, item.Kind, item.Category, item.Url, item.FileName, item.SizeBytes, item.CreatedAt);
}
