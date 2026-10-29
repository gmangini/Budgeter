using System.Net.Http.Json;
using System.Text.Json;

namespace Budgeter;

/// <summary>
/// Reads messages and button taps sent to the bot (Telegram long polling, so no public web address is needed)
/// and answers the linked chat only. Everyone else is ignored without a reply.
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

    /// <summary>Fetches pending updates once and answers each one from the linked chat. Returns how many were answered.</summary>
    public async Task<int> PollOnceAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        var offset = File.Exists(offsetPath) ? long.Parse(File.ReadAllText(offsetPath)) : 0;
        using var response = await http.GetAsync(
            Telegram.Url(botToken, "getUpdates") +
            $"?timeout={timeoutSeconds}&offset={offset}&allowed_updates={Uri.EscapeDataString("[\"message\",\"callback_query\"]")}", ct);
        if (!response.IsSuccessStatusCode)
            throw new AlertException($"Telegram getUpdates failed ({(int)response.StatusCode}): " +
                                     await Telegram.Description(response, ct));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var answered = 0;
        foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            // Move past this update first, so one that makes us fail isn't retried forever.
            AtomicFile.Write(offsetPath, (update.GetProperty("update_id").GetInt64() + 1).ToString());

            if (update.TryGetProperty("message", out var message))
            {
                if (ChatOf(message) != chatId) continue;
                if (!message.TryGetProperty("text", out var textElement) || textElement.GetString() is not { Length: > 0 } text)
                    continue;
                await SendTypingAsync(ct);
                await Telegram.SendAsync(http, botToken, chatId, await assistant.HandleReplyAsync(text, ct), ct);
                answered++;
            }
            else if (update.TryGetProperty("callback_query", out var tap))
            {
                // A tap counts only if it's on a message in my chat and made by me (in a private chat, same id).
                if (!tap.TryGetProperty("message", out var tapped) || ChatOf(tapped) != chatId) continue;
                if (tap.GetProperty("from").GetProperty("id").GetRawText() != chatId) continue;

                await PostAsync("answerCallbackQuery", new { callback_query_id = tap.GetProperty("id").GetString() }, ct);
                // Take the buttons off the tapped message so the same choice can't be made twice.
                await PostAsync("editMessageReplyMarkup", new
                {
                    chat_id = chatId,
                    message_id = tapped.GetProperty("message_id").GetInt64(),
                    reply_markup = new { inline_keyboard = Array.Empty<object>() },
                }, ct);
                var data = tap.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
                await Telegram.SendAsync(http, botToken, chatId, await assistant.HandleButtonAsync(data, ct), ct);
                answered++;
            }
        }
        return answered;
    }

    private static string ChatOf(JsonElement message) => message.GetProperty("chat").GetProperty("id").GetRawText();

    /// <summary>Cosmetic calls (typing indicator, removing buttons, stopping the tap spinner): failures are ignored.</summary>
    private async Task PostAsync(string method, object body, CancellationToken ct)
    {
        try
        {
            using var _ = await http.PostAsJsonAsync(Telegram.Url(botToken, method), body, ct);
        }
        catch (HttpRequestException)
        {
        }
    }

    private Task SendTypingAsync(CancellationToken ct) => PostAsync("sendChatAction", new { chat_id = chatId, action = "typing" }, ct);
}
