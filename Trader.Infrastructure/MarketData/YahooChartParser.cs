using System.Text.Json;
using Trader.Core.Models;

namespace Trader.Infrastructure.MarketData;

public static class YahooChartParser
{
    public static ChartSeries Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("chart", out JsonElement chart)
            || !chart.TryGetProperty("result", out JsonElement results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return new ChartSeries(null, []);
        }

        JsonElement result = results[0];

        double? lastPrice = result.TryGetProperty("meta", out JsonElement meta)
            && meta.TryGetProperty("regularMarketPrice", out JsonElement price)
            && price.ValueKind == JsonValueKind.Number
                ? price.GetDouble()
                : null;

        DateTimeOffset? lastPriceTime = meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("regularMarketTime", out JsonElement marketTime)
            && marketTime.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(marketTime.GetInt64())
                : null;

        double? dayVolume = meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("regularMarketVolume", out JsonElement dayVolumeElement)
            && dayVolumeElement.ValueKind == JsonValueKind.Number
                ? dayVolumeElement.GetDouble()
                : null;

        if (!result.TryGetProperty("timestamp", out JsonElement timestamps)
            || timestamps.ValueKind != JsonValueKind.Array
            || !result.TryGetProperty("indicators", out JsonElement indicators)
            || !indicators.TryGetProperty("quote", out JsonElement quotes)
            || quotes.GetArrayLength() == 0)
        {
            return new ChartSeries(lastPrice, [], lastPriceTime, dayVolume);
        }

        JsonElement quote = quotes[0];
        JsonElement open = quote.GetProperty("open");
        JsonElement high = quote.GetProperty("high");
        JsonElement low = quote.GetProperty("low");
        JsonElement close = quote.GetProperty("close");
        quote.TryGetProperty("volume", out JsonElement volume);

        JsonElement adjusted = default;
        bool hasAdjusted = indicators.TryGetProperty("adjclose", out JsonElement adjustedBlocks)
            && adjustedBlocks.ValueKind == JsonValueKind.Array
            && adjustedBlocks.GetArrayLength() > 0
            && adjustedBlocks[0].TryGetProperty("adjclose", out adjusted)
            && adjusted.ValueKind == JsonValueKind.Array
            && adjusted.GetArrayLength() == timestamps.GetArrayLength();

        List<Candle> candles = new(timestamps.GetArrayLength());

        for (int i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (open[i].ValueKind != JsonValueKind.Number
                || high[i].ValueKind != JsonValueKind.Number
                || low[i].ValueKind != JsonValueKind.Number
                || close[i].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            double candleVolume = volume.ValueKind == JsonValueKind.Array && volume[i].ValueKind == JsonValueKind.Number
                ? volume[i].GetDouble()
                : 0;

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()),
                open[i].GetDouble(),
                high[i].GetDouble(),
                low[i].GetDouble(),
                close[i].GetDouble(),
                candleVolume,
                hasAdjusted && adjusted[i].ValueKind == JsonValueKind.Number ? adjusted[i].GetDouble() : null));
        }

        if (candles.Count > 1 && candles[^1].Time.Second != 0 && candles[^1].Volume == 0)
        {
            candles.RemoveAt(candles.Count - 1);
        }

        return new ChartSeries(lastPrice, candles, lastPriceTime, dayVolume);
    }
}
