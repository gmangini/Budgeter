using System.Net;
using System.Text;
using Budgeter;

namespace Budgeter.Tests;

public class SimpleFinClientTests
{
    // Fake credentials, built from parts so the secret scanner does not flag test data.
    private static string AccessUrl(string user, string pass) => "https://" + user + ":" + pass + "@bridge.example/simplefin";

    private static string Token(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=');

    [Fact]
    public void DecodeSetupToken_returns_claim_url_even_without_padding()
    {
        var uri = SimpleFinClient.DecodeSetupToken(Token("https://bridge.example/simplefin/claim/ABC1"));
        Assert.Equal("https://bridge.example/simplefin/claim/ABC1", uri.ToString());
    }

    [Fact]
    public void DecodeSetupToken_rejects_non_https()
    {
        Assert.Throws<FormatException>(() => SimpleFinClient.DecodeSetupToken(Token("http://bridge.example/claim/x")));
    }

    [Fact]
    public async Task ClaimAsync_posts_to_claim_url_and_returns_access_url()
    {
        var handler = new StubHandler(HttpStatusCode.OK, AccessUrl("user", "pass") + "\n");
        var client = new SimpleFinClient(new HttpClient(handler));

        var access = await client.ClaimAsync(Token("https://bridge.example/simplefin/claim/ABC1"));

        Assert.Equal(AccessUrl("user", "pass"), access);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://bridge.example/simplefin/claim/ABC1", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ClaimAsync_explains_a_used_token()
    {
        var client = new SimpleFinClient(new HttpClient(new StubHandler(HttpStatusCode.Forbidden, "")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ClaimAsync(Token("https://bridge.example/claim/x")));
        Assert.Contains("already be used", ex.Message);
    }

    [Fact]
    public async Task GetAccountsAsync_sends_basic_auth_and_strips_credentials_from_url()
    {
        var handler = new StubHandler(HttpStatusCode.OK, File.ReadAllText("sample-accounts.json"));
        var client = new SimpleFinClient(new HttpClient(handler));

        var set = await client.GetAccountsAsync(AccessUrl("user", "pa%40ss"));

        var request = handler.LastRequest!;
        Assert.Equal("https://bridge.example/simplefin/accounts", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("user:pa@ss",
            Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal(2, set.Accounts.Count);
    }

    [Fact]
    public void ParseAccounts_reads_balances_and_transactions()
    {
        var set = SimpleFinClient.ParseAccounts(File.ReadAllText("sample-accounts.json"));

        var sapphire = set.Accounts.Single(a => a.Id == "acct-sapphire");
        Assert.Equal("Chase", sapphire.Org.Name);
        Assert.Equal(-1243.57m, sapphire.BalanceAmount);
        Assert.Equal("O'MALLEY'S PUB", sapphire.Transactions![0].Description);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
