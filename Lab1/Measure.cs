using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab1;

public static class Memory
{
    /// <summary>
    /// Peak resident set size of this process in MiB. On Linux this is VmHWM ("high water mark")
    /// from /proc/self/status – the same value Process.PeakWorkingSet64 reports.
    /// </summary>
    public static double PeakMiB()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;
                var kb = line["VmHWM:".Length..].Trim().Split(' ')[0];
                return long.Parse(kb, CultureInfo.InvariantCulture) / 1024.0;
            }
        }
        using var p = Process.GetCurrentProcess();
        return p.PeakWorkingSet64 / (1024.0 * 1024.0);
    }
}

/// <summary>The true totals, written by "gen" next to the data so every task can check itself.</summary>
public static class Expected
{
    public static string PathFor(string dataFile) => dataFile + ".counts";

    public static void Save(string path, Counts c) =>
        File.WriteAllText(path, $"negative={c.Negative}\npositive={c.Positive}\nzero={c.Zero}\ntotal={c.Total}\n");

    public static Counts? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var d = File.ReadAllLines(path)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => long.Parse(p[1].Trim(), CultureInfo.InvariantCulture));
            return new Counts(d["negative"], d["positive"], d["zero"]);
        }
        catch (Exception e) when (e is FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            throw new LabError($"{path} is corrupt – it is not the 'negative=...' text written by gen/split. " +
                               "Copy it again, or pass --expect with a good copy.");
        }
    }
}

/// <summary>Prints a human-readable summary and one machine-readable "@result" line for bench.</summary>
public static class Report
{
    public const string Marker = "@result ";

    public static void Result(string task, string mode, int threads, double milliseconds, Counts counts,
                              Counts? expected, IEnumerable<(string Key, string Value)>? extra = null)
    {
        double peak = Memory.PeakMiB();
        string verdict = expected switch
        {
            null => "UNCHECKED (no .counts file)",
            var e when e == counts => "OK – matches the expected totals",
            var e => $"WRONG – expected {e.Value}, difference in total {counts.Total - e.Value.Total:N0}",
        };

        string label = mode == "-" ? $"Task {task}" : $"Task {task} [{mode}]";
        double allocated = GC.GetTotalAllocatedBytes(precise: true) / (1024.0 * 1024.0);
        Console.WriteLine($"{label} | threads={threads} | time={milliseconds / 1000.0:F3} s | peak memory={peak:F1} MiB" +
                          $" | allocated by the process so far={allocated:N0} MiB");
        Console.WriteLine($"    {counts}");
        Console.WriteLine($"    {verdict}");

        var sb = new StringBuilder(Marker);
        void Kv(string k, object v) => sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append(' ');
        Kv("task", task);
        Kv("mode", mode);
        Kv("threads", threads);
        Kv("time_ms", Math.Round(milliseconds, 3));
        Kv("peak_mb", Math.Round(peak, 2));
        Kv("negative", counts.Negative);
        Kv("positive", counts.Positive);
        Kv("zero", counts.Zero);
        Kv("total", counts.Total);
        Kv("correct", expected is null ? "unknown" : (expected == counts ? "true" : "false"));
        if (extra is not null)
            Kv("extra", string.Join(';', extra.Select(e => $"{e.Key}:{e.Value}")));
        Console.WriteLine(sb.ToString().TrimEnd());
    }

    public static string Ms(long stopwatchTicks) =>
        (stopwatchTicks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>Starts <c>count</c> OS threads running <c>body(id)</c> and waits for all of them.</summary>
public static class Threads
{
    public static void RunAll(int count, string name, Action<int> body)
    {
        var threads = new Thread[count];
        Exception? error = null;
        for (int t = 0; t < count; t++)
        {
            int id = t;
            threads[t] = new Thread(() =>
            {
                // An exception in a thread would kill the process; keep the first one and
                // re-throw it on the calling thread after all threads have finished.
                try { body(id); }
                catch (Exception e) { Interlocked.CompareExchange(ref error, e, null); }
            }) { Name = $"{name}-{id}", IsBackground = true };
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>[from, to) of part <c>id</c> when <c>length</c> items are split into <c>parts</c> equal parts.</summary>
    public static (long From, long To) Part(long length, int id, int parts) =>
        (length * id / parts, length * (id + 1) / parts);
}
