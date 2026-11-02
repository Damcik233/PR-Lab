using System.Diagnostics;

namespace Lab1;

/// <summary>
/// Task 1b – the numbers are already in memory (int[]); the same work is split among the threads and
/// counted in different ways, to see the cost and the correctness of each kind of synchronisation.
/// </summary>
public static class Task1b
{
    /// <summary>Counters shared by all threads (one object on the heap, all three fields in one cache line).</summary>
    private sealed class SharedCounters
    {
        public long Negative, Positive, Zero;
        public Counts ToCounts() => new(Negative, Positive, Zero);
    }

    private static readonly string[] AllModes = ["unsync", "lock", "atomic", "local"];

    public static int Run(Args a)
    {
        string file = a.Str("file", "data/numbers.txt");
        int threads = Args.PositiveOrThrow(a.Int("threads", Environment.ProcessorCount), "threads");
        string modeArg = a.Str("mode", "all").ToLowerInvariant();
        int repeat = Args.PositiveOrThrow(a.Int("repeat", 3), "repeat");
        a.EnsureAllUsed();

        string[] modes = modeArg == "all" ? AllModes
            : modeArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var m in modes)
            if (!AllModes.Contains(m)) throw new UsageException($"--mode: unknown mode '{m}' (all|{string.Join('|', AllModes)})");

        var expected = Expected.Load(Expected.PathFor(file));

        // Loading is NOT part of the measurement.
        var load = Stopwatch.StartNew();
        int[] data = Numbers.LoadAll(file);
        Console.WriteLine($"loaded {data.Length:N0} numbers into int[] in {load.Elapsed.TotalSeconds:F2} s (not measured); " +
                          $"{threads} threads, {data.Length / threads:N0} numbers each");

        foreach (var mode in modes)
        {
            int times = mode == "unsync" ? repeat : 1;
            for (int run = 1; run <= times; run++)
            {
                var sw = Stopwatch.StartNew();
                Counts c = mode switch
                {
                    "unsync" => CountUnsynchronised(data, threads),
                    "lock" => CountWithLock(data, threads),
                    "atomic" => CountWithInterlocked(data, threads),
                    "local" => CountPerThread(data, threads),
                    _ => throw new UnreachableException(),
                };
                sw.Stop();

                var extra = new List<(string, string)> { ("run", run.ToString()) };
                if (expected is { } e) extra.Add(("lost_updates", (e.Total - c.Total).ToString()));
                Report.Result("1b", mode, threads, sw.Elapsed.TotalMilliseconds, c, expected, extra);
            }
        }
        return 0;
    }

    /// <summary>Version 1: every thread does a read-modify-write on the shared fields with no synchronisation.</summary>
    private static Counts CountUnsynchronised(int[] data, int threads)
    {
        var shared = new SharedCounters();
        Threads.RunAll(threads, "1b-unsync", id =>
        {
            var (from, to) = Threads.Part(data.Length, id, threads);
            for (long i = from; i < to; i++)
            {
                int v = data[i];
                if (v < 0) shared.Negative++;      // load, add 1, store – three steps, not atomic
                else if (v > 0) shared.Positive++;
                else shared.Zero++;
            }
        });
        return shared.ToCounts();
    }

    /// <summary>Version 2: the same shared fields, but every update happens while holding one lock.</summary>
    private static Counts CountWithLock(int[] data, int threads)
    {
        var shared = new SharedCounters();
        var gate = new Lock();
        Threads.RunAll(threads, "1b-lock", id =>
        {
            var (from, to) = Threads.Part(data.Length, id, threads);
            for (long i = from; i < to; i++)
            {
                int v = data[i];
                lock (gate)
                {
                    if (v < 0) shared.Negative++;
                    else if (v > 0) shared.Positive++;
                    else shared.Zero++;
                }
            }
        });
        return shared.ToCounts();
    }

    /// <summary>Extra (not required): shared fields updated with atomic CPU instructions (lock xadd), no lock.</summary>
    private static Counts CountWithInterlocked(int[] data, int threads)
    {
        var shared = new SharedCounters();
        Threads.RunAll(threads, "1b-atomic", id =>
        {
            var (from, to) = Threads.Part(data.Length, id, threads);
            for (long i = from; i < to; i++)
            {
                int v = data[i];
                if (v < 0) Interlocked.Increment(ref shared.Negative);
                else if (v > 0) Interlocked.Increment(ref shared.Positive);
                else Interlocked.Increment(ref shared.Zero);
            }
        });
        return shared.ToCounts();
    }

    /// <summary>Version 3: each thread counts in its own local variables; the main thread adds them up after Join.</summary>
    private static Counts CountPerThread(int[] data, int threads)
    {
        var results = new Counts[threads];
        Threads.RunAll(threads, "1b-local", id =>
        {
            var (from, to) = Threads.Part(data.Length, id, threads);
            long negative = 0, positive = 0, zero = 0;
            for (long i = from; i < to; i++)
            {
                int v = data[i];
                if (v < 0) negative++;
                else if (v > 0) positive++;
                else zero++;
            }
            results[id] = new Counts(negative, positive, zero); // one write per thread, at the very end
        });
        return results.Aggregate(new Counts(), (x, y) => x + y);
    }
}
