using Lab1;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine(Usage.Text);
        return 0;
    }

    var a = new Args(args);
    return a.Command switch
    {
        "info" => Info.Run(a),
        "gen" => DataTools.Generate(a),
        "split" => DataTools.Split(a),
        "1a" => Task1a.Run(a),
        "1b" => Task1b.Run(a),
        "1c" => Task1c.Run(a),
        "2" => Task2.Run(a),
        "bench" => Bench.Run(a),
        "chart" => Charts.Run(a),
        _ => throw new UsageException($"unknown command '{a.Command}'"),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine("run 'Lab1 help' for usage");
    return 2;
}
catch (Exception e) when (e is LabError or InvalidDataException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static class Usage
{
    public const string Text = """
        Lab 1 - Concurrency in practice

        usage: Lab1 <command> [--option value ...]

        Data
          gen    --out data/numbers.txt [--count 50000000] [--min -1000000] [--max 1000000] [--seed 2026]
                 Writes the numbers, one per line, and <out>.counts with the true totals.
          split  --in data/numbers.txt --out data/parts [--parts 8]
                 Splits the file in order into part_1.txt .. part_N.txt (+ expected.counts).
          info   Prints CPU / memory / OS / runtime information for the report.

        Tasks (one configuration per run; peak memory is the peak RSS of this process)
          1a     [--file data/numbers.txt] --threads N [--buffer-kb 256] [--verbose]
                 Each thread reads and parses only its own byte range of the file.
          1b     [--file data/numbers.txt] [--threads <logical CPUs>] [--mode all|unsync|lock|atomic|local]
                 [--repeat 3]   (how many times the unsync version is run)
                 Loads the numbers into int[] (not measured), then counts them in the chosen way(s).
          1c     [--file data/numbers.txt] --threads <workers> [--batch 10000] [--capacity 100]
                 One reader thread + N worker threads around a bounded blocking queue.
                 --capacity 0 means an unbounded queue.
          2      --dir <folder on the USB drive> --threads N [--pattern part_*.txt]
                 Each file is read with one File.ReadAllBytes call, then parsed and counted.

        Measuring
          bench  --task 1a|1b|1c|2 [--threads 1,2,4,8,16,32,64] [--runs 3] [--warmup 1]
                 [--csv results/<task>.csv] [--append] [--pause] [any task option ...]
                 Runs every configuration in a fresh child process (so peak memory is per
                 configuration) and writes one CSV row per result.
                 --pause waits for Enter before every run (Task 2: replug the USB drive).
          chart  --csv [label=]a.csv[,[label=]b.csv] --out results/charts/name [--title "..."] [--log-time]
                 [--xlabel "..."]
                 [--markers "10:physical cores,16:logical CPUs"]
                 Writes <out>-time.svg, <out>-speedup.svg and <out>-memory.svg (median of runs).
        """;
}
