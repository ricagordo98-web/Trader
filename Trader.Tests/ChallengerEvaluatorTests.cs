using FluentAssertions;
using Trader.Core.Backtesting;
using Trader.Core.Models;

namespace Trader.Tests;

public class ChallengerEvaluatorTests
{
    private static List<ResearchSample> Samples(bool predictive, int years = 9, int perYear = 1500)
    {
        Random random = new(5);
        List<ResearchSample> samples = [];

        for (int year = 0; year < years; year++)
        {
            for (int i = 0; i < perYear; i++)
            {
                double signal = random.NextDouble() * 2 - 1;
                double noise = random.NextDouble() * 2 - 1;
                double score = random.NextDouble() * 2 - 1;
                double netR = (predictive ? 0.6 * signal : 0) + (random.NextDouble() * 2 - 1) - 0.05;
                samples.Add(new ResearchSample("AAPL", AssetClass.Stock, new DateTimeOffset(2015 + year, 1, 5, 0, 0, 0, TimeSpan.Zero).AddDays(i % 340), TradeDirection.Buy, netR,
                    new Dictionary<string, double> { ["signal"] = signal, ["noise"] = noise, ["score"] = score }));
            }
        }

        return samples;
    }

    [Fact]
    public void Evaluate_FeatureThatPredictsTheResult_AcceptsAChallenger()
    {
        ChallengerReport report = ChallengerEvaluator.Evaluate(Samples(predictive: true));

        report.TestYears.Should().BeGreaterThanOrEqualTo(ChallengerEvaluator.MinTestYears);
        report.Results.Should().Contain(r => r.Name != "score" && r.Verdict == ChallengerEvaluator.VerdictAccepted);
    }

    [Fact]
    public void Evaluate_PureNoise_AcceptsNothing()
    {
        ChallengerReport report = ChallengerEvaluator.Evaluate(Samples(predictive: false));

        report.Results.Should().NotContain(r => r.Verdict == ChallengerEvaluator.VerdictAccepted);
    }

    [Fact]
    public void Evaluate_NotEnoughYears_ReturnsNoResults()
    {
        ChallengerReport report = ChallengerEvaluator.Evaluate(Samples(predictive: true, years: 4));

        report.Results.Should().BeEmpty();
        report.Notes.Should().Contain(n => n.Contains("insuficiente"));
    }

    [Fact]
    public void Evaluate_ReferenceModelIsNeverAccepted()
    {
        ChallengerReport report = ChallengerEvaluator.Evaluate(Samples(predictive: true));

        report.Results.Single(r => r.Name == "score").Verdict.Should().Be(ChallengerEvaluator.VerdictRejected);
    }
}
