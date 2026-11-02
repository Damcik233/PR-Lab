# Lab 1 – Concurrency in practice (C#)

One program, `Lab1`, does every task; the task and its configuration are chosen with
command-line parameters. Threads are always real OS threads created with `new Thread(...)`
(no thread pool, no `Parallel.For`).

| File | What it contains |
|---|---|
| `Program.cs` | command dispatch and `help` text |
| `Numbers.cs` | line parser, `Counts`, splitting a file into line-aligned byte ranges |
| `DataTools.cs` | `gen` (data file), `split` (8 parts), `info` (hardware) |
| `Task1a.cs` | Task 1a – each thread reads and parses its own part of the file |
| `Task1b.cs` | Task 1b – unsynchronised / lock / per-thread counters (+ `Interlocked` as an extra) |
| `Task1c.cs` | Task 1c – one reader, N workers, bounded `BlockingCollection` |
| `Task2.cs` | Task 2 – one file = one unit of work, `File.ReadAllBytes`, files split among threads |
| `Bench.cs` | runs a task for several thread counts, each run in a fresh process, writes CSV |
| `Charts.cs` | turns the CSV files into SVG charts (time, speed-up, peak memory) + a table |

## Build

Requires the .NET 10 SDK.

```bash
cd Lab1
dotnet build -c Release
alias lab1='dotnet bin/Release/net10.0/Lab1.dll'   # used below
lab1 help
```

`dotnet run -c Release -- <command> ...` also works, but it rebuilds first, so use the `dll`
for measurements.

## Data

```bash
lab1 gen      # data/numbers.txt: 50 000 000 integers in [-1 000 000, 1 000 000] (seed 2026)
              # + data/numbers.txt.counts with the true totals
lab1 split    # data/parts/part_1.txt .. part_8.txt, 6 250 000 lines each, in order
              # + data/parts/expected.counts
```

Every task compares its totals with the `.counts` file and prints `OK` or `WRONG`
(the `correct` column in the CSV).

## Running a single configuration

```bash
lab1 1a --threads 8                 # Task 1a  (--verbose shows each thread's byte range)
lab1 1b                             # Task 1b, threads = logical CPUs, all versions, unsync run 3 times
lab1 1b --mode lock --threads 4     #   one version only: unsync | lock | atomic | local
lab1 1c --threads 8                 # Task 1c, 8 workers + 1 reader, batch 10 000, queue capacity 100
lab1 1c --threads 8 --batch 1       #   batches of one line
lab1 1c --threads 8 --capacity 0    #   unbounded queue
lab1 2 --dir /media/$USER/USB/lab1 --threads 2     # Task 2
```

Each run prints the time, the peak memory (peak RSS of the process, `VmHWM`), the totals and a
machine-readable `@result` line.

## Reproducing all measurements

```bash
lab1 info > results/logs/info.txt
lab1 bench --task 1a --runs 5 --csv results/task1a.csv              # threads 1,2,4,8,16,32,64
lab1 bench --task 1b --threads 16 --runs 1 --warmup 0 --csv results/task1b.csv
lab1 bench --task 1b --threads 1,2,4,8,16,32,64 --runs 3 --warmup 0 --repeat 1 --csv results/task1b-sweep.csv
lab1 bench --task 1c --runs 3 --csv results/task1c.csv
lab1 bench --task 1c --threads 1,4,16 --runs 1 --batch 1    --csv results/task1c-batch1.csv
lab1 bench --task 1c --threads 1,4,16 --runs 3 --capacity 0 --csv results/task1c-unbounded.csv
lab1 bench --task 1c --threads 1 --runs 1 --warmup 0 --batch 1 --capacity 0 --csv results/task1c-batch1-unbounded.csv

lab1 chart --csv results/task1a.csv,results/task1c.csv --out results/charts/task1 --title "Task 1a vs 1c" \
           --xlabel "number of threads (Task 1c: worker threads, plus 1 reader)" \
           --markers "6:6 P-cores,10:10 cores,16:16 logical"
lab1 chart --csv results/task1b-sweep.csv --out results/charts/task1b --title "Task 1b" --log-time \
           --markers "10:10 cores,16:16 logical"
```

`bench` starts a new child process for every run, so the peak memory belongs to that one
configuration. It does one unrecorded warm-up run first so that the file is in the OS page
cache and every recorded run reads it from memory, not from the SSD.

## Task 2 – USB flash drive

1. Plug the drive in and find where it is mounted and how fast the link is:
   ```bash
   lsblk -o NAME,MODEL,TRAN,SIZE,MOUNTPOINTS     # e.g. sda ... usb ... /media/<you>/USB
   lsusb -t                                      # 480M = USB 2.0, 5000M = USB 3.x
   ```
2. Copy the parts to it:
   ```bash
   mkdir -p /media/$USER/USB/lab1
   cp data/parts/part_*.txt data/parts/expected.counts /media/$USER/USB/lab1/ && sync
   ```
3. Run the measurement. Before **every** run (the first one too – the files you just copied
   are still in the cache) the program asks you to eject and unplug the drive, waits until the
   folder disappears, then waits until you plug it back in and it is mounted again:
   ```bash
   lab1 bench --task 2 --threads 1,2,4 --runs 1 --pause --dir /media/$USER/USB/lab1 --csv results/task2.csv
   ```
   Eject from the file manager, or `udisksctl unmount -b /dev/sdX1 && udisksctl power-off -b /dev/sdX`.
4. Charts (USB next to the SSD reference):
   ```bash
   lab1 chart --csv "USB=results/task2.csv,SSD cold cache=results/task2-ssd-cold.csv" \
              --out results/charts/task2 --title "Task 2"
   ```

The extra USB runs without a replug (`results/task2-usb-evicted.csv`) dropped the 8 files from
the page cache before each run instead of unplugging the drive (same `posix_fadvise` call as
below, on the files on the drive), then ran `lab1 bench --task 2 --threads N --runs 1 --warmup 0 --append ...`.

The SSD reference (`results/task2-ssd-*.csv`) was measured with
`lab1 bench --task 2 --threads 1,2,4 --dir data/parts ...`; for the cold-cache runs the 8
files were dropped from the page cache before every run with `posix_fadvise(POSIX_FADV_DONTNEED)`
(for example `python3 -c "import os,sys; [os.posix_fadvise(os.open(f,os.O_RDONLY),0,0,os.POSIX_FADV_DONTNEED) for f in sys.argv[1:]]" data/parts/part_*.txt`).

Each run also prints, per file, how long the `ReadAllBytes` call took (and the MB/s) and how
long the parsing took, which shows directly whether reading or parsing dominates.
