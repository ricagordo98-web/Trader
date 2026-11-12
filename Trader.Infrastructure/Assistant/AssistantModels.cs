using System.Net.Http.Json;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trader.Core.Abstractions;
using Trader.Core.Assistant;
using Trader.Infrastructure.Summary;

namespace Trader.Infrastructure.Assistant;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public string Provider { get; set; } = "Auto";

    public string AnthropicApiKey { get; set; } = string.Empty;

    public string AnthropicModel { get; set; } = "claude-opus-5-5";

    public bool WebSearch { get; set; } = true;

    public int MaxWebSearches { get; set; } = 4;

    public int MaxHistoryTurns { get; set; } = 12;
}

public sealed class ClaudeAssistantModel(IOptions<AssistantOptions> options, ILogger<ClaudeAssistantModel> logger) : IAssistantModel
{
    private const int MaxTokens = 4000;
    private readonly AssistantOptions _options = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.AnthropicApiKey)
        && _options.Provider is not ("Ollama" or "None");

    public string Name => $"Claude ({_options.AnthropicModel})";

    public bool SupportsWebSearch => _options.WebSearch;

    public async Task<AssistantModelReply?> AnswerAsync(string system, IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            AnthropicClient client = new() { ApiKey = _options.AnthropicApiKey };
            List<MessageParam> messages = turns
                .Select(t => t.Role == "assistant" ? (MessageParam)new AssistantMessageParam { Content = t.Text } : new UserMessageParam { Content = t.Text })
                .ToList();
            List<ToolUnion>? tools = _options.WebSearch
                ? [new ToolUnion(new WebSearchTool20260209 { MaxUses = _options.MaxWebSearches })]
                : null;

            List<string> text = [];
            Dictionary<string, AssistantSource> sources = [];

            Message response = await client.Messages.Create(new MessageCreateParams
            {
                Model = _options.AnthropicModel,
                MaxTokens = MaxTokens,
                System = system,
                Messages = messages,
                Tools = tools,
                OutputConfig = new OutputConfig { Effort = Effort.Medium }
            }, cancellationToken: cancellationToken);

            if (response.StopReason == "refusal")
            {
                return new AssistantModelReply("O modelo recusou responder a este pedido. Reformula a pergunta.", []);
            }

            foreach (ContentBlock block in response.Content)
            {
                if (block.TryPickText(out TextBlock? textBlock))
                {
                    text.Add(textBlock.Text);
                }
                else if (block.TryPickWebSearchToolResult(out WebSearchToolResultBlock? result) && result.Content.Value is IEnumerable<WebSearchResultBlock> hits)
                {
                    foreach (WebSearchResultBlock hit in hits)
                    {
                        sources.TryAdd(hit.Url, new AssistantSource(hit.Title, hit.Url));
                    }
                }
            }

            string answer = string.Join(string.Empty, text).Trim();
            return answer.Length == 0 ? null : new AssistantModelReply(answer, sources.Values.ToList());
        }
        catch (Exception ex) when (ex is Anthropic.Exceptions.AnthropicApiException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Claude assistant unavailable: {Message}", ex.Message);
            return null;
        }
    }
}

public sealed class OllamaAssistantModel(HttpClient httpClient, IOptions<SummaryOptions> summary, IOptions<AssistantOptions> options, ILogger<OllamaAssistantModel> logger) : IAssistantModel
{
    private readonly SummaryOptions _summary = summary.Value;
    private readonly AssistantOptions _options = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_summary.OllamaModel) && _options.Provider != "None";

    public string Name => $"modelo local ({_summary.OllamaModel})";

    public bool SupportsWebSearch => false;

    public async Task<AssistantModelReply?> AnswerAsync(string system, IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            List<object> messages = [new { role = "system", content = system }];
            messages.AddRange(turns.Select(t => new { role = t.Role == "assistant" ? "assistant" : "user", content = t.Text }));

            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                $"{_summary.OllamaUrl.TrimEnd('/')}/api/chat",
                new
                {
                    model = _summary.OllamaModel,
                    messages,
                    stream = false,
                    keep_alive = "5m",
                    options = new { temperature = 0.2, num_ctx = 8192, num_predict = 900 }
                },
                cancellationToken);
            response.EnsureSuccessStatusCode();

            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            string? text = document.RootElement.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content)
                ? content.GetString()
                : null;

            return string.IsNullOrWhiteSpace(text) ? null : new AssistantModelReply(text.Trim(), []);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Local model unavailable for the assistant: {Message}", ex.Message);
            return null;
        }
    }
}

public sealed class FallbackAssistantModel(ClaudeAssistantModel claude, OllamaAssistantModel ollama, IOptions<AssistantOptions> options) : IAssistantModel
{
    private readonly AssistantOptions _options = options.Value;

    private IAssistantModel? Active => _options.Provider switch
    {
        "Claude" => claude.IsEnabled ? claude : null,
        "Ollama" => ollama.IsEnabled ? ollama : null,
        "None" => null,
        _ => claude.IsEnabled ? claude : ollama.IsEnabled ? ollama : null
    };

    public bool IsEnabled => Active is not null;

    public string Name => Active?.Name ?? "sem modelo";

    public bool SupportsWebSearch => Active?.SupportsWebSearch ?? false;

    public async Task<AssistantModelReply?> AnswerAsync(string system, IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        IAssistantModel? model = Active;

        if (model is null)
        {
            return null;
        }

        AssistantModelReply? reply = await model.AnswerAsync(system, turns, cancellationToken);

        if (reply is null && model == claude && _options.Provider == "Auto" && ollama.IsEnabled)
        {
            reply = await ollama.AnswerAsync(system, turns, cancellationToken);
        }

        return reply;
    }
}
