using System.Net;
using System.Text.Json;
using Budgeter;

namespace Budgeter.Tests;

/// <summary>Buttons, proposals from chat, wrap-ups, streaks and charts.</summary>
public class FeatureTests : IDisposable
{
    // Monday Nov 2, 2026: the first sync of November.
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"features-{Guid.NewGuid()}");
    private readonly List<object> _transactions = [];

    public FeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void Add(string id, string description, string amount, int month, int day) =>
        _transactions.Add(new
        {
            id,
            posted = new DateTimeOffset(2026, month, day, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            amount,
            description,
            pending = false,
        });

    private AccountSet Set() => SimpleFinClient.ParseAccounts(JsonSerializer.Serialize(new
    {
        accounts = new[] { new { id = "a", name = "Card", currency = "USD", balance = "-1", org = new { }, transactions = _transactions } },
    }));

    private BudgetStore Budget(decimal bars = 100, decimal groceries = 400)
    {
        File.WriteAllText(Path.Combine(_dir, "categories.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["PUB"] = "bars", ["KROGER"] = "groceries", ["NETFLIX"] = "subscriptions",
        }));
        var store = new BudgetStore(_dir);
        store.SaveProposed(new BudgetPlan(Now, 2,
            [new CategoryLimit("bars", bars, 150, ""), new CategoryLimit("groceries", groceries, 400, "")], ""));
        store.Approve();
        return store;
    }

    private Assistant NewAssistant(FakeLlm llm, MonthlyWrapUp? wrapUps = null)
    {
        var snapshots = new SnapshotStore(Path.Combine(_dir, "snapshot.json"));
        snapshots.Save(new TransactionSnapshot(Now, Set()));
        return new Assistant(snapshots, new Categorizer(llm, Path.Combine(_dir, "categories.json")), new BudgetStore(_dir), llm,
            () => Now, null, wrapUps);
    }

    private const string Proposal = """{"categories":[{"category":"bars","limit":80,"note":"Cut."}],"summary":"Less beer."}""";

    // ---- Budget proposals with buttons (AC-28)

    [Fact]
    public async Task Propose_replies_with_the_plan_and_approve_and_discard_buttons()
    {
        Budget();
        Add("0", "PUB", "-100.00", 8, 2); Add("1", "PUB", "-90.00", 9, 5); Add("2", "PUB", "-110.00", 10, 7);
        var assistant = NewAssistant(new FakeLlm(Proposal));

        var reply = await assistant.HandleReplyAsync("propose");

        Assert.Contains("Bars: $80 (avg $100, now $100)", reply.Text);
        Assert.Equal(["budget:approve", "budget:discard"], reply.Buttons!.Single().Select(b => b.Data));
        Assert.Equal(100m, new BudgetStore(_dir).Active!.Categories.Single(c => c.Category == "bars").Limit); // not yet

        var approved = await assistant.HandleButtonAsync("budget:approve");
        Assert.StartsWith("Budget approved: 1 categories, $80 a month.", approved.Text);
        Assert.Equal(80m, new BudgetStore(_dir).Active!.Categories.Single().Limit);
        Assert.Contains("no proposed budget", (await assistant.HandleButtonAsync("budget:approve")).Text);
    }

    [Fact]
    public async Task Discard_throws_the_proposal_away_and_keeps_the_budget()
    {
        Budget();
        Add("0", "PUB", "-100.00", 8, 2); Add("1", "PUB", "-90.00", 9, 5); Add("2", "PUB", "-110.00", 10, 7);
        var assistant = NewAssistant(new FakeLlm(Proposal));
        await assistant.HandleReplyAsync("propose");

        Assert.Contains("discarded", (await assistant.HandleButtonAsync("budget:discard")).Text);
        Assert.Null(new BudgetStore(_dir).Proposed);
        Assert.Equal(100m, new BudgetStore(_dir).Active!.Categories.Single(c => c.Category == "bars").Limit);
    }

    // ---- Monthly wrap-up (AC-29)

    private const string WrapReply = """
        {"take":"October was a bar crawl with a budget attached.","suggestions":[
          {"category":"groceries","limit":450,"reason":"You keep running out."},
          {"category":"bars","limit":100,"reason":"Same as now, so it should be dropped."}]}
        """;

    [Fact]
    public async Task Wrap_up_reports_last_month_and_offers_apply_buttons_that_work_once()
    {
        var store = Budget();
        Add("1", "PUB", "-160.00", 10, 10); Add("2", "KROGER", "-420.00", 10, 12); Add("3", "PUB", "-15.00", 11, 1);
        var wrapUps = new MonthlyWrapUp(new FakeLlm(WrapReply), store, Path.Combine(_dir, "wrapups.json"));
        var assistant = NewAssistant(new FakeLlm(), wrapUps);

        var reply = await assistant.HandleReplyAsync("tune up");

        Assert.StartsWith("October wrap-up: spent $580 of $500.\nOver in bars (+$60), groceries (+$20).", reply.Text);
        Assert.Contains("October was a bar crawl", reply.Text);
        Assert.Contains("1. groceries: $400 -> $450. You keep running out.", reply.Text);
        Assert.DoesNotContain("2. bars", reply.Text); // a "change" to the current limit is dropped
        Assert.Equal(["wrap:2026-10:0", "wrap:2026-10:skip"], reply.Buttons!.Select(r => r.Single().Data));

        Assert.Equal("Groceries limit is now $450. $450 left this month.", (await assistant.HandleButtonAsync("wrap:2026-10:0")).Text);
        Assert.Equal("Already applied.", (await assistant.HandleButtonAsync("wrap:2026-10:0")).Text);
        Assert.Equal("Kept your limits as they are.", (await assistant.HandleButtonAsync("wrap:2026-10:skip")).Text);
        Assert.Equal("That suggestion has expired.", (await assistant.HandleButtonAsync("wrap:2025-01:0")).Text);
    }

    [Fact]
    public async Task Wrap_up_still_sends_the_numbers_when_Sonnet_fails()
    {
        var store = Budget();
        Add("1", "PUB", "-60.00", 10, 10);
        var wrap = await new MonthlyWrapUp(new FakeLlm("garbage"), store, Path.Combine(_dir, "wrapups.json"))
            .BuildAsync(HistoryStore.Purchases(Set().Accounts[0].Transactions!), new Dictionary<string, string> { ["PUB"] = "bars" }, Now);

        Assert.StartsWith("October wrap-up: spent $60 of $500.", wrap!.Text);
        Assert.Null(wrap.Buttons);
    }

    private sealed class RecordingSender : IAlertSender
    {
        public List<BotReply> Sent { get; } = [];
        public string? Welcome => null;
        public string Footer => "";
        public Task SendAsync(string body, CancellationToken ct = default) => SendAsync(new BotReply(body), ct);

        public Task SendAsync(BotReply message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private Watcher NewWatcher(RecordingSender sender, FakeLlm wrapLlm, BudgetStore store) =>
        new(_ => Task.FromResult(Set()), new Categorizer(new FakeLlm(), Path.Combine(_dir, "categories.json")), store, sender,
            Path.Combine(_dir, "watch-state.json"), null, null, null, new MonthlyWrapUp(wrapLlm, store, Path.Combine(_dir, "wrapups.json")));

    [Fact]
    public async Task Watch_sends_the_wrap_up_once_on_the_first_sync_of_a_month_but_not_on_install()
    {
        var store = Budget();
        Add("1", "KROGER", "-420.00", 10, 12);
        var sender = new RecordingSender();
        var watcher = NewWatcher(sender, new FakeLlm(WrapReply, WrapReply), store);

        await watcher.PollOnceAsync(Now.AddDays(-5)); // first run, in October: nothing
        await watcher.PollOnceAsync(Now.AddDays(-4)); // still October: nothing
        await watcher.PollOnceAsync(Now);             // first November sync: the October wrap-up
        await watcher.PollOnceAsync(Now.AddHours(2)); // only once

        var wrap = Assert.Single(sender.Sent, m => m.Text.StartsWith("October wrap-up"));
        Assert.NotNull(wrap.Buttons);
    }

    [Fact]
    public async Task Old_state_files_with_plain_string_outboxes_still_load()
    {
        var path = Path.Combine(_dir, "watch-state.json");
        File.WriteAllText(path, """{"initialized":true,"outbox":["old text"]}""");

        var state = WatchState.Load(path);

        Assert.Equal("old text", state.Outbox.Single().Text);
        state.Save(path);
        Assert.Equal("old text", WatchState.Load(path).Outbox.Single().Text);
    }

    // ---- Streaks (AC-30)

    [Fact]
    public void Streak_counts_days_since_the_last_bar_charge_posted()
    {
        var purchases = new List<Purchase>
        {
            new("1", new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero), "PUB", 30m),
            new("2", new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.Zero), "PUB", 40m),
            new("3", new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero), "KROGER", 40m),
        };
        var cats = new Dictionary<string, string> { ["PUB"] = "bars", ["KROGER"] = "groceries" };

        var (since, days, any) = Streaks.BarStreak(purchases, cats, Now);

        Assert.Equal((new DateTime(2026, 10, 25), 8, true), (since, days, any));
        Assert.Equal(7, Streaks.MilestoneFor(days));
        Assert.Contains("No bar charge has posted for 8 days (last one Oct 25). Next milestone: 14 days.", Streaks.Status(purchases, cats, Now));
    }

    [Fact]
    public async Task Watch_praises_each_milestone_once_and_a_new_bar_charge_restarts_the_count()
    {
        var store = Budget();
        Add("1", "PUB", "-30.00", 10, 28);
        Add("2", "KROGER", "-40.00", 10, 29);
        var sender = new RecordingSender();
        var watcher = NewWatcher(sender, new FakeLlm(), store);

        await watcher.PollOnceAsync(new DateTimeOffset(2026, 10, 29, 18, 0, 0, TimeSpan.Zero)); // install: 1 day, nothing
        await watcher.PollOnceAsync(new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero)); // 3 days: praise
        await watcher.PollOnceAsync(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero)); // still 3: nothing new

        Assert.Single(sender.Sent, m => m.Text.StartsWith("3 days without a bar tab"));

        Add("3", "PUB", "-20.00", 11, 1);
        await watcher.PollOnceAsync(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero));
        await watcher.PollOnceAsync(new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero)); // 3 days again: praise again
        Assert.Equal(2, sender.Sent.Count(m => m.Text.StartsWith("3 days without a bar tab")));
    }

    // ---- Charts (AC-31)

    private static bool IsPng(byte[] bytes) => bytes.Length > 1000 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    [Fact]
    public void Charts_draw_png_images()
    {
        var rows = new List<CategoryStatus> { new("bars", 100, 160), new("groceries", 400, 210), new("coffee", 40, 0) };
        Assert.True(IsPng(Charts.ByCategory(rows, Now)));
        Assert.True(IsPng(Charts.ByDay([new("1", Now.AddDays(-1), "PUB", 30m), new("2", Now, "KROGER", 80m)], 500, Now)));
    }

    [Fact]
    public async Task Chart_command_and_a_Sonnet_chart_request_both_return_a_photo()
    {
        Budget();
        Add("1", "PUB", "-60.00", 11, 1);
        var llm = new FakeLlm("""{"reply":"Here's your month.","set_limits":[],"chart":"month_by_day"}""");
        var assistant = NewAssistant(llm);

        var chart = await assistant.HandleReplyAsync("chart");
        Assert.True(IsPng(chart.Photo!));
        Assert.StartsWith("Spent $60 of $500 so far.", chart.Text);

        var asked = await assistant.HandleReplyAsync("can you graph my spending by day?");
        Assert.True(IsPng(asked.Photo!));
        Assert.Equal("Here's your month.", asked.Text);
    }

    // ---- Telegram: buttons, photos, taps (AC-28, AC-32)

    private sealed class FakeTelegram(string updates) : HttpMessageHandler
    {
        public List<(string Method, string? ContentType, string Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (method == "getUpdates") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(updates) };
            Posts.Add((method, request.Content?.Headers.ContentType?.MediaType, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    [Fact]
    public async Task Telegram_sends_buttons_as_an_inline_keyboard_and_charts_as_photos()
    {
        var telegram = new FakeTelegram("""{"ok":true,"result":[]}""");
        var http = new HttpClient(telegram);

        await Telegram.SendAsync(http, "t", "555", new BotReply("Pick one", [[new Button("Approve", "budget:approve")]]));
        await Telegram.SendAsync(http, "t", "555", new BotReply("Your month", Photo: [0x89, 0x50, 0x4E, 0x47]));

        Assert.Equal("""{"chat_id":"555","text":"Pick one","reply_markup":{"inline_keyboard":[[{"text":"Approve","callback_data":"budget:approve"}]]}}""",
            telegram.Posts[0].Body);
        Assert.Equal(("sendPhoto", "multipart/form-data"), (telegram.Posts[1].Method, telegram.Posts[1].ContentType));
        Assert.Contains("Your month", telegram.Posts[1].Body);
    }

    [Fact]
    public async Task Button_taps_count_only_from_my_chat_and_remove_the_buttons()
    {
        Budget();
        Add("0", "PUB", "-100.00", 8, 2); Add("1", "PUB", "-90.00", 9, 5); Add("2", "PUB", "-110.00", 10, 7);
        var assistant = NewAssistant(new FakeLlm(Proposal));
        await assistant.HandleReplyAsync("propose");
        var telegram = new FakeTelegram("""
            {"ok":true,"result":[
              {"update_id":1,"callback_query":{"id":"x","from":{"id":999},"data":"budget:approve","message":{"message_id":7,"chat":{"id":999}}}},
              {"update_id":2,"callback_query":{"id":"y","from":{"id":999},"data":"budget:approve","message":{"message_id":7,"chat":{"id":555}}}},
              {"update_id":3,"callback_query":{"id":"z","from":{"id":555},"data":"budget:approve","message":{"message_id":8,"chat":{"id":555}}}}]}
            """);
        var bot = new TelegramBot(new HttpClient(telegram), "t", "555", assistant, Path.Combine(_dir, "offset.txt"));

        Assert.Equal(1, await bot.PollOnceAsync(0));

        Assert.Equal(["answerCallbackQuery", "editMessageReplyMarkup", "sendMessage"], telegram.Posts.Select(p => p.Method));
        Assert.Contains("\"message_id\":8", telegram.Posts[1].Body);
        Assert.Contains("Budget approved", telegram.Posts[2].Body);
        Assert.DoesNotContain(telegram.Posts, p => p.Body.Contains("999"));
        Assert.Equal(80m, new BudgetStore(_dir).Active!.Categories.Single().Limit);
    }
}
