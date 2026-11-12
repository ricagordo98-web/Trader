using Trader.Core.Analysis;
using Trader.Core.Models;
using Trader.Core.TradingSystem;

namespace Trader.Core.Assistant;

public sealed record TimeframeView(string Label, string Trend, double? Rsi, double? ChangePercent, string Detail);

public sealed record DossierNews(string Title, string Url, string Source, DateTimeOffset PublishedAt, double? Sentiment);

public sealed record DossierModel(SuggestionDto? OpenSuggestion, int ClosedOnSymbol, double? WinRateOnSymbol, double? AverageROnSymbol);

public sealed record TradeDraft(
    string Symbol,
    TradeDirection Direction,
    double Entry,
    double StopLoss,
    double TakeProfit,
    double Quantity,
    double FxRate,
    double? RiskPercent,
    double RewardRisk);

public sealed record TradeDossier(
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string Currency,
    double Price,
    double? ChangePercent,
    DateTimeOffset? PriceAsOf,
    TradeDirection Direction,
    bool DirectionFromUser,
    IReadOnlyList<TimeframeView> Timeframes,
    double Atr,
    double SwingLow,
    double SwingHigh,
    double? YearHigh,
    double? YearLow,
    TradeAnalysis? Analysis,
    IReadOnlyList<DossierNews> News,
    IReadOnlyList<string> Events,
    DossierModel Model,
    IReadOnlyList<OpenExposure> Exposure,
    PreTradeCheck? Check,
    TradeDraft Draft,
    string Summary,
    DateTimeOffset GeneratedAt);

public sealed record ChatTurn(string Role, string Text);

public sealed record AssistantRequest(string Message, IReadOnlyList<ChatTurn>? History, string? Symbol, TradeDirection? Direction);

public sealed record AssistantSource(string Title, string Url);

public sealed record AssistantReply(
    string Text,
    string Provider,
    bool UsedModel,
    TradeDossier? Dossier,
    bool ProposeTrade,
    IReadOnlyList<AssistantSource> Sources,
    string? Symbol,
    string? Notice,
    DateTimeOffset AsOf);

public sealed record AssistantStatus(bool ModelEnabled, string Provider, bool WebSearch, string Detail);

public sealed record AssistantIntent(string? Symbol, TradeDirection? Direction, bool WantsTrade);

public sealed record AssistantModelReply(string Text, IReadOnlyList<AssistantSource> Sources);

public sealed record InstrumentName(string Symbol, string Name);
