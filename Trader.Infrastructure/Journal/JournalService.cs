using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trader.Core.Abstractions;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Journal;

public sealed class JournalService(TraderDbContext dbContext) : IJournalService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AccountProfile DefaultAccount = new(10_000, 1, "EUR");

    public async Task<IReadOnlyList<JournalTradeDto>> ListAsync(string userId, TradeStatus? status, CancellationToken cancellationToken)
    {
        IQueryable<JournalTrade> query = dbContext.JournalTrades.AsNoTracking().Where(t => t.UserId == userId);

        if (status is { } filter)
        {
            query = query.Where(t => t.Status == filter);
        }

        List<JournalTrade> trades = await query.OrderByDescending(t => t.EntryTime).ToListAsync(cancellationToken);
        Dictionary<long, Suggestion> suggestions = await LoadSuggestionsAsync(trades, cancellationToken);

        return trades.Select(t => ToDto(t, suggestions)).ToList();
    }

    public async Task<JournalResult<JournalTradeDto>> OpenAsync(string userId, OpenTradeRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> errors = JournalValidator.ValidateOpen(request);

        if (errors.Count > 0)
        {
            return JournalResult<JournalTradeDto>.Invalid(errors);
        }

        string symbol = request.Symbol.Trim().ToUpperInvariant();

        if (request.SuggestionId is { } suggestionId && !await dbContext.Suggestions.AnyAsync(s => s.Id == suggestionId, cancellationToken))
        {
            return JournalResult<JournalTradeDto>.Invalid(["A sugestão indicada não existe."]);
        }

        string name = await dbContext.Instruments
            .Where(i => i.Symbol == symbol)
            .Select(i => i.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? symbol;

        JournalTrade trade = new()
        {
            UserId = userId,
            Symbol = symbol,
            Name = name,
            Direction = request.Direction,
            EntryTime = request.EntryTime,
            EntryPrice = request.EntryPrice,
            Quantity = request.Quantity,
            StopLoss = request.StopLoss,
            InitialStopLoss = request.StopLoss,
            TakeProfit = request.TakeProfit,
            FxRate = request.FxRate,
            Fees = request.Fees,
            Platform = request.Platform?.Trim() ?? string.Empty,
            Notes = request.Notes?.Trim() ?? string.Empty,
            SuggestionId = request.SuggestionId,
            Status = TradeStatus.Open
        };

        dbContext.JournalTrades.Add(trade);
        await dbContext.SaveChangesAsync(cancellationToken);

        return JournalResult<JournalTradeDto>.Ok(ToDto(trade, await LoadSuggestionsAsync([trade], cancellationToken)));
    }

    public async Task<JournalResult<JournalTradeDto>> CloseAsync(string userId, long id, CloseTradeRequest request, CancellationToken cancellationToken)
    {
        JournalTrade? trade = await dbContext.JournalTrades.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, cancellationToken);

        if (trade is null)
        {
            return JournalResult<JournalTradeDto>.Missing();
        }

        IReadOnlyList<string> errors = JournalValidator.ValidateClose(trade, request);

        if (errors.Count > 0)
        {
            return JournalResult<JournalTradeDto>.Invalid(errors);
        }

        trade.Status = TradeStatus.Closed;
        trade.ExitTime = request.ExitTime;
        trade.ExitPrice = request.ExitPrice;
        trade.Fees += request.AdditionalFees;

        if (request.Notes is not null)
        {
            trade.Notes = request.Notes.Trim();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return JournalResult<JournalTradeDto>.Ok(ToDto(trade, await LoadSuggestionsAsync([trade], cancellationToken)));
    }

    public async Task<ImportSummary> ImportAsync(string userId, IReadOnlyList<ImportedTrade> trades, CancellationToken cancellationToken)
    {
        List<(string Symbol, long Ticks, double Price, double Quantity)> existing = (await dbContext.JournalTrades
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .Select(t => new { t.Symbol, t.EntryTime, t.EntryPrice, t.Quantity })
            .ToListAsync(cancellationToken))
            .Select(t => (t.Symbol, t.EntryTime.UtcTicks, t.EntryPrice, t.Quantity))
            .ToList();

        int imported = 0;
        int skipped = 0;
        List<string> errors = [];

        foreach (ImportedTrade trade in trades)
        {
            string symbol = trade.Symbol.Trim().ToUpperInvariant();

            if (existing.Any(e => e.Symbol == symbol && Math.Abs(e.Ticks - trade.EntryTime.UtcTicks) < TimeSpan.TicksPerSecond && Math.Abs(e.Price - trade.EntryPrice) < 1e-9 && Math.Abs(e.Quantity - trade.Quantity) < 1e-9))
            {
                skipped++;
                continue;
            }

            JournalResult<JournalTradeDto> opened = await OpenAsync(
                userId,
                new OpenTradeRequest(symbol, trade.Direction, trade.EntryTime, trade.EntryPrice, trade.Quantity, trade.StopLoss, trade.TakeProfit, trade.FxRate, trade.Fees, trade.Platform, null, null),
                cancellationToken);

            if (!opened.Succeeded)
            {
                errors.Add($"{symbol} {trade.EntryTime:dd/MM/yy}: {string.Join(" ", opened.Errors)}");
                continue;
            }

            if (trade.ExitPrice is { } exitPrice && trade.ExitTime is { } exitTime)
            {
                JournalResult<JournalTradeDto> closed = await CloseAsync(userId, opened.Value!.Id, new CloseTradeRequest(exitTime, exitPrice, 0, null), cancellationToken);

                if (!closed.Succeeded)
                {
                    errors.Add($"{symbol} {exitTime:dd/MM/yy}: {string.Join(" ", closed.Errors)}");
                }
            }

            existing.Add((symbol, trade.EntryTime.UtcTicks, trade.EntryPrice, trade.Quantity));
            imported++;
        }

        return new ImportSummary(imported, skipped, errors);
    }

    public async Task<JournalResult<JournalTradeDto>> UpdateDetailsAsync(string userId, long id, TradeDetailsRequest request, CancellationToken cancellationToken)
    {
        JournalTrade? trade = await dbContext.JournalTrades.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, cancellationToken);

        if (trade is null)
        {
            return JournalResult<JournalTradeDto>.Missing();
        }

        IReadOnlyList<string> errors = JournalValidator.ValidateDetails(request);

        if (errors.Count > 0)
        {
            return JournalResult<JournalTradeDto>.Invalid(errors);
        }

        trade.Setup = request.Setup?.Trim() ?? string.Empty;
        trade.Tags = request.Tags?.Trim() ?? string.Empty;
        trade.Emotion = request.Emotion?.Trim() ?? string.Empty;

        if (request.Notes is not null)
        {
            trade.Notes = request.Notes.Trim();
        }

        if (request.ClearImage)
        {
            trade.ChartImage = null;
        }
        else if (request.ChartImage is not null)
        {
            trade.ChartImage = request.ChartImage;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return JournalResult<JournalTradeDto>.Ok(ToDto(trade, await LoadSuggestionsAsync([trade], cancellationToken)));
    }

    public async Task<JournalResult<JournalTradeDto>> UpdateLevelsAsync(string userId, long id, TradeLevelsRequest request, CancellationToken cancellationToken)
    {
        JournalTrade? trade = await dbContext.JournalTrades.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, cancellationToken);

        if (trade is null)
        {
            return JournalResult<JournalTradeDto>.Missing();
        }

        if (trade.Status != TradeStatus.Open)
        {
            return JournalResult<JournalTradeDto>.Invalid(["Só podes mudar o stop e o alvo de trades abertas."]);
        }

        if (request.StopLoss is <= 0 || request.TakeProfit is <= 0 || request.StopLoss is { } s && !double.IsFinite(s) || request.TakeProfit is { } t && !double.IsFinite(t))
        {
            return JournalResult<JournalTradeDto>.Invalid(["O stop e o alvo devem ser maiores que zero."]);
        }

        trade.InitialStopLoss ??= trade.StopLoss;

        if (trade.StopLoss != request.StopLoss)
        {
            trade.StopLoss = request.StopLoss;
            trade.StopLossAlerted = false;
        }

        if (trade.TakeProfit != request.TakeProfit)
        {
            trade.TakeProfit = request.TakeProfit;
            trade.TakeProfitAlerted = false;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return JournalResult<JournalTradeDto>.Ok(ToDto(trade, await LoadSuggestionsAsync([trade], cancellationToken)));
    }

    public async Task<string?> GetImageAsync(string userId, long id, CancellationToken cancellationToken) =>
        await dbContext.JournalTrades.AsNoTracking().Where(t => t.Id == id && t.UserId == userId).Select(t => t.ChartImage).FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> DeleteAsync(string userId, long id, CancellationToken cancellationToken) =>
        await dbContext.JournalTrades.Where(t => t.Id == id && t.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<JournalStats> GetStatsAsync(string userId, CancellationToken cancellationToken) =>
        JournalStatistics.Calculate(await ListAsync(userId, null, cancellationToken));

    public async Task<AccountProfile> GetAccountAsync(string userId, CancellationToken cancellationToken)
    {
        AppSetting? setting = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AccountKey(userId), cancellationToken);

        return setting is null
            ? DefaultAccount
            : JsonSerializer.Deserialize<AccountProfile>(setting.Value, JsonOptions) ?? DefaultAccount;
    }

    public async Task<IReadOnlyList<string>> SaveAccountAsync(string userId, AccountProfile profile, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> errors = AccountProfileValidator.Validate(profile);

        if (errors.Count > 0)
        {
            return errors;
        }

        string json = JsonSerializer.Serialize(profile with { Currency = profile.Currency.Trim().ToUpperInvariant() }, JsonOptions);
        AppSetting? setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == AccountKey(userId), cancellationToken);

        if (setting is null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = AccountKey(userId), Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return [];
    }

    private static string AccountKey(string userId) => $"account:{userId}";

    private async Task<Dictionary<long, Suggestion>> LoadSuggestionsAsync(IReadOnlyCollection<JournalTrade> trades, CancellationToken cancellationToken)
    {
        long[] ids = trades.Where(t => t.SuggestionId is not null).Select(t => t.SuggestionId!.Value).Distinct().ToArray();

        return ids.Length == 0
            ? []
            : await dbContext.Suggestions.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
    }

    private static JournalTradeDto ToDto(JournalTrade t, Dictionary<long, Suggestion> suggestions)
    {
        TradeOutcome outcome = JournalCalculator.Compute(t);
        Suggestion? suggestion = t.SuggestionId is { } id && suggestions.TryGetValue(id, out Suggestion? found) ? found : null;
        bool suggestionClosed = suggestion is not null && suggestion.Status != SuggestionStatus.Open;

        return new JournalTradeDto(
            t.Id,
            t.Symbol,
            t.Name,
            t.Direction,
            t.EntryTime,
            t.EntryPrice,
            t.Quantity,
            t.StopLoss,
            t.TakeProfit,
            t.FxRate,
            t.Fees,
            t.Platform,
            t.Notes,
            t.SuggestionId,
            t.Status,
            t.ExitTime,
            t.ExitPrice,
            outcome.RiskAmount,
            outcome.NetPnl,
            outcome.RMultiple,
            suggestionClosed ? suggestion!.Status : null,
            suggestionClosed ? suggestion!.RMultiple : null,
            t.Setup,
            t.Tags,
            t.Emotion,
            t.ChartImage is not null,
            t.InitialStopLoss);
    }
}
