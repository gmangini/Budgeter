using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Budgeter;

/// <summary>
/// Answers chat messages: `help` and `budget` in code, "bars 200" in code, anything else through Sonnet, which
/// sees only the last saved sync (never a fresh SimpleFIN call) with the totals already worked out.
/// </summary>
public sealed partial class Assistant(
    SnapshotStore snapshots,
    Categorizer categorizer,
    BudgetStore budgets,
    ILlm llm,
    Func<DateTimeOffset> clock)
{
    public const string HelpText =
        "Ask me anything about your spending, like \"what did I spend today?\", \"how much on bars this week?\" " +
        "or \"am I on track this month?\".\n\n" +
        "Change a limit: \"bars 200\" or \"set coffee to 40\".\n" +
        "budget: this month's limits, spent and left.\n" +
        "help: this message.\n\n" +
        "I answer from the last sync (every 2 hours), and charges usually post a day or two after you buy.";

    private const int MaxReply = 3500;
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");
    private readonly List<(string Question, string Answer)> _history = [];

    private const string System =
        "You are Budgeter, the user's blunt personal budgeting bot, replying in a Telegram chat. Answer the " +
        "latest message using only the data provided. The totals given are exact: use them instead of adding " +
        "things up yourself, and if you must add, add carefully. Never invent transactions. When a question is " +
        "about today or the last few days, say what the data covers (when it last synced and the latest posted " +
        "date), because charges post a day or two after purchases and may not be there yet. Keep replies short, " +
        "under 600 characters, plain text, no markdown or tables. A little sass is welcome; swearing is fine when " +
        "the topic is bars. If the user asks to change one or more monthly limits, put them in set_limits and " +
        "leave the confirmation to the app (don't restate the new limits); otherwise leave set_limits empty. You " +
        "can't move money or see any account other than these cards, and you don't give investment advice.";

    private static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "reply", "set_limits" },
        properties = new
        {
            reply = new { type = "string" },
            set_limits = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "category", "limit" },
                    properties = new
                    {
                        category = new { type = "string", @enum = Categorizer.Categories },
                        limit = new { type = "number" },
                    },
                },
            },
        },
    });

    public async Task<string> HandleAsync(string message, CancellationToken ct = default)
    {
        var text = message.Trim();
        var command = text.TrimStart('/').ToLowerInvariant();
        if (command is "help" or "start") return HelpText;

        var snapshot = snapshots.Load();
        if (command is "budget" or "status") return await BudgetReplyAsync(snapshot, ct);

        if (SetPattern().Match(command) is { Success: true } m && Categorizer.Categories.Contains(m.Groups["cat"].Value))
            return await ApplyLimitsAsync([(m.Groups["cat"].Value, decimal.Parse(m.Groups["amt"].Value, CultureInfo.InvariantCulture))], snapshot, ct);

        if (snapshot is null)
            return "I don't have any synced data yet. Once watch is running it syncs within 2 hours, then ask me again.";

        ChatReply reply;
        try
        {
            var json = await llm.AskJsonAsync(System, await ContextAsync(snapshot, ct) + History() + $"\nLatest message: {text}", Schema, ct);
            reply = JsonSerializer.Deserialize<ChatReply>(json, JsonOptions.Web) ?? new ChatReply("", []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Chat answer failed: {ex.GetType().Name}: {ex.Message}");
            return "Sorry, I couldn't answer that right now. Try again in a minute.";
        }

        var answer = reply.Reply.Trim();
        if (reply.SetLimits is { Count: > 0 } changes)
        {
            var confirmation = await ApplyLimitsAsync(changes.Select(c => (c.Category, c.Limit)).ToList(), snapshot, ct);
            answer = string.IsNullOrEmpty(answer) ? confirmation : $"{answer}\n\n{confirmation}";
        }
        if (answer.Length > MaxReply) answer = answer[..MaxReply] + "...";

        _history.Add((text, answer));
        if (_history.Count > 4) _history.RemoveAt(0);
        return answer;
    }

    private async Task<string> ApplyLimitsAsync(IReadOnlyList<(string Category, decimal Limit)> changes,
        TransactionSnapshot? snapshot, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var (category, limit) in changes)
        {
            try
            {
                var plan = budgets.SetLimit(category, limit);
                var status = (await StatusAsync(plan, snapshot, ct)).First(s => s.Category == category);
                lines.Add($"{Cap(category)} limit is now {status.Limit.ToString("C0", Usd)}. " + LeftText(status));
            }
            catch (InvalidOperationException ex)
            {
                lines.Add(ex.Message);
            }
        }
        return string.Join("\n", lines);
    }

    private async Task<string> BudgetReplyAsync(TransactionSnapshot? snapshot, CancellationToken ct)
    {
        var plan = budgets.Active;
        if (plan is null) return "No active budget yet. Run budget propose, then budget approve.";
        var now = clock().ToLocalTime();
        var rows = await StatusAsync(plan, snapshot, ct);
        var sb = new StringBuilder($"{now:MMMM}, day {now.Day} of {DateTime.DaysInMonth(now.Year, now.Month)}\n");
        foreach (var r in rows.OrderBy(r => r.Left))
            sb.Append($"\n{Cap(r.Category)}: {r.Spent.ToString("C0", Usd)} of {r.Limit.ToString("C0", Usd)}, {LeftText(r)}");
        sb.Append($"\n\nTotal: {rows.Sum(r => r.Spent).ToString("C0", Usd)} of {rows.Sum(r => r.Limit).ToString("C0", Usd)}");
        sb.Append($"\n{Freshness(snapshot)}");
        return sb.ToString();
    }

    private async Task<List<CategoryStatus>> StatusAsync(BudgetPlan plan, TransactionSnapshot? snapshot, CancellationToken ct)
    {
        var purchases = snapshot is null ? [] : Purchase.From(snapshot.Accounts);
        var categories = await categorizer.CategorizeAsync(purchases.Select(p => p.Description), ct);
        return BudgetPlanner.Status(plan, purchases, categories, clock());
    }

    /// <summary>Everything Sonnet gets to answer from, with every total computed here.</summary>
    public async Task<string> ContextAsync(TransactionSnapshot snapshot, CancellationToken ct = default)
    {
        var now = clock().ToLocalTime();
        var purchases = Purchase.From(snapshot.Accounts);
        var categories = await categorizer.CategorizeAsync(purchases.Select(p => p.Description), ct);
        string Cat(Purchase p) => categories.GetValueOrDefault(Categorizer.Key(p.Description), "other");
        string Money(decimal d) => d.ToString("C", Usd);
        DateTime Day(Purchase p) => p.Posted.ToLocalTime().Date;
        decimal Sum(IEnumerable<Purchase> ps) => ps.Sum(p => p.Amount);
        string ByCategory(IEnumerable<Purchase> ps)
        {
            var groups = ps.GroupBy(Cat).Select(g => (g.Key, Total: Sum(g))).OrderByDescending(g => g.Total).ToList();
            return groups.Count == 0
                ? "nothing"
                : string.Join(", ", groups.Select(g => $"{g.Key} {Money(g.Total)}")) + $"; total {Money(groups.Sum(g => g.Total))}";
        }

        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lastMonthStart = monthStart.AddMonths(-1);

        var sb = new StringBuilder();
        sb.AppendLine($"Now: {now:dddd, MMMM d, yyyy h:mm tt} (user's local time).");
        sb.AppendLine(Freshness(snapshot));
        sb.AppendLine("Amounts are posted card spending; negative amounts are refunds; card payments are left out.");

        if (budgets.Active is { } plan)
        {
            sb.AppendLine($"\nBudget for {now:MMMM} (day {now.Day} of {DateTime.DaysInMonth(now.Year, now.Month)}):");
            foreach (var r in BudgetPlanner.Status(plan, purchases, categories, clock()).OrderBy(r => r.Category))
                sb.AppendLine($"- {r.Category}: limit {Money(r.Limit)}, spent {Money(r.Spent)}, left {Money(r.Left)}");
        }
        else
        {
            sb.AppendLine("\nNo budget has been approved yet.");
        }

        sb.AppendLine("\nSpending per day, last 14 days:");
        for (var d = today; d > today.AddDays(-14); d = d.AddDays(-1))
        {
            var ps = purchases.Where(p => Day(p) == d).ToList();
            sb.AppendLine($"- {d:ddd MMM d}: {(ps.Count == 0 ? "nothing posted" : $"{Money(Sum(ps))} ({ByCategory(ps)})")}");
        }
        sb.AppendLine($"\nThis week so far (since Monday {weekStart:MMM d}): {ByCategory(purchases.Where(p => Day(p) >= weekStart))}");
        sb.AppendLine($"Last week ({weekStart.AddDays(-7):MMM d} to {weekStart.AddDays(-1):MMM d}): " +
                      ByCategory(purchases.Where(p => Day(p) >= weekStart.AddDays(-7) && Day(p) < weekStart)));
        sb.AppendLine($"This month so far: {ByCategory(purchases.Where(p => Day(p) >= monthStart))}");
        sb.AppendLine($"Last month ({lastMonthStart:MMMM}): {ByCategory(purchases.Where(p => Day(p) >= lastMonthStart && Day(p) < monthStart))}");

        sb.AppendLine($"\nEvery transaction since {lastMonthStart:MMM d} (posted date, merchant, category, amount):");
        foreach (var p in purchases.Where(p => Day(p) >= lastMonthStart).OrderByDescending(p => p.Posted))
            sb.AppendLine($"- {Day(p):MMM d} | {p.Description.Trim()} | {Cat(p)} | {Money(p.Amount)}");
        return sb.ToString();
    }

    public static string Freshness(TransactionSnapshot? snapshot)
    {
        if (snapshot is null) return "No data synced yet.";
        var latest = Purchase.From(snapshot.Accounts).Select(p => (DateTimeOffset?)p.Posted).Max();
        return $"Data last synced {snapshot.SyncedAt.ToLocalTime():ddd MMM d h:mm tt}; " +
               (latest is null ? "no posted charges in it." : $"latest posted charge {latest.Value.ToLocalTime():ddd MMM d}.");
    }

    private string History() =>
        _history.Count == 0
            ? ""
            : "\nEarlier in this chat:\n" + string.Concat(_history.Select(h => $"User: {h.Question}\nYou: {h.Answer}\n"));

    private static string LeftText(CategoryStatus s) =>
        s.Left >= 0
            ? $"{Math.Floor(s.Left).ToString("C0", Usd)} left this month."
            : $"{Math.Ceiling(-s.Left).ToString("C0", Usd)} over this month.";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // "bars 200", "set bars to $200", "personal care 30"
    [GeneratedRegex(@"^(?:set\s+)?(?<cat>[a-z][a-z ]*?)\s+(?:to\s+)?\$?(?<amt>\d+(?:\.\d{1,2})?)$")]
    private static partial Regex SetPattern();

    private sealed record ChatReply(
        string Reply,
        [property: System.Text.Json.Serialization.JsonPropertyName("set_limits")] List<LimitChange>? SetLimits);

    private sealed record LimitChange(string Category, decimal Limit);
}
