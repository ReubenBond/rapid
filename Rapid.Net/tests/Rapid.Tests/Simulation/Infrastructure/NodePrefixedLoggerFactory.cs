using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation.Infrastructure;

/// <summary>
/// A logger factory wrapper that prepends a node name to all log messages.
/// This enables identifying which node produced each log entry when multiple
/// nodes share the same underlying logger factory.
/// </summary>
internal sealed class NodePrefixedLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _innerFactory;
    private readonly string _nodeName;

    public NodePrefixedLoggerFactory(ILoggerFactory innerFactory, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(innerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        _innerFactory = innerFactory;
        _nodeName = nodeName;
    }

    public ILogger CreateLogger(string categoryName)
    {
        var innerLogger = _innerFactory.CreateLogger(categoryName);
        return new NodePrefixedLogger(innerLogger, _nodeName);
    }

    public void AddProvider(ILoggerProvider provider) => _innerFactory.AddProvider(provider);

    public void Dispose()
    {
        // Don't dispose the inner factory - it's shared across nodes
    }
}

/// <summary>
/// A logger wrapper that prepends a node name to all log messages.
/// </summary>
internal sealed class NodePrefixedLogger : ILogger
{
    private readonly ILogger _innerLogger;
    private readonly string _nodeName;

    public NodePrefixedLogger(ILogger innerLogger, string nodeName)
    {
        _innerLogger = innerLogger;
        _nodeName = nodeName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _innerLogger.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _innerLogger.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        _innerLogger.Log(
            logLevel,
            eventId,
            state,
            exception,
            (s, e) => $"[{_nodeName}] {formatter(s, e)}");
    }
}
