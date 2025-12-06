/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
 * except in compliance with the License. You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under the
 * License is distributed on an "AS IS" BASIS, without warranties or conditions of any kind,
 * EITHER EXPRESS OR IMPLIED. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rapid.Pb;
using System.Threading.Channels;

namespace Rapid;

/// <summary>
/// Holds all resources that are shared across a single instance of Rapid.
/// </summary>
public sealed class SharedResources : IDisposable
{
    private readonly ILogger<SharedResources> _logger;
    private readonly Endpoint _address;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<Func<Task>> _protocolExecutor;
    
    public Channel<Action> ProtocolChannel { get; }
    public TaskScheduler ScheduledTasksScheduler { get; }

    public Channel<Func<Task>> GetProtocolExecutor() => _protocolExecutor;

    public SharedResources(Endpoint address, ILoggerFactory? loggerFactory = null)
    {
        _address = address;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SharedResources>();
        
        // Create a single-threaded channel for protocol execution
        ProtocolChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        // Create protocol executor channel for async tasks
        _protocolExecutor = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        ScheduledTasksScheduler = TaskScheduler.Default;
        
        // Start the protocol executors
        _ = Task.Run(ProcessProtocolMessages);
        _ = Task.Run(ProcessProtocolMessagesAsync);
    }

    private async Task ProcessProtocolMessages()
    {
        try
        {
            await foreach (var action in ProtocolChannel.Reader.ReadAllAsync(_shutdownCts.Token))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing protocol message");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    private async Task ProcessProtocolMessagesAsync()
    {
        try
        {
            await foreach (var taskFunc in _protocolExecutor.Reader.ReadAllAsync(_shutdownCts.Token))
            {
                try
                {
                    await taskFunc();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing protocol task");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    public void ExecuteOnProtocol(Action action)
    {
        if (!ProtocolChannel.Writer.TryWrite(action))
        {
            _logger.LogWarning("Failed to queue protocol action - channel may be closed");
        }
    }

    public Task<T> ExecuteOnProtocolAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        ExecuteOnProtocol(() =>
        {
            try
            {
                var result = func();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        ProtocolChannel.Writer.Complete();
        _protocolExecutor.Writer.Complete();
    }
}
