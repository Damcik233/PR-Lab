using System.Globalization;
using System.Text;

namespace Lab1;

/// <summary>
/// chart: reads bench CSV files and writes three SVG line charts against the number of threads –
/// execution time, speed-up relative to one thread, and peak memory – using the median of the runs.
/// Also prints (and saves as &lt;out&gt;-table.md) the numbers behind the charts.
/// </summary>
public static class Charts
{
    // Validated categorical palette (fixed order) + recessive ink for text and grid.
    private static readonly string[] SeriesColors = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"];
    private const string Surface = "#ffffff", TextPrimary = "#0b0b0b", TextSecondary = "#52514e",
                         Grid = "#e6e5e1", Axis = "#a8a7a2", Reference = "#8a8984";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Row(string Series, int Threads, double TimeMs, double PeakMb, string Correct);

    private sealed record Point(int Threads, double TimeMs, double Speedup, double PeakMb, int Runs, bool AllCorrect, string Correctness);

    public static int Run(Args a)
    {
        string[] csvs = a.Required("csv").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string outPrefix = a.Required("out");
        string title = a.Str("title", Path.GetFileName(outPrefix));
        bool logTime = a.Has("log-time");
        var markers = ParseMarkers(a.Str("markers", ""));
        string xLabel = a.Str("xlabel", "number of threads");
        a.EnsureAllUsed();

        // An entry may be "label=path": the label then names the series (e.g. "USB=results/task2.csv").
        var rows = csvs.SelectMany(entry =>
        {
            int eq = entry.IndexOf('=');
            return eq > 0 ? ReadCsv(entry[(eq + 1)..].Trim(), entry[..eq].Trim()) : ReadCsv(entry, null);
        }).ToList();
        if (rows.Count == 0) throw new UsageException("no rows in the CSV file(s)");

        // Series in order of first appearance; colour follows the series, never its rank.
        var seriesNames = rows.Select(r => r.Series).Distinct().ToList();
        var series = seriesNames.Select(name =>
        {
            var groups = rows.Where(r => r.Series == name).GroupBy(r => r.Threads).OrderBy(g => g.Key).ToList();
            double baseTime = Median(groups[0].Select(r => r.TimeMs)); // 1 thread (or the smallest count present)
            var points = groups.Select(g =>
            {
                double t = Median(g.Select(r => r.TimeMs));
                var correct = g.Select(r => r.Correct).Distinct().ToList();
                return new Point(g.Key, t, baseTime / t, Median(g.Select(r => r.PeakMb)), g.Count(),
                                 correct.All(c => c == "true"), string.Join('/', correct));
            }).ToList();
            return (Name: name, Points: points);
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPrefix))!);

        string table = Table(series, groupsBase: series.ToDictionary(s => s.Name, s => s.Points[0].Threads));
        Console.WriteLine(table);
        File.WriteAllText(outPrefix + "-table.md", table);

        var xs = series.SelectMany(s => s.Points.Select(p => p.Threads)).Distinct().Order().ToArray();
        var plot = series.Select(s => (s.Name, s.Points)).ToList();

        Write(outPrefix + "-time.svg", Svg(title + " – execution time", logTime ? "seconds (log scale)" : "seconds",
            plot, xs, p => p.TimeMs / 1000.0, logTime, markers, xLabel, idealSpeedup: false));
        Write(outPrefix + "-speedup.svg", Svg(title + " – speed-up vs. 1 thread", "× faster than 1 thread",
            plot, xs, p => p.Speedup, false, markers, xLabel, idealSpeedup: true));
        Write(outPrefix + "-memory.svg", Svg(title + " – peak memory (RSS)", "MiB",
            plot, xs, p => p.PeakMb, false, markers, xLabel, idealSpeedup: false));
        return 0;
    }

    private static void Write(string path, string svg)
    {
        File.WriteAllText(path, svg);
        Console.WriteLine($"wrote {path}");
    }

    private static List<(double X, string Label)> ParseMarkers(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Select(m => m.Split(':', 2))
         .Select(p => (double.Parse(p[0], Inv), p.Length > 1 ? p[1] : p[0]))
         .ToList();

    private static IEnumerable<Row> ReadCsv(string path, string? label)
    {
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new UsageException($"{path}: no '{name}' column");
        int task = Col("task"), mode = Col("mode"), threads = Col("threads"), time = Col("time_ms"), peak = Col("peak_mb"), correct = Col("correct");
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var f = line.Split(',');
            string series = (label, f[mode]) switch
            {
                (null, "-") => "Task " + f[task],
                (null, var m) => $"{f[task]} {m}",
                (var l, "-") => l,
                (var l, var m) => $"{l} {m}",
            };
            yield return new Row(series, int.Parse(f[threads], Inv), double.Parse(f[time], Inv), double.Parse(f[peak], Inv), f[correct]);
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static string Table(List<(string Name, List<Point> Points)> series, Dictionary<string, int> groupsBase)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Series | Threads | Time, s (median) | Speed-up | Peak memory, MiB | Runs | Totals correct |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var (name, points) in series)
            foreach (var p in points)
                sb.AppendLine(string.Create(Inv,
                    $"| {name} | {p.Threads} | {p.TimeMs / 1000:F3} | {p.Speedup:F2}× | {p.PeakMb:F1} | {p.Runs} | {(p.AllCorrect ? "yes" : p.Correctness == "false" ? "**no**" : p.Correctness)} |"));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- SVG

    private static string Svg(string title, string yLabel, List<(string Name, List<Point> Points)> series, int[] xs,
                              Func<Point, double> y, bool logY, List<(double X, string Label)> markers, string xLabel, bool idealSpeedup)
    {
        const double W = 760, H = 440, L = 72, R = 150, T = 78, B = 56;
        double plotW = W - L - R, plotH = H - T - B;

        // X: log2 of the thread count, so 1, 2, 4 … 64 are evenly spaced.
        double xMin = Math.Log2(xs[0]), xMax = Math.Log2(xs[^1]);
        if (xMax - xMin < 1e-9) { xMin -= 1; xMax += 1; }
        double X(double threads) => L + (Math.Log2(threads) - xMin) / (xMax - xMin) * plotW;

        var values = series.SelectMany(s => s.Points.Select(y)).ToList();
        double[] ticks;
        Func<double, double> Y;
        if (logY)
        {
            double lo = Math.Pow(10, Math.Floor(Math.Log10(values.Min()))), hi = Math.Pow(10, Math.Ceiling(Math.Log10(values.Max())));
            if (hi <= lo) hi = lo * 10;
            ticks = Enumerable.Range(0, (int)Math.Round(Math.Log10(hi / lo)) + 1).Select(i => lo * Math.Pow(10, i)).ToArray();
            Y = v => T + plotH - (Math.Log10(v) - Math.Log10(lo)) / (Math.Log10(hi) - Math.Log10(lo)) * plotH;
        }
        else
        {
            double top = NiceMax(values.Max() * 1.08);
            double step = NiceStep(top / 5);
            top = Math.Ceiling(values.Max() * 1.08 / step) * step;
            ticks = Enumerable.Range(0, (int)Math.Round(top / step) + 1).Select(i => i * step).ToArray();
            double yTop = ticks[^1];
            Y = v => T + plotH - v / yTop * plotH;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(Inv, $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" font-family="system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif">"""));
        sb.AppendLine($"""<rect width="100%" height="100%" fill="{Surface}"/>""");
        sb.AppendLine(string.Create(Inv, $"""<text x="{L - 56}" y="26" font-size="17" font-weight="600" fill="{TextPrimary}">{Esc(title)}</text>"""));
        sb.AppendLine(string.Create(Inv, $"""<text x="{L - 56}" y="{T - 14}" font-size="12" fill="{TextSecondary}">{Esc(yLabel)}</text>"""));

        // Legend (always, when there are 2+ series) – identity never by colour alone: shape + label too.
        if (series.Count > 1)
        {
            double lx = L - 56;
            for (int i = 0; i < series.Count; i++)
            {
                sb.AppendLine(MarkerShape(i, lx + 6, 46, SeriesColors[i % SeriesColors.Length]));
                sb.AppendLine(string.Create(Inv, $"""<text x="{lx + 16}" y="50" font-size="12" fill="{TextSecondary}">{Esc(series[i].Name)}</text>"""));
                lx += 30 + series[i].Name.Length * 7;
            }
        }

        // Grid + y ticks
        foreach (var t in ticks)
        {
            double yy = Y(t);
            sb.AppendLine(string.Create(Inv, $"""<line x1="{L}" x2="{L + plotW}" y1="{yy:F1}" y2="{yy:F1}" stroke="{Grid}" stroke-width="1"/>"""));
            sb.AppendLine(string.Create(Inv, $"""<text x="{L - 8}" y="{yy + 4:F1}" font-size="11" fill="{TextSecondary}" text-anchor="end">{FormatTick(t)}</text>"""));
        }
        // X ticks
        foreach (var x in xs)
        {
            sb.AppendLine(string.Create(Inv, $"""<line x1="{X(x):F1}" x2="{X(x):F1}" y1="{T + plotH}" y2="{T + plotH + 5}" stroke="{Axis}"/>"""));
            sb.AppendLine(string.Create(Inv, $"""<text x="{X(x):F1}" y="{T + plotH + 19}" font-size="11" fill="{TextSecondary}" text-anchor="middle">{x}</text>"""));
        }
        sb.AppendLine(string.Create(Inv, $"""<line x1="{L}" x2="{L + plotW}" y1="{T + plotH}" y2="{T + plotH}" stroke="{Axis}" stroke-width="1"/>"""));
        sb.AppendLine(string.Create(Inv, $"""<text x="{L + plotW / 2}" y="{H - 12}" font-size="12" fill="{TextSecondary}" text-anchor="middle">{Esc(xLabel)}</text>"""));

        // Reference markers (e.g. physical cores / logical CPUs)
        foreach (var (mx, label) in markers.Where(m => m.X >= xs[0] && m.X <= xs[^1]))
        {
            double px = X(mx);
            sb.AppendLine(string.Create(Inv, $"""<line x1="{px:F1}" x2="{px:F1}" y1="{T - 2}" y2="{T + plotH}" stroke="{Reference}" stroke-width="1" stroke-dasharray="3 4"/>"""));
            sb.AppendLine(string.Create(Inv, $"""<text x="{px:F1}" y="{T - 6}" font-size="11" fill="{TextSecondary}" text-anchor="middle">{Esc(label)}</text>"""));
        }

        // Ideal linear speed-up, clipped to the plot
        if (idealSpeedup)
        {
            double yTop = ticks[^1];
            double xEnd = Math.Min(xs[^1], xs[0] * yTop / Math.Max(1e-9, series.Min(s => s.Points[0].Speedup)));
            sb.AppendLine(string.Create(Inv, $"""<line x1="{X(xs[0]):F1}" y1="{Y(1):F1}" x2="{X(xEnd):F1}" y2="{Y(xEnd / xs[0]):F1}" stroke="{Reference}" stroke-width="1.5" stroke-dasharray="6 5"/>"""));
            sb.AppendLine(string.Create(Inv, $"""<text x="{X(xEnd) - 4:F1}" y="{Y(xEnd / xs[0]) + 16:F1}" font-size="11" fill="{TextSecondary}" text-anchor="end">ideal (linear)</text>"""));
        }

        // Series: 2px line, then markers with a 2px surface ring, then a direct label at the end.
        var endLabels = new List<(double Y, string Text)>();
        for (int i = 0; i < series.Count; i++)
        {
            string color = SeriesColors[i % SeriesColors.Length];
            var pts = series[i].Points;
            string path = string.Join(' ', pts.Select((p, k) => string.Create(Inv, $"{(k == 0 ? 'M' : 'L')}{X(p.Threads):F1},{Y(y(p)):F1}")));
            if (pts.Count > 1)
                sb.AppendLine($"""<path d="{path}" fill="none" stroke="{color}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>""");
            foreach (var p in pts)
                sb.AppendLine(MarkerShape(i, X(p.Threads), Y(y(p)), color));
            var last = pts[^1];
            endLabels.Add((Y(y(last)), $"{series[i].Name}  {FormatValue(y(last))}"));
        }
        // Spread end labels so they do not overlap (min 15px apart).
        var order = endLabels.Select((e, i) => (e, i)).OrderBy(t => t.e.Y).ToList();
        double prev = double.NegativeInfinity;
        var placed = new double[endLabels.Count];
        foreach (var (e, i) in order) { placed[i] = Math.Max(e.Y, prev + 15); prev = placed[i]; }
        for (int i = 0; i < endLabels.Count; i++)
            sb.AppendLine(string.Create(Inv, $"""<text x="{L + plotW + 10}" y="{placed[i] + 4:F1}" font-size="11" fill="{TextSecondary}">{Esc(endLabels[i].Text)}</text>"""));

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>Different shape per series (circle, square, triangle, diamond…), 8px+, ringed with the surface colour.</summary>
    private static string MarkerShape(int i, double x, double y, string color)
    {
        string ring = $"""stroke="{Surface}" stroke-width="2" fill="{color}" """;
        return (i % 4) switch
        {
            0 => string.Create(Inv, $"""<circle cx="{x:F1}" cy="{y:F1}" r="5" {ring}/>"""),
            1 => string.Create(Inv, $"""<rect x="{x - 4.5:F1}" y="{y - 4.5:F1}" width="9" height="9" rx="1.5" {ring}/>"""),
            2 => string.Create(Inv, $"""<path d="M{x:F1},{y - 6:F1} L{x + 5.5:F1},{y + 4:F1} L{x - 5.5:F1},{y + 4:F1} Z" {ring}/>"""),
            _ => string.Create(Inv, $"""<path d="M{x:F1},{y - 6:F1} L{x + 6:F1},{y:F1} L{x:F1},{y + 6:F1} L{x - 6:F1},{y:F1} Z" {ring}/>"""),
        };
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
    }

    private static double NiceMax(double v) => v <= 0 ? 1 : NiceStep(v);

    private static string FormatTick(double v) =>
        v == 0 ? "0" : Math.Abs(v) >= 100 ? v.ToString("N0", Inv) : Math.Abs(v) >= 1 ? v.ToString("0.##", Inv) : v.ToString("0.###", Inv);

    private static string FormatValue(double v) =>
        Math.Abs(v) >= 100 ? v.ToString("N0", Inv) : Math.Abs(v) >= 10 ? v.ToString("F1", Inv) : v.ToString("F2", Inv);

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
