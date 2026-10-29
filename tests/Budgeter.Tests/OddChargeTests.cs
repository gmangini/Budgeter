using Budgeter;

namespace Budgeter.Tests;

public class OddChargeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);
    private static int _id;

    private static Purchase P(string description, decimal amount, int month, int day, int hour = 18) =>
        new($"o{Interlocked.Increment(ref _id)}", new DateTimeOffset(2026, month, day, hour, 0, 0, TimeSpan.Zero), description, amount);

    private static readonly List<Purchase> History =
    [
        P("KROGER #412", 50m, 8, 1), P("CHIPOTLE 1234", 12m, 9, 2), P("CHIPOTLE 1234", 13m, 9, 9), P("CHIPOTLE 1234", 11m, 9, 16),
    ];

    [Fact]
    public void Same_amount_same_merchant_within_a_day_is_a_possible_double_charge()
    {
        var first = P("DOORDASH*THAI PALACE", 32.35m, 10, 25, 12);
        var second = P("DOORDASH*THAI PALACE", 32.35m, 10, 25, 20);

        Assert.Equal("Double charge? The same $32.35 at DOORDASH*THAI PALACE posted earlier the same day too.",
            OddCharges.Note(second, "takeout", [.. History, first], Now));
        Assert.Null(OddCharges.Note(P("DOORDASH*THAI PALACE", 30m, 10, 25, 21), "takeout", [.. History, first], Now));
    }

    [Fact]
    public void A_charge_far_above_the_usual_is_flagged_but_normal_ones_are_not()
    {
        Assert.Equal("That's way more than usual at CHIPOTLE 1234 (usually about $12).",
            OddCharges.Note(P("CHIPOTLE 1234", 96m, 10, 1), "takeout", History, Now));
        Assert.Null(OddCharges.Note(P("CHIPOTLE 1234", 14m, 10, 1), "takeout", History, Now));
        // Three times $12 is $36, but an extra $24 isn't worth a note.
        Assert.Null(OddCharges.Note(P("CHIPOTLE 1234", 36m, 10, 1), "takeout", History, Now));
    }

    [Fact]
    public void First_visit_is_noted_once_there_is_enough_history_and_not_for_subscriptions()
    {
        Assert.Equal("First time at THE NEW SPOT.", OddCharges.Note(P("THE NEW SPOT", 20m, 10, 20), "bars", History, Now));
        Assert.Null(OddCharges.Note(P("DISNEY PLUS", 13.99m, 10, 20), "subscriptions", History, Now));
        Assert.Null(OddCharges.Note(P("THE NEW SPOT", 20m, 8, 10), "bars", History, Now)); // only 9 days of history then
    }
}
