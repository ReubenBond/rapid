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
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rapid;

/// <summary>
/// Rapid Cluster example application using modern ASP.NET hosting.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Proposal detected: {Change}")]
    private static partial void LogProposalDetected(ILogger logger, ClusterStatusChange Change);

    [LoggerMessage(Level = LogLevel.Information, Message = "View change: Config={ConfigId}, Members={MemberCount}")]
    private static partial void LogViewChange(ILogger logger, long ConfigId, int MemberCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kicked from cluster: {Change}")]
    private static partial void LogKicked(ILogger logger, ClusterStatusChange Change);

    static async Task<int> Main(string[] args)
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

        return rootCommand.Parse(args).InvokeAsync().GetAwaiter().GetResult();
    }

    static async Task RunAgentAsync(string listenAddress, string seedAddress)
    {
        var builder = WebApplication.CreateBuilder();

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var listen = RapidUtils.HostFromString(listenAddress);
        var seed = RapidUtils.HostFromString(seedAddress);

        // Configure Kestrel for gRPC
        builder.ConfigureRapidKestrel(listen.Port);

        // Add Rapid services
        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = listen;
            options.SeedAddress = seed;
        });

        // Add background service to monitor cluster
        builder.Services.AddHostedService<ClusterMonitorService>();

        var app = builder.Build();

        // Map Rapid gRPC endpoints
        app.MapRapidMembershipService();

        LogStarting(app.Logger, listenAddress);

        if (listen.Equals(seed))
        {
            LogClusterStarted(app.Logger, listenAddress);
        }
        else
        {
            LogClusterJoined(app.Logger, seedAddress);
        }

        await app.RunAsync();
    }

    /// <summary>
    /// Background service to monitor the cluster and subscribe to events.
    /// </summary>
#pragma warning disable CA1812 // Avoid uninstantiated internal classes - Instantiated by DI
    private sealed class ClusterMonitorService : BackgroundService
#pragma warning restore CA1812
    {
        private readonly IRapidCluster _cluster;
        private readonly ILogger<ClusterMonitorService> _logger;
        private readonly IHostApplicationLifetime _lifetime;

        public ClusterMonitorService(
            IRapidCluster cluster,
            ILogger<ClusterMonitorService> logger,
            IHostApplicationLifetime lifetime)
        {
            _cluster = cluster;
            _logger = logger;
            _lifetime = lifetime;

            // Register event subscriptions
            _cluster.RegisterSubscription(ClusterEvents.ViewChangeProposal, change =>
                LogProposalDetected(logger, change));

            _cluster.RegisterSubscription(ClusterEvents.ViewChange, change =>
                LogViewChange(logger, change.ConfigurationId, change.Membership.Count));

            _cluster.RegisterSubscription(ClusterEvents.Kicked, change =>
                LogKicked(logger, change));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait a bit for the cluster to initialize
            await Task.Delay(2000, stoppingToken);

            // Periodically print membership
            for (var i = 0; i < MaxTries && !stoppingToken.IsCancellationRequested; i++)
            {
                var size = _cluster.GetMembershipSize();
                LogMembershipSize(_logger, size);
                await Task.Delay(SleepIntervalMs, stoppingToken);
            }

            // Leave gracefully
            await _cluster.LeaveGracefullyAsync();

            // Signal shutdown
            _lifetime.StopApplication();
        }
    }
}
