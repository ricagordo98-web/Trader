namespace Trader.Core.Learning;

public enum LearningKind
{
    Pdf,
    Link
}

public sealed class LearningItem
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public LearningKind Kind { get; set; }
    public string? Category { get; set; }
    public string? Url { get; set; }
    public string? FileName { get; set; }
    public string? StoredName { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record LearningItemDto(
    long Id,
    string Title,
    LearningKind Kind,
    string? Category,
    string? Url,
    string? FileName,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record AddLearningLinkRequest(string Title, string Url, string? Category);

public static class LearningRules
{
    public const int MaxTitleLength = 120;
    public const int MaxCategoryLength = 40;
    public const int MaxUrlLength = 2048;
    public const long MaxPdfBytes = 50L * 1024 * 1024;

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    public static IReadOnlyList<string> ValidateLink(AddLearningLinkRequest request)
    {
        List<string> errors = ValidateCommon(request.Title, request.Category);

        if (request.Url is null
            || request.Url.Length > MaxUrlLength
            || !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            errors.Add("A ligação tem de ser um endereço http ou https válido.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidatePdfMetadata(string title, string? category) => ValidateCommon(title, category);

    public static bool LooksLikePdf(ReadOnlySpan<byte> header) => header.StartsWith(PdfSignature);

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> ValidateCommon(string? title, string? category)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            errors.Add($"O título é obrigatório e tem no máximo {MaxTitleLength} caracteres.");
        }

        if (category is not null && category.Trim().Length > MaxCategoryLength)
        {
            errors.Add($"A categoria tem no máximo {MaxCategoryLength} caracteres.");
        }

        return errors;
    }
}
