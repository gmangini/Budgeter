using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Budgeter;

/// <summary>A tap button under a Telegram message; <see cref="Data"/> (max 64 bytes) comes back when it's tapped.</summary>
public sealed record Button(string Text, string Data);

/// <summary>A message for the chat: text, optional rows of buttons, optional image (sent as a photo, text as caption).</summary>
public sealed record BotReply(string Text, List<List<Button>>? Buttons = null, byte[]? Photo = null);

/// <summary>Where purchase alerts go: Telegram, SMS, or the console in print mode.</summary>
public interface IAlertSender
{
    /// <summary>Sent once, on the first run that really delivers; null for none (print mode).</summary>
    string? Welcome { get; }

    /// <summary>Appended to every purchase alert (SMS needs its opt-out line; the others need nothing).</summary>
    string Footer { get; }

    Task SendAsync(string body, CancellationToken ct = default);

    /// <summary>Sends text with buttons; senders without buttons (SMS, print) send just the text.</summary>
    Task SendAsync(BotReply message, CancellationToken ct = default) => SendAsync(message.Text, ct);
}

public sealed class AlertException(string message) : Exception(message);

/// <summary>Print mode: shows each alert instead of sending it.</summary>
public sealed class PrintAlertSender(TextWriter output) : IAlertSender
{
    public string? Welcome => null;
    public string Footer => "";

    public Task SendAsync(string body, CancellationToken ct = default)
    {
        output.WriteLine($"[alert] {body}");
        return Task.CompletedTask;
    }

    public Task SendAsync(BotReply message, CancellationToken ct = default)
    {
        output.WriteLine($"[alert] {message.Text}");
        foreach (var row in message.Buttons ?? []) output.WriteLine("        " + string.Join("  ", row.Select(b => $"[{b.Text}]")));
        return Task.CompletedTask;
    }
}

/// <summary>A Telegram bot messaging one chat: yours, linked with `budgeter telegram link`.</summary>
public sealed class TelegramAlertSender(HttpClient http, string botToken, string chatId) : IAlertSender
{
    public string? Welcome =>
        "Budgeter is connected. You'll get a message here after each new purchase, with what's left in its budget.";

    public string Footer => "";

    public Task SendAsync(string body, CancellationToken ct = default) => SendAsync(new BotReply(body), ct);

    public Task SendAsync(BotReply message, CancellationToken ct = default) => Telegram.SendAsync(http, botToken, chatId, message, ct);
}

/// <summary>Telegram Bot API helpers. The bot token is part of every URL, so URLs are never printed.</summary>
public static class Telegram
{
    public static string Url(string botToken, string method) => $"https://api.telegram.org/bot{botToken}/{method}";

    /// <summary>Telegram limits: 4096 characters for a message, 1024 for a photo caption.</summary>
    public const int MaxText = 4096, MaxCaption = 1024;

    public static object? Keyboard(List<List<Button>>? buttons) =>
        buttons is { Count: > 0 }
            ? new { inline_keyboard = buttons.Select(row => row.Select(b => new { text = b.Text, callback_data = b.Data })) }
            : null;

    /// <summary>Sends a text message, or a photo with the text as its caption, with any buttons underneath.</summary>
    public static async Task SendAsync(HttpClient http, string botToken, string chatId, BotReply message,
        CancellationToken ct = default)
    {
        HttpResponseMessage response;
        if (message.Photo is { } photo)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(chatId), "chat_id" },
                { new StringContent(Truncate(message.Text, MaxCaption)), "caption" },
                { new ByteArrayContent(photo) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "photo", "chart.png" },
            };
            if (Keyboard(message.Buttons) is { } kb) form.Add(new StringContent(JsonSerializer.Serialize(kb)), "reply_markup");
            response = await http.PostAsync(Url(botToken, "sendPhoto"), form, ct);
        }
        else
        {
            response = await http.PostAsJsonAsync(Url(botToken, "sendMessage"), new SendMessage(chatId, Truncate(message.Text, MaxText), Keyboard(message.Buttons)), ct);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AlertException($"Telegram send failed ({(int)response.StatusCode}): " + await Description(response, ct));
        }
    }

    private sealed record SendMessage(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("reply_markup"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? ReplyMarkup);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    /// <summary>Telegram's error JSON has a readable "description"; show that, never the URL.</summary>
    public static async Task<string> Description(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public sealed record ChatCandidate(string ChatId, string Name);

    /// <summary>The private chat that most recently messaged the bot, for linking it.</summary>
    public static async Task<ChatCandidate?> LatestPrivateChatAsync(HttpClient http, string botToken,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Url(botToken, "getUpdates"), ct);
        if (!response.IsSuccessStatusCode)
            throw new AlertException($"Telegram getUpdates failed ({(int)response.StatusCode}): " +
                                     await Description(response, ct));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        ChatCandidate? latest = null;
        foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            if (!update.TryGetProperty("message", out var message)) continue;
            var chat = message.GetProperty("chat");
            if (chat.GetProperty("type").GetString() != "private") continue;
            var name = string.Join(" ", new[] { "first_name", "last_name", "username" }
                .Select(p => chat.TryGetProperty(p, out var v) ? (p == "username" ? "@" : "") + v.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s)));
            latest = new ChatCandidate(chat.GetProperty("id").GetRawText(), name);
        }
        return latest;
    }
}

/// <summary>Sends through Twilio's Messages REST API to one fixed number. Kept for if a 10DLC campaign is approved.</summary>
public sealed class TwilioSmsSender(HttpClient http, string accountSid, string authToken, string from, string to)
    : IAlertSender
{
    /// <summary>The opt-in confirmation registered with the carriers; sent once on the first real run.</summary>
    public const string OptInMessage =
        "Budgeter: You're signed up for personal budget and purchase alerts. Msg frequency varies (about 1-5/day). " +
        "Msg & data rates may apply. Reply HELP for help, STOP to opt out.";

    public string? Welcome => OptInMessage;
    public string Footer => " Reply STOP to opt out.";

    public async Task SendAsync(string body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(accountSid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = to,
                ["From"] = from,
                ["Body"] = body,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{accountSid}:{authToken}")));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AlertException($"Twilio send failed ({(int)response.StatusCode}): " +
                                     TwilioMessage(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Twilio's error JSON has a readable "message"; show that, not the whole body.</summary>
    private static string TwilioMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
