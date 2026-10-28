using System.Globalization;
using Budgeter;

const string AccessKey = "SIMPLEFIN_ACCESS_URL";
var root = Directory.GetCurrentDirectory();
var envPath = Path.Combine(root, ".env");
var sample = args.Contains("--sample");
// Sample runs keep their own state, so they never mix with real budgets or watch history.
var dataDir = sample ? Path.Combine(root, "data", "sample") : Path.Combine(root, "data");
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var command = positional.FirstOrDefault() ?? "balances";
var sub = positional.Skip(1).FirstOrDefault();
var usd = CultureInfo.GetCultureInfo("en-US");

// .env values become environment variables, so the Anthropic SDK finds ANTHROPIC_API_KEY.
var env = EnvFile.Read(envPath);
foreach (var (key, value) in env)
    if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var client = new SimpleFinClient(http);

try
{
    switch (command, sub)
    {
        case ("claim", _):
            await Claim();
            break;
        case ("balances", _):
            await Balances();
            break;
        case ("budget", "propose"):
            await Propose();
            break;
        case ("budget", "approve"):
            Approve();
            break;
        case ("budget", "show" or null):
            await Show();
            break;
        case ("watch", _):
            await Watch();
            break;
        default:
            Console.WriteLine("Usage: budgeter [claim | balances | budget propose | budget approve | budget show | watch [--print] [--once] [--replay N] [--plain]] [--sample]");
            return 2;
    }
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException or HttpRequestException
                               or Anthropic.Exceptions.AnthropicException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

async Task Claim()
{
    if (env.ContainsKey(AccessKey))
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
    var set = await LoadAccounts("sample-accounts.json", start: null);
    foreach (var error in set.Errors ?? []) Console.WriteLine($"! {error}");
    foreach (var account in set.Accounts.OrderBy(a => a.Org.Name).ThenBy(a => a.Name))
    {
        var asOf = DateTimeOffset.FromUnixTimeSeconds(account.BalanceDate).LocalDateTime;
        Console.WriteLine(
            $"{account.Org.Name ?? account.Org.Domain,-20} {account.Name,-30} " +
            $"{account.BalanceAmount.ToString("C", usd),14}  as of {asOf:MMM d h:mm tt}");
    }
}

async Task Propose()
{
    var (purchases, categories, now) = await LoadHistory(now => now.AddDays(-90));
    var plan = await new BudgetPlanner(Llm()).ProposeAsync(purchases, categories, now);
    var store = new BudgetStore(dataDir);
    store.SaveProposed(plan);

    Console.WriteLine($"Proposed monthly budget (based on {plan.BasedOnMonths} full month(s)):\n");
    Console.WriteLine($"{"Category",-15} {"Average",10} {"Limit",10}  Note");
    foreach (var c in plan.Categories)
        Console.WriteLine($"{c.Category,-15} {c.AverageSpent.ToString("C0", usd),10} {c.Limit.ToString("C0", usd),10}  {c.Note}");
    Console.WriteLine($"{"Total",-15} {plan.Categories.Sum(c => c.AverageSpent).ToString("C0", usd),10} " +
                      $"{plan.Categories.Sum(c => c.Limit).ToString("C0", usd),10}");
    Console.WriteLine($"\n{plan.Summary}");
    Console.WriteLine($"\nNothing is active yet. Edit {Path.GetRelativePath(root, store.ProposedPath)} if you like, " +
                      "then run: budget approve");
}

void Approve()
{
    var plan = new BudgetStore(dataDir).Approve();
    Console.WriteLine($"Budget approved: {plan.Categories.Count} categories, " +
                      $"{plan.Categories.Sum(c => c.Limit).ToString("C0", usd)} a month. Run: budget show");
}

async Task Show()
{
    var plan = new BudgetStore(dataDir).Active
               ?? throw new InvalidOperationException("No active budget. Run: budget propose, then budget approve");
    var (purchases, categories, now) = await LoadHistory(MonthStart);
    var rows = BudgetPlanner.Status(plan, purchases, categories, now);

    Console.WriteLine($"{now.ToLocalTime():MMMM yyyy}, day {now.ToLocalTime().Day}\n");
    Console.WriteLine($"{"Category",-15} {"Limit",10} {"Spent",10} {"Left",10}");
    foreach (var r in rows.OrderBy(r => r.Left))
        Console.WriteLine($"{r.Category,-15} {r.Limit.ToString("C0", usd),10} {r.Spent.ToString("C0", usd),10} " +
                          $"{r.Left.ToString("C0", usd),10}{(r.Left < 0 ? "  over" : "")}");
    Console.WriteLine($"{"Total",-15} {rows.Sum(r => r.Limit).ToString("C0", usd),10} " +
                      $"{rows.Sum(r => r.Spent).ToString("C0", usd),10} {rows.Sum(r => r.Left).ToString("C0", usd),10}");
}

async Task Watch()
{
    var print = args.Contains("--print");
    var once = args.Contains("--once");
    var replayIndex = Array.IndexOf(args, "--replay");
    var replay = replayIndex >= 0 && replayIndex + 1 < args.Length ? int.Parse(args[replayIndex + 1]) : 0;
    ISmsSender sms = print ? new PrintSmsSender(Console.Out) : Twilio();
    var watcher = new Watcher(
        start => LoadAccounts("sample-history.json", start),
        new Categorizer(Llm(), Path.Combine(dataDir, "categories.json")),
        new BudgetStore(dataDir),
        sms,
        Path.Combine(dataDir, "watch-state.json"),
        args.Contains("--plain") ? null : new Roaster(Llm()));

    Console.WriteLine($"Watching for new purchases ({(print ? "print mode, nothing is texted" : "texting via Twilio")}).");
    while (true)
    {
        // Sample data is fixed in time, so a sample run behaves as if it's just after the last purchase.
        var now = sample ? SampleNow() : DateTimeOffset.Now;
        var lastPoll = WatchState.Load(Path.Combine(dataDir, "watch-state.json")).LastPoll;
        if (!once && lastPoll is not null && DateTimeOffset.Now - lastPoll < Watcher.MinInterval)
        {
            // After a restart, wait out the rest of the interval instead of polling SimpleFIN early.
            var wait = lastPoll.Value + Watcher.MinInterval - DateTimeOffset.Now;
            Console.WriteLine($"Next check at {DateTime.Now + wait:h:mm tt}.");
            await Task.Delay(wait);
            continue;
        }
        try
        {
            var result = await watcher.PollOnceAsync(now, replay);
            Console.WriteLine($"{DateTime.Now:MMM d h:mm tt}: {result.NewPurchases} new purchase(s), " +
                              $"{result.Sent} text(s) sent, {result.Pending} waiting to retry.");
        }
        catch (Exception ex) when (!once && ex is not OperationCanceledException)
        {
            // Keep watching through any failure: crashing would make the service restart and poll SimpleFIN
            // again right away, and a crash loop would burn through the Bridge's daily limit.
            Console.Error.WriteLine(
                $"{DateTime.Now:MMM d h:mm tt}: check failed, will retry in 2 hours: {ex.GetType().Name}: {ex.Message}");
        }
        if (once) return;
        replay = 0;
        await Task.Delay(Watcher.MinInterval);
    }
}

DateTimeOffset SampleNow()
{
    var set = SimpleFinClient.ParseAccounts(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample-history.json")));
    return Purchase.From(set).Max(p => p.Posted).AddHours(1);
}

ISmsSender Twilio()
{
    string Need(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
        ? v
        : throw new InvalidOperationException($"No {key} in .env. Add the Twilio values, or run watch --print.");
    return new TwilioSmsSender(http, Need("TWILIO_ACCOUNT_SID"), Need("TWILIO_AUTH_TOKEN"),
        Need("TWILIO_FROM_NUMBER"), Need("BUDGETER_TO_NUMBER"));
}

async Task<(List<Purchase>, IReadOnlyDictionary<string, string>, DateTimeOffset)> LoadHistory(
    Func<DateTimeOffset, DateTimeOffset> startFrom)
{
    var now = DateTimeOffset.Now;
    var purchases = Purchase.From(await LoadAccounts("sample-history.json", startFrom(now)));
    if (sample && purchases.Count > 0)
    {
        // Sample data is fixed in time, so "now" is just after its last purchase.
        now = purchases.Max(p => p.Posted).AddHours(1);
        purchases = purchases.Where(p => p.Posted >= startFrom(now)).ToList();
    }
    var categories = await new Categorizer(Llm(), Path.Combine(dataDir, "categories.json"))
        .CategorizeAsync(purchases.Select(p => p.Description));
    return (purchases, categories, now);
}

async Task<AccountSet> LoadAccounts(string sampleFile, DateTimeOffset? start)
{
    if (sample)
        return SimpleFinClient.ParseAccounts(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, sampleFile)));
    if (!env.TryGetValue(AccessKey, out var accessUrl))
        throw new InvalidOperationException($"No {AccessKey} in .env. Run: dotnet run --project src/Budgeter -- claim");
    return await client.GetAccountsAsync(accessUrl, start);
}

static DateTimeOffset MonthStart(DateTimeOffset now)
{
    var local = now.ToLocalTime();
    return new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset);
}

ILlm Llm()
{
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        throw new InvalidOperationException("No ANTHROPIC_API_KEY in .env.");
    return new ClaudeClient(Environment.GetEnvironmentVariable("BUDGETER_MODEL") ?? "claude-sonnet-5-5");
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
