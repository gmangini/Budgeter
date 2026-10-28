using System.Text.Json;

namespace Budgeter;

/// <summary>Maps merchant descriptions to a fixed set of categories. Sonnet labels new ones; answers are cached.</summary>
public sealed class Categorizer(ILlm llm, string cachePath)
{
    public static readonly string[] Categories =
    [
        "bars", "restaurants", "takeout", "coffee", "groceries", "gas", "transport", "shopping", "entertainment",
        "subscriptions", "travel", "bills", "health", "personal care", "other",
    ];

    private const string System =
        "You categorize credit card transactions from their bank descriptions. " +
        "Pick the single best category for each. 'bars' is anywhere the purchase is mostly alcohol: pubs, bars, " +
        "breweries, taprooms, nightclubs, liquor stores. Sit-down meals are 'restaurants'; fast food, delivery apps " +
        "and counter service are 'takeout'. Use 'other' only when nothing fits.";

    private static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "items" },
        properties = new
        {
            items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "description", "category" },
                    properties = new
                    {
                        description = new { type = "string" },
                        category = new { type = "string", @enum = Categories },
                    },
                },
            },
        },
    });

    /// <summary>Returns a category for every description, asking Sonnet only about ones not seen before.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CategorizeAsync(
        IEnumerable<string> descriptions, CancellationToken ct = default)
    {
        var cache = Load();
        var missing = descriptions.Select(Key).Distinct().Where(d => !cache.ContainsKey(d)).ToList();
        if (missing.Count > 0)
        {
            var json = await llm.AskJsonAsync(System, string.Join("\n", missing), Schema, ct);
            var reply = JsonSerializer.Deserialize<Reply>(json, JsonOptions.Web)!;
            foreach (var item in reply.Items.Where(i => Categories.Contains(i.Category)))
                cache[Key(item.Description)] = item.Category;
            // Anything Sonnet skipped or renamed still gets a category, so the totals add up.
            foreach (var d in missing.Where(d => !cache.ContainsKey(d))) cache[d] = "other";
            Save(cache);
        }
        return cache;
    }

    public static string Key(string description) => description.Trim().ToUpperInvariant();

    private Dictionary<string, string> Load() =>
        File.Exists(cachePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cachePath)) ?? []
            : [];

    private void Save(Dictionary<string, string> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, JsonOptions.Indented));
    }

    private sealed record Reply(List<Item> Items);

    private sealed record Item(string Description, string Category);
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
