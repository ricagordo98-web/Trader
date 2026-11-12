using Trader.Core.Journal;
using Trader.Core.TradingSystem;

namespace Trader.Core.Abstractions;

public interface ITradingSystemService
{
    Task<TradingPlanDto> GetPlanAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> SavePlanAsync(string userId, SavePlanRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<TradingSetupDto>> ListSetupsAsync(string userId, CancellationToken cancellationToken);

    Task<(TradingSetupDto? Setup, IReadOnlyList<string> Errors)> SaveSetupAsync(string userId, long? id, SaveSetupRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteSetupAsync(string userId, long id, CancellationToken cancellationToken);

    Task<SetupBacktestResult?> BacktestSetupAsync(string userId, long id, SetupBacktestRequest request, CancellationToken cancellationToken);

    Task<PreTradeCheck> CheckAsync(string userId, PreTradeRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenExposure>> ExposureAsync(string symbol, IReadOnlyList<JournalTradeDto> trades, CancellationToken cancellationToken);

    Task<SystemTradeResult> OpenAsync(string userId, SystemTradeRequest request, CancellationToken cancellationToken);

    Task<TradeRecordDto?> GetRecordAsync(string userId, long tradeId, CancellationToken cancellationToken);

    Task<string?> GetDossierAsync(string userId, long tradeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TradeRecordDto>> ListRecordsAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReviewAsync(string userId, long tradeId, TradeReviewRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingReview>> PendingReviewsAsync(string userId, CancellationToken cancellationToken);

    Task<WeekSummary> CurrentWeekAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WeeklyReviewDto>> ListWeeklyReviewsAsync(string userId, CancellationToken cancellationToken);

    Task<WeeklyReviewDto> SaveWeeklyReviewAsync(string userId, SaveWeeklyReviewRequest request, CancellationToken cancellationToken);

    Task<SystemStatus> GetStatusAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> PromoteAsync(string userId, CancellationToken cancellationToken);

    Task<SystemPerformance> GetPerformanceAsync(string userId, CancellationToken cancellationToken);
}
