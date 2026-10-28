using System.Globalization;
using System.Text.Json;

namespace Budgeter;

/// <summary>Writes the one-line comment on a purchase: harsh at bars, sassy elsewhere, nothing for essentials.</summary>
public sealed class Roaster(ILlm llm, TimeSpan? timeout = null)
{
    public static readonly string[] Essentials = ["groceries", "gas", "bills", "health"];

    /// <summary>Longest comment kept; the whole text should still read as one SMS-sized message.</summary>
    public const int MaxLength = 120;

    private const string System =
        "You write the one-line comment at the end of a text message about a purchase the user just made. The " +
        "user built this app to be roasted about their spending and asked for it; they're an adult. Write exactly " +
        "one punchy sentence, under 100 characters, about this purchase and where it leaves their budget. " +
        "Don't repeat the amount or what's left; the message already says it. Don't mention the time of day or " +
        "day of the week; you don't know when it happened. No hashtags, no emojis, no quotes. " +
        "Roast the spending and the choices, never the person's looks, identity or health. Never encourage " +
        "drinking more, and never suggest driving after drinking.";

    private static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "comment" },
        properties = new { comment = new { type = "string" } },
    });

    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    /// <summary>1 = under pace, 2 = ahead of pace, 3 = over the monthly limit.</summary>
    public static int Heat(CategoryStatus status, DateTimeOffset when)
    {
        if (status.Left < 0) return 3;
        var local = when.ToLocalTime();
        var monthGone = (decimal)local.Day / DateTime.DaysInMonth(local.Year, local.Month);
        return status.Limit > 0 && status.Spent / status.Limit > monthGone ? 2 : 1;
    }

    public static string Tone(string category, int heat) => category switch
    {
        "bars" => heat switch
        {
            1 => "Tone: harsh and blunt. Profanity is allowed. They're drinking again; make them feel it.",
            2 => "Tone: very harsh. Profanity encouraged. They're ahead of pace on bar spending; tear into it.",
            _ => "Tone: brutal, as harsh as you can be about the spending. Profanity encouraged. They've blown " +
                 "the whole monthly bar budget; tell them to stop and go home.",
        },
        _ => heat switch
        {
            1 => "Tone: lightly sassy, a playful eye-roll. No profanity.",
            2 => "Tone: sassy and pointed, they're ahead of pace. No profanity, not vicious.",
            _ => "Tone: sassy and disappointed, they're over budget here. No profanity, not vicious.",
        },
    };

    /// <summary>The comment, or null for essentials, no budget, or when Sonnet fails or is slow.</summary>
    public async Task<string?> CommentAsync(Purchase purchase, string category, CategoryStatus? status,
        CancellationToken ct = default)
    {
        if (status is null || Essentials.Contains(category)) return null;

        var heat = Heat(status, purchase.Posted);
        var local = purchase.Posted.ToLocalTime();
        // No time of day: SimpleFIN's timestamp is when the charge posted, not when it was made.
        var user =
            $"Purchase: {purchase.Amount.ToString("C", Usd)} at {purchase.Description.Trim()}.\n" +
            $"Category: {category}. Monthly limit {status.Limit.ToString("C0", Usd)}, spent " +
            $"{status.Spent.ToString("C0", Usd)} so far, day {local.Day} of " +
            $"{DateTime.DaysInMonth(local.Year, local.Month)}.\n" +
            Tone(category, heat);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            var json = await llm.AskJsonAsync(System, user, Schema, cts.Token);
            var comment = JsonSerializer.Deserialize<Reply>(json, JsonOptions.Web)?.Comment?.Trim();
            return string.IsNullOrEmpty(comment) ? null : Shorten(comment);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure (API error, refusal, bad JSON, timeout) just means a plain text goes out instead.
            Console.Error.WriteLine($"No roast this time: {ex.Message}");
            return null;
        }
    }

    /// <summary>Cuts an over-long comment at a word boundary.</summary>
    public static string Shorten(string comment)
    {
        if (comment.Length <= MaxLength) return comment;
        var cut = comment.LastIndexOf(' ', MaxLength - 1);
        return comment[..(cut > 0 ? cut : MaxLength - 1)].TrimEnd(',', ';', ':', ' ') + "...";
    }

    private sealed record Reply(string? Comment);
}
