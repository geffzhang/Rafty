﻿using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Rafty.Commands;
using Rafty.Messages;
using Rafty.Messaging;
using Rafty.Raft;
using Rafty.ServiceDiscovery;
using Rafty.State;

namespace Rafty.Infrastructure
{
    public static class RaftyConfigurationExtensions
    {

        public static IApplicationBuilder UseRafty(this IApplicationBuilder builder, 
            Uri baseUri, 
            IMessageSender messageSender, 
            IMessageBus messageBus, 
            IStateMachine stateMachine, 
            IServiceRegistry serviceRegistry, 
            ILogger logger,
            IServersInCluster serversInCluster,
            JsonConverter[] jsonConverters,
            string raftyBasePath = null)
        {
            builder.UseRaftyForTesting(baseUri, messageSender, messageBus, stateMachine, serviceRegistry,
                logger, serversInCluster, jsonConverters, raftyBasePath);

            return builder;
        }

        public static (IApplicationBuilder builder, Server server, ServerInCluster serverInCluster) UseRaftyForTesting(this IApplicationBuilder builder,
           Uri baseUri,
           IMessageSender messageSender,
           IMessageBus messageBus,
           IStateMachine stateMachine,
           IServiceRegistry serviceRegistry,
           ILogger logger,
           IServersInCluster serversInCluster,
           JsonConverter[] jsonConverters,
           string raftyBasePath = null)
        {
            var urlConfig = RaftyUrlConfig.Get(raftyBasePath);

            var server = new Server(messageBus, serversInCluster, stateMachine, logger);

            serviceRegistry.Register(new RegisterService(RaftyServiceDiscoveryName.Get(), server.Id, baseUri));

            messageSender.SetServer(server);

            var serverInCluster = new ServerInCluster(server.Id);

            serversInCluster.Add(serverInCluster);

            builder.Map(urlConfig.appendEntriesUrl, app =>
            {
                app.Run(async context =>
                {
                    try
                    {
                        var reader = new StreamReader(context.Request.Body);
                        var content = reader.ReadToEnd();
                        var appendEntries = JsonConvert.DeserializeObject<AppendEntries>(content, jsonConverters);
                        var appendEntriesResponse = server.Receive(appendEntries);
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(appendEntriesResponse));
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(new EventId(1), exception, $"There was an error handling {urlConfig.appendEntriesUrl}");
                    }
                });
            });

            builder.Map(urlConfig.requestVoteUrl, app =>
            {
                app.Run(async context =>
                {
                    try
                    {
                        var reader = new StreamReader(context.Request.Body);
                        var content = reader.ReadToEnd();
                        var requestVote = JsonConvert.DeserializeObject<RequestVote>(content, jsonConverters);
                        var requestVoteResponse = server.Receive(requestVote);
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(requestVoteResponse));
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(new EventId(1), exception, $"There was an error handling {urlConfig.requestVoteUrl}");
                    }
                });
            });

            builder.Map(urlConfig.commandUrl, app =>
            {
                app.Run(async context =>
                {
                    try
                    {
                        var reader = new StreamReader(context.Request.Body);
                        var content = reader.ReadToEnd();
                        var command = JsonConvert.DeserializeObject<Command>(content, jsonConverters);
                        var sendCommandToLeaderResponse = server.Receive(command);
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(sendCommandToLeaderResponse));
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(new EventId(1), exception, $"There was an error handling {urlConfig.commandUrl}");
                    }
                });
            });
            return (builder, server, serverInCluster);
        }
    }
}
