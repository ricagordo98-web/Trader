using Trader.Core.Models;

namespace Trader.Core.Projection;

public static class ProjectionCalibrator
{
    public const int MaxOrigins = 40;
    public const int CalibrationSimulations = 300;
    public const double MinDirectionalMovePercent = 0.01;

    public static ProjectionCalibration? Evaluate(IReadOnlyList<Candle> candles, int horizonMinutes)
    {
        int steps = horizonMinutes / ProjectionEngine.StepMinutes;
        int stride = Math.Max(steps, 6);
        int covered80 = 0;
        int covered50 = 0;
        int origins = 0;
        int directional = 0;
        int directionHits = 0;

        for (int origin = candles.Count - 1 - steps; origin >= ProjectionEngine.MinBars && origins < MaxOrigins; origin -= stride)
        {
            Candle target = candles[origin + steps];
            double elapsed = (target.Time - candles[origin].Time).TotalMinutes;

            if (elapsed > horizonMinutes * 1.5)
            {
                continue;
            }

            ProjectionResult? result = ProjectionEngine.Project(candles.Take(origin + 1).ToList(), horizonMinutes, CalibrationSimulations);

            if (result is null)
            {
                continue;
            }

            ProjectionBand end = result.Bands[^1];
            origins++;
            covered80 += target.Close >= end.P10 && target.Close <= end.P90 ? 1 : 0;
            covered50 += target.Close >= end.P25 && target.Close <= end.P75 ? 1 : 0;

            if (Math.Abs(result.ExpectedMovePercent) >= MinDirectionalMovePercent && target.Close != result.LastPrice)
            {
                directional++;
                directionHits += Math.Sign(result.ExpectedMovePercent) == Math.Sign(target.Close - result.LastPrice) ? 1 : 0;
            }
        }

        return origins == 0
            ? null
            : new ProjectionCalibration(origins, covered80 / (double)origins, covered50 / (double)origins, directional > 0 ? directionHits / (double)directional : null, directional);
    }
}
