using System.Net.Http.Json;
using System.Text.Json;

namespace Budgeter;

/// <summary>
/// Reads messages sent to the bot (Telegram long polling, so no public web address is needed) and answers the
/// linked chat only. Everyone else is ignored without a reply.
/// </summary>
public sealed class TelegramBot(HttpClient http, string botToken, string chatId, Assistant assistant, string offsetPath)
{
    /// <summary>How long Telegram holds each getUpdates request open waiting for a message.</summary>
    public const int LongPollSeconds = 50;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(LongPollSeconds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A network blip or a Telegram error must never stop watch; wait a bit and listen again.
                Console.Error.WriteLine($"{DateTime.Now:MMM d h:mm tt}: Telegram listen failed: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    /// <summary>Fetches pending messages once and answers each one from the linked chat. Returns how many were answered.</summary>
    public async Task<int> PollOnceAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        var offset = File.Exists(offsetPath) ? long.Parse(File.ReadAllText(offsetPath)) : 0;
        using var response = await http.GetAsync(
            Telegram.Url(botToken, "getUpdates") +
            $"?timeout={timeoutSeconds}&offset={offset}&allowed_updates={Uri.EscapeDataString("[\"message\"]")}", ct);
        if (!response.IsSuccessStatusCode)
            throw new AlertException($"Telegram getUpdates failed ({(int)response.StatusCode}): " +
                                     await Telegram.Description(response, ct));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var answered = 0;
        foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            // Move past this update first, so a message that makes us fail isn't retried forever.
            AtomicFile.Write(offsetPath, (update.GetProperty("update_id").GetInt64() + 1).ToString());
            if (!update.TryGetProperty("message", out var message)) continue;
            if (message.GetProperty("chat").GetProperty("id").GetRawText() != chatId) continue;
            if (!message.TryGetProperty("text", out var textElement) || textElement.GetString() is not { Length: > 0 } text)
                continue;

            await SendTypingAsync(ct);
            var reply = await assistant.HandleAsync(text, ct);
            using var sent = await http.PostAsJsonAsync(Telegram.Url(botToken, "sendMessage"),
                new { chat_id = chatId, text = reply }, ct);
            if (!sent.IsSuccessStatusCode)
                throw new AlertException($"Telegram reply failed ({(int)sent.StatusCode}): " + await Telegram.Description(sent, ct));
            answered++;
        }
        return answered;
    }

    /// <summary>Shows "typing..." while Sonnet thinks; purely cosmetic, so failures are ignored.</summary>
    private async Task SendTypingAsync(CancellationToken ct)
    {
        try
        {
            using var _ = await http.PostAsJsonAsync(Telegram.Url(botToken, "sendChatAction"),
                new { chat_id = chatId, action = "typing" }, ct);
        }
        catch (HttpRequestException)
        {
        }
    }
}
