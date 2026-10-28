namespace Budgeter;

/// <summary>Minimal KEY=VALUE .env reader and writer. Keeps other lines and comments as they are.</summary>
public static class EnvFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            values[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim().Trim('"');
        }
        return values;
    }

    public static void Set(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var index = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
        var entry = $"{key}={value}";
        if (index >= 0) lines[index] = entry; else lines.Add(entry);
        File.WriteAllLines(path, lines);
    }
}
