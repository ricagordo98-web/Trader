namespace Trader.Core.Scoring;

public sealed record CalibrationPoint(double Confidence, double Probability);

public sealed record ConfidenceCalibration(IReadOnlyList<CalibrationPoint> Points, int Samples, double BaseRate)
{
    public const double FlatSpread = 0.06;

    public bool IsFlat => Points.Count == 0 || Points.Max(p => p.Probability) - Points.Min(p => p.Probability) < FlatSpread;

    public double Apply(double confidence)
    {
        if (Points.Count == 0)
        {
            return BaseRate;
        }

        if (confidence <= Points[0].Confidence)
        {
            return Points[0].Probability;
        }

        for (int i = 1; i < Points.Count; i++)
        {
            if (confidence <= Points[i].Confidence)
            {
                CalibrationPoint left = Points[i - 1];
                CalibrationPoint right = Points[i];
                double span = right.Confidence - left.Confidence;
                return span <= 0 ? right.Probability : left.Probability + (right.Probability - left.Probability) * (confidence - left.Confidence) / span;
            }
        }

        return Points[^1].Probability;
    }
}

public static class ConfidenceCalibrator
{
    public const int MinimumSamples = 30;
    private const double ShrinkWeight = 5;

    public static ConfidenceCalibration? Fit(IReadOnlyList<(double Confidence, bool Win)> outcomes)
    {
        if (outcomes.Count < MinimumSamples)
        {
            return null;
        }

        double baseRate = outcomes.Count(o => o.Win) / (double)outcomes.Count;
        List<Block> blocks = [];

        foreach ((double confidence, bool win) in outcomes.OrderBy(o => o.Confidence))
        {
            blocks.Add(new Block(confidence, confidence, win ? 1 : 0, 1));

            while (blocks.Count >= 2 && blocks[^2].Rate > blocks[^1].Rate)
            {
                Block right = blocks[^1];
                Block left = blocks[^2];
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add(new Block(left.From, right.To, left.Wins + right.Wins, left.Count + right.Count));
            }
        }

        List<CalibrationPoint> points = [];

        foreach (Block block in blocks)
        {
            double probability = (block.Wins + ShrinkWeight * baseRate) / (block.Count + ShrinkWeight);
            points.Add(new CalibrationPoint((block.From + block.To) / 2, probability));
        }

        for (int i = 1; i < points.Count; i++)
        {
            points[i] = points[i] with { Probability = Math.Max(points[i].Probability, points[i - 1].Probability) };
        }

        return new ConfidenceCalibration(points, outcomes.Count, baseRate);
    }

    private sealed record Block(double From, double To, double Wins, int Count)
    {
        public double Rate => Wins / Count;
    }
}
