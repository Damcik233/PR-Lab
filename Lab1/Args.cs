using System.Globalization;

namespace Lab1;

public sealed class UsageException(string message) : Exception(message);

/// <summary>A problem with the data or a child process: reported as one line, without a stack trace.</summary>
public sealed class LabError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Minimal "--key value" / "--key=value" / "--flag" command-line parser.</summary>
public sealed class Args
{
    private readonly List<KeyValuePair<string, string?>> _options = [];
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    public Args(string[] argv)
    {
        Command = argv[0].ToLowerInvariant();
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                throw new UsageException($"unexpected argument '{arg}'");

            string key = arg[2..];
            string? value = null;
            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = argv[++i];
            }
            _options.Add(new(key, value));
        }
    }

    private string? Raw(string key)
    {
        _used.Add(key);
        for (int i = _options.Count - 1; i >= 0; i--)
            if (string.Equals(_options[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return _options[i].Value ?? throw new UsageException($"--{key} needs a value");
        return null;
    }

    public bool Has(string key)
    {
        _used.Add(key);
        return _options.Exists(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public string Str(string key, string fallback) => Raw(key) ?? fallback;

    public string Required(string key) => Raw(key) ?? throw new UsageException($"--{key} is required");

    public int Int(string key, int fallback)
    {
        string? s = Raw(key);
        if (s is null) return fallback;
        return int.TryParse(s.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)
            ? v
            : throw new UsageException($"--{key}: '{s}' is not an integer");
    }

    public int[] IntList(string key, int[] fallback)
    {
        string? s = Raw(key);
        if (s is null) return fallback;
        try
        {
            return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }
        catch (FormatException)
        {
            throw new UsageException($"--{key}: '{s}' is not a comma-separated list of integers");
        }
    }

    /// <summary>Options that no command looked at, re-serialised (used by bench to forward them).</summary>
    public List<string> UnusedAsArgv()
    {
        var list = new List<string>();
        foreach (var (k, v) in _options)
        {
            if (_used.Contains(k)) continue;
            list.Add("--" + k);
            if (v is not null) list.Add(v);
        }
        return list;
    }

    /// <summary>Fails on misspelled options instead of silently ignoring them.</summary>
    public void EnsureAllUsed()
    {
        var unknown = _options.Select(o => o.Key).Where(k => !_used.Contains(k)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new UsageException($"unknown option(s) for '{Command}': {string.Join(", ", unknown.Select(k => "--" + k))}");
    }

    public static int PositiveOrThrow(int value, string name) =>
        value > 0 ? value : throw new UsageException($"--{name} must be > 0");
}
