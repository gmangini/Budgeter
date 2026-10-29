using System.Net;
using System.Text.Json;
using Budgeter;

namespace Budgeter.Tests;

public class AssistantTests : IDisposable
{
    // Thursday Oct 29, 2026, local time.
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"assistant-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static object Tx(string id, string description, string amount, int month, int day) => new
    {
        id,
        posted = new DateTimeOffset(2026, month, day, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        amount,
        description,
        pending = false,
    };

    private Assistant NewAssistant(FakeLlm llm, bool withSnapshot = true, bool withBudget = true)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "categories.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["PUB"] = "bars", ["KROGER"] = "groceries", ["CAPITAL ONE MOBILE PYMT"] = "other",
        }));
        var store = new BudgetStore(_dir);
        if (withBudget)
        {
            store.SaveProposed(new BudgetPlan(Now, 2,
                [new CategoryLimit("bars", 100, 150, ""), new CategoryLimit("groceries", 400, 400, "")], ""));
            store.Approve();
        }
        var snapshots = new SnapshotStore(Path.Combine(_dir, "snapshot.json"));
        if (withSnapshot)
        {
            var set = SimpleFinClient.ParseAccounts(JsonSerializer.Serialize(new
            {
                accounts = new[]
                {
                    new
                    {
                        id = "a", name = "Card", currency = "USD", balance = "-1", org = new { },
                        transactions = new[]
                        {
                            Tx("1", "PUB", "-40.00", 10, 28),
                            Tx("2", "KROGER", "-60.00", 10, 27),
                            Tx("3", "PUB", "-25.00", 10, 19),
                            Tx("4", "CAPITAL ONE MOBILE PYMT", "500.00", 10, 20),
                            Tx("5", "KROGER", "-80.00", 9, 15),
                        },
                    },
                },
            }));
            snapshots.Save(new TransactionSnapshot(new DateTimeOffset(2026, 10, 29, 14, 0, 0, TimeSpan.Zero), set));
        }
        return new Assistant(snapshots, new Categorizer(llm, Path.Combine(_dir, "categories.json")), store, llm, () => Now);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("/start")]
    [InlineData("/help")]
    public async Task Help_needs_no_Sonnet(string message)
    {
        var llm = new FakeLlm();
        Assert.Equal(Assistant.HelpText, await NewAssistant(llm).HandleAsync(message));
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public async Task Budget_lists_each_category_with_freshness_and_needs_no_Sonnet()
    {
        var llm = new FakeLlm();
        var reply = await NewAssistant(llm).HandleAsync("Budget");

        Assert.Contains("Bars: $65 of $100, $35 left this month.", reply);
        Assert.Contains("Groceries: $60 of $400, $340 left this month.", reply);
        Assert.Contains("Data last synced", reply);
        Assert.Empty(llm.Prompts);
    }

    [Theory]
    [InlineData("bars 200")]
    [InlineData("set bars to $200")]
    [InlineData("Bars 200")]
    public async Task Short_limit_changes_apply_without_Sonnet(string message)
    {
        var llm = new FakeLlm();
        var reply = await NewAssistant(llm).HandleAsync(message);

        Assert.Equal("Bars limit is now $200. $135 left this month.", reply);
        Assert.Equal(200m, new BudgetStore(_dir).Active!.Categories.Single(c => c.Category == "bars").Limit);
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public async Task Questions_go_to_Sonnet_with_exact_totals_and_freshness()
    {
        var llm = new FakeLlm("""{"reply":"Nothing posted today yet; last charge was $40 at a pub yesterday.","set_limits":[]}""");
        var assistant = NewAssistant(llm);

        var reply = await assistant.HandleAsync("what did I spend today?");

        Assert.Equal("Nothing posted today yet; last charge was $40 at a pub yesterday.", reply);
        var prompt = llm.Prompts.Single();
        Assert.Contains("Now: Thursday, October 29, 2026", prompt);
        Assert.Contains("latest posted charge Wed Oct 28", prompt);
        Assert.Contains("Thu Oct 29: nothing posted", prompt);
        Assert.Contains("Wed Oct 28: $40.00 (bars $40.00; total $40.00)", prompt);
        Assert.Contains("This week so far (since Monday Oct 26): groceries $60.00, bars $40.00; total $100.00", prompt);
        Assert.Contains("Last month (September): groceries $80.00; total $80.00", prompt);
        Assert.Contains("bars: limit $100.00, spent $65.00, left $35.00", prompt);
        Assert.DoesNotContain("PYMT", prompt); // card payments aren't spending
        Assert.Contains("Latest message: what did I spend today?", prompt);
    }

    [Fact]
    public async Task Sonnet_can_change_limits_and_the_app_confirms_them()
    {
        var llm = new FakeLlm("""{"reply":"Fine, more beer money.","set_limits":[{"category":"bars","limit":150}]}""");

        var reply = await NewAssistant(llm).HandleAsync("give me 50 more for bars");

        Assert.Equal("Fine, more beer money.\n\nBars limit is now $150. $85 left this month.", reply);
        Assert.Equal(150m, new BudgetStore(_dir).Active!.Categories.Single(c => c.Category == "bars").Limit);
    }

    [Fact]
    public async Task Follow_up_questions_see_the_earlier_exchange()
    {
        var llm = new FakeLlm("""{"reply":"$40.","set_limits":[]}""", """{"reply":"$65.","set_limits":[]}""");
        var assistant = NewAssistant(llm);

        await assistant.HandleAsync("bars this week?");
        await assistant.HandleAsync("and this month?");

        Assert.Contains("User: bars this week?\nYou: $40.", llm.Prompts[1]);
    }

    [Fact]
    public async Task No_snapshot_or_Sonnet_failure_gets_a_friendly_reply()
    {
        Assert.Contains("don't have any synced data", await NewAssistant(new FakeLlm(), withSnapshot: false).HandleAsync("spent today?"));
        Assert.Contains("couldn't answer", await NewAssistant(new FakeLlm("not json")).HandleAsync("spent today?"));
    }

    [Fact]
    public void SetLimit_rejects_unknown_categories_and_negatives_and_adds_new_ones()
    {
        NewAssistant(new FakeLlm());
        var store = new BudgetStore(_dir);

        Assert.Throws<InvalidOperationException>(() => store.SetLimit("barz", 10));
        Assert.Throws<InvalidOperationException>(() => store.SetLimit("bars", -1));
        store.SetLimit("travel", 150.4m);
        Assert.Equal(150m, store.Active!.Categories.Single(c => c.Category == "travel").Limit);
    }
}

public class TelegramBotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bot-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Answers getUpdates with canned updates and records everything else that's posted.</summary>
    private sealed class FakeTelegram(string updates) : HttpMessageHandler
    {
        public List<(string Method, string Body)> Posts { get; } = [];
        public List<string> UpdateUrls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (method == "getUpdates")
            {
                UpdateUrls.Add(request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(updates) };
            }
            Posts.Add((method, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    [Fact]
    public async Task Answers_only_the_linked_chat_and_remembers_its_place()
    {
        Directory.CreateDirectory(_dir);
        var telegram = new FakeTelegram("""
            {"ok":true,"result":[
              {"update_id":10,"message":{"chat":{"id":999,"type":"private"},"text":"budget"}},
              {"update_id":11,"message":{"chat":{"id":555,"type":"private"},"text":"help"}},
              {"update_id":12,"message":{"chat":{"id":555,"type":"private"},"sticker":{}}}]}
            """);
        var llm = new FakeLlm();
        var assistant = new Assistant(new SnapshotStore(Path.Combine(_dir, "snapshot.json")),
            new Categorizer(llm, Path.Combine(_dir, "categories.json")), new BudgetStore(_dir), llm, () => DateTimeOffset.Now);
        var offsetPath = Path.Combine(_dir, "offset.txt");
        var bot = new TelegramBot(new HttpClient(telegram), "123:SECRET", "555", assistant, offsetPath);

        var answered = await bot.PollOnceAsync(0);

        Assert.Equal(1, answered);
        var reply = Assert.Single(telegram.Posts, p => p.Method == "sendMessage");
        Assert.Contains("\"chat_id\":\"555\"", reply.Body);
        Assert.DoesNotContain(telegram.Posts, p => p.Body.Contains("999"));
        Assert.Equal("13", File.ReadAllText(offsetPath));

        await bot.PollOnceAsync(0);
        Assert.Contains("offset=13", telegram.UpdateUrls[1]);
    }
}
