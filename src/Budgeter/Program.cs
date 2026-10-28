using System.Globalization;
using Budgeter;

const string AccessKey = "SIMPLEFIN_ACCESS_URL";
var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
var command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "balances";
var sample = args.Contains("--sample");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var client = new SimpleFinClient(http);

try
{
    switch (command)
    {
        case "claim":
            await Claim();
            break;
        case "balances":
            await Balances();
            break;
        default:
            Console.WriteLine("Usage: budgeter [claim | balances] [--sample]");
            return 2;
    }
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException or HttpRequestException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

async Task Claim()
{
    if (EnvFile.Read(envPath).ContainsKey(AccessKey))
    {
        Console.Write($"{AccessKey} is already in .env. Replace it? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return;
    }
    Console.Write("Paste your SimpleFIN setup token (hidden): ");
    var token = ReadHidden();
    var accessUrl = await client.ClaimAsync(token);
    EnvFile.Set(envPath, AccessKey, accessUrl);
    Console.WriteLine($"Claimed. Access URL saved to .env as {AccessKey} (not shown).");
}

async Task Balances()
{
    AccountSet set;
    if (sample)
    {
        set = SimpleFinClient.ParseAccounts(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample-accounts.json")));
    }
    else
    {
        if (!EnvFile.Read(envPath).TryGetValue(AccessKey, out var accessUrl))
            throw new InvalidOperationException(
                $"No {AccessKey} in .env. Run: dotnet run --project src/Budgeter -- claim");
        set = await client.GetAccountsAsync(accessUrl);
    }

    foreach (var error in set.Errors ?? []) Console.WriteLine($"! {error}");
    var usd = CultureInfo.GetCultureInfo("en-US");
    foreach (var account in set.Accounts.OrderBy(a => a.Org.Name).ThenBy(a => a.Name))
    {
        var asOf = DateTimeOffset.FromUnixTimeSeconds(account.BalanceDate).LocalDateTime;
        Console.WriteLine(
            $"{account.Org.Name ?? account.Org.Domain,-20} {account.Name,-30} " +
            $"{account.BalanceAmount.ToString("C", usd),14}  as of {asOf:MMM d h:mm tt}");
    }
}

static string ReadHidden()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        chars.Add(key.KeyChar);
    }
    Console.WriteLine();
    return new string(chars.ToArray());
}
