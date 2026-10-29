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
