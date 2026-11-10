using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trader.Core.Abstractions;
using Trader.Core.Models;
using Trader.Core.Projection;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Projection;

public sealed class ProjectionService(TraderDbContext dbContext, IMarketDataService marketData, TimeProvider timeProvider) : IProjectionService
{
    private const string Interval = "5m";
    private const string Range = "1mo";
    private const int HistoryBars = 120;
    private static readonly TimeSpan OpenTolerance = TimeSpan.FromMinutes(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PriceProjection?> ProjectAsync(string symbol, int horizonMinutes, CancellationToken cancellationToken)
    {
        Instrument? instrument = await dbContext.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Symbol == symbol, cancellationToken);

        if (instrument is null)
        {
            return null;
        }

        List<Candle> candles = (await marketData.GetCandlesAsync(symbol, Interval, Range, cancellationToken))
            .Where(c => c.Close > 0 && double.IsFinite(c.Close) && c.High >= c.Low)
            .OrderBy(c => c.Time)
            .ToList();

        ProjectionResult? result = ProjectionEngine.Project(candles, horizonMinutes);

        if (result is null)
        {
            return null;
        }

        ProjectionCalibration? calibration = ProjectionCalibrator.Evaluate(candles, horizonMinutes);

        if (!ProjectionCalibrator.LearnedFactorsHelp(calibration))
        {
            result = ProjectionEngine.Project(candles, horizonMinutes, ProjectionEngine.DefaultSimulations, 0) ?? result;
            calibration = ProjectionCalibrator.Evaluate(candles, horizonMinutes, 0);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TimeSpan age = now - candles[^1].Time;
        bool open = age <= OpenTolerance;
        (long? trackedId, bool already) = open && result.Plan is { } plan
            ? await TrackAsync(instrument, plan, result, now, cancellationToken)
            : (null, false);

        return new PriceProjection(
            instrument.Symbol,
            instrument.Name,
            instrument.Currency,
            Interval,
            candles.TakeLast(HistoryBars).ToList(),
            result,
            calibration,
            open,
            Warnings(open, age, candles.Count),
            now,
            trackedId,
            already);
    }

    private async Task<(long? Id, bool Already)> TrackAsync(Instrument instrument, ProjectionPlan plan, ProjectionResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        long? existing = await dbContext.Suggestions
            .Where(s => s.Variant == SuggestionVariants.Projection && s.Symbol == instrument.Symbol && s.Status == SuggestionStatus.Open)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return (existing, true);
        }

        Suggestion suggestion = new()
        {
            Symbol = instrument.Symbol,
            Name = instrument.Name,
            Direction = plan.Direction,
            Confidence = plan.ProbabilityWin,
            Score = plan.ExpectedR,
            Entry = plan.Entry,
            StopLoss = plan.StopLoss,
            TakeProfit = plan.TakeProfit,
            RiskReward = plan.RiskReward,
            Explanation = $"Projeção a {result.HorizonMinutes} min: {(plan.Direction == TradeDirection.Buy ? "compra" : "venda")} com alvo e stop a 0,67 desvios. Simulação: {plan.ProbabilityTarget * 100:0}% alvo, {plan.ProbabilityStop * 100:0}% stop, {plan.ProbabilityExpire * 100:0}% expira.",
            EvidenceJson = JsonSerializer.Serialize(new SuggestionEvidence([], []), JsonOptions),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(result.HorizonMinutes),
            Status = SuggestionStatus.Open,
            Horizon = SuggestionHorizon.Intraday,
            IsShadow = true,
            ShadowReason = "projeção (alvos do gráfico)",
            Variant = SuggestionVariants.Projection
        };

        dbContext.Suggestions.Add(suggestion);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (suggestion.Id, false);
    }

    private static List<string> Warnings(bool open, TimeSpan age, int bars)
    {
        List<string> warnings = [];

        if (!open)
        {
            string ago = age.TotalHours >= 1 ? $"{age.TotalHours.ToString("0.#", CultureInfo.GetCultureInfo("pt-PT"))} h" : $"{age.TotalMinutes:0} min";
            warnings.Add($"A última vela tem {ago}: o mercado parece fechado ou os dados têm atraso. A projeção continua a partir desse último preço e não inclui o salto da próxima abertura.");
        }

        if (bars < ProjectionEngine.MinBars * 2)
        {
            warnings.Add("Há poucas velas de 5 minutos guardadas para este ativo; a estimativa da volatilidade por hora do dia é menos fiável.");
        }

        return warnings;
    }
}
