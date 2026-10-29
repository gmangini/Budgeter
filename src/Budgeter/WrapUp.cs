using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Budgeter;

public sealed record LimitSuggestion(string Category, decimal Limit, string Reason);

/// <summary>
/// Last month's wrap-up: spent vs limit (computed here), a short take from Sonnet, and up to 3 suggested limit
/// changes, each with an Apply button. Suggestions are saved so a tap days later still knows what it meant.
/// </summary>
public sealed class MonthlyWrapUp(ILlm llm, BudgetStore budgets, string storePath)
{
    public const int MaxSuggestions = 3;
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    private const string System =
        "You review one person's credit card spending for last month against their monthly budget and suggest at " +
        "most 3 limit changes for next month. Only suggest a change when it clearly helps: raise a limit they keep " +
        "blowing on an essential (groceries, gas, bills, health, transport), lower one they barely used, and for " +
        "discretionary categories they went over, usually hold the line or cut, never reward overspending at bars. " +
        "Limits are whole dollars. Each reason is one short sentence. The take is one or two blunt, slightly sassy " +
        "sentences about how the month went. Suggesting nothing is fine.";

    private static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "take", "suggestions" },
        properties = new
        {
            take = new { type = "string" },
            suggestions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "category", "limit", "reason" },
                    properties = new
                    {
                        category = new { type = "string", @enum = Categorizer.Categories },
                        limit = new { type = "number" },
                        reason = new { type = "string" },
                    },
                },
            },
        },
    });

    public static string MonthKey(DateTime month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The wrap-up for the month before <paramref name="now"/>, or null without a budget or any spending then.</summary>
    public async Task<BotReply?> BuildAsync(IReadOnlyCollection<Purchase> purchases,
        IReadOnlyDictionary<string, string> categories, DateTimeOffset now, CancellationToken ct = default)
    {
        if (budgets.Active is not { } plan) return null;
        var local = now.ToLocalTime();
        var month = new DateTime(local.Year, local.Month, 1).AddMonths(-1);
        var inMonth = purchases.Where(p => p.Posted.ToLocalTime() is var t && t.Year == month.Year && t.Month == month.Month).ToList();
        if (inMonth.Count == 0) return null;

        // Any moment inside that month picks it out; mid-month, so a daylight-saving change at either end
        // can't push it into the next or previous month.
        var midMonth = month.AddDays(14).AddHours(12);
        var rows = BudgetPlanner.Status(plan, inMonth, categories, new DateTimeOffset(midMonth, TimeZoneInfo.Local.GetUtcOffset(midMonth)));
        var over = rows.Where(r => r.Left < 0).OrderBy(r => r.Left).ToList();
        string Money(decimal d) => d.ToString("C0", Usd);

        var sb = new StringBuilder($"{month:MMMM} wrap-up: spent {Money(rows.Sum(r => r.Spent))} of {Money(rows.Sum(r => r.Limit))}.");
        sb.Append(over.Count == 0
            ? "\nUnder budget in every category."
            : "\nOver in " + string.Join(", ", over.Select(r => $"{r.Category} (+{Money(-r.Left)})")) + ".");
        var under = rows.Where(r => r.Left >= 0 && r.Limit > 0).Count();
        if (under > 0) sb.Append($"\nUnder or on budget in {under} other categor{(under == 1 ? "y" : "ies")}.");

        Reply? reply = null;
        try
        {
            var user = $"Month: {month:MMMM yyyy}\n" + string.Join("\n",
                rows.OrderBy(r => r.Category).Select(r => $"- {r.Category}: limit {Money(r.Limit)}, spent {Money(r.Spent)}"));
            reply = JsonSerializer.Deserialize<Reply>(await llm.AskJsonAsync(System, user, Schema, ct), JsonOptions.Web);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Wrap-up suggestions failed, sending the numbers only: {ex.GetType().Name}: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(reply?.Take)) sb.Append($"\n\n{reply.Take.Trim()}");
        var current = plan.Categories.ToDictionary(c => c.Category, c => c.Limit);
        var suggestions = (reply?.Suggestions ?? [])
            .Where(s => Categorizer.Categories.Contains(s.Category) && s.Limit >= 0)
            .Select(s => s with { Limit = Math.Round(s.Limit) })
            .Where(s => current.GetValueOrDefault(s.Category) != s.Limit)
            .DistinctBy(s => s.Category)
            .Take(MaxSuggestions)
            .ToList();
        if (suggestions.Count == 0) return new BotReply(sb.ToString());

        var key = MonthKey(month);
        Save(key, new Saved(suggestions, []));
        sb.Append("\n\nSuggested for this month:");
        for (var i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            sb.Append($"\n{i + 1}. {s.Category}: {Money(current.GetValueOrDefault(s.Category))} -> {Money(s.Limit)}. {s.Reason.Trim()}");
        }
        var buttons = suggestions
            .Select((s, i) => new List<Button> { new($"Apply {i + 1}: {s.Category} {Money(s.Limit)}", $"wrap:{key}:{i}") })
            .Append([new Button("Keep my limits", $"wrap:{key}:skip")])
            .ToList();
        return new BotReply(sb.ToString(), buttons);
    }

    /// <summary>Turns a tapped button's data into the suggestion it stands for (once), or a reason it can't.</summary>
    public (LimitSuggestion? Suggestion, string? Message) Resolve(string data)
    {
        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != "wrap") return (null, "I don't know that button.");
        if (parts[2] == "skip") return (null, "Kept your limits as they are.");
        var all = Load();
        if (!all.TryGetValue(parts[1], out var saved) || !int.TryParse(parts[2], out var i) || i < 0 || i >= saved.Suggestions.Count)
            return (null, "That suggestion has expired.");
        if (saved.Used.Contains(i)) return (null, "Already applied.");
        Save(parts[1], saved with { Used = [.. saved.Used, i] });
        return (saved.Suggestions[i], null);
    }

    private sealed record Saved(List<LimitSuggestion> Suggestions, List<int> Used);

    private sealed record Reply(string? Take, List<LimitSuggestion>? Suggestions);

    private Dictionary<string, Saved> Load() =>
        File.Exists(storePath)
            ? JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(storePath), JsonOptions.Web) ?? []
            : [];

    private void Save(string key, Saved saved)
    {
        var all = Load();
        all[key] = saved;
        // Keep about a year of wrap-ups.
        foreach (var old in all.Keys.OrderDescending().Skip(12).ToList()) all.Remove(old);
        AtomicFile.Write(storePath, JsonSerializer.Serialize(all, JsonOptions.Indented));
    }
}

/// <summary>Days since a bar charge last posted, and the praise for each milestone.</summary>
public static class Streaks
{
    public static readonly int[] Milestones = [3, 7, 14, 30, 60, 90];

    /// <summary>
    /// The streak start (the day of the last bar charge, or the start of history if there's none) and its length
    /// in whole days. Based on posted dates, so a bar tab that posts late can end a streak after the fact.
    /// </summary>
    public static (DateTime Since, int Days, bool AnyBarCharge) BarStreak(IReadOnlyCollection<Purchase> purchases,
        IReadOnlyDictionary<string, string> categories, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        var bars = purchases.Where(p => !p.IsRefund && categories.GetValueOrDefault(Categorizer.Key(p.Description)) == "bars").ToList();
        var since = bars.Count > 0
            ? bars.Max(p => p.Posted).ToLocalTime().Date
            : purchases.Count > 0 ? purchases.Min(p => p.Posted).ToLocalTime().Date : today;
        return (since, (int)(today - since).TotalDays, bars.Count > 0);
    }

    public static int MilestoneFor(int days) => Milestones.Where(m => m <= days).DefaultIfEmpty(0).Max();

    public static string Praise(int milestone, decimal? monthlyBarAverage)
    {
        var saved = monthlyBarAverage is > 0 ? $" That's roughly {(monthlyBarAverage.Value * milestone / 30m).ToString("C0", CultureInfo.GetCultureInfo("en-US"))} you didn't pour down your throat." : "";
        return milestone switch
        {
            3 => "3 days without a bar tab. Look at you, drinking water like a functioning adult.",
            7 => "A full week with no bar tab. Your liver sent a thank-you note." + saved,
            14 => "Two weeks bar-free. Who even are you? Keep going." + saved,
            30 => "30 days without a bar tab. Genuinely impressive." + saved,
            60 => "60 days with no bar tab. Legend behavior." + saved,
            _ => $"{milestone} days without a bar tab. I'm running out of compliments." + saved,
        };
    }

    public static string Status(IReadOnlyCollection<Purchase> purchases, IReadOnlyDictionary<string, string> categories,
        DateTimeOffset now)
    {
        if (purchases.Count == 0) return "No history synced yet, so no streak to show.";
        var (since, days, any) = BarStreak(purchases, categories, now);
        if (any && days == 0) return $"A bar charge posted today ({since:MMM d}). Streak: 0 days. Tomorrow's a new day.";
        var next = Milestones.FirstOrDefault(m => m > days);
        return (any
                   ? $"No bar charge has posted for {days} day{(days == 1 ? "" : "s")} (last one {since:MMM d})."
                   : $"No bar charges at all in the {days} days of history I have.") +
               (next > 0 ? $" Next milestone: {next} days." : "") +
               " Counted by posted date, so a late-posting tab can still show up.";
    }
}
