using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Trader.Core.Abstractions;
using Trader.Core.Agender;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Infrastructure.Agender;
using Trader.Infrastructure.Persistence;

namespace Trader.Tests;

public class AgenderPayloadTests
{
    private static JournalTradeDto Trade(long id = 1, TradeStatus status = TradeStatus.Closed, TradeDirection direction = TradeDirection.Buy) => new(
        id, "AAPL", "Apple", direction, DateTimeOffset.Parse("2026-11-02T09:00:00Z"), 100, 10, 95, 110, 0.9, 1, "T212", "nota", null,
        status, status == TradeStatus.Closed ? DateTimeOffset.Parse("2026-11-02T10:00:00Z") : null, status == TradeStatus.Closed ? 105 : null,
        45, status == TradeStatus.Closed ? 44 : null, status == TradeStatus.Closed ? 0.98 : null, null, null, "breakout", "a,b", "calmo");

    [Fact]
    public void From_ClosedBuy_MapsFlagsAndComputesTheInvestedAmountInAccountCurrency()
    {
        AgenderTradePayload payload = AgenderPayload.From(Trade(), "EUR");

        payload.IsBuy.Should().BeTrue();
        payload.IsClosed.Should().BeTrue();
        payload.Invested.Should().BeApproximately(100 * 10 * 0.9, 1e-9);
        payload.NetPnl.Should().Be(44);
        payload.Currency.Should().Be("EUR");
    }

    [Fact]
    public void From_OpenSell_IsNotClosedAndHasNoResult()
    {
        AgenderTradePayload payload = AgenderPayload.From(Trade(status: TradeStatus.Open, direction: TradeDirection.Sell), "EUR");

        payload.IsBuy.Should().BeFalse();
        payload.IsClosed.Should().BeFalse();
        payload.NetPnl.Should().BeNull();
    }

    [Fact]
    public void Fingerprint_SameTradesInAnyOrder_IsStable() =>
        AgenderPayload.Fingerprint(AgenderPayload.Build([Trade(1), Trade(2)], "EUR"))
            .Should().Be(AgenderPayload.Fingerprint(AgenderPayload.Build([Trade(2), Trade(1)], "EUR")));

    [Fact]
    public void Fingerprint_ChangedTrade_Differs() =>
        AgenderPayload.Fingerprint(AgenderPayload.Build([Trade(1)], "EUR"))
            .Should().NotBe(AgenderPayload.Fingerprint(AgenderPayload.Build([Trade(1, TradeStatus.Open)], "EUR")));

    [Theory]
    [InlineData("http://localhost:5018", "agt_key", false, 0)]
    [InlineData("ftp://localhost", "agt_key", false, 1)]
    [InlineData("not a url", "agt_key", false, 1)]
    [InlineData("http://localhost:5018", "", false, 1)]
    [InlineData("http://localhost:5018", "", true, 0)]
    public void Validate_ChecksTheAddressAndTheKey(string url, string key, bool hasKey, int errors) =>
        AgenderSettingsValidator.Validate(url, key, hasKey).Should().HaveCount(errors);
}

public sealed class AgenderSyncServiceTests : IAsyncLifetime
{
    private const string User = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly StubHandler _handler = new();
    private readonly IJournalService _journal = Substitute.For<IJournalService>();
    private readonly AgenderSyncState _state = new();
    private TraderDbContext _dbContext = null!;
    private AgenderSyncService _sut = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbContext = new TraderDbContext(new DbContextOptionsBuilder<TraderDbContext>().UseSqlite(_connection).Options);
        await _dbContext.Database.EnsureCreatedAsync();

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AgenderSyncService.HttpClientName).Returns(_ => new HttpClient(_handler, false));
        _journal.GetAccountAsync(User, Arg.Any<CancellationToken>()).Returns(new AccountProfile(10_000, 1, "EUR"));
        _journal.ListAsync(User, null, Arg.Any<CancellationToken>()).Returns([Trade(1)]);

        _sut = new AgenderSyncService(_dbContext, _journal, factory, _state, TimeProvider.System, NullLogger<AgenderSyncService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static JournalTradeDto Trade(long id) => new(
        id, "AAPL", "Apple", TradeDirection.Buy, DateTimeOffset.Parse("2026-11-02T09:00:00Z"), 100, 10, 95, 110, 1, 1, "T212", string.Empty, null,
        TradeStatus.Open, null, null, 50, null, null, null, null);

    private Task<IReadOnlyList<string>> SaveAsync(string url = "http://localhost:5018/", string? key = "agt_secret") =>
        _sut.SaveSettingsAsync(User, new SaveAgenderSettingsRequest(url, key), CancellationToken.None);

    [Fact]
    public async Task SaveSettings_Valid_NormalizesTheUrlAndNeverReturnsTheKey()
    {
        (await SaveAsync()).Should().BeEmpty();

        AgenderSettings settings = await _sut.GetSettingsAsync(User, CancellationToken.None);
        settings.Should().Be(new AgenderSettings("http://localhost:5018", true));
    }

    [Fact]
    public async Task SaveSettings_WithoutKeyAfterOneWasSaved_KeepsTheExistingKey()
    {
        await SaveAsync();

        (await SaveAsync("http://localhost:6000", null)).Should().BeEmpty();
        await _sut.SyncNowAsync(User, CancellationToken.None);

        _handler.Requests.Single().Key.Should().Be("agt_secret");
        _handler.Requests.Single().Uri.Should().Be("http://localhost:6000/api/integrations/trader/trades");
    }

    [Fact]
    public async Task SyncNow_SendsTheTradesWithTheKeyHeaderAndRecordsSuccess()
    {
        await SaveAsync();

        AgenderStatus status = await _sut.SyncNowAsync(User, CancellationToken.None);

        status.Should().Match<AgenderStatus>(s => s.Configured && s.LastError == null && s.LastCount == 1 && s.LastSuccess != null);
        _handler.Requests.Single().Key.Should().Be("agt_secret");
        _handler.Requests.Single().Body.Should().Contain("\"symbol\":\"AAPL\"");
    }

    [Fact]
    public async Task SyncAll_NothingChangedSinceTheLastSuccess_DoesNotSendAgain()
    {
        await SaveAsync();
        await _sut.SyncAllAsync(CancellationToken.None);

        await _sut.SyncAllAsync(CancellationToken.None);

        _handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncAll_AfterATradeChanges_SendsAgain()
    {
        await SaveAsync();
        await _sut.SyncAllAsync(CancellationToken.None);
        _journal.ListAsync(User, null, Arg.Any<CancellationToken>()).Returns([Trade(1), Trade(2)]);

        await _sut.SyncAllAsync(CancellationToken.None);

        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SyncNow_Unauthorized_ReportsAnErrorAndRetriesLater()
    {
        await SaveAsync();
        _handler.Status = HttpStatusCode.Unauthorized;

        AgenderStatus status = await _sut.SyncNowAsync(User, CancellationToken.None);
        status.LastError.Should().Contain("chave");

        _handler.Status = HttpStatusCode.OK;
        await _sut.SyncAllAsync(CancellationToken.None);
        _sut.GetStatus(User).LastError.Should().BeNull();
    }

    [Fact]
    public async Task SyncNow_AgenderUnreachable_ReportsAnErrorWithoutThrowing()
    {
        await SaveAsync();
        _handler.Throw = true;

        AgenderStatus status = await _sut.SyncNowAsync(User, CancellationToken.None);

        status.LastError.Should().Contain("contactar");
    }

    [Fact]
    public async Task SyncAll_UserWithoutSettings_SendsNothing()
    {
        await _sut.SyncAllAsync(CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_StopsSyncingAndClearsTheStatus()
    {
        await SaveAsync();
        await _sut.RemoveAsync(User, CancellationToken.None);

        await _sut.SyncAllAsync(CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
        _sut.GetStatus(User).Configured.Should().BeFalse();
    }

    private sealed record Captured(string Uri, string? Key, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new HttpRequestException("connection refused");
            }

            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(request.RequestUri!.ToString(), request.Headers.TryGetValues("X-Trader-Key", out IEnumerable<string>? values) ? values.Single() : null, body));

            return new HttpResponseMessage(Status) { Content = JsonContent.Create(new { }) };
        }
    }
}
