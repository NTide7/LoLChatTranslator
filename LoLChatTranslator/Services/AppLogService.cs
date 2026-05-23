using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace LoLChatTranslator.Services;

public static class AppLogService
{
    private const long MaxLogFileBytes = 2L * 1024 * 1024;
    private const int MaxQueuedWrites = 4096;

    private static readonly object DirectorySyncRoot = new();
    private static readonly object WriterSyncRoot = new();
    private static readonly ConcurrentQueue<LogEntry> PendingWrites = new();
    private static readonly SemaphoreSlim PendingSignal = new(0);
    private static int _pendingWriteCount;
    private static int _writerStarted;
    private static string? _cachedLogDirectory;

    public static bool EnableVerboseDiagnostics { get; set; }

    static AppLogService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushPending(TimeSpan.FromMilliseconds(750));
    }

    public static string ResolveLogDirectory()
    {
        lock (DirectorySyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(_cachedLogDirectory))
            {
                return _cachedLogDirectory;
            }
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(PythonEnvironmentService.ManagedRootDirectory, "logs")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probePath = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                lock (DirectorySyncRoot)
                {
                    _cachedLogDirectory = candidate;
                }

                return candidate;
            }
            catch
            {
                // Try the next writable location.
            }
        }

        throw new InvalidOperationException("无法创建可写入的日志目录。");
    }

    public static void AppendText(string fileName, string text)
    {
        Enqueue(fileName, text, null);
    }

    public static void AppendText(string fileName, string text, Encoding encoding)
    {
        Enqueue(fileName, text, encoding);
    }

    public static void AppendVerboseText(string fileName, string text)
    {
        if (!EnableVerboseDiagnostics)
        {
            return;
        }

        AppendText(fileName, text);
    }

    public static void AppendVerboseText(string fileName, string text, Encoding encoding)
    {
        if (!EnableVerboseDiagnostics)
        {
            return;
        }

        AppendText(fileName, text, encoding);
    }

    public static void FlushPending(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pendingWriteCount) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    private static void Enqueue(string fileName, string text, Encoding? encoding)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(text))
        {
            return;
        }

        EnsureWriterStarted();
        if (Interlocked.Increment(ref _pendingWriteCount) > MaxQueuedWrites)
        {
            Interlocked.Decrement(ref _pendingWriteCount);
            return;
        }

        PendingWrites.Enqueue(new LogEntry(fileName, text, encoding));
        PendingSignal.Release();
    }

    private static void EnsureWriterStarted()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessPendingWritesAsync);
    }

    private static async Task ProcessPendingWritesAsync()
    {
        while (true)
        {
            await PendingSignal.WaitAsync().ConfigureAwait(false);
            if (!PendingWrites.TryDequeue(out var entry))
            {
                continue;
            }

            try
            {
                WriteEntry(entry);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingWriteCount);
            }
        }
    }

    private static void WriteEntry(LogEntry entry)
    {
        try
        {
            var logDirectory = ResolveLogDirectory();
            var path = Path.Combine(logDirectory, entry.FileName);
            lock (WriterSyncRoot)
            {
                RotateIfNeeded(path);
                if (entry.Encoding is null)
                {
                    File.AppendAllText(path, entry.Text);
                }
                else
                {
                    File.AppendAllText(path, entry.Text, entry.Encoding);
                }
            }
        }
        catch
        {
            // Logging must never interrupt OCR, translation, or shutdown.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxLogFileBytes)
        {
            return;
        }

        var directory = info.DirectoryName ?? string.Empty;
        var archiveName = $"{Path.GetFileNameWithoutExtension(info.Name)}.1{info.Extension}";
        var archivePath = Path.Combine(directory, archiveName);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        File.Move(path, archivePath);
    }

    private sealed record LogEntry(string FileName, string Text, Encoding? Encoding);
}
