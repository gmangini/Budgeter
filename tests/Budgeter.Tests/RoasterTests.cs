using Budgeter;

namespace Budgeter.Tests;

public class RoasterTests
{
    // Oct 10 of a 31-day month: about a third of the month gone.
    private static readonly Purchase Pub = new("1", new DateTimeOffset(2026, 10, 10, 23, 30, 0, TimeSpan.Zero), "O'MALLEY'S PUB", 84);

    private sealed class ThrowingLlm(Exception ex) : ILlm
    {
        public Task<string> AskJsonAsync(string system, string user, string jsonSchema, CancellationToken ct = default) =>
            Task.FromException<string>(ex);
    }

    private sealed class SlowLlm : ILlm
    {
        public async Task<string> AskJsonAsync(string system, string user, string jsonSchema, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return """{"comment":"too late"}""";
        }
    }

    [Theory]
    [InlineData(100, 20, 1)]   // 20% spent, ~32% of the month gone: under pace
    [InlineData(100, 60, 2)]   // ahead of pace
    [InlineData(100, 130, 3)]  // over the limit
    public void Heat_follows_pace_and_limit(decimal limit, decimal spent, int heat) =>
        Assert.Equal(heat, Roaster.Heat(new CategoryStatus("bars", limit, spent), Pub.Posted));

    [Fact]
    public async Task Bars_prompt_allows_profanity_and_gets_harsher_over_budget()
    {
        var llm = new FakeLlm("""{"comment":"Again? Your liver filed a complaint."}""", """{"comment":"x"}""");
        var roaster = new Roaster(llm);

        var comment = await roaster.CommentAsync(Pub, "bars", new CategoryStatus("bars", 100, 20));
        await roaster.CommentAsync(Pub, "bars", new CategoryStatus("bars", 100, 130));

        Assert.Equal("Again? Your liver filed a complaint.", comment);
        Assert.Contains("Profanity is allowed", llm.Prompts[0]);
        Assert.Contains("brutal", llm.Prompts[1]);
        Assert.Contains("go home", llm.Prompts[1]);
        Assert.Contains("$84.00 at O'MALLEY'S PUB", llm.Prompts[0]);
    }

    [Fact]
    public async Task Other_discretionary_spending_is_sassy_without_profanity()
    {
        var llm = new FakeLlm("""{"comment":"Bold of you."}""");
        await new Roaster(llm).CommentAsync(Pub with { Description = "DOORDASH" }, "takeout", new CategoryStatus("takeout", 100, 10));

        Assert.Contains("sassy", llm.Prompts.Single());
        Assert.Contains("No profanity", llm.Prompts.Single());
    }

    [Theory]
    [InlineData("groceries")]
    [InlineData("gas")]
    [InlineData("bills")]
    [InlineData("health")]
    public async Task Essentials_get_no_comment_and_no_Sonnet_call(string category)
    {
        var llm = new FakeLlm();
        Assert.Null(await new Roaster(llm).CommentAsync(Pub, category, new CategoryStatus(category, 100, 500)));
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public async Task Sonnet_failure_or_timeout_means_no_comment_not_an_error()
    {
        var status = new CategoryStatus("bars", 100, 20);
        Assert.Null(await new Roaster(new ThrowingLlm(new InvalidOperationException("Claude declined"))).CommentAsync(Pub, "bars", status));
        Assert.Null(await new Roaster(new FakeLlm("not json")).CommentAsync(Pub, "bars", status));
        Assert.Null(await new Roaster(new SlowLlm(), TimeSpan.FromMilliseconds(50)).CommentAsync(Pub, "bars", status));
    }

    [Fact]
    public void Shorten_cuts_long_comments_at_a_word()
    {
        var shortened = Roaster.Shorten(string.Join(" ", Enumerable.Repeat("drinking", 40)));
        Assert.True(shortened.Length <= Roaster.MaxLength + 3);
        Assert.EndsWith("drinking...", shortened);
    }

    [Fact]
    public void Comment_goes_after_the_budget_line()
    {
        var text = Watcher.Message(Pub, "bars", new CategoryStatus("bars", 100, 84), "Go home.");
        Assert.Equal("Budgeter: $84.00 at O'MALLEY'S PUB (bars). $16 left for bars this month. Go home.", text);
    }
}
