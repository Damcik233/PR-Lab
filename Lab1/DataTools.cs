using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Lab1;

public static class DataTools
{
    /// <summary>gen: writes N random integers in [min, max], one per line, plus the true totals.</summary>
    public static int Generate(Args a)
    {
        string outPath = a.Str("out", "data/numbers.txt");
        long count = a.Int("count", 50_000_000);
        int min = a.Int("min", -1_000_000);
        int max = a.Int("max", 1_000_000);
        int seed = a.Int("seed", 2026);
        a.EnsureAllUsed();
        if (min > max) throw new UsageException("--min must be <= --max");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var sw = Stopwatch.StartNew();
        var rng = new Random(seed);
        var sink = new CountingSink();

        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 0))
        {
            var buffer = new byte[1 << 20];
            int pos = 0;
            for (long i = 0; i < count; i++)
            {
                int v = rng.Next(min, max + 1); // upper bound is exclusive
                sink.Add(v);
                if (pos > buffer.Length - 16)
                {
                    fs.Write(buffer, 0, pos);
                    pos = 0;
                }
                v.TryFormat(buffer.AsSpan(pos), out int written, default, CultureInfo.InvariantCulture);
                pos += written;
                buffer[pos++] = Numbers.NewLine;
            }
            fs.Write(buffer, 0, pos);
        }

        Expected.Save(Expected.PathFor(outPath), sink.Result);
        Console.WriteLine($"wrote {count:N0} numbers in [{min:N0}, {max:N0}] to {outPath} " +
                          $"({new FileInfo(outPath).Length / 1e6:F1} MB) in {sw.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"true totals: {sink.Result}  -> {Expected.PathFor(outPath)}");
        return 0;
    }

    /// <summary>split: cuts the file, in order, into N files with the same number of lines each.</summary>
    public static int Split(Args a)
    {
        string inPath = a.Str("in", "data/numbers.txt");
        string outDir = a.Str("out", "data/parts");
        int parts = Args.PositiveOrThrow(a.Int("parts", 8), "parts");
        a.EnsureAllUsed();

        var expected = Expected.Load(Expected.PathFor(inPath))
            ?? throw new UsageException($"{Expected.PathFor(inPath)} not found – run 'gen' first");
        long total = expected.Total;
        long LinesOf(int part) => total / parts + (part < total % parts ? 1 : 0);

        Directory.CreateDirectory(outDir);
        string NameOf(int part) => Path.Combine(outDir, $"part_{part + 1}.txt");

        var sw = Stopwatch.StartNew();
        using var input = new FileStream(inPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        int current = 0;
        long linesLeft = LinesOf(0);
        var output = new FileStream(NameOf(0), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        try
        {
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                int pos = 0;
                while (pos < n)
                {
                    int end = pos;
                    bool lastPart = current == parts - 1;
                    while (end < n && (lastPart || linesLeft > 0))
                    {
                        int nl = buffer.AsSpan(end, n - end).IndexOf(Numbers.NewLine);
                        if (nl < 0) { end = n; break; }
                        end += nl + 1;
                        linesLeft--;
                    }
                    output.Write(buffer, pos, end - pos);
                    pos = end;

                    if (!lastPart && linesLeft == 0)
                    {
                        output.Dispose();
                        current++;
                        linesLeft = LinesOf(current);
                        output = new FileStream(NameOf(current), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    }
                }
            }
        }
        finally
        {
            output.Dispose();
        }

        Expected.Save(Path.Combine(outDir, "expected.counts"), expected);
        for (int p = 0; p < parts; p++)
            Console.WriteLine($"{NameOf(p)}  {LinesOf(p):N0} lines  {new FileInfo(NameOf(p)).Length / 1e6:F1} MB");
        Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1} s; totals copied to {Path.Combine(outDir, "expected.counts")}");
        return 0;
    }
}

/// <summary>info: hardware and runtime facts for the report.</summary>
public static class Info
{
    public static int Run(Args a)
    {
        a.EnsureAllUsed();
        Console.WriteLine($"OS:               {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"Runtime:          {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Logical CPUs:     {Environment.ProcessorCount}");
        Console.WriteLine($"GC:               {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");

        if (OperatingSystem.IsLinux())
        {
            var cpuinfo = File.ReadAllLines("/proc/cpuinfo");
            string model = cpuinfo.FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "?";
            // physical core = unique (physical id, core id) pair
            var cores = new HashSet<string>();
            string phys = "0";
            foreach (var l in cpuinfo)
            {
                if (l.StartsWith("physical id")) phys = l.Split(':', 2)[1].Trim();
                if (l.StartsWith("core id")) cores.Add(phys + "/" + l.Split(':', 2)[1].Trim());
            }
            Console.WriteLine($"CPU model:        {model}");
            Console.WriteLine($"Physical cores:   {cores.Count}");

            // Hybrid CPUs: cores that expose 2 logical CPUs are P-cores (Hyper-Threading), the rest E-cores.
            var siblings = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
                .Select(d => Path.Combine(d, "topology", "thread_siblings_list"))
                .Where(File.Exists).Select(f => File.ReadAllText(f).Trim()).Distinct().ToList();
            int smt = siblings.Count(s => s.Contains(',') || s.Contains('-'));
            Console.WriteLine($"  with 2 threads: {smt}   with 1 thread: {siblings.Count - smt}");

            string mem = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal")).Split(':')[1].Trim();
            Console.WriteLine($"RAM:              {long.Parse(mem.Split(' ')[0]) / 1024.0 / 1024.0:F1} GiB");

            foreach (var dev in Directory.GetDirectories("/sys/block"))
            {
                string name = Path.GetFileName(dev);
                if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("zram")) continue;
                string modelFile = Path.Combine(dev, "device", "model");
                string devModel = File.Exists(modelFile) ? File.ReadAllText(modelFile).Trim() : "?";
                string rot = File.ReadAllText(Path.Combine(dev, "queue", "rotational")).Trim() == "1" ? "HDD" : "SSD/flash";
                string removable = File.ReadAllText(Path.Combine(dev, "removable")).Trim() == "1" ? ", removable" : "";
                Console.WriteLine($"Disk {name,-12} {devModel} ({rot}{removable})");
            }
        }
        return 0;
    }
}
