namespace Trader.Core.Models;

public sealed record UserPreferences(
    string Broker,
    string? DisplayCurrency,
    IReadOnlyList<AssetClass> EnabledClasses,
    bool NarrativeEnabled,
    int? QuietFromHour = null,
    int? QuietToHour = null,
    Trader.Core.Backtesting.CostModel? Costs = null,
    string? NtfyTopic = null,
    IReadOnlyList<string>? Favorites = null,
    bool PushMarketAlerts = false,
    bool ShowShadowSuggestions = false,
    double MinDisplayedConfidence = 0,
    Trader.Core.Journal.LeverageSettings? Leverage = null,
    string ProjectionChartKind = ChartKinds.Candles)
{
    public static readonly UserPreferences Default = new(BrokerCatalog.Other, null, [], true);

    public bool Allows(AssetClass assetClass) => EnabledClasses.Count == 0 || EnabledClasses.Contains(assetClass);
}

public static class ChartKinds
{
    public const string Candles = "candle_solid";

    public static readonly IReadOnlyList<BrokerOption> Options =
    [
        new(Candles, "Velas"),
        new("candle_stroke", "Velas ocas"),
        new("ohlc", "Barras OHLC"),
        new("area", "Área"),
        new("heikin", "Heikin-Ashi")
    ];
}

public sealed record BrokerOption(string Key, string Label);

public static class BrokerCatalog
{
    public const string Other = "other";

    public static readonly IReadOnlyList<BrokerOption> Options =
    [
        new("trading212-cfd", "Trading 212 (CFD)"),
        new("trading212-invest", "Trading 212 (Invest/ISA)"),
        new("xtb", "XTB"),
        new(Other, "Outra / nenhuma")
    ];

    public static string Label(string key) => Options.FirstOrDefault(o => o.Key == key)?.Label ?? Options[^1].Label;

    public static string? LabelOrNull(string key) => key == Other ? null : Options.FirstOrDefault(o => o.Key == key)?.Label;
}

public static class UserPreferencesValidator
{
    public const int MaxFavorites = 200;

    private static readonly string[] Currencies = ["EUR", "USD", "GBP"];

    private static bool CostsInRange(Trader.Core.Backtesting.CostModel costs) =>
        new[]
        {
            costs.StockRoundTripPercent, costs.EtfRoundTripPercent, costs.CommodityRoundTripPercent, costs.ForexRoundTripPercent,
            costs.CryptoRoundTripPercent, costs.IndexRoundTripPercent, costs.OvernightPercentPerDay
        }.All(v => v is >= 0 and <= 5);

    public static IReadOnlyList<string> Validate(UserPreferences preferences)
    {
        List<string> errors = [];

        if (BrokerCatalog.Options.All(o => o.Key != preferences.Broker))
        {
            errors.Add("Corretora desconhecida.");
        }

        if (preferences.DisplayCurrency is { Length: > 0 } currency && !Currencies.Contains(currency))
        {
            errors.Add($"Moeda não suportada. Usa uma destas: {string.Join(", ", Currencies)}.");
        }

        if (preferences.QuietFromHour is < 0 or > 23 || preferences.QuietToHour is < 0 or > 23 || (preferences.QuietFromHour is null) != (preferences.QuietToHour is null))
        {
            errors.Add("O horário silencioso tem de ter início e fim entre 0 e 23 horas.");
        }

        if (preferences.NtfyTopic is { Length: > 0 } topic
            && Trader.Core.Alerts.NtfySettingsValidator.Validate(new Trader.Core.Alerts.NtfySettings(Trader.Core.Alerts.NtfySettings.DefaultServer, topic)).Count > 0)
        {
            errors.Add("O teu tópico ntfy deve ter 8 a 64 caracteres (letras, números, - e _).");
        }

        if (preferences.Favorites is { } favorites && (favorites.Count > MaxFavorites || favorites.Any(f => string.IsNullOrWhiteSpace(f) || f.Length > 32)))
        {
            errors.Add($"Os favoritos têm de ser símbolos válidos (máx. {MaxFavorites}).");
        }

        if (preferences.MinDisplayedConfidence is < 0 or > 0.95)
        {
            errors.Add("A certeza mínima a mostrar tem de estar entre 0% e 95%.");
        }

        if (preferences.Costs is { } costs && !CostsInRange(costs))
        {
            errors.Add("Os custos têm de estar entre 0% e 5%.");
        }

        if (ChartKinds.Options.All(o => o.Key != preferences.ProjectionChartKind))
        {
            errors.Add("Estilo de gráfico desconhecido.");
        }

        if (preferences.Leverage is { } leverage && !leverage.IsInRange())
        {
            errors.Add($"A alavancagem tem de estar entre {Trader.Core.Journal.LeverageSettings.Min:0}x e {Trader.Core.Journal.LeverageSettings.Max:0}x.");
        }

        if (preferences.EnabledClasses.Any(c => !Enum.IsDefined(c)))
        {
            errors.Add("Classe de ativos inválida.");
        }

        return errors;
    }
}
