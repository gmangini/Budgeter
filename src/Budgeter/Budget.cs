using System.Globalization;
using System.Text.Json;

namespace Budgeter;

/// <summary>
/// A posted card transaction that counts as spending: a charge (positive <see cref="Amount"/>) or a refund
/// (negative, so it lowers its category's total).
/// </summary>
public sealed record Purchase(string Id, DateTimeOffset Posted, string Description, decimal Amount)
{
    public bool IsRefund => Amount < 0;

    /// <summary>Posted charges and refunds. Card payments and pending holds are left out.</summary>
    public static List<Purchase> From(AccountSet set) =>
        set.Accounts
            .SelectMany(a => a.Transactions ?? [])
            .Where(t => t.Pending != true)
            .Select(t => (t, amount: decimal.Parse(t.Amount, CultureInfo.InvariantCulture)))
            .Where(x => x.amount < 0 || !IsCardPayment(x.t.Description))
            .Select(x => new Purchase(x.t.Id, DateTimeOffset.FromUnixTimeSeconds(x.t.Posted), x.t.Description, -x.amount))
            .Where(p => p.Amount != 0)
            .ToList();

    /// <summary>A credit that pays the card down, as opposed to a refund from a merchant.</summary>
    public static bool IsCardPayment(string description)
    {
        var d = description.ToUpperInvariant();
        return d.Contains("PAYMENT") || d.Contains("PYMT") || d.Contains("AUTOPAY") || d.Contains("THANK YOU");
    }
}

public sealed record CategoryLimit(string Category, decimal Limit, decimal AverageSpent, string Note);

public sealed record BudgetPlan(DateTimeOffset Created, int BasedOnMonths, List<CategoryLimit> Categories, string Summary);

public sealed record CategoryStatus(string Category, decimal Limit, decimal Spent)
{
    public decimal Left => Limit - Spent;
}

public sealed class BudgetPlanner(ILlm llm)
{
    private const string System =
        "You set a monthly credit card budget for one person from their actual spending. For each category give a " +
        "monthly limit in whole dollars: close to the average for essentials (groceries, gas, bills, health, " +
        "transport), and a realistic trim for discretionary spending, trimming bars hardest. Never set a limit " +
        "above the average. Keep each note to one short sentence explaining the limit. The summary is two " +
        "sentences, blunt and a little sassy.";

    private static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "categories", "summary" },
        properties = new
        {
            categories = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "category", "limit", "note" },
                    properties = new
                    {
                        category = new { type = "string", @enum = Categorizer.Categories },
                        limit = new { type = "number" },
                        note = new { type = "string" },
                    },
                },
            },
            summary = new { type = "string" },
        },
    });

    /// <summary>Months before the current one that the history fully covers.</summary>
    public static List<(int Year, int Month)> CompleteMonths(IReadOnlyCollection<Purchase> purchases, DateTimeOffset now)
    {
        if (purchases.Count == 0) return [];
        var first = purchases.Min(p => p.Posted.ToLocalTime());
        var month = new DateTime(first.Year, first.Month, 1);
        // A month the history starts partway into would understate spending, so skip it.
        if (first.Day > 3) month = month.AddMonths(1);
        var current = new DateTime(now.ToLocalTime().Year, now.ToLocalTime().Month, 1);
        var months = new List<(int, int)>();
        for (; month < current; month = month.AddMonths(1)) months.Add((month.Year, month.Month));
        return months;
    }

    /// <summary>Average monthly spending per category over the complete months.</summary>
    public static Dictionary<string, decimal> MonthlyAverages(IReadOnlyCollection<Purchase> purchases,
        IReadOnlyDictionary<string, string> categories, IReadOnlyCollection<(int Year, int Month)> months)
    {
        if (months.Count == 0) return [];
        return purchases
            .Where(p => months.Contains((p.Posted.ToLocalTime().Year, p.Posted.ToLocalTime().Month)))
            .GroupBy(p => categories.GetValueOrDefault(Categorizer.Key(p.Description), "other"))
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(p => p.Amount) / months.Count, 2));
    }

    public async Task<BudgetPlan> ProposeAsync(IReadOnlyCollection<Purchase> purchases,
        IReadOnlyDictionary<string, string> categories, DateTimeOffset now, CancellationToken ct = default)
    {
        var months = CompleteMonths(purchases, now);
        if (months.Count == 0)
            throw new InvalidOperationException("Need at least one full month of history to propose a budget.");
        var averages = MonthlyAverages(purchases, categories, months);

        var user = $"Average monthly spending over the last {months.Count} full month(s):\n" +
                   string.Join("\n", averages.OrderByDescending(a => a.Value)
                       .Select(a => $"- {a.Key}: ${a.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));
        var json = await llm.AskJsonAsync(System, user, Schema, ct);
        var reply = JsonSerializer.Deserialize<Reply>(json, JsonOptions.Web)!;

        var byCategory = reply.Categories.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.First());
        // Every category with spending gets a line; the averages come from the data, never from the model.
        var limits = averages
            .OrderByDescending(a => a.Value)
            .Select(a => byCategory.TryGetValue(a.Key, out var c)
                ? new CategoryLimit(a.Key, Math.Round(Math.Max(0, c.Limit)), a.Value, c.Note)
                : new CategoryLimit(a.Key, Math.Round(a.Value), a.Value, "No suggestion; set to your average."))
            .ToList();
        return new BudgetPlan(now, months.Count, limits, reply.Summary);
    }

    /// <summary>Limit, spent and left per budget category for the month containing <paramref name="now"/>.</summary>
    public static List<CategoryStatus> Status(BudgetPlan plan, IReadOnlyCollection<Purchase> purchases,
        IReadOnlyDictionary<string, string> categories, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var spent = purchases
            .Where(p => p.Posted.ToLocalTime().Year == local.Year && p.Posted.ToLocalTime().Month == local.Month)
            .GroupBy(p => categories.GetValueOrDefault(Categorizer.Key(p.Description), "other"))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var rows = plan.Categories.Select(c => new CategoryStatus(c.Category, c.Limit, spent.GetValueOrDefault(c.Category)));
        // Spending in a category the budget has no line for still shows, with a zero limit.
        var unbudgeted = spent.Keys.Except(plan.Categories.Select(c => c.Category))
            .Select(k => new CategoryStatus(k, 0, spent[k]));
        return rows.Concat(unbudgeted).ToList();
    }

    private sealed record Reply(List<ReplyCategory> Categories, string Summary);

    private sealed record ReplyCategory(string Category, decimal Limit, string Note);
}

/// <summary>The proposed and active budgets as JSON files in the data folder.</summary>
public sealed class BudgetStore(string dataDir)
{
    public string ProposedPath => Path.Combine(dataDir, "budget.proposed.json");
    public string ActivePath => Path.Combine(dataDir, "budget.json");

    public void SaveProposed(BudgetPlan plan) => Write(ProposedPath, plan);

    /// <summary>Promotes the proposal to the active budget, keeping any edits made to the proposed file.</summary>
    public BudgetPlan Approve()
    {
        var plan = Read(ProposedPath)
                   ?? throw new InvalidOperationException("No proposed budget. Run: budget propose");
        Write(ActivePath, plan);
        File.Delete(ProposedPath);
        return plan;
    }

    public BudgetPlan? Active => Read(ActivePath);

    /// <summary>Changes (or adds) one category's monthly limit in the active budget.</summary>
    public BudgetPlan SetLimit(string category, decimal limit)
    {
        if (!Categorizer.Categories.Contains(category))
            throw new InvalidOperationException(
                $"Unknown category '{category}'. Use one of: {string.Join(", ", Categorizer.Categories)}.");
        if (limit < 0) throw new InvalidOperationException("A limit can't be negative.");
        var plan = Active ?? throw new InvalidOperationException("No active budget yet. Run: budget propose, then budget approve");

        var limit0 = Math.Round(limit);
        var categories = plan.Categories.Any(c => c.Category == category)
            ? plan.Categories.Select(c => c.Category == category ? c with { Limit = limit0 } : c).ToList()
            : [.. plan.Categories, new CategoryLimit(category, limit0, 0, "Added from chat.")];
        var updated = plan with { Categories = categories };
        Write(ActivePath, updated);
        return updated;
    }

    private static BudgetPlan? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<BudgetPlan>(File.ReadAllText(path), JsonOptions.Web) : null;

    // Atomic: watch reads the budget while the bot may be changing it.
    private static void Write(string path, BudgetPlan plan) =>
        AtomicFile.Write(path, JsonSerializer.Serialize(plan, JsonOptions.Indented));
}
