// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Environment = System.Environment;

namespace Lumora.Core.Logging
{
    public static class Logger
    {
        private static readonly string LogDirectory;
        private static readonly string LogFile;
        private static readonly ConcurrentQueue<string> _logQueue = new();
        private static readonly AutoResetEvent _logEvent = new(false);
        private static readonly CancellationTokenSource _cancellationTokenSource = new();
        public delegate void GameEngineLogProxy(LogLevel level,String messages);
        public static event GameEngineLogProxy? LogToGameEngine;
        static Logger()
        {
            LogDirectory = ResolveLogDirectory();
            LogFile = string.IsNullOrWhiteSpace(LogDirectory)
                ? string.Empty
                : Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            // Start the background logging task
            Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
        }

        public static event Action<LogLevel, string, string> OnLogWritten = null!;

        // Flip to true when actively diagnosing - Debug calls are firehose and choke
        // the console/file otherwise. Several call sites also read this to skip building expensive
        // diagnostic strings at all, so it stays a plain bool. - xlinka
        public static bool EnableDebug
        {
            get => MinimumLevel == LogLevel.DEBUG;
            set => MinimumLevel = value ? LogLevel.DEBUG : LogLevel.LOG;
        }

        // The floor. Anything below it is dropped before it costs a timestamp, a formatted string, a
        // queue entry or a GD.Print. Default LOG, so DEBUG stays off; set LUMORA_LOG_LEVEL to
        // DEBUG/LOG/WARN/ERROR to move it without a rebuild - WARN is the one to use when you want a
        // session log you can actually read. -xlinka
        public static LogLevel MinimumLevel { get; set; } = ReadLevelFromEnvironment();

        // The enum is ordered by age, not by severity (DEBUG was added last), so rank it explicitly
        // rather than comparing the underlying values.
        private static int Severity(LogLevel level) => level switch
        {
            LogLevel.DEBUG => 0,
            LogLevel.LOG => 1,
            LogLevel.WARN => 2,
            LogLevel.ERROR => 3,
            _ => 1,
        };

        private static LogLevel ReadLevelFromEnvironment()
        {
            var raw = Environment.GetEnvironmentVariable("LUMORA_LOG_LEVEL");
            return raw?.Trim().ToUpperInvariant() switch
            {
                "DEBUG" => LogLevel.DEBUG,
                "LOG" or "INFO" => LogLevel.LOG,
                "WARN" or "WARNING" => LogLevel.WARN,
                "ERROR" => LogLevel.ERROR,
                _ => LogLevel.LOG,
            };
        }

        private static void WriteLog(LogLevel level, string message)
        {
            if (Severity(level) < Severity(MinimumLevel)) return;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] [{level}] {message}";
            _logQueue.Enqueue(logEntry);
            _logEvent.Set();

            // Keep stdout focused on important messages.
            if (level == LogLevel.WARN || level == LogLevel.ERROR)
            {
                Console.WriteLine(logEntry);
            }
            LogToGameEngine?.Invoke(level,message);
            OnLogWritten?.Invoke(level, timestamp, message);
        }


        private static void ProcessLogQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logEvent.WaitOne(TimeSpan.FromSeconds(10)); // Wait for either a timeout or a new log entry
                    FlushLogQueue();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Logger encountered an error: {ex.Message}");
                }
            }
        }

        private static void FlushLogQueue()
        {
            if (_logQueue.IsEmpty) return;
            if (string.IsNullOrWhiteSpace(LogFile))
            {
                while (_logQueue.TryDequeue(out _)) { }
                return;
            }

            try
            {
                using var writer = new StreamWriter(LogFile, append: true);
                while (_logQueue.TryDequeue(out var logEntry))
                {
                    writer.WriteLine(logEntry);
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Failed to write logs to file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Failed to write logs to file: {ex.Message}");
            }
        }

        private static string ResolveLogDirectory()
        {
            foreach (var basePath in GetCandidateBasePaths())
            {
                if (string.IsNullOrWhiteSpace(basePath))
                    continue;

                try
                {
                    var logDirectory = Path.Combine(basePath, "LumoraVR", "logs");
                    Directory.CreateDirectory(logDirectory);
                    return logDirectory;
                }
                catch
                {
                    // Try the next candidate. Logging must never block startup.
                }
            }

            return string.Empty;
        }

        private static string[] GetCandidateBasePaths()
        {
            return new[]
            {
                Lumora.Core.Persistence.PathResolver.LocalPath,
                Path.GetTempPath()
            };
        }

        public static void Shutdown()
        {
            _cancellationTokenSource.Cancel();
            FlushLogQueue(); // Ensure any remaining logs are written
        }

        public static void Log(string message)
        {
            WriteLog(LogLevel.LOG, message);
        }
        public static void Warn(string message)
        {
            WriteLog(LogLevel.WARN, message);
        }
        public static void Error(string message)
        {
            WriteLog(LogLevel.ERROR, message);
        }
        public static void Debug(string message)
        {
            WriteLog(LogLevel.DEBUG, message);
        }

        public enum LogLevel
        {
            LOG,
            WARN,
            ERROR,
            DEBUG
        }
    }
}
