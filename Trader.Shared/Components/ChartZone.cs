namespace Trader.Shared.Components;

public sealed record ChartZone(long From, long To, double High, double Low, string Label, string Color, bool ExtendRight);
