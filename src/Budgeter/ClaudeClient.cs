using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Budgeter;

/// <summary>What the app needs from an LLM: one prompt in, JSON matching a schema out. Swap for a fake in tests.</summary>
public interface ILlm
{
    Task<string> AskJsonAsync(string system, string user, string jsonSchema, CancellationToken ct = default);
}

/// <summary>Claude through the official Anthropic SDK. Reads ANTHROPIC_API_KEY from the environment.</summary>
public sealed class ClaudeClient(string model) : ILlm
{
    private readonly AnthropicClient _client = new();

    public async Task<string> AskJsonAsync(string system, string user, string jsonSchema, CancellationToken ct = default)
    {
        var request = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                // Structured output: the reply always parses against the schema.
                Format = new BetaJsonOutputFormat
                {
                    Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonSchema)!,
                },
            },
            // If a safety classifier declines, the API re-serves the request on a fallback model.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = new List<BetaTextBlockParam> { new() { Text = system } },
            Messages = [new BetaMessageParam { Role = Role.User, Content = user }],
        };

        var response = await _client.Beta.Messages.Create(request, cancellationToken: ct);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude declined the request.");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("Claude's reply was cut off (max_tokens).");

        return string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
    }
}
