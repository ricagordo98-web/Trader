using Trader.Core.Assistant;
using Trader.Core.Models;

namespace Trader.Core.Abstractions;

public interface IAssistantModel
{
    bool IsEnabled { get; }

    string Name { get; }

    bool SupportsWebSearch { get; }

    Task<AssistantModelReply?> AnswerAsync(string system, IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken);
}

public interface ITradeDossierService
{
    Task<TradeDossier?> BuildAsync(string userId, string symbol, TradeDirection? direction, CancellationToken cancellationToken);
}

public interface IAssistantService
{
    Task<AssistantReply> AskAsync(string userId, AssistantRequest request, CancellationToken cancellationToken);

    AssistantStatus GetStatus();
}
