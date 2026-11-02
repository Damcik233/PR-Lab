using System.Diagnostics;

namespace Lab1;

/// <summary>
/// Task 1a – the file is cut into as many byte ranges as there are threads; every thread opens the file,
/// reads and parses only its own range, and counts into private counters. The totals are added at the end.
/// </summary>
public static class Task1a
{
    public static int Run(Args a)
    {
        string file = a.Str("file", "data/numbers.txt");
        int threads = Args.PositiveOrThrow(a.Int("threads", Environment.ProcessorCount), "threads");
        int bufferBytes = Args.PositiveOrThrow(a.Int("buffer-kb", 256), "buffer-kb") * 1024;
        bool verbose = a.Has("verbose");
        a.EnsureAllUsed();

        var expected = Expected.Load(Expected.PathFor(file));
        long length = new FileInfo(file).Length;
        var results = new Counts[threads];
        var ranges = new (long Start, long End)[threads];

        var sw = Stopwatch.StartNew();
        Threads.RunAll(threads, "1a-worker", id =>
        {
            using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);

            // Raw equal byte range, then both ends moved forward to the next line start.
            var (rawStart, rawEnd) = Threads.Part(length, id, threads);
            long start = Numbers.AlignToLineStart(handle, rawStart, length);
            long end = Numbers.AlignToLineStart(handle, rawEnd, length);

            var sink = new CountingSink();
            Numbers.ScanRange(handle, start, end, new byte[bufferBytes], ref sink);
            results[id] = sink.Result;
            ranges[id] = (start, end);
        });
        sw.Stop();

        var total = results.Aggregate(new Counts(), (x, y) => x + y);

        if (verbose)
        {
            for (int t = 0; t < threads; t++)
                Console.WriteLine($"  thread {t,2}: bytes [{ranges[t].Start,11:N0}, {ranges[t].End,11:N0})  lines {results[t].Total,11:N0}");
            // The ranges must tile the file exactly: no gap (skipped line), no overlap (double count).
            bool tiled = ranges[0].Start == 0 && ranges[^1].End == length &&
                         Enumerable.Range(1, threads - 1).All(t => ranges[t].Start == ranges[t - 1].End);
            Console.WriteLine($"  ranges tile the file without gaps or overlaps: {tiled}");
        }

        Report.Result("1a", "-", threads, sw.Elapsed.TotalMilliseconds, total, expected,
                      [("buffer_kb", (bufferBytes / 1024).ToString())]);
        return 0;
    }
}
