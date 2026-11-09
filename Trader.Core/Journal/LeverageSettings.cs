using Trader.Core.Models;

namespace Trader.Core.Journal;

public sealed record LeverageSettings(
    double Stock = 5,
    double Etf = 5,
    double Commodity = 10,
    double Forex = 30,
    double Crypto = 2,
    double Index = 20)
{
    public const double Min = 1;
    public const double Max = 500;

    public double For(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Stock => Stock,
        AssetClass.Etf => Etf,
        AssetClass.Commodity => Commodity,
        AssetClass.Forex => Forex,
        AssetClass.Crypto => Crypto,
        _ => Index
    };

    public bool IsInRange() => new[] { Stock, Etf, Commodity, Forex, Crypto, Index }.All(v => v is >= Min and <= Max);
}
