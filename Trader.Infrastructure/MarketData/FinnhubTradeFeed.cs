using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trader.Core.Abstractions;
using Trader.Infrastructure.News.Sources;

namespace Trader.Infrastructure.MarketData;

public sealed class FinnhubTradeFeed(IOptions<SourcesOptions> options, ILogger<FinnhubTradeFeed> logger) : BackgroundService, ILiveTradeFeed
{
    private const int MaxSymbols = 45;
    private const int MaxTradesPerSymbol = 20_000;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(3);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    private DateTimeOffset _lastMessage;

    private sealed class SymbolState
    {
        public object Gate { get; } = new();

        public List<LiveTrade> Trades { get; } = [];

        public DateTimeOffset LastRequested { get; set; }

        public bool Subscribed { get; set; }
    }

    private readonly ConcurrentDictionary<string, SymbolState> _symbols = new();
    private readonly string _apiKey = options.Value.Finnhub.ApiKey;

    public bool Track(string symbol)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || FinnhubSymbolMapper.ToFinnhub(symbol) is not { } mapped)
        {
            return false;
        }

        if (!_symbols.ContainsKey(mapped) && _symbols.Count >= MaxSymbols)
        {
            return false;
        }

        _symbols.GetOrAdd(mapped, _ => new SymbolState()).LastRequested = DateTimeOffset.UtcNow;
        return true;
    }

    public IReadOnlyList<LiveTrade> GetTrades(string symbol, DateTimeOffset since)
    {
        if (FinnhubSymbolMapper.ToFinnhub(symbol) is not { } mapped || !_symbols.TryGetValue(mapped, out SymbolState? state))
        {
            return [];
        }

        lock (state.Gate)
        {
            return state.Trades.Where(t => t.Time >= since).ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return;
        }

        TimeSpan backoff = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
            {
                logger.LogWarning("Finnhub trade feed disconnected: {Message}", ex.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (SymbolState state in _symbols.Values)
            {
                state.Subscribed = false;
            }

            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        await socket.ConnectAsync(new Uri($"wss://ws.finnhub.io?token={Uri.EscapeDataString(_apiKey)}"), cancellationToken);
        logger.LogInformation("Finnhub trade feed connected");
        _lastMessage = DateTimeOffset.UtcNow;

        Task receiving = ReceiveAsync(socket, cancellationToken);

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open && !receiving.IsCompleted)
        {
            if (DateTimeOffset.UtcNow - _lastMessage > SilenceLimit && _symbols.Values.Any(s => s.Subscribed))
            {
                logger.LogWarning("Finnhub trade feed silent for {Seconds}s, reconnecting", SilenceLimit.TotalSeconds);
                socket.Abort();
                break;
            }

            await SyncSubscriptionsAsync(socket, cancellationToken);
            await Task.WhenAny(receiving, Task.Delay(1000, cancellationToken));
        }

        try
        {
            await receiving;
        }
        catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SyncSubscriptionsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach ((string symbol, SymbolState state) in _symbols)
        {
            bool idle = now - state.LastRequested > IdleAfter;

            if (!state.Subscribed && !idle)
            {
                await SendAsync(socket, "subscribe", symbol, cancellationToken);
                state.Subscribed = true;
            }
            else if (state.Subscribed && idle)
            {
                await SendAsync(socket, "unsubscribe", symbol, cancellationToken);
                state.Subscribed = false;

                lock (state.Gate)
                {
                    state.Trades.Clear();
                }
            }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string type, string symbol, CancellationToken cancellationToken) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, symbol })),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            _lastMessage = DateTimeOffset.UtcNow;
            message.Write(buffer, 0, result.Count);

            if (!result.EndOfMessage)
            {
                continue;
            }

            try
            {
                Handle(message.ToArray());
            }
            catch (JsonException ex)
            {
                logger.LogDebug("Ignoring malformed Finnhub message: {Message}", ex.Message);
            }

            message.SetLength(0);
        }
    }

    internal void Handle(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "trade" || !root.TryGetProperty("data", out JsonElement data))
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - Retention;

        foreach (JsonElement trade in data.EnumerateArray())
        {
            string? symbol = trade.GetProperty("s").GetString();

            if (symbol is null || !_symbols.TryGetValue(symbol, out SymbolState? state))
            {
                continue;
            }

            LiveTrade entry = new(
                DateTimeOffset.FromUnixTimeMilliseconds(trade.GetProperty("t").GetInt64()),
                trade.GetProperty("p").GetDouble(),
                trade.TryGetProperty("v", out JsonElement volume) ? volume.GetDouble() : 0);

            lock (state.Gate)
            {
                state.Trades.Add(entry);

                if (state.Trades.Count > MaxTradesPerSymbol)
                {
                    state.Trades.RemoveRange(0, state.Trades.Count - MaxTradesPerSymbol);
                }

                if (state.Trades.Count > 0 && state.Trades[0].Time < cutoff)
                {
                    state.Trades.RemoveAll(t => t.Time < cutoff);
                }
            }
        }
    }
}
