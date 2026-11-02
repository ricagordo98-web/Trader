using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trader.Core.Journal;
using Trader.Core.Models;

namespace Trader.Core.Agender;

public sealed record AgenderSettings(string Url, bool HasKey);

public sealed record SaveAgenderSettingsRequest(string Url, string? Key);

public sealed record AgenderStatus(bool Configured, DateTimeOffset? LastSuccess, string? LastError, int LastCount);

public sealed record AgenderView(AgenderSettings Settings, AgenderStatus Status);

public sealed record AgenderTradePayload(
    long ExternalId,
    string Symbol,
    string Name,
    bool IsBuy,
    bool IsClosed,
    DateTimeOffset EntryTime,
    double EntryPrice,
    double Quantity,
    double? StopLoss,
    double? TakeProfit,
    DateTimeOffset? ExitTime,
    double? ExitPrice,
    double Fees,
    double Invested,
    double? RiskAmount,
    double? NetPnl,
    double? RMultiple,
    string Currency,
    string Platform,
    string Notes,
    string Setup,
    string Tags,
    string Emotion);

public sealed record AgenderSyncPayload(IReadOnlyList<AgenderTradePayload> Trades);

public static class AgenderPayload
{
    public static AgenderTradePayload From(JournalTradeDto trade, string currency) => new(
        trade.Id,
        trade.Symbol,
        trade.Name,
        trade.Direction == TradeDirection.Buy,
        trade.Status == TradeStatus.Closed,
        trade.EntryTime,
        trade.EntryPrice,
        trade.Quantity,
        trade.StopLoss,
        trade.TakeProfit,
        trade.ExitTime,
        trade.ExitPrice,
        trade.Fees,
        trade.EntryPrice * trade.Quantity * trade.FxRate,
        trade.RiskAmount,
        trade.NetPnl,
        trade.RMultiple,
        currency,
        trade.Platform,
        trade.Notes,
        trade.Setup,
        trade.Tags,
        trade.Emotion);

    public static AgenderSyncPayload Build(IEnumerable<JournalTradeDto> trades, string currency) =>
        new([.. trades.OrderBy(t => t.Id).Select(t => From(t, currency))]);

    public static string Fingerprint(AgenderSyncPayload payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}

public static class AgenderSettingsValidator
{
    public const int MaxKeyLength = 200;

    public static IReadOnlyList<string> Validate(string url, string? key, bool hasExistingKey)
    {
        List<string> errors = [];

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            errors.Add("O endereço do Agender tem de começar por http:// ou https://.");
        }

        if (string.IsNullOrWhiteSpace(key) && !hasExistingKey)
        {
            errors.Add("Cola a chave gerada no Agender (Definições → Trader).");
        }

        if ((key?.Trim().Length ?? 0) > MaxKeyLength)
        {
            errors.Add("A chave é demasiado longa.");
        }

        return errors;
    }

    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');
}
