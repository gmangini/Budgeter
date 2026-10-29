using System.Globalization;

namespace Budgeter;

/// <summary>
/// Flags charges worth a second look: the same amount at the same merchant twice within a day, a charge far
/// bigger than usual there, or a first visit. Not fraud protection; it catches double charges. Pure code.
/// </summary>
public static class OddCharges
{
    /// <summary>A charge this many times the usual one (and at least <see cref="MinJump"/> more) is "way more".</summary>
    public const decimal BigFactor = 3m;
    public const decimal MinJump = 25m;

    /// <summary>History needed before "first time" means anything (an existing monthly habit would have shown up).</summary>
    public static readonly TimeSpan NewMerchantHistory = TimeSpan.FromDays(35);

    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    /// <param name="earlier">Purchases posted before this one (it may also be in there; it's skipped by id).</param>
    public static string? Note(Purchase purchase, string category, IReadOnlyCollection<Purchase> earlier, DateTimeOffset now)
    {
        var key = Subscriptions.Key(purchase.Description);
        var before = earlier.Where(p => !p.IsRefund && p.Id != purchase.Id && Subscriptions.Key(p.Description) == key).ToList();

        var twin = before.FirstOrDefault(p => p.Amount == purchase.Amount && (purchase.Posted - p.Posted).Duration() <= TimeSpan.FromHours(24));
        if (twin is not null)
            return $"Double charge? The same {purchase.Amount.ToString("C", Usd)} at {purchase.Description.Trim()} " +
                   $"posted {(twin.Posted.ToLocalTime().Date == purchase.Posted.ToLocalTime().Date ? "earlier the same day" : $"on {twin.Posted.ToLocalTime():MMM d}")} too.";

        if (before.Count >= 3)
        {
            var usual = Median(before.Select(p => p.Amount));
            if (purchase.Amount >= usual * BigFactor && purchase.Amount - usual >= MinJump)
                return $"That's way more than usual at {purchase.Description.Trim()} (usually about {usual.ToString("C0", Usd)}).";
        }

        // Subscriptions get their own "new subscription?" note.
        if (before.Count == 0 && category != "subscriptions" && earlier.Count > 0 &&
            purchase.Posted - earlier.Min(p => p.Posted) >= NewMerchantHistory)
            return $"First time at {purchase.Description.Trim()}.";
        return null;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
