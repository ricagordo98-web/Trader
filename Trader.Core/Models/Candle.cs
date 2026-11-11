namespace Trader.Core.Models;

public sealed record Candle(
    DateTimeOffset Time,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    double? AdjustedClose = null);
