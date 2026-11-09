using Trader.Core.Models;
using Trader.Core.Scoring;

namespace Trader.Core.Analysis;

public enum FindingKind
{
    Positive,
    Neutral,
    Warning,
    Negative
}

public sealed record TradeAnalysisRequest(string Symbol, TradeDirection Direction, double Entry, double StopLoss, double TakeProfit);

public sealed record AnalysisFinding(string Category, FindingKind Kind, string Title, string Detail, int Points);

public sealed record SuggestedLevels(double Entry, double StopLoss, double TakeProfit, double RiskReward);

public sealed record ModelView(TradeDirection? Direction, double Score, IReadOnlyList<SignalResult> Signals, SuggestedLevels Levels);

public sealed record AnalysisHistory(int Samples, double PositiveShare, double AverageR, string Scope);

public sealed record TradeContext(string Name, AssetClass AssetClass, MarketSnapshot Market, SentimentSnapshot Sentiment, ScoringOptions Scoring);

public sealed record TradeAnalysis(
    string Symbol,
    string Name,
    TradeDirection Direction,
    double Price,
    double Atr,
    double Entry,
    double StopLoss,
    double TakeProfit,
    double RiskReward,
    int Score,
    string VerdictCode,
    string Verdict,
    string Summary,
    IReadOnlyList<AnalysisFinding> Findings,
    ModelView Model,
    SentimentSnapshot Sentiment,
    AnalysisHistory? History,
    IReadOnlyList<string> UpcomingEvents,
    DateTimeOffset GeneratedAt);
