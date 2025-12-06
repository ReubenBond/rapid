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

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Rapid;

/// <summary>
/// Rapid Cluster example application.
/// </summary>
internal sealed partial class Program
{
    private const int SleepIntervalMs = 1000;
    private const int MaxTries = 400;

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Rapid agent on {Listen}")]
    private static partial void LogStarting(ILogger logger, string Listen);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cluster started on {Listen}")]
    private static partial void LogClusterStarted(ILogger logger, string Listen);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cluster joined. Seed: {Seed}")]
    private static partial void LogClusterJoined(ILogger logger, string Seed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Current membership size: {Size}")]
    private static partial void LogMembershipSize(ILogger logger, int Size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Membership size hasn't stabilized after {MaxTries} attempts")]
    private static partial void LogStabilizationWarning(ILogger logger, int MaxTries);

    [LoggerMessage(Level = LogLevel.Information, Message = "Press Ctrl+C to shut down")]
    private static partial void LogPressCtrlC(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proposal detected: {Change}")]
    private static partial void LogProposalDetected(ILogger logger, ClusterStatusChange Change);

    [LoggerMessage(Level = LogLevel.Information, Message = "View change: Config={ConfigId}, Members={MemberCount}")]
    private static partial void LogViewChange(ILogger logger, long ConfigId, int MemberCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kicked from cluster: {Change}")]
    private static partial void LogKicked(ILogger logger, ClusterStatusChange Change);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error running agent")]
    private static partial void LogError(ILogger logger, Exception ex);

    static int Main(string[] args)
    {
        var listenOption = new Option<string>(
            "--listen",
            "The listening address (e.g., 127.0.0.1:1234)")
        { Required = true };

        var seedOption = new Option<string>(
            "--seed",
            "The seed node's address for bootstrap (e.g., 127.0.0.1:1234)")
        { Required = true };

        var rootCommand = new RootCommand("Rapid.NET Standalone Agent");
        rootCommand.Options.Add(listenOption);
        rootCommand.Options.Add(seedOption);

        rootCommand.SetAction((parseResult) =>
        {
            var listen = parseResult.GetValue(listenOption);
            var seed = parseResult.GetValue(seedOption);
            RunAgentAsync(listen!, seed!).Wait();
        });

        return rootCommand.Parse(args).Invoke();
    }

    static async Task RunAgentAsync(string listenAddress, string seedAddress)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Program>();

#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            var listen = RapidUtils.HostFromString(listenAddress);
            var seed = RapidUtils.HostFromString(seedAddress);

            LogStarting(logger, listenAddress);

            // Build and start/join cluster
            var builder = new Cluster.ClusterBuilder(listen)
                .UseLoggerFactory(loggerFactory);

            Cluster cluster;
            if (listen.Equals(seed))
            {
                LogClusterStarted(logger, listenAddress);
                cluster = await builder.StartAsync();
            }
            else
            {
                LogClusterJoined(logger, seedAddress);
                cluster = await builder.JoinAsync(seed);
            }

            // Register event handlers
            cluster.RegisterSubscription(ClusterEvents.ViewChangeProposal, change =>
            {
                LogProposalDetected(logger, change);
            });

            cluster.RegisterSubscription(ClusterEvents.ViewChange, change =>
            {
                LogViewChange(logger, change.ConfigurationId, change.Membership.Count);
            });

            cluster.RegisterSubscription(ClusterEvents.Kicked, change =>
            {
                LogKicked(logger, change);
            });

            // Periodically print membership
            for (var i = 0; i < MaxTries; i++)
            {
                var size = cluster.GetMembershipSize();
                LogMembershipSize(logger, size);
                await Task.Delay(SleepIntervalMs);
            }

            await cluster.LeaveGracefullyAsync();
        }
        catch (Exception ex)
        {
            LogError(logger, ex);
            return;
        }
#pragma warning restore CA1031
    }
}
