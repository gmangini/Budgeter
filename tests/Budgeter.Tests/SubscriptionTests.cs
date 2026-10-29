using Budgeter;

namespace Budgeter.Tests;

public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);
    private static int _id;

    private static Purchase P(string description, decimal amount, int month, int day, int year = 2026) =>
        new($"p{Interlocked.Increment(ref _id)}", new DateTimeOffset(year, month, day, 18, 0, 0, TimeSpan.Zero), description, amount);

    private static readonly Dictionary<string, string> Cats = new()
    {
        ["NETFLIX.COM"] = "subscriptions",
        ["SPOTIFY USA"] = "subscriptions",
        ["DUKE ENERGY"] = "bills",
        ["KROGER #412"] = "groceries",
        ["KROGER #77"] = "groceries",
        ["GYM CLASS PASS"] = "subscriptions",
        ["DISNEY PLUS"] = "subscriptions",
        ["ICLOUD STORAGE"] = "subscriptions",
    };

    [Theory]
    [InlineData("AMAZON.COM*2K4", "AMAZON.COM")]
    [InlineData("KROGER #412", "KROGER")]
    [InlineData("SHELL OIL 5521", "SHELL OIL")]
    [InlineData("NETFLIX.COM", "NETFLIX.COM")]
    [InlineData("UBER *TRIP", "UBER TRIP")]
    public void Key_drops_store_numbers_and_order_codes(string description, string key) =>
        Assert.Equal(key, Subscriptions.Key(description));

    [Fact]
    public void Finds_monthly_subscriptions_and_varying_bills_but_not_habits()
    {
        var purchases = new List<Purchase>
        {
            P("NETFLIX.COM", 15.49m, 7, 15), P("NETFLIX.COM", 15.49m, 8, 15), P("NETFLIX.COM", 15.49m, 9, 15), P("NETFLIX.COM", 15.49m, 10, 15),
            P("SPOTIFY USA", 11.99m, 8, 3), P("SPOTIFY USA", 11.99m, 9, 3), P("SPOTIFY USA", 11.99m, 10, 3),
            P("DUKE ENERGY", 95m, 8, 20), P("DUKE ENERGY", 140m, 9, 20), P("DUKE ENERGY", 118m, 10, 20),
            // Groceries every week at different store numbers and amounts: a habit, not a subscription.
            P("KROGER #412", 80m, 10, 1), P("KROGER #77", 140m, 10, 8), P("KROGER #412", 60m, 10, 15), P("KROGER #412", 95m, 10, 22),
        };

        var subs = Subscriptions.Find(purchases, Cats, Now);

        var netflix = subs.Single(s => s.Key == "NETFLIX.COM");
        Assert.Equal((Cadence.Monthly, 15.49m, false, false), (netflix.Cadence, netflix.MonthlyCost, netflix.Varies, netflix.MaybeCanceled));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 18, 0, 0, TimeSpan.Zero), netflix.NextExpected);
        Assert.True(subs.Single(s => s.Key == "DUKE ENERGY").Varies);
        Assert.DoesNotContain(subs, s => s.Key == "KROGER");
    }

    [Fact]
    public void Weekly_and_yearly_rhythms_are_found_and_priced_per_month()
    {
        var subs = Subscriptions.Find(
        [
            P("GYM CLASS PASS", 12m, 10, 1), P("GYM CLASS PASS", 12m, 10, 8), P("GYM CLASS PASS", 12m, 10, 15), P("GYM CLASS PASS", 12m, 10, 22),
            P("ICLOUD STORAGE", 120m, 10, 10, 2025), P("ICLOUD STORAGE", 120m, 10, 12),
        ], Cats, Now);

        Assert.Equal((Cadence.Weekly, 52m), (subs.Single(s => s.Key == "GYM CLASS PASS").Cadence, subs.Single(s => s.Key == "GYM CLASS PASS").MonthlyCost));
        Assert.Equal((Cadence.Yearly, 10m), (subs.Single(s => s.Key == "ICLOUD STORAGE").Cadence, subs.Single(s => s.Key == "ICLOUD STORAGE").MonthlyCost));
    }

    [Fact]
    public void A_subscription_that_stops_charging_is_marked_possibly_canceled_and_left_out_of_the_total()
    {
        var purchases = new List<Purchase>
        {
            P("DISNEY PLUS", 13.99m, 6, 5), P("DISNEY PLUS", 13.99m, 7, 5), P("DISNEY PLUS", 13.99m, 8, 5), // expected Sep 5: nothing since
            P("NETFLIX.COM", 15.49m, 8, 15), P("NETFLIX.COM", 15.49m, 9, 15), P("NETFLIX.COM", 15.49m, 10, 15),
        };

        Assert.True(Subscriptions.Find(purchases, Cats, Now).Single(s => s.Key == "DISNEY PLUS").MaybeCanceled);
        var summary = Subscriptions.Summary(purchases, Cats, Now);
        Assert.StartsWith("Subscriptions: 1, $15.49 a month, $185.88 a year.", summary);
        Assert.Contains("Possibly canceled (expected but no charge):\n- DISNEY PLUS: last $13.99 on Aug 5", summary);
    }

    [Fact]
    public void Summary_lists_each_one_and_varying_bills_separately()
    {
        var purchases = new List<Purchase>
        {
            P("NETFLIX.COM", 15.49m, 8, 15), P("NETFLIX.COM", 15.49m, 9, 15), P("NETFLIX.COM", 17.99m, 10, 15),
            P("SPOTIFY USA", 11.99m, 8, 3), P("SPOTIFY USA", 11.99m, 9, 3), P("SPOTIFY USA", 11.99m, 10, 3),
            P("DUKE ENERGY", 95m, 8, 20), P("DUKE ENERGY", 140m, 9, 20), P("DUKE ENERGY", 118m, 10, 20),
        };

        var summary = Subscriptions.Summary(purchases, Cats, Now);

        Assert.StartsWith("Subscriptions: 2, $29.98 a month, $359.76 a year.", summary);
        Assert.Contains("- NETFLIX.COM: $17.99 monthly, next about Nov 15 (was $15.49)", summary);
        Assert.Contains("- SPOTIFY USA: $11.99 monthly, next about Nov 3", summary);
        Assert.Contains("Recurring bills that vary (not in the total):\n- DUKE ENERGY: about $117.67 a month", summary);
    }

    [Fact]
    public void Alert_note_flags_a_price_change_with_its_yearly_cost()
    {
        var earlier = new List<Purchase> { P("NETFLIX.COM", 15.49m, 8, 15), P("NETFLIX.COM", 15.49m, 9, 15) };
        var note = Subscriptions.AlertNote(P("NETFLIX.COM", 17.99m, 10, 15), "subscriptions", earlier, Cats, Now);
        Assert.Equal("Heads up: NETFLIX.COM went up from $15.49 to $17.99 (+$30 a year).", note);

        Assert.Null(Subscriptions.AlertNote(P("NETFLIX.COM", 15.49m, 10, 15), "subscriptions", earlier, Cats, Now));
    }

    [Fact]
    public void Alert_note_flags_a_new_subscription_only_with_enough_history()
    {
        var longHistory = new List<Purchase> { P("KROGER #412", 50m, 8, 1), P("NETFLIX.COM", 15.49m, 9, 15) };
        var shortHistory = new List<Purchase> { P("KROGER #412", 50m, 10, 20) };
        var disney = P("DISNEY PLUS", 13.99m, 10, 25);

        Assert.Equal("New subscription? First charge from DISNEY PLUS.", Subscriptions.AlertNote(disney, "subscriptions", longHistory, Cats, Now));
        Assert.Null(Subscriptions.AlertNote(disney, "subscriptions", shortHistory, Cats, Now));
        Assert.Null(Subscriptions.AlertNote(P("NEW BAR", 20m, 10, 25), "bars", longHistory, Cats, Now));
    }

    [Fact]
    public void History_merges_by_id_and_forgets_after_about_13_months()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid()}.json");
        var store = new HistoryStore(path);
        AccountSet Set(params Transaction[] t) => new(null, [new Account("a", "", "USD", "0", null, 0, new Org(null, null), [.. t])]);
        Transaction T(string id, DateTimeOffset when, bool pending = false) => new(id, when.ToUnixTimeSeconds(), "-1.00", "X", pending);

        store.Merge(Set(T("old", Now.AddDays(-500)), T("a", Now.AddDays(-80))), Now);
        var merged = store.Merge(Set(T("a", Now.AddDays(-80)), T("b", Now.AddDays(-1)), T("hold", Now, pending: true)), Now);

        Assert.Equal(["a", "b"], merged.Select(t => t.Id));
        File.Delete(path);
    }
}
