using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid;

/// <summary>
/// Holds all resources that are shared across a single instance of Rapid.
/// </summary>
public sealed partial class SharedResources : IDisposable
{
    private readonly ILogger<SharedResources> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<Func<Task>> _protocolExecutor;

    private Channel<Func<Task>> ProtocolExecutor => _protocolExecutor;

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing protocol message")]
    private partial void LogProtocolMessageError(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing protocol task")]
    private partial void LogProtocolTaskError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to queue protocol action - channel may be closed")]
    private partial void LogFailedToQueueAction();

    public SharedResources(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SharedResources>();

        // Create protocol executor channel for async tasks
        _protocolExecutor = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Start the protocol executors
        _ = Task.Run(ProcessProtocolMessagesAsync);
    }

    private async Task ProcessProtocolMessagesAsync()
    {
        try
        {
            await foreach (var taskFunc in _protocolExecutor.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
#pragma warning disable CA1031 // Do not catch general exception types
                try
                {
                    await taskFunc().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogProtocolTaskError(ex);
                }
#pragma warning restore CA1031 // Do not catch general exception types
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    public async Task ScheduleAsyncCallback(Func<Task> asyncFunc)
    {
        await ProtocolExecutor.Writer.WriteAsync(asyncFunc).ConfigureAwait(false);
    }

    public void ScheduleCallback(Func<Task> asyncFunc)
    {
        ProtocolExecutor.Writer.TryWrite(asyncFunc);
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _protocolExecutor.Writer.Complete();
        _shutdownCts.Dispose();
    }
}
