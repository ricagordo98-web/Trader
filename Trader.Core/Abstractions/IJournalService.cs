using Trader.Core.Journal;

namespace Trader.Core.Abstractions;

public sealed record JournalResult<T>(T? Value, IReadOnlyList<string> Errors, bool NotFound)
{
    public bool Succeeded => Value is not null && Errors.Count == 0 && !NotFound;

    public static JournalResult<T> Ok(T value) => new(value, [], false);

    public static JournalResult<T> Invalid(IReadOnlyList<string> errors) => new(default, errors, false);

    public static JournalResult<T> Missing() => new(default, [], true);
}

public interface IJournalService
{
    Task<IReadOnlyList<JournalTradeDto>> ListAsync(string userId, TradeStatus? status, CancellationToken cancellationToken);

    Task<JournalResult<JournalTradeDto>> OpenAsync(string userId, OpenTradeRequest request, CancellationToken cancellationToken);

    Task<JournalResult<JournalTradeDto>> CloseAsync(string userId, long id, CloseTradeRequest request, CancellationToken cancellationToken);

    Task<JournalResult<JournalTradeDto>> UpdateDetailsAsync(string userId, long id, TradeDetailsRequest request, CancellationToken cancellationToken);

    Task<JournalResult<JournalTradeDto>> UpdateLevelsAsync(string userId, long id, TradeLevelsRequest request, CancellationToken cancellationToken);

    Task<string?> GetImageAsync(string userId, long id, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string userId, long id, CancellationToken cancellationToken);

    Task<JournalStats> GetStatsAsync(string userId, CancellationToken cancellationToken);

    Task<AccountProfile> GetAccountAsync(string userId, CancellationToken cancellationToken);

    Task<ImportSummary> ImportAsync(string userId, IReadOnlyList<ImportedTrade> trades, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> SaveAccountAsync(string userId, AccountProfile profile, CancellationToken cancellationToken);
}
