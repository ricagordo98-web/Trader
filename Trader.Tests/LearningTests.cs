using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trader.Core.Learning;
using Trader.Infrastructure.Learning;
using Trader.Infrastructure.Persistence;

namespace Trader.Tests;

public class LearningRulesTests
{
    [Theory]
    [InlineData("Guia de RSI", "https://example.com/rsi", true)]
    [InlineData("Guia", "http://example.com", true)]
    [InlineData("Guia", "ftp://example.com", false)]
    [InlineData("Guia", "javascript:alert(1)", false)]
    [InlineData("Guia", "not a url", false)]
    [InlineData("  ", "https://example.com", false)]
    public void ValidateLink_TitleAndUrl_AcceptsOnlyHttpWithATitle(string title, string url, bool valid) =>
        LearningRules.ValidateLink(new AddLearningLinkRequest(title, url, null)).Any().Should().Be(!valid);

    [Fact]
    public void ValidateLink_CategoryTooLong_IsRejected() =>
        LearningRules.ValidateLink(new AddLearningLinkRequest("Guia", "https://example.com", new string('x', LearningRules.MaxCategoryLength + 1))).Should().ContainSingle();

    [Fact]
    public void LooksLikePdf_ChecksTheSignature()
    {
        LearningRules.LooksLikePdf("%PDF-1.7 rest"u8).Should().BeTrue();
        LearningRules.LooksLikePdf("<html>"u8).Should().BeFalse();
    }
}

public class LearningServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trader-learning-" + Guid.NewGuid().ToString("N"));
    private TraderDbContext _dbContext = default!;
    private LearningService _service = default!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbContext = new TraderDbContext(new DbContextOptionsBuilder<TraderDbContext>().UseSqlite(_connection).Options);
        await _dbContext.Database.EnsureCreatedAsync();
        _service = new LearningService(_dbContext, Options.Create(new LearningOptions { Directory = _directory }), TimeProvider.System, NullLogger<LearningService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static MemoryStream Pdf(string body = "hello") => new(Encoding.ASCII.GetBytes("%PDF-1.4\n" + body));

    [Fact]
    public async Task AddPdfAsync_ValidPdf_IsStoredOnDiskAndCanBeReadBack()
    {
        (LearningItemDto? item, IReadOnlyList<string> errors) = await _service.AddPdfAsync("RSI", "Técnica", "rsi.pdf", Pdf("conteudo"), CancellationToken.None);

        errors.Should().BeEmpty();
        item!.Kind.Should().Be(LearningKind.Pdf);
        item.Category.Should().Be("Técnica");
        Directory.GetFiles(_directory).Should().ContainSingle();

        (Stream Content, string FileName)? file = await _service.OpenPdfAsync(item.Id, CancellationToken.None);
        await using Stream content = file!.Value.Content;
        using StreamReader reader = new(content);
        (await reader.ReadToEndAsync()).Should().EndWith("conteudo");
        file.Value.FileName.Should().Be("rsi.pdf");
    }

    [Fact]
    public async Task AddPdfAsync_NotAPdf_IsRejectedAndLeavesNothingBehind()
    {
        using MemoryStream notPdf = new("<html></html>"u8.ToArray());

        (LearningItemDto? item, IReadOnlyList<string> errors) = await _service.AddPdfAsync("X", null, "x.pdf", notPdf, CancellationToken.None);

        item.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("PDF"));
        Directory.GetFiles(_directory).Should().BeEmpty();
        (await _service.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddPdfAsync_PathInTheFileName_KeepsOnlyTheName()
    {
        (LearningItemDto? item, _) = await _service.AddPdfAsync("X", null, @"..\..\evil.pdf", Pdf(), CancellationToken.None);

        item!.FileName.Should().Be("evil.pdf");
        Directory.GetFiles(_directory).Single().Should().StartWith(_directory);
    }

    [Fact]
    public async Task AddLinkAsync_ValidLink_IsListedNewestFirst()
    {
        await _service.AddLinkAsync(new AddLearningLinkRequest("Primeiro", "https://a.example", null), CancellationToken.None);
        await Task.Delay(5);
        await _service.AddLinkAsync(new AddLearningLinkRequest("Segundo", "https://b.example", " Fórum "), CancellationToken.None);

        IReadOnlyList<LearningItemDto> items = await _service.ListAsync(CancellationToken.None);

        items.Select(i => i.Title).Should().Equal("Segundo", "Primeiro");
        items[0].Category.Should().Be("Fórum");
    }

    [Fact]
    public async Task DeleteAsync_Pdf_RemovesTheRowAndTheFile()
    {
        (LearningItemDto? item, _) = await _service.AddPdfAsync("X", null, "x.pdf", Pdf(), CancellationToken.None);

        (await _service.DeleteAsync(item!.Id, CancellationToken.None)).Should().BeTrue();

        Directory.GetFiles(_directory).Should().BeEmpty();
        (await _service.OpenPdfAsync(item.Id, CancellationToken.None)).Should().BeNull();
        (await _service.DeleteAsync(item.Id, CancellationToken.None)).Should().BeFalse();
    }
}

public class LearningCatalogTests
{
    [Fact]
    public void Catalog_HasUniqueLessonsAndValidHttpsResources()
    {
        LearningCatalog.Lessons.Select(l => l.Id).Should().OnlyHaveUniqueItems();
        LearningCatalog.Lessons.Should().OnlyContain(l => l.Paragraphs.Count > 0 && l.Title.Length > 0);
        LearningCatalog.Resources.Select(r => r.Url).Should().OnlyHaveUniqueItems();
        LearningCatalog.Resources.Should().OnlyContain(r => r.Url.StartsWith("https://") && r.Title.Length <= LearningRules.MaxTitleLength && r.Category.Length <= LearningRules.MaxCategoryLength);
        LearningCatalog.Resources.Should().OnlyContain(r => LearningRules.ValidateLink(new AddLearningLinkRequest(r.Title, r.Url, r.Category)).Count == 0);
    }
}

public class TradingTermsTests
{
    [Fact]
    public void Glossary_HasUniqueCompleteBilingualTerms()
    {
        TradingTerms.Terms.Select(t => t.English).Should().OnlyHaveUniqueItems();
        TradingTerms.Terms.Should().OnlyContain(t =>
            t.English.Length > 0 && t.Portuguese.Length > 0 && t.DefinitionPt.Length > 10 && t.DefinitionEn.Length > 10 && t.Category.Length > 0);
    }

    [Theory]
    [InlineData("Margin call")]
    [InlineData("Call option")]
    [InlineData("Stock / Share")]
    [InlineData("Leverage")]
    [InlineData("Put option")]
    [InlineData("Short (sell short)")]
    public void Glossary_CoversTheTermsTheUserAskedAbout(string english) =>
        TradingTerms.Terms.Should().Contain(t => t.English == english);
}
