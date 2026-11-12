using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Calendar;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Core.TradingSystem;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.TradingSystem;

public sealed class TradingSystemService(
    TraderDbContext dbContext,
    IJournalService journal,
    ICalendarService calendar,
    IMarketDataService marketData,
    TimeProvider timeProvider,
    ILogger<TradingSystemService> logger) : ITradingSystemService
{
    private const int MaxDossierLength = 200_000;
    private const int MaxImageLength = 600_000;
    private const int MaxCorrelationSymbols = 8;
    private const int ExcursionBatch = 10;
    private const int MaxBacktestSymbols = 40;
    private const int MinJustificationLength = 10;
    private static readonly TimeSpan ReviewEditWindow = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Local;

    private sealed record PhaseState(SystemPhase Phase, DateTimeOffset Since);

    public async Task<TradingPlanDto> GetPlanAsync(string userId, CancellationToken cancellationToken)
    {
        List<TradingPlan> plans = await dbContext.TradingPlans.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.Version)
            .ToListAsync(cancellationToken);

        TradingPlan? current = plans.FirstOrDefault();
        (bool editable, string? reason) = await EditableAsync(userId, current, null, cancellationToken);
        IReadOnlyList<PlanVersionInfo> history = plans.Select(p => new PlanVersionInfo(p.Version, p.CreatedAt, p.ChangeNote)).ToList();

        return current is null
            ? new TradingPlanDto(0, null, string.Empty, string.Empty, await DefaultRulesAsync(userId, cancellationToken), false, true, null, history)
            : new TradingPlanDto(current.Version, current.CreatedAt, current.Description, current.ChangeNote, Rules(current), true, editable, reason, history);
    }

    public async Task<IReadOnlyList<string>> SavePlanAsync(string userId, SavePlanRequest request, CancellationToken cancellationToken)
    {
        TradingRules rules = request.Rules with
        {
            Checklist = request.Rules.Checklist.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList(),
            AllowedAssetClasses = request.Rules.AllowedAssetClasses.Distinct().ToList()
        };

        List<string> errors = [.. TradingRulesValidator.Validate(rules)];

        if (request.Description is { Length: > 4000 } || request.ChangeNote is { Length: > 500 })
        {
            errors.Add("A descrição pode ter até 4000 caracteres e a nota da mudança até 500.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        TradingPlan? current = await dbContext.TradingPlans
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(cancellationToken);

        (bool editable, string? reason) = await EditableAsync(userId, current, request.ReviewId, cancellationToken);

        if (!editable)
        {
            return [reason ?? "O plano está bloqueado."];
        }

        bool used = current is not null && await dbContext.TradeRecords.AnyAsync(r => r.UserId == userId && r.PlanVersion == current.Version, cancellationToken);
        string json = JsonSerializer.Serialize(rules, JsonOptions);
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (current is not null && !used)
        {
            current.RulesJson = json;
            current.Description = request.Description?.Trim() ?? string.Empty;
            current.ChangeNote = request.ChangeNote?.Trim() ?? current.ChangeNote;
            current.CreatedAt = now;
        }
        else
        {
            dbContext.TradingPlans.Add(new TradingPlan
            {
                UserId = userId,
                Version = (current?.Version ?? 0) + 1,
                CreatedAt = now,
                RulesJson = json,
                Description = request.Description?.Trim() ?? string.Empty,
                ChangeNote = request.ChangeNote?.Trim() ?? (current is null ? "Primeira versão" : string.Empty)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        AccountProfile account = await journal.GetAccountAsync(userId, cancellationToken);
        await journal.SaveAccountAsync(userId, account with
        {
            RiskPercent = rules.RiskPercent,
            MaxDailyLossPercent = rules.MaxDailyLossPercent,
            MaxTradesPerDay = rules.MaxTradesPerDay
        }, cancellationToken);

        return [];
    }

    public async Task<IReadOnlyList<TradingSetupDto>> ListSetupsAsync(string userId, CancellationToken cancellationToken)
    {
        List<TradingSetup> setups = await dbContext.TradingSetups.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.Active)
            .ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

        IReadOnlyList<SystemTrade> trades = await LoadTradesAsync(userId, cancellationToken);
        Dictionary<long, PerformanceGroup> performance = trades
            .Where(t => t.Record?.SetupId is not null && t.Trade.Status == TradeStatus.Closed && t.Trade.RMultiple is not null)
            .GroupBy(t => t.Record!.SetupId!.Value)
            .ToDictionary(g => g.Key, g => SystemStatistics.Group(g.First().Record!.SetupName, g.Select(t => t.Trade.RMultiple!.Value)));

        return setups.Select(s => ToDto(s, performance.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<(TradingSetupDto? Setup, IReadOnlyList<string> Errors)> SaveSetupAsync(string userId, long? id, SaveSetupRequest request, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > 40)
        {
            errors.Add("O nome do setup deve ter entre 1 e 40 caracteres.");
        }

        if (request.Description is { Length: > 2000 } || request.EntryCriteria is { Length: > 2000 } || request.ExitCriteria is { Length: > 2000 })
        {
            errors.Add("Cada texto pode ter até 2000 caracteres.");
        }

        if (request.Image is { Length: > MaxImageLength })
        {
            errors.Add("A imagem é demasiado grande.");
        }

        if (request.Checklist is { Count: > 15 })
        {
            errors.Add("A checklist do setup pode ter até 15 pontos.");
        }

        if (await dbContext.TradingSetups.AnyAsync(s => s.UserId == userId && s.Name == name && s.Id != (id ?? 0), cancellationToken))
        {
            errors.Add("Já tens um setup com esse nome.");
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        TradingSetup? setup = id is { } existingId
            ? await dbContext.TradingSetups.FirstOrDefaultAsync(s => s.Id == existingId && s.UserId == userId, cancellationToken)
            : null;

        if (id is not null && setup is null)
        {
            return (null, ["Setup não encontrado."]);
        }

        if (setup is null)
        {
            setup = new TradingSetup { UserId = userId, CreatedAt = timeProvider.GetUtcNow() };
            dbContext.TradingSetups.Add(setup);
        }

        setup.Name = name;
        setup.Description = request.Description?.Trim() ?? string.Empty;
        setup.EntryCriteria = request.EntryCriteria?.Trim() ?? string.Empty;
        setup.ExitCriteria = request.ExitCriteria?.Trim() ?? string.Empty;
        setup.Checklist = string.Join('\n', (request.Checklist ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()));
        setup.Image = request.Image;
        setup.StrategyJson = JsonSerializer.Serialize(request.Strategy ?? new SetupStrategy(), JsonOptions);
        setup.Active = request.Active;

        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(setup, null), []);
    }

    public async Task<bool> DeleteSetupAsync(string userId, long id, CancellationToken cancellationToken)
    {
        TradingSetup? setup = await dbContext.TradingSetups.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);

        if (setup is null)
        {
            return false;
        }

        if (await dbContext.TradeRecords.AnyAsync(r => r.SetupId == id, cancellationToken))
        {
            setup.Active = false;
        }
        else
        {
            dbContext.TradingSetups.Remove(setup);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<SetupBacktestResult?> BacktestSetupAsync(string userId, long id, SetupBacktestRequest request, CancellationToken cancellationToken)
    {
        TradingSetup? setup = await dbContext.TradingSetups.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);

        if (setup is null)
        {
            return null;
        }

        SetupStrategy strategy = Strategy(setup);

        if (strategy.Entry == SetupEntryRule.None)
        {
            return SetupBacktester.Summarize([], 0);
        }

        TradingRules rules = (await GetPlanAsync(userId, cancellationToken)).Rules;
        List<string> symbols = request.Symbols is { Count: > 0 }
            ? request.Symbols.Select(s => s.Trim().ToUpperInvariant()).Distinct().Take(MaxBacktestSymbols).ToList()
            : await dbContext.Instruments.AsNoTracking()
                .Where(i => rules.AllowedAssetClasses.Count == 0 || rules.AllowedAssetClasses.Contains(i.AssetClass))
                .OrderBy(i => i.Symbol)
                .Select(i => i.Symbol)
                .Take(MaxBacktestSymbols)
                .ToListAsync(cancellationToken);

        int years = Math.Clamp(request.Years, 1, 10);
        List<SetupBacktestTrade> trades = [];
        int tested = 0;

        foreach (string symbol in symbols)
        {
            try
            {
                IReadOnlyList<Candle> candles = await marketData.GetCandlesAsync(symbol, "1d", $"{years}y", cancellationToken);
                trades.AddRange(SetupBacktester.Run(symbol, candles, strategy));
                tested++;
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug("No candles for setup backtest {Symbol}: {Message}", symbol, ex.Message);
            }
        }

        return SetupBacktester.Summarize(trades, tested);
    }

    public async Task<PreTradeCheck> CheckAsync(string userId, PreTradeRequest request, CancellationToken cancellationToken)
    {
        TradingPlanDto plan = await GetPlanAsync(userId, cancellationToken);
        AccountProfile account = await journal.GetAccountAsync(userId, cancellationToken);
        PhaseState phase = await GetPhaseAsync(userId, cancellationToken);
        string symbol = request.Symbol.Trim().ToUpperInvariant();
        AssetClass? assetClass = await dbContext.Instruments.AsNoTracking()
            .Where(i => i.Symbol == symbol)
            .Select(i => (AssetClass?)i.AssetClass)
            .FirstOrDefaultAsync(cancellationToken);

        IReadOnlyList<JournalTradeDto> trades = await journal.ListAsync(userId, null, cancellationToken);
        int pending = await PendingCountAsync(userId, cancellationToken);
        TradingSetupDto? setup = null;

        if (request.SetupId is { } setupId && await dbContext.TradingSetups.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setupId && s.UserId == userId, cancellationToken) is { } entity)
        {
            setup = ToDto(entity, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TradingRules rules = plan.Exists ? plan.Rules : plan.Rules with { RequireSetup = false, Checklist = [] };
        IReadOnlyList<MarketEvent> events = await LoadEventsAsync(symbol, rules, now, cancellationToken);
        IReadOnlyList<OpenExposure> exposure = await ExposureAsync(symbol, trades, cancellationToken);

        return PreTradeChecker.Evaluate(new PreTradeInputs(
            request with { Symbol = symbol },
            rules,
            plan.Exists,
            plan.Version,
            account,
            phase.Phase,
            assetClass,
            trades,
            pending,
            setup,
            events,
            exposure,
            now,
            Zone));
    }

    public async Task<SystemTradeResult> OpenAsync(string userId, SystemTradeRequest request, CancellationToken cancellationToken)
    {
        OpenTradeRequest trade = request.Trade;
        PreTradeCheck check = await CheckAsync(userId, new PreTradeRequest(trade.Symbol, trade.Direction, trade.EntryPrice, trade.StopLoss, trade.TakeProfit, trade.Quantity, trade.FxRate, request.SetupId), cancellationToken);

        if (check.Blocked)
        {
            IEnumerable<string> failed = check.Items.Where(i => i.Status == CheckStatus.Fail && (i.Severity == CheckSeverity.Hard || check.Blocked)).Select(i => $"{i.Label}: {i.Detail}");
            return new SystemTradeResult(null, check, [check.Verdict, .. failed]);
        }

        HashSet<string> confirmed = (request.CheckedManualItems ?? []).Select(i => i.Trim()).ToHashSet();

        if (check.ManualItems.Any(i => !confirmed.Contains(i)))
        {
            return new SystemTradeResult(null, check, ["Confirma todos os pontos da checklist antes de registar a trade."]);
        }

        string justification = request.Justification?.Trim() ?? string.Empty;

        if (check.NeedsJustification && justification.Length < MinJustificationLength)
        {
            return new SystemTradeResult(null, check, ["Esta trade quebra regras do teu plano: escreve porque vais avançar mesmo assim."]);
        }

        if (justification.Length > 1000 || request.DossierJson is { Length: > MaxDossierLength })
        {
            return new SystemTradeResult(null, check, ["A justificação ou o dossiê são demasiado grandes."]);
        }

        OpenTradeRequest toOpen = check.Phase == SystemPhase.Paper ? trade with { Platform = "Papel" } : trade;
        JournalResult<JournalTradeDto> opened = await journal.OpenAsync(userId, toOpen, cancellationToken);

        if (!opened.Succeeded)
        {
            return new SystemTradeResult(null, check, opened.Errors);
        }

        JournalTradeDto dto = opened.Value!;
        TradingSetup? setup = request.SetupId is { } setupId
            ? await dbContext.TradingSetups.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setupId && s.UserId == userId, cancellationToken)
            : null;

        if (setup is not null || !string.IsNullOrWhiteSpace(request.Emotion))
        {
            JournalResult<JournalTradeDto> detailed = await journal.UpdateDetailsAsync(userId, dto.Id, new TradeDetailsRequest(setup?.Name, null, request.Emotion?.Trim(), null, null), cancellationToken);
            dto = detailed.Value ?? dto;
        }

        TradingRules rules = (await GetPlanAsync(userId, cancellationToken)).Rules;
        dbContext.TradeRecords.Add(new TradeRecord
        {
            TradeId = dto.Id,
            UserId = userId,
            PlanVersion = check.PlanVersion,
            SetupId = setup?.Id,
            SetupName = setup?.Name ?? string.Empty,
            Phase = check.Phase,
            ChecklistJson = JsonSerializer.Serialize(check.Items, JsonOptions),
            RuleBreaks = check.Items.Count(i => i.Status == CheckStatus.Fail && i.Severity == CheckSeverity.Rule),
            Justification = justification,
            DossierJson = string.IsNullOrWhiteSpace(request.DossierJson) ? null : request.DossierJson,
            BreakEvenAtR = rules.BreakEvenAtR,
            PartialAtR = rules.PartialAtR,
            MaxHoldingDays = rules.MaxHoldingDays
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new SystemTradeResult(dto, check, []);
    }

    public async Task<TradeRecordDto?> GetRecordAsync(string userId, long tradeId, CancellationToken cancellationToken) =>
        await dbContext.TradeRecords.AsNoTracking().FirstOrDefaultAsync(r => r.TradeId == tradeId && r.UserId == userId, cancellationToken) is { } record
            ? ToDto(record)
            : null;

    public async Task<string?> GetDossierAsync(string userId, long tradeId, CancellationToken cancellationToken) =>
        await dbContext.TradeRecords.AsNoTracking()
            .Where(r => r.TradeId == tradeId && r.UserId == userId)
            .Select(r => r.DossierJson)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<TradeRecordDto>> ListRecordsAsync(string userId, CancellationToken cancellationToken) =>
        (await dbContext.TradeRecords.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<string>> ReviewAsync(string userId, long tradeId, TradeReviewRequest request, CancellationToken cancellationToken)
    {
        TradeRecord? record = await dbContext.TradeRecords.FirstOrDefaultAsync(r => r.TradeId == tradeId && r.UserId == userId, cancellationToken);

        if (record is null)
        {
            return ["Esta trade não foi registada pelo sistema."];
        }

        if (request.WentWell is { Length: > 1000 } || request.WentWrong is { Length: > 1000 } || request.Lesson is { Length: > 1000 })
        {
            return ["Cada resposta pode ter até 1000 caracteres."];
        }

        record.FollowedPlan = request.FollowedPlan;
        record.ManagementFollowed = request.ManagementFollowed;
        record.WentWell = request.WentWell?.Trim() ?? string.Empty;
        record.WentWrong = request.WentWrong?.Trim() ?? string.Empty;
        record.Lesson = request.Lesson?.Trim() ?? string.Empty;
        record.ReviewedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        return [];
    }

    public async Task<IReadOnlyList<PendingReview>> PendingReviewsAsync(string userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<SystemTrade> trades = await LoadTradesAsync(userId, cancellationToken);

        return trades
            .Where(t => t.Trade.Status == TradeStatus.Closed && t.Record is { ReviewedAt: null })
            .OrderBy(t => t.Trade.ExitTime)
            .Select(t => new PendingReview(t.Trade.Id, t.Trade.Symbol, t.Trade.Direction, t.Trade.ExitTime, t.Trade.RMultiple, t.Trade.NetPnl, t.Record!.SetupName, t.Record.RuleBreaks))
            .ToList();
    }

    public async Task<WeekSummary> CurrentWeekAsync(string userId, CancellationToken cancellationToken)
    {
        DateOnly week = await ReviewWeekAsync(userId, cancellationToken);
        return SystemStatistics.Week(week, await LoadTradesAsync(userId, cancellationToken), Zone);
    }

    public async Task<IReadOnlyList<WeeklyReviewDto>> ListWeeklyReviewsAsync(string userId, CancellationToken cancellationToken) =>
        (await dbContext.WeeklyReviews.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.WeekStart)
            .Take(52)
            .ToListAsync(cancellationToken))
        .Select(ToDto)
        .ToList();

    public async Task<WeeklyReviewDto> SaveWeeklyReviewAsync(string userId, SaveWeeklyReviewRequest request, CancellationToken cancellationToken)
    {
        WeekSummary summary = await CurrentWeekAsync(userId, cancellationToken);
        WeeklyReview review = new()
        {
            UserId = userId,
            WeekStart = summary.WeekStart,
            CreatedAt = timeProvider.GetUtcNow(),
            SummaryJson = JsonSerializer.Serialize(summary, JsonOptions),
            WhatWorked = Trim(request.WhatWorked, 2000),
            WhatFailed = Trim(request.WhatFailed, 2000),
            RuleBreaks = Trim(request.RuleBreaks, 2000),
            Decision = Trim(request.Decision, 2000)
        };

        dbContext.WeeklyReviews.Add(review);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(review);
    }

    public async Task<SystemStatus> GetStatusAsync(string userId, CancellationToken cancellationToken)
    {
        TradingPlanDto plan = await GetPlanAsync(userId, cancellationToken);
        TradingRules rules = plan.Rules;
        AccountProfile account = await journal.GetAccountAsync(userId, cancellationToken);
        IReadOnlyList<SystemTrade> trades = await LoadTradesAsync(userId, cancellationToken);
        List<JournalTradeDto> journalTrades = trades.Select(t => t.Trade).ToList();
        DateTimeOffset now = timeProvider.GetUtcNow();

        PhaseState phase = await GetPhaseAsync(userId, cancellationToken);
        PhaseProgress progress = SystemStatistics.Progress(phase.Phase, phase.Since, rules, trades);

        if (progress.Demoted)
        {
            phase = new PhaseState(phase.Phase - 1, now);
            await SavePhaseAsync(userId, phase, cancellationToken);
            progress = SystemStatistics.Progress(phase.Phase, phase.Since, rules, trades) with { Demoted = true, Message = progress.Message };
        }

        BreakerState breakers = CircuitBreakers.Evaluate(rules, account, journalTrades, now, Zone);
        int pending = trades.Count(t => t.Trade.Status == TradeStatus.Closed && t.Record is { ReviewedAt: null });
        int setupCount = await dbContext.TradingSetups.CountAsync(s => s.UserId == userId && s.Active, cancellationToken);

        List<string> blockers = breakers.Items.Where(i => i.Status == CheckStatus.Fail).Select(i => i.Detail).ToList();

        if (rules.MaxPendingReviews > 0 && pending >= rules.MaxPendingReviews)
        {
            blockers.Add($"Tens {pending} trades fechadas por rever. Revê-as antes de abrir outra.");
        }

        bool weeklyDue = await WeeklyReviewDueAsync(userId, trades, now, cancellationToken);
        List<TradeRecord> closedRecords = trades.Where(t => t.Trade.Status == TradeStatus.Closed && t.Record is not null).Select(t => t.Record!).ToList();
        double? adherence = closedRecords.Count == 0 ? null : closedRecords.Count(r => r.RuleBreaks == 0) * 100.0 / closedRecords.Count;

        List<string> next = [];

        if (!plan.Exists)
        {
            next.Add("Escreve o teu plano de trading (separador Plano).");
        }

        if (setupCount == 0)
        {
            next.Add("Adiciona pelo menos um setup ao teu playbook (separador Setups).");
        }

        if (pending > 0)
        {
            next.Add($"Revê {pending} trade(s) fechada(s) (separador Revisões).");
        }

        if (weeklyDue)
        {
            next.Add("Faz a revisão semanal (separador Revisões).");
        }

        if (progress.CanPromote)
        {
            next.Add("Cumpriste os critérios da fase: podes avançar para a seguinte.");
        }

        List<ManagementNote> management = trades
            .Where(t => t.Trade.Status == TradeStatus.Open && t.Record is not null)
            .SelectMany(t => ManagementNotes(t))
            .ToList();

        return new SystemStatus(
            plan.Exists,
            plan.Version,
            setupCount,
            blockers.Count == 0,
            blockers,
            breakers.TradesToday,
            rules.MaxTradesPerDay,
            breakers.DailyPnl,
            breakers.DailyLossLimit,
            breakers.WeeklyPnl,
            breakers.WeeklyLossLimit,
            breakers.OpenPositions,
            rules.MaxOpenPositions,
            breakers.LosingStreak,
            breakers.CooldownUntil is { } until && until > now ? until : null,
            pending,
            weeklyDue,
            adherence,
            progress,
            next,
            management,
            account.Currency);
    }

    public async Task<IReadOnlyList<string>> PromoteAsync(string userId, CancellationToken cancellationToken)
    {
        TradingRules rules = (await GetPlanAsync(userId, cancellationToken)).Rules;
        PhaseState phase = await GetPhaseAsync(userId, cancellationToken);
        PhaseProgress progress = SystemStatistics.Progress(phase.Phase, phase.Since, rules, await LoadTradesAsync(userId, cancellationToken));

        if (!progress.CanPromote)
        {
            return [progress.Message];
        }

        await SavePhaseAsync(userId, new PhaseState(phase.Phase + 1, timeProvider.GetUtcNow()), cancellationToken);
        return [];
    }

    public async Task<SystemPerformance> GetPerformanceAsync(string userId, CancellationToken cancellationToken)
    {
        await FillExcursionsAsync(userId, cancellationToken);
        return SystemStatistics.Performance(await LoadTradesAsync(userId, cancellationToken), Zone);
    }

    private static IEnumerable<ManagementNote> ManagementNotes(SystemTrade trade)
    {
        JournalTradeDto t = trade.Trade;
        TradeRecord record = trade.Record!;
        double initial = t.InitialStopLoss ?? t.StopLoss ?? t.EntryPrice;
        bool stopStillAtRisk = t.StopLoss is { } stop && (t.Direction == TradeDirection.Buy ? stop < t.EntryPrice : stop > t.EntryPrice);

        if (record.BreakEvenAlerted && stopStillAtRisk && Math.Abs(initial - t.EntryPrice) > 0)
        {
            yield return new ManagementNote(t.Id, t.Symbol, "Passou o nível de break-even: move o stop para a entrada.");
        }

        if (record.PartialAlerted)
        {
            yield return new ManagementNote(t.Id, t.Symbol, "Chegou ao nível de realização parcial do plano.");
        }

        if (record.TimeAlerted)
        {
            yield return new ManagementNote(t.Id, t.Symbol, "Ultrapassou o tempo máximo do plano: fecha-a ou justifica na revisão.");
        }
    }

    private async Task FillExcursionsAsync(string userId, CancellationToken cancellationToken)
    {
        List<TradeRecord> missing = await dbContext.TradeRecords
            .Where(r => r.UserId == userId && r.MaeR == null)
            .Join(dbContext.JournalTrades.Where(t => t.Status == TradeStatus.Closed), r => r.TradeId, t => t.Id, (r, _) => r)
            .Take(ExcursionBatch)
            .ToListAsync(cancellationToken);

        if (missing.Count == 0)
        {
            return;
        }

        long[] ids = missing.Select(r => r.TradeId).ToArray();
        Dictionary<long, JournalTrade> trades = await dbContext.JournalTrades.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (TradeRecord record in missing)
        {
            if (!trades.TryGetValue(record.TradeId, out JournalTrade? trade) || trade.ExitTime is not { } exit || (trade.InitialStopLoss ?? trade.StopLoss) is not { } stop)
            {
                record.MaeR = 0;
                record.MfeR = 0;
                continue;
            }

            try
            {
                bool recent = now - trade.EntryTime < TimeSpan.FromDays(700);
                IReadOnlyList<Candle> candles = await marketData.GetCandlesAsync(trade.Symbol, recent ? "1h" : "1d", recent ? "2y" : "10y", cancellationToken);
                List<Candle> during = candles.Where(c => c.Time >= trade.EntryTime.AddHours(recent ? -1 : -24) && c.Time <= exit).ToList();

                if (TradeExcursion.Compute(trade.Direction, trade.EntryPrice, stop, during) is { } excursion)
                {
                    record.MaeR = excursion.MaeR;
                    record.MfeR = excursion.MfeR;
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug("No candles for excursion of trade {Id}: {Message}", trade.Id, ex.Message);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SystemTrade>> LoadTradesAsync(string userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<JournalTradeDto> trades = await journal.ListAsync(userId, null, cancellationToken);
        Dictionary<long, TradeRecord> records = await dbContext.TradeRecords.AsNoTracking().Where(r => r.UserId == userId).ToDictionaryAsync(r => r.TradeId, cancellationToken);

        return trades.Select(t => new SystemTrade(t, records.GetValueOrDefault(t.Id))).ToList();
    }

    private async Task<int> PendingCountAsync(string userId, CancellationToken cancellationToken) =>
        await dbContext.TradeRecords
            .Where(r => r.UserId == userId && r.ReviewedAt == null)
            .Join(dbContext.JournalTrades.Where(t => t.Status == TradeStatus.Closed), r => r.TradeId, t => t.Id, (r, _) => r)
            .CountAsync(cancellationToken);

    private async Task<(bool Editable, string? Reason)> EditableAsync(string userId, TradingPlan? current, long? reviewId, CancellationToken cancellationToken)
    {
        if (current is null || !await dbContext.TradeRecords.AnyAsync(r => r.UserId == userId && r.PlanVersion == current.Version, cancellationToken))
        {
            return (true, null);
        }

        DateTimeOffset since = timeProvider.GetUtcNow() - ReviewEditWindow;
        List<WeeklyReview> recent = await dbContext.WeeklyReviews.AsNoTracking()
            .Where(r => r.UserId == userId && (reviewId == null || r.Id == reviewId))
            .ToListAsync(cancellationToken);

        return recent.Any(r => r.CreatedAt >= since)
            ? (true, null)
            : (false, $"Já há trades feitas com a versão {current.Version} do plano. Só podes mudá-lo nas 24 horas a seguir a uma revisão semanal (separador Revisões).");
    }

    private async Task<TradingRules> DefaultRulesAsync(string userId, CancellationToken cancellationToken)
    {
        AccountProfile account = await journal.GetAccountAsync(userId, cancellationToken);
        TradingRules rules = new();

        return rules with
        {
            RiskPercent = account.RiskPercent > 0 ? account.RiskPercent : rules.RiskPercent,
            MaxDailyLossPercent = account.MaxDailyLossPercent > 0 ? account.MaxDailyLossPercent : rules.MaxDailyLossPercent,
            MaxTradesPerDay = account.MaxTradesPerDay > 0 ? account.MaxTradesPerDay : rules.MaxTradesPerDay
        };
    }

    private async Task<IReadOnlyList<MarketEvent>> LoadEventsAsync(string symbol, TradingRules rules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int hours = Math.Max(rules.EarningsBlackoutHours, rules.AvoidHighImpactMacro ? rules.MacroBlackoutHours : 0);

        if (hours <= 0)
        {
            return [];
        }

        try
        {
            CalendarFeed? feed = await calendar.GetAsync(Math.Clamp((int)Math.Ceiling(hours / 24.0) + 1, 2, 30), cancellationToken);
            List<MarketEvent> events = [];

            foreach (CalendarEvent e in feed?.Events ?? [])
            {
                DateTimeOffset time = CalendarTimes.TimeFor(e) ?? new DateTimeOffset(e.Date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

                if (e.Kind == CalendarKind.Earnings && string.Equals(e.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new MarketEvent($"Resultados de {symbol}", time, true));
                }
                else if (e.Kind == CalendarKind.Macro && MacroKnowledge.Find(e.Title)?.Importance == EventImportance.High && CalendarTimes.TimeFor(e) is { } macroTime)
                {
                    events.Add(new MarketEvent(e.Title, macroTime, false));
                }
            }

            return events;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not load calendar events for the pre-trade check: {Message}", ex.Message);
            return [];
        }
    }

    public async Task<IReadOnlyList<OpenExposure>> ExposureAsync(string symbol, IReadOnlyList<JournalTradeDto> trades, CancellationToken cancellationToken)
    {
        List<JournalTradeDto> open = trades
            .Where(t => t.Status == TradeStatus.Open && !string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Take(MaxCorrelationSymbols)
            .ToList();

        if (open.Count == 0)
        {
            return [];
        }

        IReadOnlyList<Candle>? baseCandles = await DailyCandlesAsync(symbol, cancellationToken);
        List<OpenExposure> result = [];

        foreach (JournalTradeDto trade in open)
        {
            double? correlation = null;

            if (baseCandles is not null && await DailyCandlesAsync(trade.Symbol, cancellationToken) is { } other)
            {
                List<DateOnly> days = baseCandles.Select(c => DateOnly.FromDateTime(c.Time.UtcDateTime))
                    .Intersect(other.Select(c => DateOnly.FromDateTime(c.Time.UtcDateTime)))
                    .Order()
                    .ToList();

                if (days.Count >= 30)
                {
                    correlation = Math.Round(SimpleIndicators.Correlation(SimpleIndicators.DailyReturns(baseCandles, days), SimpleIndicators.DailyReturns(other, days)), 2);
                }
            }

            result.Add(new OpenExposure(trade.Symbol, trade.Direction, correlation));
        }

        return result;
    }

    private async Task<IReadOnlyList<Candle>?> DailyCandlesAsync(string symbol, CancellationToken cancellationToken)
    {
        try
        {
            return await marketData.GetCandlesAsync(symbol, "1d", "6mo", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("No daily candles for {Symbol}: {Message}", symbol, ex.Message);
            return null;
        }
    }

    private async Task<DateOnly> ReviewWeekAsync(string userId, CancellationToken cancellationToken)
    {
        DateOnly today = CircuitBreakers.LocalDate(timeProvider.GetUtcNow(), Zone);
        DateOnly week = CircuitBreakers.WeekStart(today);
        DateOnly previous = week.AddDays(-7);

        if (today.DayNumber - week.DayNumber < 2 && !await dbContext.WeeklyReviews.AnyAsync(r => r.UserId == userId && r.WeekStart == previous, cancellationToken))
        {
            return previous;
        }

        return week;
    }

    private async Task<bool> WeeklyReviewDueAsync(string userId, IReadOnlyList<SystemTrade> trades, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateOnly today = CircuitBreakers.LocalDate(now, Zone);
        DateOnly week = CircuitBreakers.WeekStart(today);
        DateOnly previous = week.AddDays(-7);
        List<DateOnly> reviewed = await dbContext.WeeklyReviews.AsNoTracking().Where(r => r.UserId == userId && r.WeekStart >= previous).Select(r => r.WeekStart).ToListAsync(cancellationToken);

        bool HadTrades(DateOnly start) => trades.Any(t => t.Record is not null && CircuitBreakers.LocalDate(t.Trade.EntryTime, Zone) is var d && d >= start && d < start.AddDays(7));

        return (HadTrades(previous) && !reviewed.Contains(previous))
            || (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && HadTrades(week) && !reviewed.Contains(week));
    }

    private async Task<PhaseState> GetPhaseAsync(string userId, CancellationToken cancellationToken)
    {
        AppSetting? setting = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == PhaseKey(userId), cancellationToken);

        return setting is null
            ? new PhaseState(SystemPhase.Paper, DateTimeOffset.MinValue)
            : JsonSerializer.Deserialize<PhaseState>(setting.Value, JsonOptions) ?? new PhaseState(SystemPhase.Paper, DateTimeOffset.MinValue);
    }

    private async Task SavePhaseAsync(string userId, PhaseState state, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(state, JsonOptions);
        AppSetting? setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == PhaseKey(userId), cancellationToken);

        if (setting is null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = PhaseKey(userId), Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string PhaseKey(string userId) => $"sysphase:{userId}";

    private static TradingRules Rules(TradingPlan plan) => JsonSerializer.Deserialize<TradingRules>(plan.RulesJson, JsonOptions) ?? new TradingRules();

    private static SetupStrategy Strategy(TradingSetup setup) => JsonSerializer.Deserialize<SetupStrategy>(setup.StrategyJson, JsonOptions) ?? new SetupStrategy();

    private static string Trim(string? value, int max) => (value?.Trim() ?? string.Empty) is var text && text.Length > max ? text[..max] : value?.Trim() ?? string.Empty;

    private static TradingSetupDto ToDto(TradingSetup s, PerformanceGroup? performance) => new(
        s.Id,
        s.Name,
        s.Description,
        s.EntryCriteria,
        s.ExitCriteria,
        s.Checklist.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        s.Image,
        Strategy(s),
        s.Active,
        performance);

    private static TradeRecordDto ToDto(TradeRecord r) => new(
        r.TradeId,
        r.PlanVersion,
        r.SetupId,
        r.SetupName,
        r.Phase,
        JsonSerializer.Deserialize<List<CheckItem>>(r.ChecklistJson, JsonOptions) ?? [],
        r.RuleBreaks,
        r.Justification,
        r.DossierJson is not null,
        r.ManagementFollowed,
        r.ReviewedAt,
        r.FollowedPlan,
        r.WentWell,
        r.WentWrong,
        r.Lesson,
        r.MaeR,
        r.MfeR);

    private static WeeklyReviewDto ToDto(WeeklyReview r) => new(
        r.Id,
        r.WeekStart,
        r.CreatedAt,
        JsonSerializer.Deserialize<WeekSummary>(r.SummaryJson, JsonOptions) ?? new WeekSummary(r.WeekStart, 0, 0, 0, 0, 0, 0, 0, null, 0, []),
        r.WhatWorked,
        r.WhatFailed,
        r.RuleBreaks,
        r.Decision);
}
