using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Budgeter;

public interface ISmsSender
{
    /// <summary>True when messages reach a real phone (false for print mode).</summary>
    bool IsReal { get; }

    Task SendAsync(string body, CancellationToken ct = default);
}

/// <summary>Print mode: shows each text instead of sending it. No Twilio needed.</summary>
public sealed class PrintSmsSender(TextWriter output) : ISmsSender
{
    public bool IsReal => false;

    public Task SendAsync(string body, CancellationToken ct = default)
    {
        output.WriteLine($"[SMS] {body}");
        return Task.CompletedTask;
    }
}

/// <summary>Sends through Twilio's Messages REST API to one fixed number.</summary>
public sealed class TwilioSmsSender(HttpClient http, string accountSid, string authToken, string from, string to)
    : ISmsSender
{
    public bool IsReal => true;

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
            throw new SmsException($"Twilio send failed ({(int)response.StatusCode}): " +
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

public sealed class SmsException(string message) : Exception(message);
