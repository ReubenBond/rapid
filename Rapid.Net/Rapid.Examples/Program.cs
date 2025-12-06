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
internal class Program
{
    private const int SleepIntervalMs = 1000;
    private const int MaxTries = 400;

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

        try
        {
            var listen = Utils.HostFromString(listenAddress);
            var seed = Utils.HostFromString(seedAddress);

            logger.LogInformation("Starting Rapid agent on {Listen}", listenAddress);

            // Build and start/join cluster
            var builder = new Cluster.ClusterBuilder(listen)
                .UseLoggerFactory(loggerFactory);

            Cluster cluster;
            if (listen.Equals(seed))
            {
                logger.LogInformation("Starting as seed node");
                cluster = await builder.StartAsync();
            }
            else
            {
                logger.LogInformation("Joining cluster via seed {Seed}", seedAddress);
                cluster = await builder.JoinAsync(seed);
            }

            // Register event handlers
            cluster.RegisterSubscription(ClusterEvents.ViewChangeProposal, change =>
            {
                logger.LogInformation("Proposal detected: {Change}", change);
            });

            cluster.RegisterSubscription(ClusterEvents.ViewChange, change =>
            {
                logger.LogInformation("View change: Config={ConfigId}, Members={MemberCount}",
                    change.ConfigurationId, change.Membership.Count);
            });

            cluster.RegisterSubscription(ClusterEvents.Kicked, change =>
            {
                logger.LogWarning("Kicked from cluster: {Change}", change);
            });

            // Periodically print membership
            for (int i = 0; i < MaxTries; i++)
            {
                var size = cluster.GetMembershipSize();
                logger.LogInformation("Node {Listen} -- cluster size {Size}", listenAddress, size);
                await Task.Delay(SleepIntervalMs);
            }

            await cluster.LeaveGracefullyAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception in Rapid agent");
            return;
        }
    }
}
