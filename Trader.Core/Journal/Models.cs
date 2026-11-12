using Trader.Core.Models;

namespace Trader.Core.Journal;

public enum TradeStatus
{
    Open,
    Closed
}

public sealed class JournalTrade
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TradeDirection Direction { get; set; }
    public DateTimeOffset EntryTime { get; set; }
    public double EntryPrice { get; set; }
    public double Quantity { get; set; }
    public double? StopLoss { get; set; }
    public double? InitialStopLoss { get; set; }
    public double? TakeProfit { get; set; }
    public double FxRate { get; set; } = 1;
    public double Fees { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public long? SuggestionId { get; set; }
    public TradeStatus Status { get; set; }
    public DateTimeOffset? ExitTime { get; set; }
    public double? ExitPrice { get; set; }
    public bool StopLossAlerted { get; set; }
    public bool TakeProfitAlerted { get; set; }
    public string Setup { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string Emotion { get; set; } = string.Empty;
    public string? ChartImage { get; set; }
}

public sealed record OpenTradeRequest(
    string Symbol,
    TradeDirection Direction,
    DateTimeOffset EntryTime,
    double EntryPrice,
    double Quantity,
    double? StopLoss,
    double? TakeProfit,
    double FxRate,
    double Fees,
    string? Platform,
    string? Notes,
    long? SuggestionId);

public sealed record CloseTradeRequest(
    DateTimeOffset ExitTime,
    double ExitPrice,
    double AdditionalFees,
    string? Notes);

public sealed record TradeLevelsRequest(double? StopLoss, double? TakeProfit);

public sealed record TradeDetailsRequest(string? Setup, string? Tags, string? Emotion, string? Notes, string? ChartImage, bool ClearImage = false);

public sealed record AccountProfile(double Capital, double RiskPercent, string Currency, double MaxDailyLossPercent = 0, int MaxTradesPerDay = 0);

public sealed record JournalTradeDto(
    long Id,
    string Symbol,
    string Name,
    TradeDirection Direction,
    DateTimeOffset EntryTime,
    double EntryPrice,
    double Quantity,
    double? StopLoss,
    double? TakeProfit,
    double FxRate,
    double Fees,
    string Platform,
    string Notes,
    long? SuggestionId,
    TradeStatus Status,
    DateTimeOffset? ExitTime,
    double? ExitPrice,
    double? RiskAmount,
    double? NetPnl,
    double? RMultiple,
    SuggestionStatus? SuggestionOutcome,
    double? SuggestionR,
    string Setup = "",
    string Tags = "",
    string Emotion = "",
    bool HasImage = false,
    double? InitialStopLoss = null);

public sealed record LinkedComparison(
    long TradeId,
    string Symbol,
    TradeDirection Direction,
    SuggestionStatus SuggestionOutcome,
    double SuggestionR,
    double YourR,
    DateTimeOffset ClosedAt);

public sealed record JournalStats(
    int ClosedCount,
    int OpenCount,
    int Wins,
    double WinRate,
    double NetPnl,
    double Expectancy,
    double? AverageR,
    double TotalR,
    double? ProfitFactor,
    double MaxDrawdown,
    double BestTrade,
    double WorstTrade,
    IReadOnlyList<CurvePoint> Curve,
    int LinkedCount,
    double? LinkedAverageYourR,
    double? LinkedAverageSuggestionR,
    IReadOnlyList<LinkedComparison> Comparisons);
