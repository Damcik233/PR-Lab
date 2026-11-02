using System.Diagnostics;

namespace Lab1;

/// <summary>
/// Task 2 – many files on slow storage. A file is the unit of work: a thread reads the whole file with a
/// single File.ReadAllBytes call into one byte[], then parses and counts it. The files are divided among
/// the threads in equal contiguous groups.
/// </summary>
public static class Task2
{
    public static int Run(Args a)
    {
        string dir = a.Required("dir");
        int threads = Args.PositiveOrThrow(a.Int("threads", 1), "threads");
        string pattern = a.Str("pattern", "part_*.txt");
        string expectPath = a.Str("expect", Path.Combine(dir, "expected.counts"));
        a.EnsureAllUsed();

        if (!Directory.Exists(dir)) throw new UsageException($"directory '{dir}' not found (is the drive mounted?)");
        string[] files = Directory.GetFiles(dir, pattern).OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new UsageException($"no files matching '{pattern}' in '{dir}'");
        var expected = Expected.Load(expectPath);

        var results = new Counts[threads];
        var log = new List<string>[threads];
        var readTicks = new long[threads];
        var parseTicks = new long[threads];
        long bytesTotal = 0;

        var sw = Stopwatch.StartNew();
        Threads.RunAll(threads, "2-worker", id =>
        {
            var (from, to) = Threads.Part(files.Length, id, threads);
            var sink = new CountingSink();
            log[id] = [];
            for (long f = from; f < to; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                byte[] content = File.ReadAllBytes(files[f]);          // one read into one buffer
                long t1 = Stopwatch.GetTimestamp();
                try
                {
                    Numbers.ParseLines(content, isLast: true, ref sink);   // parse + count from memory
                }
                catch (InvalidDataException e)
                {
                    throw new LabError($"{files[f]} is corrupt: {e.Message}", e);
                }
                long t2 = Stopwatch.GetTimestamp();

                readTicks[id] += t1 - t0;
                parseTicks[id] += t2 - t1;
                Interlocked.Add(ref bytesTotal, content.Length);
                double readS = (t1 - t0) / (double)Stopwatch.Frequency;
                log[id].Add($"  thread {id}: {Path.GetFileName(files[f]),-12} {content.Length / 1e6,6:F1} MB  " +
                            $"read {readS * 1000,8:F0} ms ({content.Length / 1e6 / readS,7:F1} MB/s)  " +
                            $"parse {(t2 - t1) * 1000.0 / Stopwatch.Frequency,6:F0} ms");
            }
            results[id] = sink.Result;
        });
        sw.Stop();

        foreach (var l in log.SelectMany(x => x)) Console.WriteLine(l);
        double readMs = readTicks.Sum() * 1000.0 / Stopwatch.Frequency;
        double parseMs = parseTicks.Sum() * 1000.0 / Stopwatch.Frequency;
        double throughput = bytesTotal / 1e6 / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"    {files.Length} files, {bytesTotal / 1e6:F0} MB; thread-time spent reading {readMs:F0} ms, " +
                          $"parsing {parseMs:F0} ms; overall {throughput:F1} MB/s");

        var total = results.Aggregate(new Counts(), (x, y) => x + y);
        Report.Result("2", "-", threads, sw.Elapsed.TotalMilliseconds, total, expected,
        [
            ("files", files.Length.ToString()),
            ("read_ms_sum", readMs.ToString("F1")),
            ("parse_ms_sum", parseMs.ToString("F1")),
            ("mb_per_s", throughput.ToString("F1")),
        ]);
        return 0;
    }
}
