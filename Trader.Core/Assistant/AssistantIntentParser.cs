using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Trader.Core.Models;

namespace Trader.Core.Assistant;

public static partial class AssistantIntentParser
{
    private static readonly HashSet<string> UppercaseStopWords =
    [
        "EUA", "BCE", "PIB", "IPC", "OK", "SL", "TP", "RSI", "EMA", "SMA", "ATR", "CPI", "FOMC", "ETF", "ETFS", "R", "RR", "EU", "UE", "IA", "AI", "PT", "USA", "US", "UK", "CEO", "EPS", "IPO", "PM", "AM", "MACD", "ADX", "VWAP", "XTB", "BCP"
    ];

    private static readonly (string Alias, string[] Symbols)[] Aliases =
    [
        ("ouro", ["GC=F", "GLD"]),
        ("prata", ["SI=F", "SLV"]),
        ("petroleo", ["CL=F", "BZ=F", "USO"]),
        ("brent", ["BZ=F", "CL=F"]),
        ("gas natural", ["NG=F"]),
        ("cobre", ["HG=F"]),
        ("bitcoin", ["BTC-USD"]),
        ("ethereum", ["ETH-USD"]),
        ("solana", ["SOL-USD"]),
        ("euro dolar", ["EURUSD=X"]),
        ("euro-dolar", ["EURUSD=X"]),
        ("libra", ["GBPUSD=X"]),
        ("iene", ["USDJPY=X"]),
        ("s&p 500", ["^GSPC", "SPY"]),
        ("s&p500", ["^GSPC", "SPY"]),
        ("sp500", ["^GSPC", "SPY"]),
        ("nasdaq", ["^IXIC", "^NDX", "QQQ"]),
        ("dow jones", ["^DJI", "DIA"]),
        ("dax", ["^GDAXI"]),
        ("psi", ["PSI20.LS", "^PSI20"]),
        ("vix", ["^VIX"])
    ];

    private static readonly string[] NameNoise =
    [
        " incorporated", " inc.", " inc", " corporation", " corp.", " corp", " holdings", " holding", " limited", " ltd.", " ltd", " plc", " co.", " company", " class a", " class b", " class c", " n.v.", " s.a.", " ag", " se", " group", " etf", " trust", ","
    ];

    public static AssistantIntent Parse(string text, IReadOnlyList<InstrumentName> instruments, string? contextSymbol = null)
    {
        string normalized = Normalize(text);
        TradeDirection? direction = Direction(normalized);
        bool wantsTrade = TradeIntent().IsMatch(normalized);
        string? symbol = FindSymbol(text, normalized, instruments) ?? contextSymbol;

        return new AssistantIntent(symbol, direction, wantsTrade && direction is not null);
    }

    public static TradeDirection? Direction(string normalized)
    {
        bool buy = BuyWords().IsMatch(normalized);
        bool sell = SellWords().IsMatch(normalized);

        return buy == sell ? null : buy ? TradeDirection.Buy : TradeDirection.Sell;
    }

    public static string? FindSymbol(string original, string normalized, IReadOnlyList<InstrumentName> instruments)
    {
        Dictionary<string, string> bySymbol = instruments
            .GroupBy(i => i.Symbol.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First().Symbol);

        foreach (Match match in Cashtag().Matches(original))
        {
            if (Resolve(match.Groups[1].Value.ToUpperInvariant(), bySymbol) is { } tagged)
            {
                return tagged;
            }
        }

        foreach (Match match in UpperToken().Matches(original))
        {
            string token = match.Value;

            if (!UppercaseStopWords.Contains(token) && Resolve(token, bySymbol) is { } found)
            {
                return found;
            }
        }

        string padded = $" {Regex.Replace(normalized, @"[^\p{L}\p{N}&\-]+", " ")} ";

        foreach ((string alias, string[] symbols) in Aliases)
        {
            if (padded.Contains($" {alias} ", StringComparison.Ordinal))
            {
                string? existing = symbols.FirstOrDefault(s => bySymbol.ContainsKey(s.ToUpperInvariant()));

                if (existing is not null)
                {
                    return bySymbol[existing.ToUpperInvariant()];
                }
            }
        }

        return instruments
            .Select(i => (i.Symbol, Key: NameKey(i.Name)))
            .Where(x => x.Key.Length >= 4 && Regex.IsMatch(padded, $@"[\s\p{{P}}]{Regex.Escape(x.Key)}[\s\p{{P}}]"))
            .OrderByDescending(x => x.Key.Length)
            .Select(x => x.Symbol)
            .FirstOrDefault();
    }

    public static string Normalize(string text)
    {
        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string NameKey(string name)
    {
        string key = Normalize(name);

        foreach (string noise in NameNoise)
        {
            int index = key.IndexOf(noise, StringComparison.Ordinal);

            if (index > 0)
            {
                key = key[..index];
            }
        }

        return Regex.Replace(key, @"[^\p{L}\p{N}&\-]+", " ").Trim();
    }

    private static string? Resolve(string token, Dictionary<string, string> bySymbol)
    {
        if (bySymbol.TryGetValue(token, out string? exact))
        {
            return exact;
        }

        string? prefixed = bySymbol.Keys.FirstOrDefault(k => k.StartsWith(token, StringComparison.Ordinal) && k.Length > token.Length && k[token.Length] is '-' or '=' or '.');

        return prefixed is null ? null : bySymbol[prefixed];
    }

    [GeneratedRegex(@"\$([A-Za-z][A-Za-z0-9.\-=^]{0,11})")]
    private static partial Regex Cashtag();

    [GeneratedRegex(@"\^?\b[A-Z][A-Z0-9]{1,9}(?:[.\-=][A-Z0-9]{1,4})?\b")]
    private static partial Regex UpperToken();

    [GeneratedRegex(@"\b(compr\w*|long|buy)\b")]
    private static partial Regex BuyWords();

    [GeneratedRegex(@"\b(vend\w*|short\w*|sell)\b")]
    private static partial Regex SellWords();

    [GeneratedRegex(@"\b(vou (comprar|vender|entrar|shortar|abrir)|quero (comprar|vender|entrar|abrir)|regist\w*|cri\w* (a |uma )?trade|abr\w* (a |uma )?(trade|posicao)|nova trade)\b")]
    private static partial Regex TradeIntent();
}
