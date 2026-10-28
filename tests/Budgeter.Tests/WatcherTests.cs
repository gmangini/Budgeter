using System.Net;
using System.Text.Json;
using Budgeter;

namespace Budgeter.Tests;

public class WatcherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watch-{Guid.NewGuid()}");
    private readonly List<object> _transactions = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void Add(string id, string description, string amount, int day, bool pending = false) =>
        _transactions.Add(new
        {
            id,
            posted = new DateTimeOffset(2026, 10, day, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            amount,
            description,
            pending,
        });

    private Task<AccountSet> Fetch(DateTimeOffset _) => Task.FromResult(SimpleFinClient.ParseAccounts(
        JsonSerializer.Serialize(new
        {
            accounts = new[]
            {
                new { id = "a", name = "Card", currency = "USD", balance = "-1", balance_date = 0, org = new { },
                      transactions = _transactions },
            },
        })));

    private sealed class RecordingSms(bool real = false, int failFirst = 0) : ISmsSender
    {
        private int _failures = failFirst;
        public List<string> Sent { get; } = [];
        public bool IsReal => real;

        public Task SendAsync(string body, CancellationToken ct = default)
        {
            if (_failures-- > 0) throw new SmsException("Twilio is down");
            Sent.Add(body);
            return Task.CompletedTask;
        }
    }

    private Watcher NewWatcher(ISmsSender sms, string categoriesJson = """{"items":[]}""")
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_dir).FullName, "categories.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["PUB"] = "bars", ["KROGER"] = "groceries" }));
        var store = new BudgetStore(_dir);
        store.SaveProposed(new BudgetPlan(Now, 2, [new CategoryLimit("bars", 100, 150, "")], ""));
        store.Approve();
        return new Watcher(Fetch, new Categorizer(new FakeLlm(categoriesJson, categoriesJson), Path.Combine(_dir, "categories.json")),
            store, sms, Path.Combine(_dir, "watch-state.json"));
    }

    [Fact]
    public async Task First_run_marks_history_seen_and_texts_nothing()
    {
        Add("1", "PUB", "-40.00", 2);
        Add("2", "KROGER", "-60.00", 3);
        var sms = new RecordingSms();

        var result = await NewWatcher(sms).PollOnceAsync(Now);

        Assert.Equal(0, result.NewPurchases);
        Assert.Empty(sms.Sent);
    }

    [Fact]
    public async Task New_purchase_gets_one_text_with_whats_left_and_never_a_second()
    {
        Add("1", "PUB", "-40.00", 2);
        var sms = new RecordingSms();
        var watcher = NewWatcher(sms);
        await watcher.PollOnceAsync(Now);

        Add("2", "PUB", "-25.50", 25);
        await watcher.PollOnceAsync(Now);
        await watcher.PollOnceAsync(Now);
        // A restart is just a new Watcher reading the same state file.
        await NewWatcher(sms).PollOnceAsync(Now);

        var text = Assert.Single(sms.Sent);
        Assert.Equal("Budgeter: $25.50 at PUB (bars). $34 left for bars this month. Reply STOP to opt out.", text);
    }

    [Fact]
    public async Task Each_text_in_a_batch_shows_whats_left_after_that_purchase()
    {
        var sms = new RecordingSms();
        var watcher = NewWatcher(sms);
        await watcher.PollOnceAsync(Now);

        Add("1", "PUB", "-30.00", 24);
        Add("2", "PUB", "-50.00", 25);
        await watcher.PollOnceAsync(Now);

        Assert.Contains("$70 left", sms.Sent[0]);
        Assert.Contains("$20 left", sms.Sent[1]);
    }

    [Fact]
    public async Task History_older_than_the_lookback_is_never_texted_even_after_it_is_forgotten()
    {
        // Way before the fetch window, and older than the 60-day memory, so it gets pruned from the state.
        _transactions.Add(new { id = "old", posted = Now.AddDays(-120).ToUnixTimeSeconds(), amount = "-9.00",
                                description = "PUB", pending = false });
        var sms = new RecordingSms();
        var watcher = NewWatcher(sms);

        await watcher.PollOnceAsync(Now);
        await watcher.PollOnceAsync(Now);

        Assert.Empty(sms.Sent);
    }

    [Fact]
    public async Task Over_budget_says_how_much_over()
    {
        var sms = new RecordingSms();
        var watcher = NewWatcher(sms);
        await watcher.PollOnceAsync(Now);

        Add("1", "PUB", "-130.00", 25);
        await watcher.PollOnceAsync(Now);

        Assert.Contains("$30 over for bars this month.", Assert.Single(sms.Sent));
    }

    [Fact]
    public async Task Payments_refunds_and_pending_holds_send_no_text_and_refunds_lower_the_total()
    {
        Add("1", "PUB", "-90.00", 2);
        var sms = new RecordingSms();
        var watcher = NewWatcher(sms);
        await watcher.PollOnceAsync(Now);

        Add("2", "CAPITAL ONE MOBILE PYMT", "500.00", 20);
        Add("3", "PUB", "30.00", 21);
        Add("4", "PUB", "-15.00", 22, pending: true);
        await watcher.PollOnceAsync(Now);
        Assert.Empty(sms.Sent);

        Add("5", "PUB", "-10.00", 25);
        await watcher.PollOnceAsync(Now);
        // 100 limit - (90 - 30 refund + 10) = 30 left; the payment and the pending hold don't count.
        Assert.Contains("$30 left for bars", Assert.Single(sms.Sent));
    }

    [Fact]
    public async Task Failed_text_stays_queued_and_goes_out_on_the_next_poll()
    {
        var sms = new RecordingSms(failFirst: 1);
        var watcher = NewWatcher(sms);
        await watcher.PollOnceAsync(Now);

        Add("1", "PUB", "-20.00", 25);
        var first = await watcher.PollOnceAsync(Now);
        Assert.Equal((0, 1), (first.Sent, first.Pending));

        var second = await watcher.PollOnceAsync(Now);
        Assert.Equal((1, 0), (second.Sent, second.Pending));
        Assert.Single(sms.Sent);
    }

    [Fact]
    public async Task Real_sender_gets_the_opt_in_confirmation_once()
    {
        var sms = new RecordingSms(real: true);
        var watcher = NewWatcher(sms);

        await watcher.PollOnceAsync(Now);
        await watcher.PollOnceAsync(Now);

        Assert.Equal([Watcher.OptInMessage], sms.Sent);
    }

    [Fact]
    public async Task Replay_leaves_the_newest_purchases_to_be_texted_on_the_first_run()
    {
        Add("1", "PUB", "-40.00", 2);
        Add("2", "KROGER", "-60.00", 3);
        Add("3", "PUB", "-12.00", 4);
        var sms = new RecordingSms();

        var result = await NewWatcher(sms).PollOnceAsync(Now, replay: 2);

        Assert.Equal(2, result.NewPurchases);
        Assert.Contains("KROGER", sms.Sent[0]);
        Assert.Contains("$12.00 at PUB", sms.Sent[1]);
    }

    [Fact]
    public async Task Twilio_sender_posts_the_message_and_reports_errors()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"code":21211,"message":"Invalid 'To' number"}""");
        var sender = new TwilioSmsSender(new HttpClient(handler), "AC123", "tok", "+18885550100", "+15555550123");

        var ex = await Assert.ThrowsAsync<SmsException>(() => sender.SendAsync("hi"));

        Assert.Contains("Invalid 'To' number", ex.Message);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC123/Messages.json", handler.Request!.RequestUri!.ToString());
        Assert.Equal("To=%2B15555550123&From=%2B18885550100&Body=hi", handler.Body);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
