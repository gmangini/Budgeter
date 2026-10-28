using System.Text.Json;
using Budgeter;

namespace Budgeter.Tests;

/// <summary>Returns canned replies and records what it was asked. No network, no cost.</summary>
internal sealed class FakeLlm(params string[] replies) : ILlm
{
    private readonly Queue<string> _replies = new(replies);
    public List<string> Prompts { get; } = [];

    public Task<string> AskJsonAsync(string system, string user, string jsonSchema, CancellationToken ct = default)
    {
        Prompts.Add(user);
        return Task.FromResult(_replies.Dequeue());
    }
}

public class CategorizerTests
{
    private static string Reply(params (string d, string c)[] items) =>
        JsonSerializer.Serialize(new { items = items.Select(i => new { description = i.d, category = i.c }) });

    [Fact]
    public async Task Asks_only_about_new_descriptions_and_caches_answers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-{Guid.NewGuid()}.json");
        var llm = new FakeLlm(Reply(("O'MALLEY'S PUB", "bars")), Reply(("KROGER #412", "groceries")));
        var categorizer = new Categorizer(llm, path);

        var first = await categorizer.CategorizeAsync(["O'Malley's Pub ", "O'MALLEY'S PUB"]);
        var second = await categorizer.CategorizeAsync(["o'malley's pub", "KROGER #412"]);

        Assert.Equal("bars", first["O'MALLEY'S PUB"]);
        Assert.Equal("groceries", second["KROGER #412"]);
        Assert.Equal(["O'MALLEY'S PUB", "KROGER #412"], llm.Prompts);
        File.Delete(path);
    }

    [Fact]
    public async Task Skipped_or_unknown_categories_fall_back_to_other()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-{Guid.NewGuid()}.json");
        var categorizer = new Categorizer(new FakeLlm(Reply(("A", "made-up"))), path);

        var result = await categorizer.CategorizeAsync(["A", "B"]);

        Assert.Equal("other", result["A"]);
        Assert.Equal("other", result["B"]);
        File.Delete(path);
    }
}

public class BudgetPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static Purchase P(string description, decimal amount, int month, int day) =>
        new(Guid.NewGuid().ToString(), new DateTimeOffset(2026, month, day, 18, 0, 0, TimeSpan.Zero), description, amount);

    private static readonly Dictionary<string, string> Cats = new()
    {
        ["PUB"] = "bars",
        ["KROGER"] = "groceries",
    };

    [Fact]
    public void Purchase_From_keeps_posted_charges_only()
    {
        var set = SimpleFinClient.ParseAccounts("""
            {"accounts":[{"id":"a","name":"Card","currency":"USD","balance":"-1","balance-date":0,"org":{},
              "transactions":[
                {"id":"1","posted":1790000000,"amount":"-20.00","description":"PUB"},
                {"id":"2","posted":1790000000,"amount":"500.00","description":"PAYMENT"},
                {"id":"3","posted":1790000000,"amount":"-9.00","description":"HOLD","pending":true}]}]}
            """);

        var purchase = Assert.Single(Purchase.From(set));
        Assert.Equal(20.00m, purchase.Amount);
    }

    [Fact]
    public void CompleteMonths_skips_the_partial_first_month_and_the_current_one()
    {
        var months = BudgetPlanner.CompleteMonths([P("PUB", 1, 7, 20), P("PUB", 1, 10, 2)], Now);
        Assert.Equal([(2026, 8), (2026, 9)], months);
    }

    [Fact]
    public async Task Propose_uses_averages_from_the_data_and_fills_categories_the_model_skipped()
    {
        var purchases = new List<Purchase>
        {
            P("PUB", 200, 8, 1), P("PUB", 100, 9, 5), P("KROGER", 300, 8, 10), P("KROGER", 100, 9, 10),
            P("PUB", 999, 10, 3), // current month: not part of the averages
        };
        var llm = new FakeLlm("""
            {"categories":[{"category":"bars","limit":90.4,"note":"Fewer rounds."}],"summary":"Tough love."}
            """);

        var plan = await new BudgetPlanner(llm).ProposeAsync(purchases, Cats, Now);

        Assert.Equal(2, plan.BasedOnMonths);
        var bars = plan.Categories.Single(c => c.Category == "bars");
        Assert.Equal(150m, bars.AverageSpent);
        Assert.Equal(90m, bars.Limit);
        var groceries = plan.Categories.Single(c => c.Category == "groceries");
        Assert.Equal(200m, groceries.Limit);
        Assert.Contains("bars: $150.00", llm.Prompts.Single());
    }

    [Fact]
    public async Task Propose_refuses_without_a_full_month()
    {
        var planner = new BudgetPlanner(new FakeLlm());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.ProposeAsync([P("PUB", 10, 10, 1)], Cats, Now));
    }

    [Fact]
    public void Status_counts_this_month_only_and_shows_unbudgeted_spending()
    {
        var plan = new BudgetPlan(Now, 2, [new CategoryLimit("bars", 100, 150, "")], "");
        var purchases = new List<Purchase> { P("PUB", 70, 10, 2), P("PUB", 50, 10, 20), P("PUB", 500, 9, 30), P("KROGER", 40, 10, 4) };

        var rows = BudgetPlanner.Status(plan, purchases, Cats, Now);

        var bars = rows.Single(r => r.Category == "bars");
        Assert.Equal(120m, bars.Spent);
        Assert.Equal(-20m, bars.Left);
        Assert.Equal(40m, rows.Single(r => r.Category == "groceries").Spent);
    }

    [Fact]
    public void Approve_promotes_the_edited_proposal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"budget-{Guid.NewGuid()}");
        var store = new BudgetStore(dir);
        Assert.Throws<InvalidOperationException>(() => store.Approve());

        store.SaveProposed(new BudgetPlan(Now, 2, [new CategoryLimit("bars", 100, 150, "")], "s"));
        File.WriteAllText(store.ProposedPath, File.ReadAllText(store.ProposedPath).Replace("\"limit\": 100", "\"limit\": 60"));

        Assert.Null(store.Active);
        store.Approve();
        Assert.Equal(60m, store.Active!.Categories.Single().Limit);
        Assert.False(File.Exists(store.ProposedPath));
        Directory.Delete(dir, true);
    }
}
