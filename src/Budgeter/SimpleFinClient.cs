using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Budgeter;

/// <summary>SimpleFIN protocol: claim a setup token once, then read accounts with the access URL.</summary>
public sealed class SimpleFinClient(HttpClient http)
{
    /// <summary>A setup token is the base64 of a one-time claim URL.</summary>
    public static Uri DecodeSetupToken(string token)
    {
        var bytes = Convert.FromBase64String(PadBase64(token.Trim()));
        var url = Encoding.UTF8.GetString(bytes);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("Setup token does not decode to an https claim URL.");
        return uri;
    }

    /// <summary>POSTs to the claim URL; returns the long-lived access URL. The token is dead afterwards.</summary>
    public async Task<string> ClaimAsync(string setupToken, CancellationToken ct = default)
    {
        var claimUrl = DecodeSetupToken(setupToken);
        using var response = await http.PostAsync(claimUrl, new ByteArrayContent([]), ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Claim failed ({(int)response.StatusCode}). The token may already be used; generate a new one.");
        var accessUrl = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!Uri.TryCreate(accessUrl, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.UserInfo))
            throw new InvalidOperationException("Claim returned something that is not an access URL.");
        return accessUrl;
    }

    public async Task<AccountSet> GetAccountsAsync(string accessUrl, DateTimeOffset? start = null,
        CancellationToken ct = default)
    {
        var (baseUrl, auth) = SplitAccessUrl(accessUrl);
        var query = start is null ? "" : $"?start-date={start.Value.ToUnixTimeSeconds()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/accounts{query}");
        request.Headers.Authorization = auth;
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SimpleFIN /accounts failed ({(int)response.StatusCode}).");
        return ParseAccounts(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>HttpClient ignores user:pass in a URL, so move it into a Basic header.</summary>
    public static (string BaseUrl, AuthenticationHeaderValue Auth) SplitAccessUrl(string accessUrl)
    {
        var uri = new Uri(accessUrl);
        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo)));
        var baseUrl = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString().TrimEnd('/');
        return (baseUrl, auth);
    }

    public static AccountSet ParseAccounts(string json) =>
        JsonSerializer.Deserialize<AccountSet>(json) ?? throw new FormatException("Empty SimpleFIN response.");

    private static string PadBase64(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
}

public sealed record AccountSet(
    [property: JsonPropertyName("errors")] List<string>? Errors,
    [property: JsonPropertyName("accounts")] List<Account> Accounts);

public sealed record Account(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("balance")] string Balance,
    [property: JsonPropertyName("available-balance")] string? AvailableBalance,
    [property: JsonPropertyName("balance-date")] long BalanceDate,
    [property: JsonPropertyName("org")] Org Org,
    [property: JsonPropertyName("transactions")] List<Transaction>? Transactions)
{
    public decimal BalanceAmount => decimal.Parse(Balance, System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record Org(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("domain")] string? Domain);

public sealed record Transaction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("posted")] long Posted,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("pending")] bool? Pending);
