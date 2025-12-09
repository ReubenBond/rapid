using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Factory for creating file-based loggers for simulation tests.
/// Creates a unique log file per simulation and provides methods for attaching
/// the log file to the test context.
/// </summary>
internal sealed class SimulationLoggerFactory : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _logFilePath;
    private bool _disposed;

    /// <summary>
    /// Creates a new simulation logger factory.
    /// </summary>
    /// <param name="testName">The name of the test (used in the log file name).</param>
    /// <param name="seed">The simulation seed (used in the log file name).</param>
    /// <param name="timeProvider">The time provider for timestamps in log entries.</param>
    public SimulationLoggerFactory(string? testName, int seed, TimeProvider timeProvider)
    {
        _logFilePath = GenerateLogFilePath(testName, seed);
        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder
            .AddProvider(new FileLoggerProvider(_logFilePath, timeProvider))
            .SetMinimumLevel(LogLevel.Debug));
    }

    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Gets the underlying logger factory.
    /// </summary>
    public ILoggerFactory Factory => _loggerFactory;

    /// <summary>
    /// Creates a logger for the specified type.
    /// </summary>
    public ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();

    /// <summary>
    /// Creates a logger for the specified category name.
    /// </summary>
    public ILogger CreateLogger(string categoryName) => _loggerFactory.CreateLogger(categoryName);

    /// <summary>
    /// Attaches the log file to the test context if it exists.
    /// Should be called during test disposal.
    /// </summary>
    /// <param name="testContext">The xUnit test context.</param>
    public void AttachToTestContext(ITestContext? testContext)
    {
        if (testContext == null || !File.Exists(_logFilePath))
        {
            return;
        }

        var logFileName = Path.GetFileName(_logFilePath);
        testContext.AddAttachment(logFileName, _logFilePath);
    }

    /// <summary>
    /// Generates a unique log file path for a simulation.
    /// </summary>
    private static string GenerateLogFilePath(string? testName, int seed)
    {
        var sanitizedTestName = SanitizeFileName(testName ?? "unknown_test");
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(Path.GetTempPath(), $"rapid_sim_{sanitizedTestName}_{seed}_{uniqueId}.log");
    }

    /// <summary>
    /// Sanitizes a string to be used as a file name by removing invalid characters.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            sanitized.Append(invalidChars.Contains(c) ? '_' : c);
        }
        // Truncate to a reasonable length to avoid path length issues
        var result = sanitized.ToString();
        return result.Length > 100 ? result[..100] : result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loggerFactory.Dispose();
    }
}

/// <summary>
/// A logger provider that writes log messages to a file.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private bool _disposed;

    public FileLoggerProvider(string filePath, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        
        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        _writer = new StreamWriter(filePath, append: false) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this);
    }

    internal void WriteLog(string message)
    {
        if (_disposed) return;
        
        lock (_lock)
        {
            if (_disposed) return;
            _writer.WriteLine(message);
        }
    }

    internal DateTimeOffset GetTimestamp() => _timeProvider.GetUtcNow();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}

/// <summary>
/// A logger that writes to a file via the FileLoggerProvider.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var timestamp = _provider.GetTimestamp();
        var levelStr = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "NONE "
        };

        var message = formatter(state, exception);
        var logLine = $"{timestamp:O} [{levelStr}] {_categoryName}: {message}";
        
        if (exception != null)
        {
            logLine += Environment.NewLine + exception;
        }

        _provider.WriteLog(logLine);
    }
}
