using System.Diagnostics;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive;

/// <summary>Periodic status lines keep long operations visibly alive, even during a stall.</summary>
namespace Mash.UI.MNXFile.CLI;

internal sealed class ProgressDisplay : IDisposable {
    private readonly object _gate = new();
    private readonly object _outputGate = new();
    private readonly string _operation;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Timer _timer;
    private readonly bool _interactive = !Console.IsErrorRedirected;
    private long? _totalBytes;
    private int? _totalFiles;
    private long _completedBytes;
    private long _currentBytes;
    private long _currentTotal;
    private int _completedFiles;
    private string? _currentName;
    private bool _finished;
    private bool _disposed;
    private int _lastLineLength;
    public ProgressDisplay(string operation, long? totalBytes = null, int? totalFiles = null) {
        _operation = operation;
        _totalBytes = totalBytes;
        _totalFiles = totalFiles;
        Print();
        TimeSpan interval = TimeSpan.FromSeconds(_interactive ? 1 : 5);
        _timer = new Timer(_ => Print(), null, interval, interval);
        }

    public void Configure(long totalBytes, int totalFiles) {
        lock (_gate) {
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            }

        Print();
        }

    public void StartFile(string name, long size) {
        lock (_gate) {
            _currentName = name;
            _currentBytes = 0;
            _currentTotal = size;
            }
        }

    public void AdvanceFile(long bytes) {
        lock (_gate) {
            _currentBytes = bytes;
            }
        }

    public void CompleteFile() {
        lock (_gate) {
            _completedBytes = checked(_completedBytes + _currentTotal);
            _completedFiles++;
            _currentBytes = 0;
            _currentTotal = 0;
            _currentName = null;
            }

        }

    public void AdvanceBytes(long bytes) {
        lock (_gate) {
            _currentBytes = bytes;
            }
        }

    public void Finish() {
        lock (_gate) {
            _finished = true;
            }

        Print();
        }

    private void Print() {
        string line;
        bool final;
        lock (_gate) {
            if (_disposed) {
                return;
                }

            line = $"[{DateTime.Now:HH:mm:ss}] {_operation}: ";
            if (_totalBytes is null || _totalFiles is null) {
                line += $"scanning source entries; elapsed {_elapsed.Elapsed:hh\\:mm\\:ss}";
                }
            else {
                long done = _completedBytes + _currentBytes;
                line += $"bytes {done:N0}/{_totalBytes.Value:N0}; " + $"files {_completedFiles:N0}/{_totalFiles.Value:N0}";
                if (_currentName is not null) {
                    line += $" (current {_completedFiles + 1:N0}/{_totalFiles.Value:N0}: " + $"{Path.GetFileName(_currentName)}, {_currentBytes:N0}/{_currentTotal:N0})";
                    }

                line += $"; elapsed {_elapsed.Elapsed:hh\\:mm\\:ss}";
                }

            if (_finished) {
                line += "; done";
                }

            final = _finished;
            }

        lock (_outputGate) {
            lock (_gate) {
                if (_disposed || (_finished && !final)) {
                    return;
                    }
                }

            // A slow terminal never holds the worker's progress-state lock.
            WriteLine(line, final);
            }
        }

    private void WriteLine(string line, bool final) {
        if (!_interactive) {
            Console.Error.WriteLine(line);
            return;
            }

        int width;
        try {
            width = Math.Max(20, Console.WindowWidth - 1);
            }
        catch {
            width = 120;
            }

        if (line.Length > width) {
            line = line[..width];
            }

        Console.Error.Write('\r');
        Console.Error.Write(line.PadRight(_lastLineLength));
        if (final) {
            Console.Error.WriteLine();
            }
        else {
            Console.Error.Flush();
            }

        _lastLineLength = line.Length;
        }

    public void Dispose() {
        _timer.Dispose();
        string? stopped = null;
        lock (_gate) {
            if (!_finished && !_disposed) {
                stopped = $"[{DateTime.Now:HH:mm:ss}] {_operation}: stopped after " +
                    $"{_elapsed.Elapsed:hh\\:mm\\:ss}; bytes " +
                    $"{_completedBytes + _currentBytes:N0}/{_totalBytes?.ToString("N0") ?? "?"}; " +
                    $"files {_completedFiles:N0}/{_totalFiles?.ToString("N0") ?? "?"}";
                }

            _disposed = true;
            }

        if (stopped is not null) {
            lock (_outputGate) {
                WriteLine(stopped, true);
                }
            }
        }
    }
