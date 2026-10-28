using System.Globalization;
using System.Text.Json;

namespace Budgeter;

/// <summary>What `watch` remembers between polls and across restarts.</summary>
public sealed class WatchState
{
    public bool Initialized { get; set; }
    public bool OptInSent { get; set; }
    public DateTimeOffset? LastPoll { get; set; }

    /// <summary>Transaction id -> posted time, for every transaction already handled.</summary>
    public Dictionary<string, DateTimeOffset> Seen { get; set; } = [];

    /// <summary>Texts not yet delivered; retried on the next poll.</summary>
    public List<string> Outbox { get; set; } = [];

    public static WatchState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<WatchState>(File.ReadAllText(path), JsonOptions.Web)! : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write then rename, so a crash mid-write can't leave a half-written file that re-texts everything.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions.Indented));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record PollResult(int NewPurchases, int Sent, int Pending);

/// <summary>One poll: find new purchases, queue a text for each, then send everything queued.</summary>
public sealed class Watcher(
    Func<DateTimeOffset, Task<AccountSet>> fetch,
    Categorizer categorizer,
    BudgetStore budgets,
    ISmsSender sms,
    string statePath,
    Roaster? roaster = null)
{
    /// <summary>SimpleFIN Bridge allows about 24 requests a day; every 2 hours stays well inside it.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(2);

    /// <summary>Sent once, on the first real run, to match what was registered with the carriers.</summary>
    public const string OptInMessage =
        "Budgeter: You're signed up for personal budget and purchase alerts. Msg frequency varies (about 1-5/day). " +
        "Msg & data rates may apply. Reply HELP for help, STOP to opt out.";

    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    /// <param name="replay">First run only: leave this many of the newest purchases unseen, to try it out.</param>
    public async Task<PollResult> PollOnceAsync(DateTimeOffset now, int replay = 0, CancellationToken ct = default)
    {
        var state = WatchState.Load(statePath);
        var monthStart = MonthStart(now);
        // Look back past the 1st, so a charge that posts late isn't missed at the start of a month.
        var fetchStart = monthStart.AddDays(-10);
        var set = await fetch(fetchStart);
        var purchases = Purchase.From(set);
        // Only transactions inside the fetch window can be news. Older ones may have been forgotten (pruned
        // below), so without this cut they'd look new again whenever a fetch returns more history than asked for.
        var posted = set.Accounts.SelectMany(a => a.Transactions ?? [])
            .Where(t => t.Pending != true && DateTimeOffset.FromUnixTimeSeconds(t.Posted) >= fetchStart)
            .ToList();

        var newCount = 0;
        if (!state.Initialized)
        {
            // First run: everything already there is history, not news.
            var keep = purchases.Where(p => !p.IsRefund).OrderByDescending(p => p.Posted).Take(replay)
                .Select(p => p.Id).ToHashSet();
            foreach (var t in posted.Where(t => !keep.Contains(t.Id)))
                state.Seen[t.Id] = DateTimeOffset.FromUnixTimeSeconds(t.Posted);
            state.Initialized = true;
        }

        if (sms.IsReal && !state.OptInSent)
        {
            state.Outbox.Insert(0, OptInMessage);
            state.OptInSent = true;
        }

        var fresh = posted.Where(t => !state.Seen.ContainsKey(t.Id)).ToList();
        if (fresh.Count > 0)
        {
            var categories = await categorizer.CategorizeAsync(purchases.Select(p => p.Description), ct);
            var plan = budgets.Active;
            var freshIds = fresh.Select(t => t.Id).ToHashSet();

            // Payments and refunds are marked seen without a text; refunds already lower the category's total.
            foreach (var p in purchases.Where(p => freshIds.Contains(p.Id) && !p.IsRefund).OrderBy(p => p.Posted))
            {
                var category = categories.GetValueOrDefault(Categorizer.Key(p.Description), "other");
                // What was left right after this purchase, not after the whole batch.
                var status = plan is null
                    ? null
                    : BudgetPlanner.Status(plan, purchases.Where(x => x.Posted <= p.Posted).ToList(), categories, now)
                        .FirstOrDefault(s => s.Category == category);
                var comment = roaster is null ? null : await roaster.CommentAsync(p, category, status, ct);
                state.Outbox.Add(Message(p, category, status, comment));
                newCount++;
            }
            foreach (var t in fresh) state.Seen[t.Id] = DateTimeOffset.FromUnixTimeSeconds(t.Posted);
        }

        // Forget ids too old to come back in a fetch, so the file doesn't grow forever.
        foreach (var old in state.Seen.Where(s => s.Value < monthStart.AddDays(-60)).Select(s => s.Key).ToList())
            state.Seen.Remove(old);
        state.LastPoll = now;
        // Save before sending: a crash after this point can only re-send what's in the outbox, never re-queue.
        state.Save(statePath);

        var sent = 0;
        while (state.Outbox.Count > 0)
        {
            try
            {
                await sms.SendAsync(state.Outbox[0], ct);
            }
            catch (Exception ex) when (ex is SmsException or HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Text not sent, will retry next poll: {ex.Message}");
                break;
            }
            state.Outbox.RemoveAt(0);
            sent++;
            state.Save(statePath);
        }
        return new PollResult(newCount, sent, state.Outbox.Count);
    }

    public static string Message(Purchase p, string category, CategoryStatus? status, string? comment = null)
    {
        var text = $"Budgeter: {p.Amount.ToString("C", Usd)} at {p.Description.Trim()} ({category}).";
        if (status is not null)
            text += status.Left >= 0
                // Round toward caution: never claim more is left, or less is over, than really is.
                ? $" {Math.Floor(status.Left).ToString("C0", Usd)} left for {category} this month."
                : $" {Math.Ceiling(-status.Left).ToString("C0", Usd)} over for {category} this month.";
        else
            text += " No budget set for this category.";
        if (!string.IsNullOrWhiteSpace(comment)) text += " " + comment;
        return text + " Reply STOP to opt out.";
    }

    public static DateTimeOffset MonthStart(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        return new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset);
    }
}
