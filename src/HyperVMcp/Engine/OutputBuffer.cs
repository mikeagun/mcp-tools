// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;

namespace HyperVMcp.Engine;

/// <summary>
/// Stores command output with two modes:
/// - "full": retains all lines up to a cap (searchable, range-viewable)
/// - "tail": retains only the last N lines (lightweight ring buffer)
///
/// Optionally streams all lines to a disk file as they arrive (save_to).
/// Thread-safe for concurrent AddLine (from streaming backend) and reads (from tool handlers).
/// </summary>
public sealed class OutputBuffer : IDisposable
{
    public const int FullModeMaxLines = 50_000;
    public const int TailModeMaxLines = 1_000;

    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private readonly string _mode; // "full" or "tail"
    private readonly int _maxLines;
    private StreamWriter? _diskWriter;
    private int _firstLineNumber = 1; // 1-indexed line number of _lines[0]
    private int _totalLinesReceived;
    private bool _disposed;
    private bool _freed;

    public OutputBuffer(string mode = "full", string? saveTo = null)
    {
        _mode = mode == "tail" ? "tail" : "full";
        _maxLines = _mode == "tail" ? TailModeMaxLines : FullModeMaxLines;

        if (!string.IsNullOrEmpty(saveTo))
        {
            var dir = Path.GetDirectoryName(saveTo);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _diskWriter = new StreamWriter(saveTo, append: false) { AutoFlush = true };
        }
    }

    /// <summary>Current output mode.</summary>
    public string Mode => _mode;

    /// <summary>Total lines ever received (monotonic).</summary>
    public int TotalLinesReceived { get { lock (_lock) return _totalLinesReceived; } }

    /// <summary>Lines currently retained in buffer.</summary>
    public int RetainedLineCount { get { lock (_lock) return _freed ? 0 : _lines.Count; } }

    /// <summary>First available line number (1-indexed). Lines before this were dropped.</summary>
    public int FirstAvailableLine { get { lock (_lock) return _freed ? 0 : _firstLineNumber; } }

    /// <summary>True if any lines were dropped due to buffer cap.</summary>
    public bool WasTruncated { get { lock (_lock) return _firstLineNumber > 1; } }

    /// <summary>True if output has been explicitly freed.</summary>
    public bool IsFreed { get { lock (_lock) return _freed; } }

    /// <summary>Path to disk file if streaming, null otherwise.</summary>
    public string? SavedTo => _diskWriter != null ? (_diskWriter.BaseStream as FileStream)?.Name : null;

    public void AddLine(string line)
    {
        lock (_lock)
        {
            if (_freed) return;

            _totalLinesReceived++;
            _lines.Add(line);

            // Enforce cap.
            while (_lines.Count > _maxLines)
            {
                _lines.RemoveAt(0);
                _firstLineNumber++;
            }
        }

        // Write to disk outside lock (AutoFlush handles thread safety for the file).
        try { _diskWriter?.WriteLine(line); }
        catch { /* best-effort disk write */ }
    }

    /// <summary>Get the last N lines.</summary>
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

    /// <summary>
    /// Get the last N lines that are newer than sinceLine.
    /// Returns up to maxLines of the newest output after sinceLine.
    /// If more lines arrived than maxLines, skips older ones (returns tail of new lines).
    /// </summary>
    public OutputSlice GetTailSince(int sinceLine, int maxLines)
    {
        lock (_lock)
        {
            if (_freed) return FreedSlice();

            var lastLine = _firstLineNumber + _lines.Count - 1;
            // Nothing new since sinceLine.
            if (lastLine <= sinceLine || _lines.Count == 0)
                return new OutputSlice
                {
                    Lines = [],
                    FromLine = sinceLine + 1,
                    ToLine = sinceLine,
                    TotalLines = _totalLinesReceived,
                    RetainedLines = _lines.Count,
                    FirstAvailableLine = _firstLineNumber,
                    SinceLine = sinceLine,
                };

            // New lines start after sinceLine.
            var newStartLine = Math.Max(sinceLine + 1, _firstLineNumber);
            var totalNewLines = lastLine - newStartLine + 1;

            // Take last maxLines of the new lines (tail of new, not head).
            var takeStart = newStartLine;
            var skippedLines = 0;
            if (totalNewLines > maxLines)
            {
                skippedLines = totalNewLines - maxLines;
                takeStart = lastLine - maxLines + 1;
            }

            var startIdx = takeStart - _firstLineNumber;
            var count = Math.Min(maxLines, _lines.Count - startIdx);
            var lines = _lines.GetRange(startIdx, count);

            return new OutputSlice
            {
                Lines = lines,
                FromLine = takeStart,
                ToLine = takeStart + count - 1,
                TotalLines = _totalLinesReceived,
                RetainedLines = _lines.Count,
                FirstAvailableLine = _firstLineNumber,
                SinceLine = sinceLine,
                SkippedLines = skippedLines,
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
            var clampedFrom = Math.Max(fromLine, _firstLineNumber);
            var clampedTo = Math.Min(toLine, lastLine);
            clampedTo = Math.Min(clampedTo, clampedFrom + maxLines - 1);

            if (clampedFrom > clampedTo)
                return new OutputSlice
                {
                    Lines = [],
                    FromLine = clampedFrom,
                    ToLine = clampedTo,
                    TotalLines = _totalLinesReceived,
                    RetainedLines = _lines.Count,
                    FirstAvailableLine = _firstLineNumber,
                };

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
    }

    /// <summary>Get lines centered around a specific line number.</summary>
    public OutputSlice GetAroundLine(int centerLine, int maxLines = 200)
    {
        var half = maxLines / 2;
        return GetLines(centerLine - half, centerLine + half, maxLines);
    }

    /// <summary>Search retained output with regex. Returns matches with context and pagination.</summary>
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

    /// <summary>Find line number of first regex match. Returns null if not found.</summary>
    public int? FindFirstMatch(string pattern)
    {
        lock (_lock)
        {
            if (_freed) return null;
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            for (var i = 0; i < _lines.Count; i++)
            {
                if (regex.IsMatch(_lines[i]))
                    return _firstLineNumber + i;
            }
            return null;
        }
    }

    /// <summary>Write all retained lines to a file.</summary>
    public (int LinesWritten, long BytesWritten) SaveTo(string path)
    {
        lock (_lock)
        {
            if (_freed) return (0, 0);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            long bytes = 0;
            using var writer = new StreamWriter(path);
            foreach (var line in _lines)
            {
                writer.WriteLine(line);
                bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 2; // +crlf
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _diskWriter?.Dispose();
    }
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
    /// <summary>Set when GetTailSince was used — the line number the agent last saw.</summary>
    public int? SinceLine { get; init; }
    /// <summary>Lines skipped between SinceLine and FromLine (when more new lines than maxLines).</summary>
    public int SkippedLines { get; init; }
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
