using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Models;
using Trader.Core.Projection;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Projection;

public sealed class ProjectionRecordService(
    TraderDbContext dbContext,
    IMarketDataService marketData,
    TimeProvider timeProvider,
    ILogger<ProjectionRecordService> logger) : IProjectionRecordService
{
    private const int ListLimit = 100;

    public async Task<(ProjectionRecordDto? Record, IReadOnlyList<string> Errors)> RegisterAsync(RegisterProjectionRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<string> errors = ProjectionAccuracy.Validate(request, now);

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        Instrument? instrument = await dbContext.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Symbol == request.Symbol, cancellationToken);

        if (instrument is null)
        {
            return (null, ["O ativo não está na lista."]);
        }

        DateTimeOffset target = request.OriginTime.AddMinutes(request.HorizonMinutes);
        ProjectionRecord? existing = await dbContext.ProjectionRecords
            .FirstOrDefaultAsync(r => r.Symbol == request.Symbol && r.TargetTime == target && r.HorizonMinutes == request.HorizonMinutes, cancellationToken);

        if (existing is not null)
        {
            return (ProjectionAccuracy.ToDto(existing), []);
        }

        ProjectionRecord record = new()
        {
            Symbol = instrument.Symbol,
            Name = instrument.Name,
            CreatedAt = now,
            OriginTime = request.OriginTime,
            TargetTime = target,
            HorizonMinutes = request.HorizonMinutes,
            LastPrice = request.LastPrice,
            ProjectedMedian = request.ProjectedMedian,
            P10 = request.P10,
            P25 = request.P25,
            P75 = request.P75,
            P90 = request.P90,
            OneSigmaPercent = request.OneSigmaPercent,
            PlanDirection = request.PlanDirection,
            PlanStop = request.PlanStop,
            PlanTarget = request.PlanTarget,
            SuggestionId = request.SuggestionId
        };

        dbContext.ProjectionRecords.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (ProjectionAccuracy.ToDto(record), []);
    }

    public async Task<ProjectionRecordsView> ListAsync(CancellationToken cancellationToken)
    {
        List<ProjectionRecord> records = await dbContext.ProjectionRecords
            .OrderByDescending(r => r.CreatedAt)
            .Take(ListLimit)
            .ToListAsync(cancellationToken);

        await ResolveDueAsync(records, cancellationToken);

        List<ProjectionRecordDto> items = records.Select(ProjectionAccuracy.ToDto).ToList();

        return new ProjectionRecordsView(items, ProjectionAccuracy.Summarize(items));
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
        await dbContext.ProjectionRecords.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private async Task ResolveDueAsync(List<ProjectionRecord> records, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool changed = false;

        foreach (IGrouping<string, ProjectionRecord> group in records.Where(r => r.ResolvedAt is null && r.TargetTime <= now).GroupBy(r => r.Symbol))
        {
            IReadOnlyList<Candle> candles;

            try
            {
                candles = await marketData.GetCandlesAsync(group.Key, "5m", "1mo", cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Could not resolve projections for {Symbol}: {Message}", group.Key, ex.Message);
                continue;
            }

            foreach (ProjectionRecord record in group)
            {
                (double? price, bool done) = ProjectionAccuracy.Resolve(record, candles, now);

                if (done)
                {
                    record.ActualPrice = price;
                    record.ResolvedAt = now;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
