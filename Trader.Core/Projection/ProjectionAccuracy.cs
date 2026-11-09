using Trader.Core.Models;

namespace Trader.Core.Projection;

public static class ProjectionAccuracy
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Unavailable = "unavailable";

    private static readonly TimeSpan MaxGapToTarget = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    public static IReadOnlyList<string> Validate(RegisterProjectionRequest request, DateTimeOffset now)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(request.Symbol) || request.Symbol.Length > 32)
        {
            errors.Add("Indica o ativo.");
        }

        if (request.HorizonMinutes is < 15 or > 480 || request.HorizonMinutes % ProjectionEngine.StepMinutes != 0)
        {
            errors.Add("O horizonte da projeção é inválido.");
        }

        double[] prices = [request.LastPrice, request.ProjectedMedian, request.P10, request.P25, request.P75, request.P90];

        if (prices.Any(p => p <= 0 || !double.IsFinite(p)) || request.OneSigmaPercent <= 0 || !double.IsFinite(request.OneSigmaPercent))
        {
            errors.Add("Os preços da projeção têm de ser positivos.");
        }
        else if (!(request.P10 <= request.P25 && request.P25 <= request.ProjectedMedian && request.ProjectedMedian <= request.P75 && request.P75 <= request.P90))
        {
            errors.Add("As bandas da projeção estão fora de ordem.");
        }

        if ((request.PlanDirection is null) != (request.PlanStop is null) || (request.PlanDirection is null) != (request.PlanTarget is null))
        {
            errors.Add("O plano da projeção está incompleto.");
        }

        if (request.OriginTime.AddMinutes(request.HorizonMinutes) <= now)
        {
            errors.Add("Esta projeção já passou o horizonte (o mercado parece fechado). Só dá para registar projeções com o mercado aberto.");
        }

        return errors;
    }

    public static (double? Price, bool Done) Resolve(ProjectionRecord record, IReadOnlyList<Candle> candles, DateTimeOffset now)
    {
        if (now < record.TargetTime)
        {
            return (null, false);
        }

        Candle? atTarget = candles
            .Where(c => c.Time <= record.TargetTime && record.TargetTime - c.Time <= MaxGapToTarget && c.Close > 0)
            .OrderByDescending(c => c.Time)
            .FirstOrDefault();

        if (atTarget is not null)
        {
            return (atTarget.Close, true);
        }

        return now - record.TargetTime > GiveUpAfter ? (null, true) : (null, false);
    }

    public static ProjectionRecordDto ToDto(ProjectionRecord record)
    {
        string state = record.ResolvedAt is null ? Pending : record.ActualPrice is null ? Unavailable : Resolved;
        double? actual = record.ActualPrice;
        double? errorAbsolute = null;
        double? errorPercent = null;
        double? errorSigmas = null;
        string? band = null;
        double? targetDistance = null;
        double? targetDistancePercent = null;

        if (actual is { } price)
        {
            errorAbsolute = price - record.ProjectedMedian;
            errorPercent = errorAbsolute / record.ProjectedMedian * 100;
            errorSigmas = Math.Log(price / record.ProjectedMedian) / (record.OneSigmaPercent / 100);
            band = price >= record.P25 && price <= record.P75 ? "50" : price >= record.P10 && price <= record.P90 ? "80" : "out";

            if (record.PlanTarget is { } target)
            {
                targetDistance = price - target;
                targetDistancePercent = targetDistance / record.LastPrice * 100;
            }
        }

        return new ProjectionRecordDto(
            record.Id,
            record.Symbol,
            record.Name,
            record.CreatedAt,
            record.TargetTime,
            record.HorizonMinutes,
            record.LastPrice,
            record.ProjectedMedian,
            record.P10,
            record.P90,
            record.PlanDirection,
            record.PlanTarget,
            record.PlanStop,
            record.SuggestionId,
            state,
            actual,
            errorAbsolute,
            errorPercent,
            errorSigmas,
            band,
            targetDistance,
            targetDistancePercent);
    }

    public static ProjectionAccuracySummary? Summarize(IReadOnlyList<ProjectionRecordDto> items)
    {
        List<ProjectionRecordDto> done = items.Where(i => i.ErrorPercent is not null).ToList();

        return done.Count == 0
            ? null
            : new ProjectionAccuracySummary(
                done.Count,
                done.Average(i => i.ErrorPercent!.Value),
                done.Average(i => Math.Abs(i.ErrorPercent!.Value)),
                done.Average(i => i.ErrorSigmas!.Value),
                done.Count(i => i.Band == "50") / (double)done.Count,
                done.Count(i => i.Band is "50" or "80") / (double)done.Count);
    }
}
