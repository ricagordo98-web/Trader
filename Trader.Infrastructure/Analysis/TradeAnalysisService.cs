using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Analysis;
using Trader.Core.Calendar;
using Trader.Core.Models;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Analysis;

public sealed class TradeAnalysisService(
    TraderDbContext dbContext,
    ISuggestionService suggestions,
    ICalendarService calendar,
    TimeProvider timeProvider,
    ILogger<TradeAnalysisService> logger) : ITradeAnalysisService
{
    private static readonly TimeSpan EventWindow = TimeSpan.FromHours(48);

    public async Task<TradeAnalysis?> AnalyzeAsync(TradeAnalysisRequest request, CancellationToken cancellationToken)
    {
        TradeContext? context = await suggestions.GetContextAsync(request.Symbol, cancellationToken);

        if (context is null)
        {
            return null;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AnalysisHistory? history = await GetHistoryAsync(context.AssetClass, request.Direction, cancellationToken);
        IReadOnlyList<string> events = await GetUpcomingEventsAsync(now, cancellationToken);

        return TradeAnalyzer.Analyze(request, context, history, events, now);
    }

    private async Task<AnalysisHistory?> GetHistoryAsync(AssetClass assetClass, TradeDirection direction, CancellationToken cancellationToken)
    {
        List<double> results = await (
            from suggestion in dbContext.Suggestions.AsNoTracking()
            join instrument in dbContext.Instruments.AsNoTracking() on suggestion.Symbol equals instrument.Symbol
            where instrument.AssetClass == assetClass
                && suggestion.Direction == direction
                && suggestion.Horizon == SuggestionHorizon.Swing
                && suggestion.Variant == "A"
                && suggestion.Status != SuggestionStatus.Open
                && suggestion.RMultiple != null
            select suggestion.RMultiple!.Value)
            .ToListAsync(cancellationToken);

        if (results.Count == 0)
        {
            return null;
        }

        return new AnalysisHistory(results.Count, results.Count(r => r > 0) / (double)results.Count, results.Average(), AssetLabel(assetClass));
    }

    private async Task<IReadOnlyList<string>> GetUpcomingEventsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            CalendarFeed? feed = await calendar.GetAsync(2, cancellationToken);

            return (feed?.Events ?? [])
                .Where(e => e.Kind == CalendarKind.Macro && MacroKnowledge.Find(e.Title)?.Importance == EventImportance.High)
                .Select(e => (Event: e, Time: CalendarTimes.TimeFor(e)))
                .Where(x => x.Time is { } time && time >= now && time - now <= EventWindow)
                .Select(x => x.Event.Title)
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not load calendar events for the trade analysis: {Message}", ex.Message);
            return [];
        }
    }

    private static string AssetLabel(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Stock => "ações",
        AssetClass.Etf => "ETFs",
        AssetClass.Commodity => "matérias-primas",
        AssetClass.Forex => "forex",
        AssetClass.Crypto => "cripto",
        _ => "índices"
    };
}
