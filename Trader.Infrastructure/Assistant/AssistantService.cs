using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Trader.Core.Abstractions;
using Trader.Core.Assistant;
using Trader.Core.Models;
using Trader.Core.TradingSystem;

namespace Trader.Infrastructure.Assistant;

public sealed class AssistantService(
    IAssistantModel model,
    ITradeDossierService dossiers,
    IInstrumentService instrumentService,
    ITradingSystemService system,
    IOptions<AssistantOptions> options,
    TimeProvider timeProvider) : IAssistantService
{
    private const int MaxMessageLength = 2000;
    private const int MaxHistoryTextLength = 4000;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");
    private readonly AssistantOptions _options = options.Value;

    public AssistantStatus GetStatus() => model.IsEnabled
        ? new AssistantStatus(true, model.Name, model.SupportsWebSearch, model.SupportsWebSearch
            ? "Pesquisa web ligada. As tuas perguntas e os dados do ativo são enviados ao serviço do modelo."
            : "Modelo local: nada sai do teu computador, mas não pesquisa na web.")
        : new AssistantStatus(false, "sem modelo", false, "Sem modelo configurado: recolho o dossiê e as regras, mas não escrevo texto. Configura o Claude ou o Ollama no servidor.");

    public async Task<AssistantReply> AskAsync(string userId, AssistantRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string message = request.Message?.Trim() ?? string.Empty;

        if (message.Length == 0)
        {
            return Reply("Diz-me o que queres saber, por exemplo: «vou comprar NVDA, o que devo ver antes?».", null, false, false, null, null, now);
        }

        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength];
        }

        IReadOnlyList<Instrument> instruments = await instrumentService.ListAsync(cancellationToken);
        AssistantIntent intent = AssistantIntentParser.Parse(message, instruments.Select(i => new InstrumentName(i.Symbol, i.Name)).ToList(), request.Symbol);
        TradeDirection? direction = intent.Direction ?? request.Direction;

        TradeDossier? dossier = intent.Symbol is { } symbol ? await dossiers.BuildAsync(userId, symbol, direction, cancellationToken) : null;
        string facts = dossier is not null ? DossierText.Facts(dossier) : await GeneralFactsAsync(userId, cancellationToken);
        bool propose = intent.WantsTrade && dossier is not null;

        if (!model.IsEnabled)
        {
            string text = dossier is null
                ? $"{facts}\nPara eu escrever respostas ou procurar na web falta configurar um modelo no servidor (Claude ou Ollama). Se quiseres um dossiê, indica o ativo."
                : $"{dossier.Summary}\n\nSem modelo configurado, só te mostro os dados recolhidos. Abre o dossiê para veres tudo.";

            return Reply(text, "sem modelo", false, propose, dossier, null, now);
        }

        List<ChatTurn> turns = (request.History ?? [])
            .Where(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Text))
            .TakeLast(_options.MaxHistoryTurns)
            .Select(t => new ChatTurn(t.Role, t.Text.Length > MaxHistoryTextLength ? t.Text[..MaxHistoryTextLength] : t.Text))
            .ToList();

        while (turns.Count > 0 && turns[0].Role != "user")
        {
            turns.RemoveAt(0);
        }

        if (turns.Count > 0 && turns[^1].Role == "user")
        {
            turns.RemoveAt(turns.Count - 1);
        }

        turns.Add(new ChatTurn("user", AssistantPrompt.UserMessage(message, facts)));

        AssistantModelReply? reply = await model.AnswerAsync(AssistantPrompt.System(now, model.SupportsWebSearch), turns, cancellationToken);

        if (reply is null)
        {
            string fallback = dossier?.Summary ?? "Não consegui falar com o modelo agora.";
            return Reply(fallback, model.Name, false, propose, dossier, "O modelo não respondeu; mostro-te só o resumo dos dados.", now);
        }

        IReadOnlyList<AssistantSource> sources = [.. (dossier?.News.Select(n => new AssistantSource(n.Title, n.Url)) ?? []), .. reply.Sources];
        return new AssistantReply(reply.Text, model.Name, true, dossier, propose, sources, dossier?.Symbol, null, now);
    }

    private async Task<string> GeneralFactsAsync(string userId, CancellationToken cancellationToken)
    {
        SystemStatus status = await system.GetStatusAsync(userId, cancellationToken);
        StringBuilder sb = new();
        sb.AppendLine("Não foi indicado nenhum ativo nesta pergunta.");
        sb.AppendLine($"PLANO DE TRADING: {(status.PlanExists ? $"versão {status.PlanVersion}" : "ainda não escrito")}; setups no playbook: {status.SetupCount}.");
        sb.AppendLine($"HOJE: {status.TradesToday} de {status.MaxTradesPerDay} trades; resultado {status.DailyPnl.ToString("+0.00;-0.00", Pt)} {status.Currency} (limite -{status.DailyLossLimit.ToString("0.00", Pt)}); semana {status.WeeklyPnl.ToString("+0.00;-0.00", Pt)} {status.Currency}.");
        sb.AppendLine($"POSIÇÕES ABERTAS: {status.OpenPositions} de {status.MaxOpenPositions}. FASE: {SystemStatistics.PhaseLabel(status.Phase.Phase)}. {(status.CanTrade ? "Podes abrir trades." : $"BLOQUEADO: {string.Join(" ", status.Blockers)}")}");

        return sb.ToString();
    }

    private static AssistantReply Reply(string text, string? provider, bool usedModel, bool propose, TradeDossier? dossier, string? notice, DateTimeOffset now) =>
        new(text, provider ?? "sem modelo", usedModel, dossier, propose, [], dossier?.Symbol, notice, now);
}
