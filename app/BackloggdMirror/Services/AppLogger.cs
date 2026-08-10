using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BackloggdMirror.Services;

/// <summary>
/// File logger writing one file per day under %LOCALAPPDATA%\Apploggd\Logs, rolling to a numbered
/// file past <see cref="_maxFileSize"/> and keeping only the newest <see cref="_maxLogFiles"/>.
///
/// These logs are the only diagnostic left after a data wipe, which is why that folder is the one
/// thing <see cref="AppDataResetService"/> preserves.
/// </summary>
public class AppLogger : IAppLogger
{
    private readonly string _logDirectory;
    private readonly long _maxFileSize;
    private readonly int _maxLogFiles;
    private readonly object _writeLock = new object();

    public AppLogger(string? customLogDirectory = null, long maxFileSize = 10 * 1024 * 1024, int maxLogFiles = 5)
    {
        _logDirectory = customLogDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "Logs");
        _maxFileSize = maxFileSize;
        _maxLogFiles = maxLogFiles;
        Initialize();
    }

    private void Initialize()
    {
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }
    }

    public void Info(string message) => Log("INFO", message);

    public void Warning(string message) => Log("WARNING", message);

    public void Error(string message, Exception? ex = null)
    {
        string logMessage = message;
        if (ex != null)
        {
            logMessage += $" | Exception: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
        }
        Log("ERROR", logMessage);
    }

    private void Log(string level, string message)
    {
        try
        {
            Initialize();

            var today = DateTime.Now;
            string logFileName = $"log_{today:yyyyMMdd}.log";
            string logFilePath = Path.Combine(_logDirectory, logFileName);

            lock (_writeLock)
            {
                var fileInfo = new FileInfo(logFilePath);

                // Roll onto log_yyyyMMdd_N.log once the current file is full, so a single noisy day
                // cannot grow one file without bound.
                int index = 1;
                while (fileInfo.Exists && fileInfo.Length >= _maxFileSize)
                {
                    logFileName = $"log_{today:yyyyMMdd}_{index}.log";
                    logFilePath = Path.Combine(_logDirectory, logFileName);
                    fileInfo = new FileInfo(logFilePath);
                    index++;
                }

                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(logFilePath, logEntry, Encoding.UTF8);
            }

            CleanupOldLogs();
        }
        catch (Exception)
        {
            // A logger that throws would take down the operation it was reporting on, so every
            // failure here is swallowed — including the one where the disk is full.
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            lock (_writeLock)
            {
                var dirInfo = new DirectoryInfo(_logDirectory);
                if (!dirInfo.Exists) return;

                // Newest first, so everything past the retention count is the oldest.
                var logFiles = dirInfo.GetFiles("log_*.log")
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                if (logFiles.Count > _maxLogFiles)
                {
                    for (int i = _maxLogFiles; i < logFiles.Count; i++)
                    {
                        try
                        {
                            logFiles[i].Delete();
                        }
                        catch
                        {
                            // ignore cleanup errors
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore directory errors
        }
    }
}
