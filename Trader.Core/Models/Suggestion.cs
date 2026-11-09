namespace Trader.Core.Models;

public enum TradeDirection
{
    Buy,
    Sell
}

public enum SuggestionHorizon
{
    Swing,
    Intraday
}

public enum SuggestionStatus
{
    Open,
    HitTakeProfit,
    HitStopLoss,
    Expired
}

public static class SuggestionVariants
{
    public const string Projection = "P";
}

public sealed class Suggestion
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TradeDirection Direction { get; set; }
    public double Confidence { get; set; }
    public double Score { get; set; }
    public double Entry { get; set; }
    public double StopLoss { get; set; }
    public double TakeProfit { get; set; }
    public double RiskReward { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public SuggestionStatus Status { get; set; }
    public SuggestionHorizon Horizon { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public double? ExitPrice { get; set; }
    public double? RMultiple { get; set; }
    public bool IsShadow { get; set; }
    public string ShadowReason { get; set; } = string.Empty;
    public double? MfeR { get; set; }
    public double? MaeR { get; set; }
    public double? SmartR { get; set; }
    public string Variant { get; set; } = "A";
}
