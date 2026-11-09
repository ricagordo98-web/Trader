using MudBlazor;
using Trader.Core.Models;
using System.Text.Json;
using Trader.Core.Alerts;
using Trader.Core.Charts;
using Trader.Core.Journal;

namespace Trader.Shared.Components;

public partial class ChartDialog
{
    private const string IndicatorStoreKey = "chart.indicators.v1";
    private const double PlanStopAtr = 1.5;
    private const double PlanTargetAtr = 2.5;
    private const int ReplayMinCandles = 40;

    private sealed record VisualOption(string Key, string Label);

    private sealed record PlanState(double Entry, double Stop, double Target);

    private static readonly VisualOption[] Kinds =
    [
        new("candle_solid", "Velas"),
        new("candle_stroke", "Velas ocas"),
        new("ohlc", "Barras OHLC"),
        new("area", "Área"),
        new("heikin", "Heikin-Ashi")
    ];

    private static readonly VisualOption[] Axes =
    [
        new("normal", "Escala normal"),
        new("log", "Escala logarítmica"),
        new("percentage", "Escala em percentagem")
    ];

    private static readonly (string Group, string Label)[] LevelGroups =
    [
        (AutoLevelGroups.PreviousDay, "Dia anterior"),
        (AutoLevelGroups.OpeningRange, "Abertura (30 min)"),
        (AutoLevelGroups.Pivots, "Pivots"),
        (AutoLevelGroups.Swing, "Suporte e resistência")
    ];

    private static readonly (string Kind, string Label)[] EventKinds =
    [
        ("news", "Notícias"),
        ("earnings", "Resultados"),
        ("trade", "As minhas trades"),
        ("suggestion", "Sugestões")
    ];

    private readonly HashSet<string> _levelGroups = [];
    private readonly HashSet<string> _eventKinds = [];
    private readonly Dictionary<string, IndicatorSettingsDialog.Result> _indicatorConfigs = [];
    private IReadOnlyList<AutoLevel> _autoLevels = [];
    private IReadOnlyList<ChartLevel> _allLevels = [];
    private string _kind = "candle_solid";
    private string _axis = "normal";
    private bool _configApplied;
    private PlanState? _plan;
    private AccountProfile _account = new(10_000, 1, "EUR");
    private LeverageSettings _leverage = new();
    private double? _alertLinePrice;
    private bool _replaying;
    private bool _replayPlaying;
    private int _replayIndex;
    private int _replaySpeed = 1;
    private IReadOnlyList<Candle> _fullCandles = [];
    private CancellationTokenSource? _replayCts;

    private double Factor => Quote.DisplayFactor <= 0 ? 1 : Quote.DisplayFactor;

    private PositionResult? PlanResult => _plan is { } plan
        ? PositionCalculator.Calculate(new PositionInput(_account.Capital, _account.RiskPercent, plan.Entry / Factor, plan.Stop / Factor, plan.Target / Factor, _leverage.For(Quote.AssetClass), 1, Quote.AssetClass))
        : null;

    private string PlanJournalLink => _plan is { } plan
        ? string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"/journal?symbol={Uri.EscapeDataString(Quote.Symbol)}&entry={plan.Entry / Factor:R}&sl={plan.Stop / Factor:R}&tp={plan.Target / Factor:R}")
        : "/journal";

    private void RebuildLevels() =>
        _allLevels =
        [
            .. _levels,
            .. ExtraLevels ?? [],
            .. _autoLevels.Where(l => _levelGroups.Contains(l.Group)).Select(l => new ChartLevel(l.Label, l.Price, LevelColor(l.Group)))
        ];

    private static string LevelColor(string group) => group switch
    {
        AutoLevelGroups.PreviousDay => "#A78BFA",
        AutoLevelGroups.OpeningRange => "#38BDF8",
        AutoLevelGroups.Pivots => "#F59E0B",
        _ => "#9CA3AF"
    };

    private async Task ApplyStoredIndicatorConfigAsync()
    {
        if (_configApplied || _chart is null)
        {
            return;
        }

        _configApplied = true;

        try
        {
            string? json = await Store.GetAsync(IndicatorStoreKey);

            if (json is not null && JsonSerializer.Deserialize<Dictionary<string, IndicatorSettingsDialog.Result>>(json) is { } stored)
            {
                foreach ((string name, IndicatorSettingsDialog.Result config) in stored)
                {
                    _indicatorConfigs[name] = config;
                    await _chart.SetIndicatorConfigAsync(name, config.Parameters, config.Color, config.Size);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
    }

    private async Task ConfigureIndicatorAsync()
    {
        DialogParameters<IndicatorSettingsDialog> parameters = new()
        {
            { x => x.Options, Indicators.Select(i => (i.Name, i.Label)).ToList() }
        };

        IDialogReference reference = await DialogService.ShowAsync<IndicatorSettingsDialog>(string.Empty, parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true });
        DialogResult? result = await reference.Result;

        if (result is { Canceled: false, Data: IndicatorSettingsDialog.Result config } && _chart is not null)
        {
            _indicatorConfigs[config.Name] = config;
            await _chart.SetIndicatorConfigAsync(config.Name, config.Parameters, config.Color, config.Size);

            try
            {
                await Store.SetAsync(IndicatorStoreKey, JsonSerializer.Serialize(_indicatorConfigs));
            }
            catch (InvalidOperationException)
            {
            }

            if (!_activeIndicators.Contains(config.Name))
            {
                Snackbar.Add($"Configuração guardada. Ativa «{config.Name}» no menu Indicadores para a veres.", Severity.Info);
            }
        }
    }

    private async Task SetKindAsync(VisualOption option)
    {
        _kind = option.Key;

        if (_chart is not null)
        {
            await _chart.SetKindAsync(option.Key);
        }
    }

    private async Task SetAxisAsync(VisualOption option)
    {
        _axis = option.Key;

        if (_chart is not null)
        {
            await _chart.SetYAxisTypeAsync(option.Key);
        }
    }

    private async Task ToggleLevelGroupAsync(string group)
    {
        if (!_levelGroups.Add(group))
        {
            _levelGroups.Remove(group);
        }

        if (_autoLevels.Count == 0)
        {
            try
            {
                _autoLevels = await Api.GetLevelsAsync(Quote.Symbol, _cts.Token);
            }
            catch (HttpRequestException)
            {
                Snackbar.Add("Não foi possível calcular os níveis.", Severity.Warning);
                _levelGroups.Remove(group);
            }
        }

        RebuildLevels();
    }

    private async Task ToggleEventKindAsync(string kind)
    {
        if (!_eventKinds.Add(kind))
        {
            _eventKinds.Remove(kind);
        }

        await RefreshEventMarkersAsync();
    }

    private Dictionary<string, ChartEventDto> _markerEvents = [];

    private async Task OnMarkerClickedAsync(string key)
    {
        if (_markerEvents.TryGetValue(key, out ChartEventDto? chartEvent))
        {
            await DialogService.ShowAsync<ChartEventDialog>(
                string.Empty,
                new DialogParameters<ChartEventDialog> { { x => x.Event, chartEvent } },
                new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true });
        }
    }

    private async Task RefreshEventMarkersAsync()
    {
        if (_chart is null || _candles.Count == 0)
        {
            return;
        }

        if (_eventKinds.Count == 0)
        {
            await _chart.SetMarkersAsync([]);
            return;
        }

        try
        {
            IReadOnlyList<ChartEventDto> events = await Api.GetChartEventsAsync(Quote.Symbol, _candles[0].Time, _candles[^1].Time, _cts.Token);

            List<ChartEventDto> shown = events.Where(e => _eventKinds.Contains(e.Kind)).ToList();
            _markerEvents = shown.Select((e, index) => (Key: index.ToString(System.Globalization.CultureInfo.InvariantCulture), Event: e)).ToDictionary(x => x.Key, x => x.Event);

            await _chart.SetMarkersAsync(_markerEvents
                .Select(x => new ChartMarker(x.Value.Time.ToUnixTimeMilliseconds(), x.Value.Label, EventColor(x.Value.Kind), x.Key))
                .ToList());
        }
        catch (HttpRequestException)
        {
            Snackbar.Add("Não foi possível carregar os eventos.", Severity.Warning);
        }
    }

    private static string EventColor(string kind) => kind switch
    {
        "news" => "#38BDF8",
        "earnings" => "#F59E0B",
        "trade" => "#22C55E",
        _ => "#A78BFA"
    };

    private async Task TogglePlanAsync()
    {
        if (_plan is not null)
        {
            _plan = null;

            if (_chart is not null)
            {
                await _chart.ClearPlanAsync();
            }

            return;
        }

        if (_chart is null || _candles.Count < 20)
        {
            return;
        }

        try
        {
            _account = await Api.GetAccountAsync(_cts.Token);
        }
        catch (HttpRequestException)
        {
        }

        double last = _candles[^1].Close;
        double atr = _candles.TakeLast(14).Average(c => c.High - c.Low);
        _plan = new PlanState(last, last - PlanStopAtr * atr, last + PlanTargetAtr * atr);
        await _chart.StartPlanAsync(_plan.Entry, _plan.Stop, _plan.Target);
    }

    private Task OnPlanChangedAsync((string Which, double Price) change)
    {
        if (_plan is { } plan)
        {
            _plan = change.Which switch
            {
                "entry" => plan with { Entry = change.Price },
                "stop" => plan with { Stop = change.Price },
                _ => plan with { Target = change.Price }
            };
        }

        return InvokeAsync(StateHasChanged);
    }

    private async Task FlipPlanAsync()
    {
        if (_plan is not { } plan || _chart is null)
        {
            return;
        }

        _plan = new PlanState(plan.Entry, plan.Entry + (plan.Entry - plan.Stop), plan.Entry - (plan.Target - plan.Entry));
        await _chart.StartPlanAsync(_plan.Entry, _plan.Stop, _plan.Target);
    }

    private async Task ToggleAlertLineAsync()
    {
        if (_alertLinePrice is not null)
        {
            _alertLinePrice = null;

            if (_chart is not null)
            {
                await _chart.ClearAlertLineAsync();
            }

            return;
        }

        if (_chart is null || _candles.Count == 0)
        {
            return;
        }

        _alertLinePrice = _candles[^1].Close;
        await _chart.StartAlertLineAsync(_alertLinePrice.Value);
    }

    private Task OnAlertLineChangedAsync(double price)
    {
        _alertLinePrice = price;
        return InvokeAsync(StateHasChanged);
    }

    private async Task CreateAlertAsync()
    {
        if (_alertLinePrice is not { } displayed || _candles.Count == 0)
        {
            return;
        }

        double price = displayed / Factor;
        double current = _candles[^1].Close / Factor;
        PriceCondition condition = price >= current ? PriceCondition.Above : PriceCondition.Below;

        string? error = await Api.AddPriceAlertAsync(new CreatePriceAlertRequest(Quote.Symbol, condition, price, "Criado no gráfico"), _cts.Token);

        if (error is null)
        {
            Snackbar.Add($"Alerta criado: {Quote.Symbol} {(condition == PriceCondition.Above ? "acima de" : "abaixo de")} {price:0.#####} (moeda original do ativo).", Severity.Success);
            await ToggleAlertLineAsync();
        }
        else
        {
            Snackbar.Add(error, Severity.Warning);
        }
    }

    private bool CanReplay => _candles.Count >= ReplayMinCandles;

    private async Task ToggleReplayAsync()
    {
        if (_replaying)
        {
            await StopReplayAsync();
            return;
        }

        if (!CanReplay)
        {
            return;
        }

        _fullCandles = _candles;
        _replayIndex = Math.Max(ReplayMinCandles / 2, _fullCandles.Count * 2 / 3);
        _replaying = true;
        _candles = _fullCandles.Take(_replayIndex).ToList();
    }

    private async Task SeekReplayAsync(int index)
    {
        if (!_replaying)
        {
            return;
        }

        _replayIndex = Math.Clamp(index, ReplayMinCandles / 2, _fullCandles.Count);
        _candles = _fullCandles.Take(_replayIndex).ToList();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleReplayPlayAsync()
    {
        if (_replayPlaying)
        {
            await PauseReplayAsync();
            return;
        }

        _replayPlaying = true;
        _replayCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = ReplayLoopAsync(_replayCts.Token);
    }

    private Task PauseReplayAsync()
    {
        _replayPlaying = false;
        _replayCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task ReplayLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _replayIndex < _fullCandles.Count)
            {
                await Task.Delay(Math.Max(60, 1000 / _replaySpeed), cancellationToken);
                await StepReplayAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }

        _replayPlaying = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task StepReplayAsync()
    {
        if (!_replaying || _chart is null || _replayIndex >= _fullCandles.Count)
        {
            return;
        }

        Candle next = _fullCandles[_replayIndex];
        _replayIndex++;

        await InvokeAsync(async () =>
        {
            await _chart.PushBarAsync(next);
            StateHasChanged();
        });
    }

    private async Task StopReplayAsync()
    {
        await PauseReplayAsync();
        _replaying = false;
        _candles = _fullCandles;
        await InvokeAsync(StateHasChanged);
    }
}
