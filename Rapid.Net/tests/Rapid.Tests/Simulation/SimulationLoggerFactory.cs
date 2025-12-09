using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation;

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
