// Copyright (c) Microsoft Corporation.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;

namespace MsBuildMcp.Engine;

/// <summary>
/// Stores build output with two modes:
/// - "full": retains lines up to a memory byte cap (default 50MB), with lazy disk spill
///   for overflow. Search/get transparently read from disk when lines were evicted.
/// - "tail": retains only the last 1K lines in memory (no disk). Lightweight for fix loops.
///
/// Thread-safe for concurrent AddLine (from build reader) and reads (from tool handlers).
/// </summary>
public sealed class OutputBuffer : IDisposable
{
    public const long MaxMemoryBytes = 50 * 1024 * 1024; // 50MB
    public const int TailModeMaxLines = 1_000;
    private const int LineOverheadBytes = 40; // .NET string object overhead estimate

    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private readonly string _mode; // "full" or "tail"
    private readonly string? _buildId;
    private StreamWriter? _diskWriter;
    private string? _diskPath;
    private long _memoryBytes;
    private int _firstLineNumber = 1; // 1-indexed line number of _lines[0]
    private int _totalLinesReceived;
    private bool _disposed;
    private bool _freed;

    public OutputBuffer(string mode = "full", string? buildId = null)
    {
        _mode = mode == "tail" ? "tail" : "full";
        _buildId = buildId;
    }

    /// <summary>Current output mode.</summary>
    public string Mode => _mode;

    /// <summary>Total lines ever received (monotonic).</summary>
    public int TotalLinesReceived { get { lock (_lock) return _totalLinesReceived; } }

    /// <summary>Lines currently retained in buffer.</summary>
    public int RetainedLineCount { get { lock (_lock) return _freed ? 0 : _lines.Count; } }

    /// <summary>First available line number in memory (1-indexed).</summary>
    public int FirstAvailableLine { get { lock (_lock) return _freed ? 0 : _firstLineNumber; } }

    /// <summary>True if output has been explicitly freed.</summary>
    public bool IsFreed { get { lock (_lock) return _freed; } }

    /// <summary>True if a disk spill file exists.</summary>
    public bool HasDiskSpill { get { lock (_lock) return _diskPath != null; } }

    /// <summary>Path to disk spill file, if any.</summary>
    public string? DiskPath { get { lock (_lock) return _diskPath; } }

    public void AddLine(string line)
    {
        lock (_lock)
        {
            if (_freed) return;

            _totalLinesReceived++;
            var lineBytes = (long)Encoding.UTF8.GetByteCount(line) + LineOverheadBytes;

            if (_mode == "tail")
            {
                _lines.Add(line);
                _memoryBytes += lineBytes;
                while (_lines.Count > TailModeMaxLines)
                {
                    _memoryBytes -= Encoding.UTF8.GetByteCount(_lines[0]) + LineOverheadBytes;
                    _lines.RemoveAt(0);
                    _firstLineNumber++;
                }
                return;
            }

            // Full mode: add to memory, spill to disk if cap exceeded
            _lines.Add(line);
            _memoryBytes += lineBytes;

            if (_memoryBytes > MaxMemoryBytes)
            {
                EnsureDiskSpillLocked();

                // Evict oldest lines from memory until under cap
                while (_memoryBytes > MaxMemoryBytes && _lines.Count > TailModeMaxLines)
                {
                    _memoryBytes -= Encoding.UTF8.GetByteCount(_lines[0]) + LineOverheadBytes;
                    _lines.RemoveAt(0);
                    _firstLineNumber++;
                }
            }

            // Stream new line to disk if spill file exists
            if (_diskWriter != null)
            {
                try { _diskWriter.WriteLine(line); }
                catch { /* best-effort disk write */ }
            }
        }
    }

    /// <summary>
    /// Create the disk spill file and flush all current memory lines to it.
    /// Called once when memory cap is first exceeded. Must be called under _lock.
    /// </summary>
    private void EnsureDiskSpillLocked()
    {
        if (_diskWriter != null) return;

        var dir = Path.Combine(Path.GetTempPath(), "msbuild-mcp");
        Directory.CreateDirectory(dir);
        _diskPath = Path.Combine(dir, $"{_buildId ?? "build"}.log");
        _diskWriter = new StreamWriter(_diskPath, append: false) { AutoFlush = true };

        // Flush all current memory lines to disk
        foreach (var existingLine in _lines)
        {
            try { _diskWriter.WriteLine(existingLine); }
            catch { break; }
        }
    }

    /// <summary>Get the last N lines from memory.</summary>
    public OutputSlice GetTail(int n)
    {
        lock (_lock)
        {
            if (_freed) return FreedSlice();
            var count = Math.Min(n, _lines.Count);
            var startIdx = _lines.Count - count;
            var lines = _lines.GetRange(startIdx, count);
            return new OutputSlice
            {
                Lines = lines,
                FromLine = _firstLineNumber + startIdx,
                ToLine = _firstLineNumber + _lines.Count - 1,
                TotalLines = _totalLinesReceived,
                RetainedLines = _lines.Count,
                FirstAvailableLine = _firstLineNumber,
            };
        }
    }

    /// <summary>Get lines by line number range (1-indexed, inclusive).</summary>
    public OutputSlice GetLines(int fromLine, int toLine, int maxLines = 200)
    {
        lock (_lock)
        {
            if (_freed) return FreedSlice();

            var lastLine = _firstLineNumber + _lines.Count - 1;

            // If requested range is entirely in memory
            if (fromLine >= _firstLineNumber)
            {
                var clampedFrom = Math.Max(fromLine, _firstLineNumber);
                var clampedTo = Math.Min(toLine, lastLine);
                clampedTo = Math.Min(clampedTo, clampedFrom + maxLines - 1);

                if (clampedFrom > clampedTo)
                    return EmptySlice(clampedFrom, clampedTo);

                var startIdx = clampedFrom - _firstLineNumber;
                var count = clampedTo - clampedFrom + 1;
                var lines = _lines.GetRange(startIdx, count);

                return new OutputSlice
                {
                    Lines = lines,
                    FromLine = clampedFrom,
                    ToLine = clampedTo,
                    TotalLines = _totalLinesReceived,
                    RetainedLines = _lines.Count,
                    FirstAvailableLine = _firstLineNumber,
                };
            }

            // Requested range includes evicted lines — try disk
            if (_diskPath != null && File.Exists(_diskPath))
                return GetLinesFromDisk(fromLine, toLine, maxLines);

            // No disk file, clamp to what's in memory
            return GetLines(Math.Max(fromLine, _firstLineNumber), toLine, maxLines);
        }
    }

    /// <summary>Get lines centered around a specific line number.</summary>
    public OutputSlice GetAroundLine(int centerLine, int maxLines = 200)
    {
        var half = maxLines / 2;
        return GetLines(centerLine - half, centerLine + half, maxLines);
    }

    /// <summary>Find line number of first regex match. Searches disk then memory.</summary>
    public int? FindFirstMatch(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);

        lock (_lock)
        {
            if (_freed) return null;

            // Search disk file first (for evicted lines)
            if (_diskPath != null && File.Exists(_diskPath) && _firstLineNumber > 1)
            {
                var diskMatch = FindFirstMatchInDisk(regex, 1, _firstLineNumber - 1);
                if (diskMatch.HasValue) return diskMatch;
            }

            // Search memory
            for (var i = 0; i < _lines.Count; i++)
            {
                if (regex.IsMatch(_lines[i]))
                    return _firstLineNumber + i;
            }
            return null;
        }
    }

    /// <summary>Search output with regex. Returns matches with context and pagination.</summary>
    public SearchResult Search(string pattern, int contextLines = 3, int maxResults = 20, int skip = 0)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid regex pattern: {ex.Message}", ex);
        }

        lock (_lock)
        {
            if (_freed) return new SearchResult
            {
                Matches = [],
                TotalMatches = 0,
                TotalLines = _totalLinesReceived,
                RetainedLines = 0,
                FirstAvailableLine = 0,
                Freed = true,
            };

            var allMatches = new List<SearchMatch>();

            // Search disk file for evicted lines
            if (_diskPath != null && File.Exists(_diskPath) && _firstLineNumber > 1)
                SearchDiskFile(regex, contextLines, allMatches, 1, _firstLineNumber - 1);

            // Search memory
            for (var i = 0; i < _lines.Count; i++)
            {
                if (!regex.IsMatch(_lines[i])) continue;

                var lineNum = _firstLineNumber + i;
                var ctxStart = Math.Max(0, i - contextLines);
                var ctxEnd = Math.Min(_lines.Count - 1, i + contextLines);
                var context = new List<string>();
                for (var j = ctxStart; j <= ctxEnd; j++)
                {
                    if (j == i) continue;
                    context.Add($"{_firstLineNumber + j}: {_lines[j]}");
                }

                allMatches.Add(new SearchMatch
                {
                    Line = lineNum,
                    Text = _lines[i],
                    Context = context,
                });
            }

            var paged = allMatches.Skip(skip).Take(maxResults).ToList();

            return new SearchResult
            {
                Matches = paged,
                TotalMatches = allMatches.Count,
                TotalLines = _totalLinesReceived,
                RetainedLines = _lines.Count,
                FirstAvailableLine = _firstLineNumber,
            };
        }
    }

    /// <summary>Write all output to a file. Uses disk spill file if available (file copy), else memory.</summary>
    public (int LinesWritten, long BytesWritten) SaveTo(string path)
    {
        lock (_lock)
        {
            if (_freed) return (0, 0);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // If we have a disk spill file, copy it (has all lines including evicted)
            if (_diskPath != null && File.Exists(_diskPath))
            {
                // Flush writer before copying
                try { _diskWriter?.Flush(); } catch { }
                File.Copy(_diskPath, path, overwrite: true);
                var info = new FileInfo(path);
                return (_totalLinesReceived, info.Length);
            }

            // Otherwise write from memory
            long bytes = 0;
            using var writer = new StreamWriter(path);
            foreach (var line in _lines)
            {
                writer.WriteLine(line);
                bytes += Encoding.UTF8.GetByteCount(line) + 2;
            }
            return (_lines.Count, bytes);
        }
    }

    /// <summary>Free all retained output to release memory.</summary>
    public int Free()
    {
        lock (_lock)
        {
            if (_freed) return 0;
            var count = _lines.Count;
            _lines.Clear();
            _lines.TrimExcess();
            _freed = true;
            return count;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _diskWriter?.Dispose();
        // Clean up temp file
        if (_diskPath != null)
        {
            try { File.Delete(_diskPath); } catch { }
        }
    }

    // --- Disk read-back helpers ---

    /// <summary>Read a range of lines from the disk spill file. Sequential scan.</summary>
    private OutputSlice GetLinesFromDisk(int fromLine, int toLine, int maxLines)
    {
        // Must be called under _lock (for _totalLinesReceived etc.)
        // but we read from a file that's being appended to — safe because we only
        // read up to lines we know exist.
        _diskWriter?.Flush();
        var lines = new List<string>();
        var clampedTo = Math.Min(toLine, fromLine + maxLines - 1);
        var lineNum = 0;
        var actualFrom = 0;
        var actualTo = 0;

        try
        {
            using var reader = new StreamReader(_diskPath!, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNum++;
                if (lineNum < fromLine) continue;
                if (lineNum > clampedTo) break;

                if (lines.Count == 0) actualFrom = lineNum;
                lines.Add(line);
                actualTo = lineNum;
            }
        }
        catch { /* best-effort disk read */ }

        return new OutputSlice
        {
            Lines = lines,
            FromLine = actualFrom,
            ToLine = actualTo,
            TotalLines = _totalLinesReceived,
            RetainedLines = _lines.Count,
            FirstAvailableLine = 1, // Disk has everything from line 1
        };
    }

    /// <summary>Search a range of lines in the disk file for regex matches.</summary>
    private void SearchDiskFile(Regex regex, int contextLines,
        List<SearchMatch> allMatches, int fromLine, int toLine)
    {
        _diskWriter?.Flush();
        // Read the relevant portion of the disk file with a context window
        var contextBuffer = new List<(int lineNum, string text)>();
        var lineNum = 0;

        try
        {
            using var reader = new StreamReader(_diskPath!, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNum++;
                if (lineNum > toLine + contextLines) break;

                contextBuffer.Add((lineNum, line));
                // Keep context buffer bounded
                if (contextBuffer.Count > contextLines * 2 + 1 + 100)
                    contextBuffer.RemoveRange(0, contextBuffer.Count - (contextLines * 2 + 1));

                if (lineNum < fromLine || lineNum > toLine) continue;
                if (!regex.IsMatch(line)) continue;

                // Gather context from buffer
                var context = new List<string>();
                foreach (var (ctxNum, ctxText) in contextBuffer)
                {
                    if (ctxNum == lineNum) continue;
                    if (ctxNum >= lineNum - contextLines && ctxNum <= lineNum + contextLines)
                        context.Add($"{ctxNum}: {ctxText}");
                }

                allMatches.Add(new SearchMatch
                {
                    Line = lineNum,
                    Text = line,
                    Context = context,
                });
            }
        }
        catch { /* best-effort disk search */ }
    }

    /// <summary>Find first regex match in a range of lines on disk.</summary>
    private int? FindFirstMatchInDisk(Regex regex, int fromLine, int toLine)
    {
        _diskWriter?.Flush();
        var lineNum = 0;
        try
        {
            using var reader = new StreamReader(_diskPath!, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNum++;
                if (lineNum < fromLine) continue;
                if (lineNum > toLine) break;
                if (regex.IsMatch(line)) return lineNum;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    private OutputSlice FreedSlice() => new()
    {
        Lines = [],
        FromLine = 0,
        ToLine = 0,
        TotalLines = _totalLinesReceived,
        RetainedLines = 0,
        FirstAvailableLine = 0,
        Freed = true,
    };

    private OutputSlice EmptySlice(int from, int to) => new()
    {
        Lines = [],
        FromLine = from,
        ToLine = to,
        TotalLines = _totalLinesReceived,
        RetainedLines = _lines.Count,
        FirstAvailableLine = _firstLineNumber,
    };
}

public sealed class OutputSlice
{
    public required List<string> Lines { get; init; }
    public int FromLine { get; init; }
    public int ToLine { get; init; }
    public int TotalLines { get; init; }
    public int RetainedLines { get; init; }
    public int FirstAvailableLine { get; init; }
    public bool Freed { get; init; }
}

public sealed class SearchMatch
{
    public int Line { get; init; }
    public required string Text { get; init; }
    public required List<string> Context { get; init; }
}

public sealed class SearchResult
{
    public required List<SearchMatch> Matches { get; init; }
    public int TotalMatches { get; init; }
    public int TotalLines { get; init; }
    public int RetainedLines { get; init; }
    public int FirstAvailableLine { get; init; }
    public bool Freed { get; init; }
}
