using FluentAssertions;
using Trader.Core.Backtesting;

namespace Trader.Tests;

public class ExitStudyTests
{
    private static List<ExitSample> Samples(double uplift, int years = 8, int perYear = 600)
    {
        Random random = new(3);
        List<ExitSample> samples = [];

        for (int year = 0; year < years; year++)
        {
            for (int i = 0; i < perYear; i++)
            {
                double current = random.NextDouble() * 2 - 1;
                double[] net = new double[ExitStudyAnalyzer.Variants.Count];
                net[0] = current;

                for (int v = 1; v < net.Length; v++)
                {
                    net[v] = current + (v == 1 ? uplift : 0) + (random.NextDouble() - 0.5) * 0.2;
                }

                samples.Add(new ExitSample(new DateTimeOffset(2018 + year, 3, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i % 250), net));
            }
        }

        return samples;
    }

    [Fact]
    public void Analyze_VariantConsistentlyBetter_IsFlaggedBetter()
    {
        ExitStudy study = ExitStudyAnalyzer.Analyze(Samples(0.1));

        study.Variants.Single(v => v.Name == "atr-2-4").Verdict.Should().Be(ExitStudyAnalyzer.VerdictBetter);
    }

    [Fact]
    public void Analyze_NoDifference_NothingIsBetter()
    {
        ExitStudy study = ExitStudyAnalyzer.Analyze(Samples(0));

        study.Variants.Should().NotContain(v => v.Verdict == ExitStudyAnalyzer.VerdictBetter);
    }

    [Fact]
    public void Analyze_TooFewSamples_ReturnsNoVariants() =>
        ExitStudyAnalyzer.Analyze(Samples(0.1, years: 1, perYear: 100)).Variants.Should().BeEmpty();
}
