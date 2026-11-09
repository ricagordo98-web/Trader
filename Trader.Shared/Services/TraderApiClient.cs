using System.Net.Http.Json;
using Trader.Core.Agender;
using Trader.Core.Analysis;
using Trader.Core.Projection;
using Trader.Core.Alerts;
using Trader.Core.Backtesting;
using Trader.Core.Charts;
using Trader.Core.Calendar;
using Trader.Core.Charts;
using Trader.Core.Abstractions;
using Trader.Core.Explain;
using Trader.Core.Intraday;
using Trader.Core.Market;
using Trader.Core.Health;
using Trader.Core.Journal;
using Trader.Core.Maintenance;
using Trader.Core.Models;
using Trader.Core.Scoring;
using Trader.Core.Simulator;
using Trader.Core.Summary;

namespace Trader.Shared.Services;

public sealed class TraderApiClient(HttpClient httpClient)
{
    public async Task<AlertFeed> GetAlertsAsync(int limit = 30, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<AlertFeed>($"api/alerts?limit={limit}", cancellationToken) ?? new AlertFeed([], 0);

    public async Task MarkAlertsSeenAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/alerts/seen", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<PriceAlertDto>> GetPriceAlertsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<PriceAlertDto>>("api/alerts/price", cancellationToken) ?? [];

    public async Task<string?> AddPriceAlertAsync(CreatePriceAlertRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/alerts/price", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> DeletePriceAlertAsync(long id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync($"api/alerts/price/{id}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ChartTemplate>> GetChartTemplatesAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<ChartTemplate>>("api/chart/templates", cancellationToken) ?? [];

    public async Task<string?> SaveChartTemplatesAsync(IReadOnlyList<ChartTemplate> templates, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/chart/templates", templates, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<DailySummary?> GetDailySummaryAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.GetAsync("api/summary", cancellationToken);
        response.EnsureSuccessStatusCode();

        return response.StatusCode == System.Net.HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<DailySummary>(cancellationToken);
    }

    public async Task<DailySummary?> GetWeeklySummaryAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<DailySummary>("api/summary/weekly", cancellationToken);

    public async Task<DailySummary?> GenerateDailySummaryAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/summary/generate", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DailySummary>(cancellationToken);
    }

    public async Task<CalendarFeed> GetCalendarAsync(int days, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<CalendarFeed>($"api/calendar?days={days}", cancellationToken) ?? new CalendarFeed([], []);

    public async Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<BackupInfo>>("api/system/backups", cancellationToken) ?? [];

    public async Task<string?> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/system/backups", null, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> HasPendingDatabaseImportAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<DatabaseImportStatus>("api/system/import", cancellationToken))?.Pending ?? false;

    public async Task<string?> ImportDatabaseAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using StreamContent body = new(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage response = await httpClient.PostAsync("api/system/import", body, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string?> CancelDatabaseImportAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync("api/system/import", cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<AgenderView> GetAgenderAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<AgenderView>("api/agender", cancellationToken)
        ?? new AgenderView(new AgenderSettings(string.Empty, false), new AgenderStatus(false, null, null, 0));

    public async Task<string?> SaveAgenderAsync(SaveAgenderSettingsRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/agender", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string?> RemoveAgenderAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync("api/agender", cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<(TradeAnalysis? Analysis, string? Error)> AnalyzeTradeAsync(TradeAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/analysis", request, cancellationToken);

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<TradeAnalysis>(cancellationToken), null)
            : (null, await ApiError.ReadAsync(response));
    }

    public async Task<(PriceProjection? Projection, string? Error)> GetProjectionAsync(string symbol, int horizonMinutes, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.GetAsync($"api/projection/{Uri.EscapeDataString(symbol)}?horizon={horizonMinutes}", cancellationToken);

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<PriceProjection>(cancellationToken), null)
            : (null, await ApiError.ReadAsync(response));
    }

    public async Task<AgenderStatus> SyncAgenderAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/agender/sync", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgenderStatus>(cancellationToken))!;
    }

    public async Task<NtfySettings> GetNtfyAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<NtfySettings>("api/alerts/ntfy", cancellationToken) ?? new NtfySettings(NtfySettings.DefaultServer, string.Empty);

    public async Task<string?> SaveNtfyAsync(NtfySettings settings, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/alerts/ntfy", settings, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string?> SendNtfyTestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/alerts/ntfy/test", null, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public Uri NewsHubUrl => new(httpClient.BaseAddress!, "hubs/news");

    public async Task<IReadOnlyList<InstrumentQuote>> GetInstrumentsAsync(CancellationToken cancellationToken = default, bool native = false) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<InstrumentQuote>>(native ? "api/instruments?native=true" : "api/instruments", cancellationToken) ?? [];

    public async Task<IReadOnlyDictionary<string, string>> GetCachedExplanationsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<Dictionary<string, string>>($"api/explain/cached?symbols={Uri.EscapeDataString(string.Join(',', symbols))}", cancellationToken) ?? [];

    public async Task<IReadOnlyList<AutoLevel>> GetLevelsAsync(string symbol, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<AutoLevel>>($"api/instruments/{Uri.EscapeDataString(symbol)}/levels", cancellationToken) ?? [];

    public async Task<IReadOnlyList<ChartEventDto>> GetChartEventsAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<ChartEventDto>>(
            $"api/instruments/{Uri.EscapeDataString(symbol)}/events?from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}",
            cancellationToken) ?? [];

    public async Task<IReadOnlyList<ChartLayoutDto>> GetChartLayoutsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<ChartLayoutDto>>("api/chart-layouts", cancellationToken) ?? [];

    public async Task<string?> SaveChartLayoutAsync(string name, string json, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/chart-layouts", new { name, json }, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> DeleteChartLayoutAsync(string name, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync($"api/chart-layouts/{Uri.EscapeDataString(name)}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<AlertRuleDto>> GetAlertRulesAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<AlertRuleDto>>("api/alert-rules", cancellationToken) ?? [];

    public async Task<string?> SaveAlertRuleAsync(AlertRuleDto rule, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/alert-rules", rule, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> DeleteAlertRuleAsync(long id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync($"api/alert-rules/{id}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<SessionStatus>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SessionStatus>>("api/market/sessions", cancellationToken) ?? [];

    public async Task<IReadOnlyList<CompanyProfile>> GetProfilesAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<CompanyProfile>>($"api/market/profiles?symbols={Uri.EscapeDataString(string.Join(',', symbols))}", cancellationToken) ?? [];

    public async Task<OrderFlowSnapshot?> GetFlowAsync(string symbol, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<OrderFlowSnapshot>($"api/market/flow/{Uri.EscapeDataString(symbol)}", cancellationToken);

    public async Task<IReadOnlyList<TradingHalt>> GetHaltsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<TradingHalt>>("api/market/halts", cancellationToken) ?? [];

    public async Task<PaperOverview> GetPaperAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<PaperOverview>("api/simulator", cancellationToken)
        ?? new PaperOverview(PaperAccount.Default, new PaperSummary(PaperAccount.Default.Capital, PaperAccount.Default.Capital, 0, 0, 0, 0, 0, 0, 0), [], []);

    public async Task<string?> OpenPaperTradeAsync(OpenPaperTradeRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/simulator/trades", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> ClosePaperTradeAsync(long id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync($"api/simulator/trades/{id}/close", null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<string?> SavePaperAccountAsync(PaperAccount account, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/simulator/account", account, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task ResetPaperAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync("api/simulator", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<UserPreferences> GetProfileAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<UserPreferences>("api/settings/profile", cancellationToken) ?? UserPreferences.Default;

    public async Task<string?> SaveProfileAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/settings/profile", preferences, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string> GetCryptoBasisAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<PriceSourceSettings>("api/settings/price-source", cancellationToken))?.CryptoBasis ?? CryptoBasis.Usd;

    public async Task<string?> SetCryptoBasisAsync(string basis, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/settings/price-source", new PriceSourceSettings(basis), cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string> GetDisplayCurrencyAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<DisplayCurrencySettings>("api/settings/display-currency", cancellationToken))?.Currency ?? string.Empty;

    public async Task<string?> SetDisplayCurrencyAsync(string currency, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/settings/display-currency", new DisplayCurrencySettings(currency), cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, string interval, string range, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<Candle>>(
            $"api/instruments/{Uri.EscapeDataString(symbol)}/candles?interval={interval}&range={range}",
            cancellationToken) ?? [];

    public async Task<IReadOnlyList<Candle>> GetLiveCandlesAsync(string symbol, string interval, string range, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<Candle>>(
            $"api/instruments/{Uri.EscapeDataString(symbol)}/live?interval={interval}&range={range}",
            cancellationToken) ?? [];

    public async Task<string> GetDrawingsAsync(string symbol, CancellationToken cancellationToken = default) =>
        await httpClient.GetStringAsync($"api/instruments/{Uri.EscapeDataString(symbol)}/drawings", cancellationToken);

    public async Task SaveDrawingsAsync(string symbol, string json, CancellationToken cancellationToken = default)
    {
        using StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await httpClient.PutAsync($"api/instruments/{Uri.EscapeDataString(symbol)}/drawings", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ArticleDto>> GetNewsAsync(NewsQuery query, CancellationToken cancellationToken = default)
    {
        List<string> parameters = [$"limit={query.Limit}"];

        if (!string.IsNullOrWhiteSpace(query.Symbol))
        {
            parameters.Add($"symbol={Uri.EscapeDataString(query.Symbol)}");
        }

        if (query.SourceType is { } sourceType)
        {
            parameters.Add($"sourceType={sourceType}");
        }

        if (!string.IsNullOrWhiteSpace(query.Sentiment))
        {
            parameters.Add($"sentiment={Uri.EscapeDataString(query.Sentiment)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            parameters.Add($"search={Uri.EscapeDataString(query.Search)}");
        }

        if (query.Before is { } before)
        {
            parameters.Add($"before={Uri.EscapeDataString(before.ToString("O"))}");
        }

        return await httpClient.GetFromJsonAsync<IReadOnlyList<ArticleDto>>($"api/news?{string.Join('&', parameters)}", cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<CommunityTrend>> GetCommunityTrendingAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<CommunityTrend>>("api/community/trending", cancellationToken) ?? [];

    public async Task<IReadOnlyList<SentimentSummary>> GetSentimentSummaryAsync(int hours = 24, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SentimentSummary>>($"api/news/sentiment?hours={hours}", cancellationToken) ?? [];

    public async Task<IReadOnlyList<SuggestionDto>> GetSuggestionsAsync(string? symbol, bool? open, int limit = 100, CancellationToken cancellationToken = default, SuggestionHorizon? horizon = null, bool native = false, bool? shadow = null)
    {
        List<string> parameters = [$"limit={limit}"];

        if (shadow is { } includeShadow)
        {
            parameters.Add($"shadow={includeShadow.ToString().ToLowerInvariant()}");
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            parameters.Add($"symbol={Uri.EscapeDataString(symbol)}");
        }

        if (open is { } isOpen)
        {
            parameters.Add($"open={isOpen.ToString().ToLowerInvariant()}");
        }

        if (horizon is { } selected)
        {
            parameters.Add($"horizon={selected}");
        }

        if (native)
        {
            parameters.Add("native=true");
        }

        return await httpClient.GetFromJsonAsync<IReadOnlyList<SuggestionDto>>($"api/suggestions?{string.Join('&', parameters)}", cancellationToken) ?? [];
    }

    public async Task<TrackRecordDto> GetTrackRecordAsync(CancellationToken cancellationToken = default, SuggestionHorizon? horizon = null) =>
        await httpClient.GetFromJsonAsync<TrackRecordDto>(horizon is { } selected ? $"api/suggestions/track-record?horizon={selected}" : "api/suggestions/track-record", cancellationToken)
        ?? new TrackRecordDto(0, 0, 0, 0, 0, 0, null, [], [], [], []);

    public async Task<(MoveExplanation? Explanation, string? Error)> ExplainAsync(string symbol, DateTimeOffset? from, DateTimeOffset? to, bool refresh, CancellationToken cancellationToken = default)
    {
        List<string> parameters = [];

        if (from is not null && to is not null)
        {
            parameters.Add($"from={Uri.EscapeDataString(from.Value.UtcDateTime.ToString("o"))}");
            parameters.Add($"to={Uri.EscapeDataString(to.Value.UtcDateTime.ToString("o"))}");
        }

        if (refresh)
        {
            parameters.Add("refresh=true");
        }

        using HttpResponseMessage response = await httpClient.GetAsync($"api/explain/{Uri.EscapeDataString(symbol)}?{string.Join('&', parameters)}", cancellationToken);

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<MoveExplanation>(cancellationToken), null)
            : (null, await ApiError.ReadAsync(response));
    }

    public async Task<ExplainStats?> GetExplainStatsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ExplainStats>("api/explain/stats", cancellationToken);

    public async Task<RadarSnapshot> GetRadarAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<RadarSnapshot>("api/radar", cancellationToken)
        ?? new RadarSnapshot(DateTimeOffset.MinValue, 0, [], []);

    public async Task<RadarSnapshot> ScanRadarAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/radar/scan", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RadarSnapshot>(cancellationToken) ?? new RadarSnapshot(DateTimeOffset.MinValue, 0, [], []);
    }

    public async Task RefreshSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync("api/suggestions/refresh", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<WatchlistItem>> GetWatchlistAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<WatchlistItem>>("api/watchlist", cancellationToken) ?? [];

    public async Task<string?> AddInstrumentAsync(AddInstrumentRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/watchlist", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> RemoveInstrumentAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync($"api/watchlist/{Uri.EscapeDataString(symbol)}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<ModelReliabilityReport> GetReliabilityAsync(SuggestionHorizon? horizon, CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<ModelReliabilityReport>(horizon is null ? "api/suggestions/reliability" : $"api/suggestions/reliability?horizon={horizon}", cancellationToken))!;

    public async Task<SentimentStudyResult> GetSentimentStudyAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<SentimentStudyResult>("api/suggestions/reliability/sentiment", cancellationToken))!;

    public async Task<Trader.Core.Intraday.IntradayOptions> GetIntradaySettingsAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<Trader.Core.Intraday.IntradayOptions>("api/settings/intraday", cancellationToken))!;

    public async Task<string?> SaveIntradaySettingsAsync(Trader.Core.Intraday.IntradayOptions options, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/settings/intraday", options, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task ResetIntradaySettingsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync("api/settings/intraday", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<WeightLearning> GetWeightLearningAsync(SuggestionHorizon horizon, CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<WeightLearning>($"api/suggestions/reliability/weights?horizon={horizon}", cancellationToken))!;

    public async Task<WeightLearning> ApplyWeightLearningAsync(SuggestionHorizon horizon, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync($"api/suggestions/reliability/weights/apply?horizon={horizon}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WeightLearning>(cancellationToken))!;
    }

    public async Task<MetaModelFit> GetMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<MetaModelFit>($"api/suggestions/reliability/meta?horizon={horizon}", cancellationToken))!;

    public async Task<MetaModelFit> ApplyMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsync($"api/suggestions/reliability/meta/apply?horizon={horizon}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MetaModelFit>(cancellationToken))!;
    }

    public async Task DisableMetaModelAsync(SuggestionHorizon horizon, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync($"api/suggestions/reliability/meta?horizon={horizon}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<VariantSettings> GetVariantBAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<VariantSettings>("api/settings/intraday/variant-b", cancellationToken))!;

    public async Task<string?> SaveVariantBAsync(VariantSettings settings, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/settings/intraday/variant-b", settings, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task ResetVariantBAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync("api/settings/intraday/variant-b", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<HealthItem>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<HealthItem>>("api/system/health", cancellationToken) ?? [];

    public async Task<IReadOnlyList<SourceStatus>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SourceStatus>>("api/sources", cancellationToken) ?? [];

    public async Task<ScoringOptions> GetScoringAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ScoringOptions>("api/settings/scoring", cancellationToken) ?? new ScoringOptions();

    public async Task<string?> SaveScoringAsync(ScoringOptions options, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/settings/scoring", options, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task ResetScoringAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync("api/settings/scoring", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/auth/manage/info", new { oldPassword, newPassword }, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string?> CreateUserAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/auth/register", new { email, password }, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<IntradayBacktestResult?> GetLastIntradayBacktestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.GetAsync("api/backtest/intraday/last", cancellationToken);

        return response.StatusCode == System.Net.HttpStatusCode.NoContent || !response.IsSuccessStatusCode
            ? null
            : await response.Content.ReadFromJsonAsync<IntradayBacktestResult>(cancellationToken);
    }

    public async Task<(IntradayBacktestResult? Result, string? Error)> RunIntradayBacktestAsync(IntradayBacktestRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/backtest/intraday", request, cancellationToken);

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<IntradayBacktestResult>(cancellationToken), null)
            : (null, await ApiError.ReadAsync(response));
    }

    public async Task<BacktestResult?> GetLastBacktestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.GetAsync("api/backtest/last", cancellationToken);
        response.EnsureSuccessStatusCode();

        return response.StatusCode == System.Net.HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<BacktestResult>(cancellationToken);
    }

    public async Task<(BacktestResult? Result, string? Error)> RunBacktestAsync(BacktestRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/backtest", request, cancellationToken);

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<BacktestResult>(cancellationToken), null)
            : (null, await ApiError.ReadAsync(response));
    }

    public async Task<IReadOnlyList<JournalTradeDto>> GetTradesAsync(TradeStatus? status = null, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<JournalTradeDto>>(
            status is { } filter ? $"api/journal/trades?status={filter}" : "api/journal/trades",
            cancellationToken) ?? [];

    public async Task<string?> OpenTradeAsync(OpenTradeRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/journal/trades", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string?> CloseTradeAsync(long id, CloseTradeRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync($"api/journal/trades/{id}/close", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<bool> DeleteTradeAsync(long id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync($"api/journal/trades/{id}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<string> ExportJournalCsvAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetStringAsync("api/journal/export", cancellationToken);

    public async Task<ImportPreview> PreviewImportAsync(string csv, CancellationToken cancellationToken = default)
    {
        using StringContent content = new(csv, System.Text.Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await httpClient.PostAsync("api/journal/import/preview", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ImportPreview>(cancellationToken) ?? new ImportPreview("desconhecido", [], []);
    }

    public async Task<ImportSummary> ImportTradesAsync(string csv, CancellationToken cancellationToken = default)
    {
        using StringContent content = new(csv, System.Text.Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await httpClient.PostAsync("api/journal/import", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ImportSummary>(cancellationToken) ?? new ImportSummary(0, 0, []);
    }

    public async Task<JournalStats> GetJournalStatsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<JournalStats>("api/journal/stats", cancellationToken)
        ?? new JournalStats(0, 0, 0, 0, 0, 0, null, 0, null, 0, 0, 0, [], 0, null, null, []);

    public async Task<AccountProfile> GetAccountAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<AccountProfile>("api/journal/account", cancellationToken) ?? new AccountProfile(10_000, 1, "EUR");

    public async Task<JournalInsights> GetJournalInsightsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<JournalInsights>("api/journal/insights", cancellationToken)
        ?? new JournalInsights(0, [], [], [], [], [], [], [], [], 0, 0, null, null);

    public async Task<DailyRiskStatus?> GetDailyRiskAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<DailyRiskStatus>("api/journal/risk", cancellationToken);

    public async Task<string?> UpdateTradeDetailsAsync(long id, TradeDetailsRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync($"api/journal/trades/{id}/details", request, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }

    public async Task<string?> GetTradeImageAsync(long id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.GetAsync($"api/journal/trades/{id}/image", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("image").GetString();
    }

    public async Task<string?> SaveAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync("api/journal/account", profile, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ApiError.ReadAsync(response);
    }
}
