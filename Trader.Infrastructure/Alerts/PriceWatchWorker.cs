using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Alerts;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Core.TradingSystem;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Alerts;

public sealed class PriceWatchWorker(IServiceScopeFactory scopeFactory, ILogger<PriceWatchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);

            using PeriodicTimer timer = new(Interval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            TraderDbContext dbContext = scope.ServiceProvider.GetRequiredService<TraderDbContext>();

            List<JournalTrade> trades = await dbContext.JournalTrades
                .Where(t => t.Status == TradeStatus.Open && (t.StopLoss != null || t.TakeProfit != null))
                .ToListAsync(cancellationToken);

            List<JournalTrade> managed = await dbContext.JournalTrades
                .Where(t => t.Status == TradeStatus.Open && dbContext.TradeRecords.Any(r => r.TradeId == t.Id && (!r.BreakEvenAlerted || !r.PartialAlerted || !r.TimeAlerted)))
                .ToListAsync(cancellationToken);

            List<PriceAlert> priceAlerts = await dbContext.PriceAlerts
                .Where(a => a.TriggeredAt == null)
                .ToListAsync(cancellationToken);

            if (trades.Count == 0 && priceAlerts.Count == 0 && managed.Count == 0)
            {
                return;
            }

            IMarketDataService marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
            Dictionary<string, double?> prices = await LoadPricesAsync(marketData, trades.Concat(managed).Select(t => t.Symbol).Concat(priceAlerts.Select(a => a.Symbol)), cancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<Alert> raised = [];
            IJournalService journal = scope.ServiceProvider.GetRequiredService<IJournalService>();
            Dictionary<string, string> currencies = [];

            foreach (JournalTrade trade in trades)
            {
                if (prices.GetValueOrDefault(trade.Symbol) is not { } price || LevelChecker.Check(trade, price) is not { } level)
                {
                    continue;
                }

                double exitPrice = level == TradeLevel.StopLoss ? trade.StopLoss!.Value : trade.TakeProfit!.Value;

                if (level == TradeLevel.StopLoss)
                {
                    trade.StopLossAlerted = true;
                }
                else
                {
                    trade.TakeProfitAlerted = true;
                }

                LevelChecker.CloseAtLevel(trade, level, exitPrice, now);

                if (!currencies.TryGetValue(trade.UserId, out string? currency))
                {
                    currency = (await journal.GetAccountAsync(trade.UserId, cancellationToken)).Currency;
                    currencies[trade.UserId] = currency;
                }

                raised.Add(AlertFactory.ForTradeLevel(trade, level, exitPrice, now, currency, price));
            }

            await RaiseManagementAsync(dbContext, managed, prices, now, raised, cancellationToken);

            foreach (PriceAlert alert in priceAlerts)
            {
                if (prices.GetValueOrDefault(alert.Symbol) is { } price && LevelChecker.IsTriggered(alert, price))
                {
                    alert.TriggeredAt = now;
                    raised.Add(AlertFactory.ForPriceAlert(alert, price, now));
                }
            }

            if (raised.Count > 0)
            {
                await scope.ServiceProvider.GetRequiredService<IAlertService>().RaiseAsync(raised, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Price watch cycle failed");
        }
    }

    private static async Task RaiseManagementAsync(TraderDbContext dbContext, List<JournalTrade> managed, Dictionary<string, double?> prices, DateTimeOffset now, List<Alert> raised, CancellationToken cancellationToken)
    {
        if (managed.Count == 0)
        {
            return;
        }

        long[] ids = managed.Select(t => t.Id).ToArray();
        Dictionary<long, TradeRecord> records = await dbContext.TradeRecords.Where(r => ids.Contains(r.TradeId)).ToDictionaryAsync(r => r.TradeId, cancellationToken);

        foreach (JournalTrade trade in managed)
        {
            if (!records.TryGetValue(trade.Id, out TradeRecord? record) || prices.GetValueOrDefault(trade.Symbol) is not { } price)
            {
                continue;
            }

            double initialStop = trade.InitialStopLoss ?? trade.StopLoss ?? trade.EntryPrice;

            if (TradeManagement.Check(trade.Direction, trade.EntryPrice, initialStop, price, trade.EntryTime, now, record) is not { } action)
            {
                continue;
            }

            switch (action)
            {
                case ManagementAction.BreakEven:
                    record.BreakEvenAlerted = true;
                    break;
                case ManagementAction.Partial:
                    record.PartialAlerted = true;
                    break;
                default:
                    record.TimeAlerted = true;
                    break;
            }

            raised.Add(new Alert
            {
                UserId = trade.UserId,
                Kind = AlertKind.Rule,
                Symbol = trade.Symbol,
                Title = $"{trade.Symbol} · gestão da trade",
                Message = TradeManagement.Message(action, record),
                TradeId = trade.Id,
                CreatedAt = now
            });
        }
    }

    private async Task<Dictionary<string, double?>> LoadPricesAsync(IMarketDataService marketData, IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        Dictionary<string, double?> prices = [];

        foreach (string symbol in symbols.Distinct())
        {
            try
            {
                prices[symbol] = await marketData.GetLatestPriceAsync(symbol, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug("No price for {Symbol}: {Message}", symbol, ex.Message);
            }
        }

        return prices;
    }
}
