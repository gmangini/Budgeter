using System.Text.Json;

namespace Budgeter;

/// <summary>The accounts and transactions from the last SimpleFIN sync, and when it happened.</summary>
public sealed record TransactionSnapshot(DateTimeOffset SyncedAt, AccountSet Accounts);

/// <summary>
/// Saves each sync so the bot can answer questions without calling SimpleFIN (whose daily limit `watch` needs).
/// </summary>
public sealed class SnapshotStore(string path)
{
    public TransactionSnapshot? Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<TransactionSnapshot>(File.ReadAllText(path), JsonOptions.Web) : null;

    public void Save(TransactionSnapshot snapshot) => AtomicFile.Write(path, JsonSerializer.Serialize(snapshot, JsonOptions.Web));
}

/// <summary>
/// Every posted transaction Budgeter has ever synced, kept for about 13 months. SimpleFIN only returns ~90 days,
/// so this is what lets yearly subscriptions show up once two charges a year apart have been seen.
/// </summary>
public sealed class HistoryStore(string path)
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(400);

    public List<Transaction> Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<Transaction>>(File.ReadAllText(path), JsonOptions.Web) ?? [] : [];

    /// <summary>Adds new posted transactions (by id), drops ones older than <see cref="Keep"/>, and returns the result.</summary>
    public List<Transaction> Merge(AccountSet set, DateTimeOffset now)
    {
        var byId = Load().ToDictionary(t => t.Id);
        foreach (var t in set.Accounts.SelectMany(a => a.Transactions ?? []).Where(t => t.Pending != true))
            byId[t.Id] = t;
        var cutoff = (now - Keep).ToUnixTimeSeconds();
        var kept = byId.Values.Where(t => t.Posted >= cutoff).OrderBy(t => t.Posted).ToList();
        AtomicFile.Write(path, JsonSerializer.Serialize(kept, JsonOptions.Web));
        return kept;
    }

    /// <summary>History as purchases (charges and refunds, no card payments).</summary>
    public static List<Purchase> Purchases(IEnumerable<Transaction> transactions) =>
        Purchase.From(new AccountSet(null, [new Account("history", "", "USD", "0", null, 0, new Org(null, null), transactions.ToList())]));
}

/// <summary>Write to a temp file, then rename: a reader (or a crash) never sees half a file.</summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
