using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Agender;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Agender;

public sealed class AgenderSyncState
{
    private sealed record Entry(string? Fingerprint, DateTimeOffset? LastSuccess, string? LastError, int LastCount);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public string? Fingerprint(string userId) => _entries.TryGetValue(userId, out Entry? entry) ? entry.Fingerprint : null;

    public AgenderStatus Get(string userId, bool configured) =>
        _entries.TryGetValue(userId, out Entry? entry)
            ? new AgenderStatus(configured, entry.LastSuccess, entry.LastError, entry.LastCount)
            : new AgenderStatus(configured, null, null, 0);

    public void Success(string userId, string fingerprint, int count, DateTimeOffset now) =>
        _entries[userId] = new Entry(fingerprint, now, null, count);

    public void Failure(string userId, string error) =>
        _entries.AddOrUpdate(
            userId,
            _ => new Entry(null, null, error, 0),
            (_, previous) => previous with { LastError = error });

    public void Reset(string userId) => _entries.TryRemove(userId, out _);
}

public sealed class AgenderSyncService(
    TraderDbContext dbContext,
    IJournalService journal,
    IHttpClientFactory httpClientFactory,
    AgenderSyncState state,
    TimeProvider time,
    ILogger<AgenderSyncService> logger) : IAgenderSync
{
    public const string HttpClientName = "agender";
    public const string DefaultUrl = "http://localhost:5018";

    private const string KeyPrefix = "agender:";
    private const string SyncPath = "/api/integrations/trader/trades";
    private const string KeyHeader = "X-Trader-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record StoredSettings(string Url, string Key);

    public async Task<AgenderSettings> GetSettingsAsync(string userId, CancellationToken cancellationToken)
    {
        StoredSettings? stored = await LoadAsync(userId, cancellationToken);

        return stored is null ? new AgenderSettings(DefaultUrl, false) : new AgenderSettings(stored.Url, stored.Key.Length > 0);
    }

    public async Task<IReadOnlyList<string>> SaveSettingsAsync(string userId, SaveAgenderSettingsRequest request, CancellationToken cancellationToken)
    {
        StoredSettings? existing = await LoadAsync(userId, cancellationToken);
        IReadOnlyList<string> errors = AgenderSettingsValidator.Validate(request.Url, request.Key, existing is { Key.Length: > 0 });

        if (errors.Count > 0)
        {
            return errors;
        }

        string key = string.IsNullOrWhiteSpace(request.Key) ? existing!.Key : request.Key.Trim();
        string json = JsonSerializer.Serialize(new StoredSettings(AgenderSettingsValidator.NormalizeUrl(request.Url), key), JsonOptions);
        AppSetting? setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == KeyPrefix + userId, cancellationToken);

        if (setting is null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = KeyPrefix + userId, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        state.Reset(userId);
        return [];
    }

    public async Task RemoveAsync(string userId, CancellationToken cancellationToken)
    {
        await dbContext.AppSettings.Where(s => s.Key == KeyPrefix + userId).ExecuteDeleteAsync(cancellationToken);
        state.Reset(userId);
    }

    public AgenderStatus GetStatus(string userId) => state.Get(userId, dbContext.AppSettings.AsNoTracking().Any(s => s.Key == KeyPrefix + userId));

    public async Task<AgenderStatus> SyncNowAsync(string userId, CancellationToken cancellationToken)
    {
        StoredSettings? stored = await LoadAsync(userId, cancellationToken);

        if (stored is not null)
        {
            await SyncUserAsync(userId, stored, true, cancellationToken);
        }

        return GetStatus(userId);
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        List<string> userIds = await dbContext.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(KeyPrefix))
            .Select(s => s.Key.Substring(KeyPrefix.Length))
            .ToListAsync(cancellationToken);

        foreach (string userId in userIds)
        {
            if (await LoadAsync(userId, cancellationToken) is { } stored)
            {
                await SyncUserAsync(userId, stored, false, cancellationToken);
            }
        }
    }

    private async Task SyncUserAsync(string userId, StoredSettings settings, bool force, CancellationToken cancellationToken)
    {
        IReadOnlyList<JournalTradeDto> trades = await journal.ListAsync(userId, null, cancellationToken);
        AccountProfile account = await journal.GetAccountAsync(userId, cancellationToken);
        AgenderSyncPayload payload = AgenderPayload.Build(trades, account.Currency);
        string fingerprint = AgenderPayload.Fingerprint(payload);

        if (!force && state.Fingerprint(userId) == fingerprint)
        {
            return;
        }

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpRequestMessage request = new(HttpMethod.Put, settings.Url + SyncPath) { Content = JsonContent.Create(payload) };
            request.Headers.Add(KeyHeader, settings.Key);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                state.Success(userId, fingerprint, payload.Trades.Count, time.GetUtcNow());
                return;
            }

            state.Failure(userId, response.StatusCode == HttpStatusCode.Unauthorized
                ? "O Agender recusou a chave. Gera uma nova em Definições → Trader."
                : $"O Agender respondeu com o erro {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Agender sync failed: {Message}", ex.Message);
            state.Failure(userId, "Não foi possível contactar o Agender. Verifica se está a correr e se o endereço está certo.");
        }
    }

    private async Task<StoredSettings?> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        string? json = await dbContext.AppSettings.AsNoTracking().Where(s => s.Key == KeyPrefix + userId).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);

        return json is null ? null : JsonSerializer.Deserialize<StoredSettings>(json, JsonOptions);
    }
}
