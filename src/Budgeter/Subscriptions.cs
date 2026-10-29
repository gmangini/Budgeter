using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Budgeter;

public enum Cadence { Weekly, Monthly, Yearly }

/// <summary>A charge that repeats on a rhythm. <see cref="Varies"/> marks a bill whose amount changes (utilities).</summary>
public sealed record Subscription(
    string Key,
    string Name,
    string Category,
    Cadence Cadence,
    decimal LastAmount,
    decimal? PreviousAmount,
    DateTimeOffset LastCharged,
    DateTimeOffset NextExpected,
    int Charges,
    bool Varies,
    bool MaybeCanceled)
{
    /// <summary>What it costs per month at its latest price (average price when it varies).</summary>
    public decimal MonthlyCost { get; init; }

    public bool PriceChanged => !Varies && PreviousAmount is { } prev && Subscriptions.IsPriceChange(prev, LastAmount);
}

/// <summary>Finds subscriptions in purchase history. Pure code: no Sonnet calls, so it's free to run.</summary>
public static partial class Subscriptions
{
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Merchant without store numbers and order codes: "AMAZON.COM*2K4" and "KROGER #412" -> "AMAZON.COM", "KROGER".</summary>
    public static string Key(string description)
    {
        var d = NumberToken().Replace(description.ToUpperInvariant(), " ");
        return Spaces().Replace(d.Replace('*', ' ').Replace('#', ' '), " ").Trim(' ', '-', '.');
    }

    public static bool IsPriceChange(decimal before, decimal after) =>
        Math.Abs(after - before) >= Math.Max(0.50m, before * 0.02m);

    public static List<Subscription> Find(IReadOnlyCollection<Purchase> purchases,
        IReadOnlyDictionary<string, string> categories, DateTimeOffset now)
    {
        var found = new List<Subscription>();
        foreach (var group in purchases.Where(p => !p.IsRefund).GroupBy(p => Key(p.Description)))
        {
            var charges = group.OrderBy(p => p.Posted).ToList();
            if (charges.Count < 2) continue;
            var gaps = charges.Zip(charges.Skip(1), (a, b) => (b.Posted - a.Posted).TotalDays).ToList();
            if (CadenceOf(gaps) is not { } cadence || (cadence == Cadence.Weekly && charges.Count < 3)) continue;

            var category = categories.GetValueOrDefault(Categorizer.Key(charges[^1].Description), "other");
            // Steady price before the latest charge (so a price change on the latest one still counts as steady).
            var earlier = charges.Count > 2 ? charges.Take(charges.Count - 1).Select(c => c.Amount).ToList()
                                            : charges.Select(c => c.Amount).ToList();
            var varies = earlier.Max() - earlier.Min() > Math.Max(1m, earlier.Average() * 0.10m);
            // Regular but varying spending only counts when it's a bill; otherwise it's just a habit (weekly groceries).
            if (varies && category is not ("bills" or "subscriptions")) continue;
            // Two charges with different prices: can't tell a price change from a varying bill, so call it varying.
            if (charges.Count == 2 && IsPriceChange(charges[0].Amount, charges[1].Amount) && category != "subscriptions")
                varies = true;

            var last = charges[^1];
            var next = cadence switch
            {
                Cadence.Weekly => last.Posted.AddDays(7),
                Cadence.Monthly => last.Posted.AddMonths(1),
                _ => last.Posted.AddYears(1),
            };
            var grace = cadence == Cadence.Weekly ? 4 : 10;
            var price = varies ? charges.Average(c => c.Amount) : last.Amount;
            found.Add(new Subscription(group.Key, last.Description.Trim(), category, cadence, last.Amount,
                charges[^2].Amount, last.Posted, next, charges.Count, varies, now > next.AddDays(grace))
            {
                MonthlyCost = Math.Round(cadence switch
                {
                    Cadence.Weekly => price * 52 / 12,
                    Cadence.Monthly => price,
                    _ => price / 12,
                }, 2),
            });
        }
        return found.OrderByDescending(s => s.MonthlyCost).ToList();
    }

    /// <summary>Most gaps (70%) must fit one rhythm; a skipped month or a one-off extra charge is tolerated.</summary>
    private static Cadence? CadenceOf(List<double> gaps)
    {
        foreach (var (cadence, min, max) in new[] { (Cadence.Weekly, 6.0, 8.0), (Cadence.Monthly, 26.0, 35.0), (Cadence.Yearly, 350.0, 380.0) })
            if (gaps.Count(g => g >= min && g <= max) >= Math.Ceiling(gaps.Count * 0.7))
                return cadence;
        return null;
    }

    /// <summary>
    /// The extra line for a purchase alert: a price change on a known subscription, or a possible new one.
    /// <paramref name="earlier"/> is history from before this purchase.
    /// </summary>
    public static string? AlertNote(Purchase purchase, string category, IReadOnlyCollection<Purchase> earlier,
        IReadOnlyDictionary<string, string> categories, DateTimeOffset now)
    {
        var key = Key(purchase.Description);
        var before = earlier.Where(p => !p.IsRefund && p.Id != purchase.Id && Key(p.Description) == key).ToList();

        if (before.Count > 0)
        {
            var sub = Find([.. before, purchase], categories, now).FirstOrDefault(s => s.Key == key);
            if (sub is { PriceChanged: true, PreviousAmount: { } prev })
            {
                var perYear = (sub.LastAmount - prev) * sub.Cadence switch { Cadence.Weekly => 52, Cadence.Monthly => 12, _ => 1 };
                return $"Heads up: {sub.Name} went {(sub.LastAmount > prev ? "up" : "down")} from {prev.ToString("C", Usd)} " +
                       $"to {sub.LastAmount.ToString("C", Usd)} ({(perYear > 0 ? "+" : "-")}{Math.Abs(perYear).ToString("C0", Usd)} a year).";
            }
            return null;
        }

        // Only call it "new" once there's enough history that an existing monthly charge would have shown up.
        var historyStart = earlier.Count == 0 ? now : earlier.Min(p => p.Posted);
        return category == "subscriptions" && purchase.Posted - historyStart >= TimeSpan.FromDays(35)
            ? $"New subscription? First charge from {purchase.Description.Trim()}."
            : null;
    }

    /// <summary>The `subscriptions` reply: each one, the totals, varying bills, and possibly canceled ones.</summary>
    public static string Summary(IReadOnlyCollection<Purchase> purchases, IReadOnlyDictionary<string, string> categories,
        DateTimeOffset now)
    {
        if (purchases.Count == 0) return "No history synced yet, so no subscriptions to show.";
        var all = Find(purchases, categories, now);
        var active = all.Where(s => !s.Varies && !s.MaybeCanceled).ToList();
        var bills = all.Where(s => s.Varies && !s.MaybeCanceled).ToList();
        var canceled = all.Where(s => s.MaybeCanceled).ToList();
        var days = (int)(now - purchases.Min(p => p.Posted)).TotalDays;
        string Money(decimal d) => d.ToString("C", Usd);

        var sb = new StringBuilder();
        var monthly = active.Sum(s => s.MonthlyCost);
        sb.Append(active.Count == 0
            ? "No subscriptions found yet."
            : $"Subscriptions: {active.Count}, {Money(monthly)} a month, {Money(monthly * 12)} a year.\n");
        foreach (var s in active)
            sb.Append($"\n- {s.Name}: {Money(s.LastAmount)} {s.Cadence.ToString().ToLowerInvariant()}, next about " +
                      $"{s.NextExpected.ToLocalTime():MMM d}{(s.PriceChanged ? $" (was {Money(s.PreviousAmount!.Value)})" : "")}");
        if (bills.Count > 0)
        {
            sb.Append("\n\nRecurring bills that vary (not in the total):");
            foreach (var s in bills)
                sb.Append($"\n- {s.Name}: about {Money(s.MonthlyCost)} a month, last {Money(s.LastAmount)}");
        }
        if (canceled.Count > 0)
        {
            sb.Append("\n\nPossibly canceled (expected but no charge):");
            foreach (var s in canceled)
                sb.Append($"\n- {s.Name}: last {Money(s.LastAmount)} on {s.LastCharged.ToLocalTime():MMM d}");
        }
        sb.Append($"\n\nBased on {days} days of history. Yearly ones show up after two charges a year apart.");
        return sb.ToString();
    }

    [GeneratedRegex(@"\b[A-Z0-9]*\d[A-Z0-9]*\b")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
