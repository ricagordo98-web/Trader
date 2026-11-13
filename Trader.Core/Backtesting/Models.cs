using Trader.Core.Models;

namespace Trader.Core.Backtesting;

public sealed class CostModel
{
    public double StockRoundTripPercent { get; set; } = 0.10;
    public double EtfRoundTripPercent { get; set; } = 0.10;
    public double CommodityRoundTripPercent { get; set; } = 0.06;
    public double ForexRoundTripPercent { get; set; } = 0.02;
    public double CryptoRoundTripPercent { get; set; } = 0.40;
    public double IndexRoundTripPercent { get; set; } = 0.04;
    public double OvernightPercentPerDay { get; set; } = 0.015;

    public double RoundTripPercent(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Stock => StockRoundTripPercent,
        AssetClass.Etf => EtfRoundTripPercent,
        AssetClass.Commodity => CommodityRoundTripPercent,
        AssetClass.Crypto => CryptoRoundTripPercent,
        AssetClass.Index => IndexRoundTripPercent,
        _ => ForexRoundTripPercent
    };

    public double CostPercent(AssetClass assetClass, double daysHeld) =>
        RoundTripPercent(assetClass) + OvernightPercentPerDay * Math.Max(0, daysHeld);

    public double CostInR(AssetClass assetClass, double entry, double risk, double daysHeld) =>
        risk <= 0 ? 0 : CostPercent(assetClass, daysHeld) / 100 * entry / risk;
}

public sealed class BacktestRequest
{
    public string Range { get; set; } = "5y";
    public bool UseSentiment { get; set; }
    public bool UseMarketContext { get; set; } = true;
    public CostModel Costs { get; set; } = new();
}

public sealed record BacktestTrade(
    string Symbol,
    AssetClass AssetClass,
    TradeDirection Direction,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    double Entry,
    double Exit,
    SuggestionStatus Outcome,
    double DaysHeld,
    double GrossR,
    double NetR,
    double NetPercent);

public sealed record BacktestStats(
    int Trades,
    int Wins,
    double WinRate,
    double AverageGrossR,
    double AverageNetR,
    double TotalNetR,
    double? ProfitFactor,
    double MaxDrawdownR,
    double AverageNetPercent,
    double StdDevR,
    double ConfidenceLow,
    double ConfidenceHigh);

public sealed record BacktestGroup(string Name, BacktestStats Stats);

public sealed record BacktestVerdict(string Code, string Title, string Detail);

public sealed record BacktestResult(
    string Range,
    DateTimeOffset GeneratedAt,
    DateTimeOffset From,
    DateTimeOffset To,
    int SymbolsTested,
    CostModel Costs,
    BacktestStats Strategy,
    BacktestStats Baseline,
    BacktestStats FirstHalf,
    BacktestStats SecondHalf,
    double BuyAndHoldPercent,
    IReadOnlyList<BacktestGroup> ByAssetClass,
    IReadOnlyList<BacktestGroup> ByDirection,
    IReadOnlyList<BacktestGroup> BySymbol,
    IReadOnlyList<CurvePoint> Curve,
    BacktestVerdict Verdict,
    IReadOnlyList<string> Notes,
    bool UsedSentiment = false,
    bool UsedMarketContext = false,
    FeatureResearch? Research = null,
    BacktestRobustness? Robustness = null,
    ChallengerReport? Challengers = null,
    ExitStudy? Exits = null);
