using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Budgeter;

/// <summary>What `watch` remembers between polls and across restarts.</summary>
public sealed class WatchState
{
    public bool Initialized { get; set; }
    public bool OptInSent { get; set; }
    public DateTimeOffset? LastPoll { get; set; }

    /// <summary>Transaction id -> posted time, for every transaction already handled.</summary>
    public Dictionary<string, DateTimeOffset> Seen { get; set; } = [];

    /// <summary>Messages not yet delivered; retried on the next poll.</summary>
    public List<OutboxMessage> Outbox { get; set; } = [];

    /// <summary>The month ("2026-10") whose wrap-up has been sent.</summary>
    public string? LastWrapUp { get; set; }

    /// <summary>Start of the current no-bar-tab streak, and the biggest milestone already praised for it.</summary>
    public DateTime? StreakSince { get; set; }
    public int StreakPraised { get; set; }

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

/// <summary>A queued message. Older state files stored plain strings; those still load.</summary>
[JsonConverter(typeof(OutboxMessageConverter))]
public sealed record OutboxMessage(string Text, List<List<Button>>? Buttons = null)
{
    public static implicit operator OutboxMessage(string text) => new(text);
}

public sealed class OutboxMessageConverter : JsonConverter<OutboxMessage>
{
    private sealed record Shape(string Text, List<List<Button>>? Buttons);

    public override OutboxMessage Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new(reader.GetString()!);
        var shape = JsonSerializer.Deserialize<Shape>(ref reader, options)!;
        return new(shape.Text, shape.Buttons);
    }

    public override void Write(Utf8JsonWriter writer, OutboxMessage value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Shape(value.Text, value.Buttons), options);
}

public sealed record PollResult(int NewPurchases, int Sent, int Pending);

/// <summary>One poll: find new purchases, queue an alert for each, then send everything queued.</summary>
public sealed class Watcher(
    Func<DateTimeOffset, Task<AccountSet>> fetch,
    Categorizer categorizer,
    BudgetStore budgets,
    IAlertSender alerts,
    string statePath,
    Roaster? roaster = null,
    SnapshotStore? snapshots = null,
    HistoryStore? historyStore = null,
    MonthlyWrapUp? wrapUps = null)
{
    /// <summary>SimpleFIN Bridge allows about 24 requests a day; every 2 hours stays well inside it.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(2);

    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    /// <param name="replay">First run only: leave this many of the newest purchases unseen, to try it out.</param>
    public async Task<PollResult> PollOnceAsync(DateTimeOffset now, int replay = 0, CancellationToken ct = default)
    {
        var state = WatchState.Load(statePath);
        var monthStart = MonthStart(now);
        // Look back past the 1st, so a charge that posts late isn't missed at the start of a month.
        var fetchStart = monthStart.AddDays(-10);
        // The same single request covers ~90 days (about what SimpleFIN keeps): saved for the bot to answer from
        // and merged into the long-term history that subscriptions are found in.
        var set = await fetch(now.AddDays(-90));
        snapshots?.Save(new TransactionSnapshot(now, set));
        var allPurchases = HistoryStore.Purchases(
            historyStore?.Merge(set, now) ?? set.Accounts.SelectMany(a => a.Transactions ?? []).Where(t => t.Pending != true));
        var purchases = Purchase.From(set).Where(p => p.Posted >= fetchStart).ToList();
        // Only transactions inside the fetch window can be news. Older ones may have been forgotten (pruned
        // below), so without this cut they'd look new again whenever a fetch returns more history than asked for.
        var posted = set.Accounts.SelectMany(a => a.Transactions ?? [])
            .Where(t => t.Pending != true && DateTimeOffset.FromUnixTimeSeconds(t.Posted) >= fetchStart)
            .ToList();

        var newCount = 0;
        var firstRun = !state.Initialized;
        if (!state.Initialized)
        {
            // First run: everything already there is history, not news.
            var keep = purchases.Where(p => !p.IsRefund).OrderByDescending(p => p.Posted).Take(replay)
                .Select(p => p.Id).ToHashSet();
            foreach (var t in posted.Where(t => !keep.Contains(t.Id)))
                state.Seen[t.Id] = DateTimeOffset.FromUnixTimeSeconds(t.Posted);
            state.Initialized = true;
        }

        // OptInSent keeps its name from the SMS days: it means "the sender's welcome went out".
        if (alerts.Welcome is { } welcome && !state.OptInSent)
        {
            state.Outbox.Insert(0, welcome);
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
                // Posted times are often just the day, so twins can share one: order those by id, flagging only the second.
                var earlier = allPurchases.Where(x => x.Posted < p.Posted ||
                                                      (x.Posted == p.Posted && string.CompareOrdinal(x.Id, p.Id) < 0)).ToList();
                var notes = new[] { OddCharges.Note(p, category, earlier, now), Subscriptions.AlertNote(p, category, earlier, categories, now) };
                var note = string.Join(" ", notes.Where(n => n is not null));
                var comment = roaster is null ? null : await roaster.CommentAsync(p, category, status, ct);
                state.Outbox.Add(Message(p, category, status, comment, note) + alerts.Footer);
                newCount++;
            }
            foreach (var t in fresh) state.Seen[t.Id] = DateTimeOffset.FromUnixTimeSeconds(t.Posted);
        }

        await QueueMonthlyMessagesAsync(state, allPurchases, firstRun, now, ct);

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
                await alerts.SendAsync(new BotReply(state.Outbox[0].Text, state.Outbox[0].Buttons), ct);
            }
            catch (Exception ex) when (ex is AlertException or HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Alert not sent, will retry next poll: {ex.Message}");
                break;
            }
            state.Outbox.RemoveAt(0);
            sent++;
            state.Save(statePath);
        }
        return new PollResult(newCount, sent, state.Outbox.Count);
    }

    /// <summary>
    /// Last month's wrap-up on the first sync of a new month, and praise when a no-bar-tab streak passes a
    /// milestone. On the very first run both are just noted, so a new install doesn't open with old news.
    /// </summary>
    private async Task QueueMonthlyMessagesAsync(WatchState state, List<Purchase> allPurchases, bool firstRun,
        DateTimeOffset now, CancellationToken ct)
    {
        var categories = await categorizer.CategorizeAsync(allPurchases.Select(p => p.Description), ct);

        var local = now.ToLocalTime();
        var lastMonth = MonthlyWrapUp.MonthKey(new DateTime(local.Year, local.Month, 1).AddMonths(-1));
        if (state.LastWrapUp != lastMonth)
        {
            if (!firstRun && wrapUps is not null && await wrapUps.BuildAsync(allPurchases, categories, now, ct) is { } wrapUp)
                state.Outbox.Add(new OutboxMessage(wrapUp.Text, wrapUp.Buttons));
            state.LastWrapUp = lastMonth;
        }

        var (since, days, _) = Streaks.BarStreak(allPurchases, categories, now);
        var milestone = Streaks.MilestoneFor(days);
        if (state.StreakSince != since)
        {
            // A new streak (a bar charge posted, or the very first run): start counting from here.
            state.StreakSince = since;
            state.StreakPraised = firstRun ? milestone : 0;
        }
        if (milestone > state.StreakPraised)
        {
            var barAverage = budgets.Active?.Categories.FirstOrDefault(c => c.Category == "bars")?.AverageSpent;
            state.Outbox.Add(Streaks.Praise(milestone, barAverage));
            state.StreakPraised = milestone;
        }
    }

    public static string Message(Purchase p, string category, CategoryStatus? status, string? comment = null,
        string? note = null)
    {
        var text = $"Budgeter: {p.Amount.ToString("C", Usd)} at {p.Description.Trim()} ({category}).";
        if (status is not null)
            text += status.Left >= 0
                // Round toward caution: never claim more is left, or less is over, than really is.
                ? $" {Math.Floor(status.Left).ToString("C0", Usd)} left for {category} this month."
                : $" {Math.Ceiling(-status.Left).ToString("C0", Usd)} over for {category} this month.";
        else
            text += " No budget set for this category.";
        if (!string.IsNullOrWhiteSpace(note)) text += " " + note;
        if (!string.IsNullOrWhiteSpace(comment)) text += " " + comment;
        return text;
    }

    public static DateTimeOffset MonthStart(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        // The offset on the 1st, not today's: a daylight-saving change in between would be an hour off.
        var first = new DateTime(local.Year, local.Month, 1);
        return new DateTimeOffset(first, TimeZoneInfo.Local.GetUtcOffset(first));
    }
}
