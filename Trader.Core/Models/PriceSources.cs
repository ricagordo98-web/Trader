using System.Text.RegularExpressions;
using Trader.Core.Abstractions;

namespace Trader.Core.Models;

public sealed record PriceSourceInfo(string History, string? Live, string Note);

public static partial class PriceSources
{
    public const string Yahoo = "Yahoo Finance";

    public static PriceSourceInfo Describe(string symbol, AssetClass assetClass, string cryptoBasis)
    {
        string upper = symbol.ToUpperInvariant();

        if (assetClass == AssetClass.Crypto)
        {
            bool aligned = cryptoBasis == CryptoBasis.Usd;

            return new PriceSourceInfo(
                Yahoo + " (USD, média de várias bolsas)",
                aligned ? "Binance ajustada ao USD do Yahoo" : "Binance (USDT)",
                aligned
                    ? "O preço ao vivo vem dos trades da Binance, corrigido para o USD do Yahoo. Cada bolsa e cada site mostram valores ligeiramente diferentes (cerca de 0,1%); compara sempre com o preço da tua corretora."
                    : "O preço ao vivo é o da Binance em USDT, que costuma valer cerca de 0,1% acima do dólar. Cada bolsa e cada site mostram valores ligeiramente diferentes; compara sempre com o preço da tua corretora.");
        }

        if (assetClass == AssetClass.Forex)
        {
            return new PriceSourceInfo(Yahoo, "OANDA (via Finnhub)", "Cotação ao vivo da OANDA. A tua corretora aplica o seu próprio spread.");
        }

        if (assetClass is AssetClass.Stock or AssetClass.Etf && UsListing().IsMatch(upper))
        {
            return new PriceSourceInfo(Yahoo, "Bolsas dos EUA (via Finnhub)", "Trades ao vivo das bolsas dos EUA. A tua corretora pode cotar CFDs com spread e fora de horas de forma diferente.");
        }

        return new PriceSourceInfo(Yahoo, null, "Cotação do Yahoo Finance, que pode ter atraso (cerca de 10 minutos em futuros e até 15 minutos em índices e bolsas europeias).");
    }

    [GeneratedRegex("^[A-Z]{1,5}$")]
    private static partial Regex UsListing();
}
