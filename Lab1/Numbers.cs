using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace Lab1;

/// <summary>The three totals every task produces.</summary>
public readonly record struct Counts(long Negative, long Positive, long Zero)
{
    public long Total => Negative + Positive + Zero;

    public static Counts operator +(Counts a, Counts b) =>
        new(a.Negative + b.Negative, a.Positive + b.Positive, a.Zero + b.Zero);

    public override string ToString() =>
        $"negative={Negative:N0}  positive={Positive:N0}  zero={Zero:N0}  total={Total:N0}";
}

/// <summary>Receives every parsed number. Implemented by structs so the JIT inlines the call.</summary>
public interface INumberSink
{
    void Add(int value);
}

/// <summary>Counts signs in plain locals – private to the thread that owns it.</summary>
public struct CountingSink : INumberSink
{
    public long Negative, Positive, Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int value)
    {
        if (value < 0) Negative++;
        else if (value > 0) Positive++;
        else Zero++;
    }

    public readonly Counts Result => new(Negative, Positive, Zero);
}

/// <summary>Collects the numbers into a growable int[] (Task 1b).</summary>
public struct ArraySink(int initialCapacity) : INumberSink
{
    public int[] Items = new int[Math.Max(16, initialCapacity)];
    public int Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int value)
    {
        if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
        Items[Count++] = value;
    }
}

public static class Numbers
{
    public const byte NewLine = (byte)'\n';

    /// <summary>Parses one line: optional sign, then decimal digits; a trailing '\r' is ignored.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length > 0 && s[^1] == (byte)'\r') s = s[..^1];
        if (s.IsEmpty || s.Length > 11) ThrowBadLine(s);

        int i = 0;
        bool negative = false;
        if (s[0] == (byte)'-') { negative = true; i = 1; }
        else if (s[0] == (byte)'+') { i = 1; }
        if (i == s.Length) ThrowBadLine(s);

        int value = 0;
        for (; i < s.Length; i++)
        {
            uint digit = (uint)(s[i] - (byte)'0');
            if (digit > 9) ThrowBadLine(s);
            value = value * 10 + (int)digit;
        }
        return negative ? -value : value;
    }

    /// <summary>Same as <see cref="Parse(ReadOnlySpan{byte})"/> for a line that is already a string (Task 1c).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Parse(ReadOnlySpan<char> s)
    {
        if (s.Length > 0 && s[^1] == '\r') s = s[..^1];
        if (s.IsEmpty || s.Length > 11) ThrowBadLine(s.ToString());

        int i = 0;
        bool negative = false;
        if (s[0] == '-') { negative = true; i = 1; }
        else if (s[0] == '+') { i = 1; }
        if (i == s.Length) ThrowBadLine(s.ToString());

        int value = 0;
        for (; i < s.Length; i++)
        {
            uint digit = (uint)(s[i] - '0');
            if (digit > 9) ThrowBadLine(s.ToString());
            value = value * 10 + (int)digit;
        }
        return negative ? -value : value;
    }

    private static void ThrowBadLine(ReadOnlySpan<byte> s) =>
        ThrowBadLine(System.Text.Encoding.UTF8.GetString(s));

    private static void ThrowBadLine(string s)
    {
        // Show at most 40 characters, with unprintable bytes as '?' (a corrupt file is binary garbage).
        var shown = new string(s.Take(40).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        throw new InvalidDataException($"not an integer line: \"{shown}{(s.Length > 40 ? "..." : "")}\"");
    }

    /// <summary>
    /// Parses every complete line ('\n'-terminated) in <paramref name="data"/> and returns how many bytes
    /// were consumed. When <paramref name="isLast"/> is true, a final line without '\n' is parsed as well.
    /// </summary>
    public static int ParseLines<TSink>(ReadOnlySpan<byte> data, bool isLast, ref TSink sink)
        where TSink : struct, INumberSink
    {
        int consumed = 0;
        while (true)
        {
            int nl = data[consumed..].IndexOf(NewLine);
            if (nl < 0) break;
            sink.Add(Parse(data.Slice(consumed, nl)));
            consumed += nl + 1;
        }

        if (isLast && consumed < data.Length)
        {
            var rest = data[consumed..];
            if (!(rest.Length == 1 && rest[0] == (byte)'\r'))
                sink.Add(Parse(rest));
            consumed = data.Length;
        }
        return consumed;
    }

    /// <summary>
    /// Returns the first line start at or after <paramref name="offset"/>:
    /// 0 for offset 0, otherwise the byte after the first '\n' at position &gt;= offset - 1.
    /// <para>
    /// Every thread applies this to both ends of its raw range, so the aligned ranges of
    /// neighbouring threads share the same boundary: every line is owned by exactly one thread,
    /// namely the one whose raw range contains the line's first byte.
    /// </para>
    /// </summary>
    public static long AlignToLineStart(SafeFileHandle file, long offset, long fileLength)
    {
        if (offset <= 0) return 0;
        if (offset >= fileLength) return fileLength;

        Span<byte> buffer = stackalloc byte[4096];
        long pos = offset - 1; // if the previous byte is '\n', offset itself is a line start
        while (pos < fileLength)
        {
            int n = RandomAccess.Read(file, buffer, pos);
            if (n == 0) break;
            int i = buffer[..n].IndexOf(NewLine);
            if (i >= 0) return pos + i + 1;
            pos += n;
        }
        return fileLength;
    }

    /// <summary>
    /// Reads the byte range [start, end) of the file in chunks of <c>buffer.Length</c> bytes and feeds every
    /// number in it to <paramref name="sink"/>. <paramref name="start"/> and <paramref name="end"/> must be
    /// line starts (see <see cref="AlignToLineStart"/>). Uses positional reads, so several threads can read
    /// the same file at the same time without sharing a file position.
    /// </summary>
    public static void ScanRange<TSink>(SafeFileHandle file, long start, long end, byte[] buffer, ref TSink sink)
        where TSink : struct, INumberSink
    {
        long pos = start;
        int carry = 0; // bytes of an incomplete line kept at the front of the buffer
        while (pos < end)
        {
            int want = (int)Math.Min(buffer.Length - carry, end - pos);
            int n = RandomAccess.Read(file, buffer.AsSpan(carry, want), pos);
            if (n == 0) throw new EndOfStreamException($"unexpected end of file at byte {pos}");
            pos += n;

            int length = carry + n;
            int used = ParseLines(buffer.AsSpan(0, length), isLast: pos >= end, ref sink);
            carry = length - used;
            if (carry == buffer.Length)
                throw new InvalidDataException("a line is longer than the read buffer");
            buffer.AsSpan(used, carry).CopyTo(buffer); // memmove semantics, overlap is fine
        }
    }

    /// <summary>Reads a whole file on one thread into an int[] (Task 1b's untimed loading step).</summary>
    public static int[] LoadAll(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        long length = RandomAccess.GetLength(file);
        var sink = new ArraySink((int)Math.Min(int.MaxValue / 2, length / 7 + 1024));
        ScanRange(file, 0, length, new byte[1 << 20], ref sink);
        var items = sink.Items;
        Array.Resize(ref items, sink.Count);
        return items;
    }
}
