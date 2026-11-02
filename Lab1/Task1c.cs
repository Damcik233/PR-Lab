using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Lab1;

/// <summary>
/// Task 1c – producer/consumers. One reader thread reads the file line by line and puts batches of lines
/// into a bounded blocking queue; N worker threads take batches, parse and count them.
/// </summary>
public static class Task1c
{
    public static int Run(Args a)
    {
        string file = a.Str("file", "data/numbers.txt");
        int workers = Args.PositiveOrThrow(a.Int("threads", a.Int("workers", 4)), "threads");
        int batchSize = Args.PositiveOrThrow(a.Int("batch", 10_000), "batch");
        int capacity = a.Int("capacity", 100); // 0 = unbounded
        a.EnsureAllUsed();
        if (capacity < 0) throw new UsageException("--capacity must be >= 0");

        var expected = Expected.Load(Expected.PathFor(file));

        // BlockingCollection over a ConcurrentQueue (FIFO). With a capacity, Add blocks while it is full;
        // Take blocks while it is empty; CompleteAdding "closes" it.
        using var queue = capacity > 0
            ? new BlockingCollection<string[]>(new ConcurrentQueue<string[]>(), capacity)
            : new BlockingCollection<string[]>(new ConcurrentQueue<string[]>());

        var results = new Counts[workers];
        var workerWaitTicks = new long[workers];
        var workerBatches = new long[workers];
        long readerWaitTicks = 0, readerFinishedAt = 0, fullCount = 0, batchCount = 0, maxQueued = 0;
        Exception? readerError = null;

        long startedAt = Stopwatch.GetTimestamp();

        var reader = new Thread(() =>
        {
            void Put(string[] batch)
            {
                batchCount++;
                if (!queue.TryAdd(batch))            // fast path: there was room
                {
                    fullCount++;                     // queue full -> the reader has to wait
                    long t0 = Stopwatch.GetTimestamp();
                    queue.Add(batch);
                    readerWaitTicks += Stopwatch.GetTimestamp() - t0;
                }
                maxQueued = Math.Max(maxQueued, queue.Count); // longest the queue ever got
            }

            try
            {
                using var sr = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    new FileStreamOptions { BufferSize = 1 << 16, Options = FileOptions.SequentialScan });
                var batch = new string[batchSize];
                int n = 0;
                string? line;
                while ((line = sr.ReadLine()) is not null)
                {
                    batch[n++] = line;
                    if (n == batchSize)
                    {
                        Put(batch);
                        batch = new string[batchSize]; // the old array now belongs to a worker
                        n = 0;
                    }
                }
                if (n > 0)
                {
                    Array.Resize(ref batch, n);
                    Put(batch);
                }
            }
            catch (Exception e)
            {
                readerError = e;
            }
            finally
            {
                queue.CompleteAdding(); // end of file: close the queue so the workers can stop
                readerFinishedAt = Stopwatch.GetTimestamp();
            }
        }) { Name = "1c-reader", IsBackground = true };

        reader.Start();
        Threads.RunAll(workers, "1c-worker", id =>
        {
            var sink = new CountingSink();
            long waited = 0, taken = 0;
            while (true)
            {
                if (!queue.TryTake(out var batch))
                {
                    // Queue empty: wait until the reader adds a batch, or closes the queue.
                    long t0 = Stopwatch.GetTimestamp();
                    bool got = queue.TryTake(out batch, Timeout.Infinite); // false = closed and empty
                    waited += Stopwatch.GetTimestamp() - t0;
                    if (!got) break;
                }
                foreach (var line in batch!)
                    sink.Add(Numbers.Parse(line.AsSpan()));
                taken++;
            }
            results[id] = sink.Result;
            workerWaitTicks[id] = waited;
            workerBatches[id] = taken;
        });
        reader.Join();
        double totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (readerError is not null) throw new InvalidOperationException("reader failed", readerError);

        var total = results.Aggregate(new Counts(), (x, y) => x + y);
        double readerMs = (readerFinishedAt - startedAt) * 1000.0 / Stopwatch.Frequency;
        double avgWorkerWaitMs = workerWaitTicks.Average() * 1000.0 / Stopwatch.Frequency;

        Console.WriteLine($"    reader: {batchCount:N0} batches, finished after {readerMs:F0} ms, " +
                          $"waited on a full queue {fullCount:N0} times for {Report.Ms(readerWaitTicks)} ms; " +
                          $"longest queue {maxQueued:N0} batches");
        Console.WriteLine($"    workers: waited on an empty queue {avgWorkerWaitMs:F0} ms on average; " +
                          $"batches per worker min/max {workerBatches.Min():N0}/{workerBatches.Max():N0}");

        Report.Result("1c", "-", workers, totalMs, total, expected,
        [
            ("batch", batchSize.ToString()),
            ("capacity", capacity.ToString()),
            ("reader_ms", readerMs.ToString("F1")),
            ("reader_wait_ms", Report.Ms(readerWaitTicks)),
            ("queue_full_events", fullCount.ToString()),
            ("max_queue", maxQueued.ToString()),
            ("avg_worker_wait_ms", avgWorkerWaitMs.ToString("F1")),
        ]);
        return 0;
    }
}
