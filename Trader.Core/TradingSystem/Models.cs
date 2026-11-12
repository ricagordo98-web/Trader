using Trader.Core.Journal;
using Trader.Core.Models;

namespace Trader.Core.TradingSystem;

public enum SystemPhase
{
    Paper,
    SmallReal,
    Full
}

public enum CheckStatus
{
    Pass,
    Fail,
    Unknown
}

public enum CheckSeverity
{
    Hard,
    Rule,
    Info
}

public sealed record TradingRules
{
    public double RiskPercent { get; init; } = 1;
    public double MinRewardRisk { get; init; } = 1.5;
    public int MaxTradesPerDay { get; init; } = 3;
    public double MaxDailyLossPercent { get; init; } = 2;
    public double MaxWeeklyLossPercent { get; init; } = 5;
    public int MaxOpenPositions { get; init; } = 5;
    public int MaxConsecutiveLosses { get; init; } = 3;
    public int CooldownHours { get; init; } = 24;
    public int EarningsBlackoutHours { get; init; } = 48;
    public bool AvoidHighImpactMacro { get; init; } = true;
    public int MacroBlackoutHours { get; init; } = 12;
    public IReadOnlyList<AssetClass> AllowedAssetClasses { get; init; } = [];
    public int? TradingStartHour { get; init; }
    public int? TradingEndHour { get; init; }
    public bool RequireSetup { get; init; } = true;
    public double MaxCorrelation { get; init; } = 0.7;
    public int MaxCorrelatedPositions { get; init; } = 1;
    public int MaxPendingReviews { get; init; } = 3;
    public double BreakEvenAtR { get; init; } = 1;
    public double PartialAtR { get; init; } = 2;
    public int MaxHoldingDays { get; init; } = 10;
    public bool AllowJustifiedBreaks { get; init; } = true;
    public IReadOnlyList<string> Checklist { get; init; } =
    [
        "O setup está completo, não estou a antecipar a entrada",
        "Sei exatamente onde saio se estiver errado",
        "Não estou a tentar recuperar uma perda",
        "Estou calmo e com tempo para gerir a trade"
    ];
    public int PaperTradesToPromote { get; init; } = 30;
    public int SmallTradesToPromote { get; init; } = 50;
    public double MinAdherencePercent { get; init; } = 80;
    public double SmallRiskFactor { get; init; } = 0.25;
    public int DemotionWindow { get; init; } = 20;
    public double DemotionAverageR { get; init; } = -0.3;
}

public sealed class TradingPlan
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ChangeNote { get; set; } = string.Empty;
    public string RulesJson { get; set; } = "{}";
}

public sealed record TradingPlanDto(
    int Version,
    DateTimeOffset? CreatedAt,
    string Description,
    string ChangeNote,
    TradingRules Rules,
    bool Exists,
    bool Editable,
    string? LockedReason,
    IReadOnlyList<PlanVersionInfo> History);

public sealed record PlanVersionInfo(int Version, DateTimeOffset CreatedAt, string ChangeNote);

public sealed record SavePlanRequest(TradingRules Rules, string? Description, string? ChangeNote, long? ReviewId);

public enum SetupEntryRule
{
    None,
    TrendPullback,
    Breakout,
    RsiReversal,
    MovingAverageCross
}

public sealed record SetupStrategy
{
    public SetupEntryRule Entry { get; init; } = SetupEntryRule.None;
    public bool LongOnly { get; init; } = true;
    public int Lookback { get; init; } = 20;
    public double RsiLevel { get; init; } = 30;
    public double StopAtr { get; init; } = 1.5;
    public double TargetR { get; init; } = 2;
    public int MaxBars { get; init; } = 20;
}

public sealed class TradingSetup
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EntryCriteria { get; set; } = string.Empty;
    public string ExitCriteria { get; set; } = string.Empty;
    public string Checklist { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string StrategyJson { get; set; } = "{}";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record TradingSetupDto(
    long Id,
    string Name,
    string Description,
    string EntryCriteria,
    string ExitCriteria,
    IReadOnlyList<string> Checklist,
    string? Image,
    SetupStrategy Strategy,
    bool Active,
    PerformanceGroup? Performance);

public sealed record SaveSetupRequest(
    string Name,
    string? Description,
    string? EntryCriteria,
    string? ExitCriteria,
    IReadOnlyList<string>? Checklist,
    string? Image,
    SetupStrategy? Strategy,
    bool Active = true);

public sealed class TradeRecord
{
    public long TradeId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int PlanVersion { get; set; }
    public long? SetupId { get; set; }
    public string SetupName { get; set; } = string.Empty;
    public SystemPhase Phase { get; set; }
    public string ChecklistJson { get; set; } = "[]";
    public int RuleBreaks { get; set; }
    public string Justification { get; set; } = string.Empty;
    public string? DossierJson { get; set; }
    public double BreakEvenAtR { get; set; }
    public double PartialAtR { get; set; }
    public int MaxHoldingDays { get; set; }
    public bool BreakEvenAlerted { get; set; }
    public bool PartialAlerted { get; set; }
    public bool TimeAlerted { get; set; }
    public bool? ManagementFollowed { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public bool? FollowedPlan { get; set; }
    public string WentWell { get; set; } = string.Empty;
    public string WentWrong { get; set; } = string.Empty;
    public string Lesson { get; set; } = string.Empty;
    public double? MaeR { get; set; }
    public double? MfeR { get; set; }
}

public sealed record TradeRecordDto(
    long TradeId,
    int PlanVersion,
    long? SetupId,
    string SetupName,
    SystemPhase Phase,
    IReadOnlyList<CheckItem> Checklist,
    int RuleBreaks,
    string Justification,
    bool HasDossier,
    bool? ManagementFollowed,
    DateTimeOffset? ReviewedAt,
    bool? FollowedPlan,
    string WentWell,
    string WentWrong,
    string Lesson,
    double? MaeR,
    double? MfeR);

public sealed class WeeklyReview
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateOnly WeekStart { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public string WhatWorked { get; set; } = string.Empty;
    public string WhatFailed { get; set; } = string.Empty;
    public string RuleBreaks { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
}

public sealed record WeeklyReviewDto(
    long Id,
    DateOnly WeekStart,
    DateTimeOffset CreatedAt,
    WeekSummary Summary,
    string WhatWorked,
    string WhatFailed,
    string RuleBreaks,
    string Decision);

public sealed record WeekSummary(
    DateOnly WeekStart,
    int Opened,
    int Closed,
    int Wins,
    double TotalR,
    double NetPnl,
    int InRules,
    int OutOfRules,
    double? AdherencePercent,
    int PendingReviews,
    IReadOnlyList<string> Lessons);

public sealed record SaveWeeklyReviewRequest(string? WhatWorked, string? WhatFailed, string? RuleBreaks, string? Decision);

public sealed record CheckItem(string Code, string Label, CheckStatus Status, CheckSeverity Severity, string Detail);

public sealed record PreTradeRequest(
    string Symbol,
    TradeDirection Direction,
    double Entry,
    double? StopLoss,
    double? TakeProfit,
    double Quantity,
    double FxRate,
    long? SetupId);

public sealed record PreTradeCheck(
    IReadOnlyList<CheckItem> Items,
    IReadOnlyList<string> ManualItems,
    bool Blocked,
    bool NeedsJustification,
    double? RiskPercent,
    double? RewardRisk,
    double SuggestedQuantity,
    SystemPhase Phase,
    double RiskFactor,
    int PlanVersion,
    bool PlanExists,
    string Verdict);

public sealed record SystemTradeRequest(
    OpenTradeRequest Trade,
    long? SetupId,
    IReadOnlyList<string>? CheckedManualItems,
    string? Justification,
    string? DossierJson,
    string? Emotion);

public sealed record SystemTradeResult(JournalTradeDto? Trade, PreTradeCheck? Check, IReadOnlyList<string> Errors);

public sealed record TradeReviewRequest(bool FollowedPlan, bool? ManagementFollowed, string? WentWell, string? WentWrong, string? Lesson);

public sealed record PendingReview(long TradeId, string Symbol, TradeDirection Direction, DateTimeOffset? ClosedAt, double? RMultiple, double? NetPnl, string SetupName, int RuleBreaks);

public sealed record PhaseProgress(
    SystemPhase Phase,
    DateTimeOffset Since,
    int Trades,
    int Needed,
    double? AverageR,
    double? AdherencePercent,
    bool CanPromote,
    bool Demoted,
    string Message);

public sealed record SystemStatus(
    bool PlanExists,
    int PlanVersion,
    int SetupCount,
    bool CanTrade,
    IReadOnlyList<string> Blockers,
    int TradesToday,
    int MaxTradesPerDay,
    double DailyPnl,
    double DailyLossLimit,
    double WeeklyPnl,
    double WeeklyLossLimit,
    int OpenPositions,
    int MaxOpenPositions,
    int LosingStreak,
    DateTimeOffset? CooldownUntil,
    int PendingReviews,
    bool WeeklyReviewDue,
    double? AdherencePercent,
    PhaseProgress Phase,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<ManagementNote> Management,
    string Currency);

public sealed record ManagementNote(long TradeId, string Symbol, string Message);

public sealed record PerformanceGroup(
    string Label,
    int Count,
    double WinRate,
    double? AverageR,
    double TotalR,
    double? ProfitFactor,
    double? CiLow,
    double? CiHigh);

public sealed record ExcursionSummary(int Count, double? AverageMaeR, double? AverageMfeR, int LosersThatReachedOneR, int WinnersThatWentNearStop);

public sealed record SystemPerformance(
    PerformanceGroup All,
    PerformanceGroup InRules,
    PerformanceGroup OutOfRules,
    PerformanceGroup OutsideSystem,
    IReadOnlyList<PerformanceGroup> BySetup,
    IReadOnlyList<PerformanceGroup> ByPlanVersion,
    IReadOnlyList<PerformanceGroup> ByPhase,
    IReadOnlyList<PerformanceGroup> ByWeekday,
    IReadOnlyList<PerformanceGroup> ByHour,
    ExcursionSummary Excursion,
    int? TradesNeeded,
    IReadOnlyList<CurvePoint> Curve,
    double MaxDrawdownR,
    string Verdict);

public sealed record SetupBacktestRequest(IReadOnlyList<string>? Symbols, int Years = 5);

public sealed record SetupBacktestTrade(string Symbol, TradeDirection Direction, DateTimeOffset EntryTime, double Entry, double Exit, double R);

public sealed record SetupBacktestResult(
    PerformanceGroup Result,
    IReadOnlyList<PerformanceGroup> BySymbol,
    IReadOnlyList<SetupBacktestTrade> Recent,
    int SymbolsTested,
    double CostR,
    string Verdict);
