using System.Diagnostics;
using System.Globalization;

namespace Lab1;

/// <summary>
/// bench: runs one task over a list of thread counts, each run in a fresh child process (so the peak
/// memory belongs to that configuration only), and writes every "@result" line to a CSV file.
/// </summary>
public static class Bench
{
    public static readonly string[] Columns =
        ["task", "mode", "threads", "run", "time_ms", "peak_mb", "negative", "positive", "zero", "total", "correct", "extra"];

    public static int Run(Args a)
    {
        string task = a.Required("task").ToLowerInvariant();
        if (task is not ("1a" or "1b" or "1c" or "2")) throw new UsageException("--task must be 1a, 1b, 1c or 2");

        int[] defaultThreads = task switch
        {
            "2" => [1, 2, 4],
            "1b" => [Environment.ProcessorCount],
            _ => [1, 2, 4, 8, 16, 32, 64],
        };
        int[] threadList = a.IntList("threads", defaultThreads);
        int runs = Args.PositiveOrThrow(a.Int("runs", task == "2" ? 1 : 3), "runs");
        int warmup = a.Int("warmup", task == "2" ? 0 : 1);
        string csv = a.Str("csv", $"results/task{task}.csv");
        bool append = a.Has("append");
        bool pause = a.Has("pause");
        bool quiet = a.Has("quiet");
        List<string> forward = a.UnusedAsArgv(); // everything else goes to the task itself

        int dirIndex = forward.FindIndex(x => x.Equals("--dir", StringComparison.OrdinalIgnoreCase));
        string? watchedDir = dirIndex >= 0 && dirIndex + 1 < forward.Count ? forward[dirIndex + 1] : null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csv))!);
        bool writeHeader = !append || !File.Exists(csv) || new FileInfo(csv).Length == 0;
        using var writer = new StreamWriter(csv, append) { AutoFlush = true };
        if (writeHeader) writer.WriteLine(string.Join(',', Columns));

        Console.WriteLine($"bench task {task}: threads {string.Join(',', threadList)}, {runs} run(s) each, " +
                          $"{warmup} warm-up run(s), extra options: {(forward.Count > 0 ? string.Join(' ', forward) : "none")}");

        for (int w = 0; w < warmup; w++)
        {
            Console.WriteLine($"warm-up {w + 1}/{warmup} (not recorded) ...");
            RunChild(task, threadList.Max(), forward, echo: false);
        }

        // Runs are the outer loop, so a slow drift (thermal throttling, background load) is spread over
        // all thread counts instead of hitting only the last ones.
        for (int run = 1; run <= runs; run++)
        {
            foreach (int threads in threadList)
            {
                if (pause) WaitForUser(watchedDir, task, threads, run);
                Console.WriteLine($"--- run {run}/{runs}, threads={threads}");
                foreach (var r in RunChild(task, threads, forward, echo: !quiet))
                {
                    r["run"] = run.ToString(CultureInfo.InvariantCulture);
                    writer.WriteLine(string.Join(',', Columns.Select(c => r.GetValueOrDefault(c, ""))));
                    Console.WriteLine($"    => {r["mode"]} {double.Parse(r["time_ms"], CultureInfo.InvariantCulture),10:F1} ms  " +
                                      $"{r["peak_mb"],8} MiB  correct={r["correct"]}");
                }
            }
        }
        Console.WriteLine($"results written to {csv}");
        return 0;
    }

    private static void WaitForUser(string? dir, string task, int threads, int run)
    {
        Console.WriteLine();
        Console.WriteLine($">>> Next: task {task}, threads={threads}, run {run}.");
        if (dir is null)
        {
            Console.WriteLine(">>> Eject the USB drive, unplug it, plug it back in, then press Enter.");
            Console.ReadLine();
            return;
        }

        // The folder lives on the drive, so it disappears when the drive is ejected and comes back
        // when it is mounted again – no key press needed.
        Console.WriteLine($">>> EJECT the USB drive first (do not just pull it out), then unplug it.");
        if (EjectCommand(dir) is { } eject) Console.WriteLine($">>>   e.g. in another terminal: {eject}");
        Console.WriteLine($">>> (waiting for {dir} to disappear)");
        while (Directory.Exists(dir)) Thread.Sleep(250);
        Console.WriteLine(">>> Drive removed. Plug it back in ...");
        while (!Directory.Exists(dir)) Thread.Sleep(250);
        Console.WriteLine(">>> Drive is back. Starting in 3 s.");
        Thread.Sleep(3000); // let the desktop finish mounting / indexing the drive
    }

    /// <summary>Linux: the udisksctl command that safely ejects the drive holding <paramref name="dir"/>.</summary>
    private static string? EjectCommand(string dir)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(dir)) return null;
        string full = Path.GetFullPath(dir);
        // The mount whose mount point is the longest prefix of the folder.
        var mount = File.ReadAllLines("/proc/self/mounts")
            .Select(l => l.Split(' '))
            .Where(f => f.Length > 2 && f[0].StartsWith("/dev/", StringComparison.Ordinal))
            .Select(f => (Device: f[0], Point: f[1].Replace("\\040", " ")))
            .Where(m => full == m.Point || full.StartsWith(m.Point.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(m => m.Point.Length)
            .FirstOrDefault();
        if (mount.Device is null) return null;
        // Partition (e.g. /dev/sda1) -> whole disk (/dev/sda) via sysfs.
        string part = Path.GetFileName(mount.Device);
        string sys = Path.Combine("/sys/class/block", part);
        string disk = File.Exists(Path.Combine(sys, "partition"))
            ? "/dev/" + Path.GetFileName(Path.GetDirectoryName(new DirectoryInfo(sys).ResolveLinkTarget(true)?.FullName ?? sys))
            : mount.Device;
        return $"udisksctl unmount -b {mount.Device} && udisksctl power-off -b {disk}";
    }

    private static List<Dictionary<string, string>> RunChild(string task, int threads, List<string> forward, bool echo)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("cannot find own executable"),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // "dotnet Lab1.dll ..." -> the child must also be started through dotnet with the dll path.
        if (Path.GetFileNameWithoutExtension(psi.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(Bench).Assembly.Location);
        psi.ArgumentList.Add(task);
        psi.ArgumentList.Add("--threads");
        psi.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
        foreach (var x in forward) psi.ArgumentList.Add(x);

        var results = new List<Dictionary<string, string>>();
        using var p = Process.Start(psi)!;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            if (line.StartsWith(Report.Marker, StringComparison.Ordinal))
                results.Add(ParseResult(line));
            else if (echo)
                Console.WriteLine("    " + line);
        }
        p.WaitForExit();
        if (p.ExitCode != 0) throw new LabError($"the run failed (exit code {p.ExitCode}) – see the error above; no row was written for it");
        return results;
    }

    private static Dictionary<string, string> ParseResult(string line) =>
        line[Report.Marker.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : "");
}
