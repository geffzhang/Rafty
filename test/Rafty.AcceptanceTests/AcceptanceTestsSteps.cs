using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Newtonsoft.Json;
using Shouldly;

namespace Rafty.AcceptanceTests
{
    public class AcceptanceTestsSteps : IDisposable
    {
        private List<ServerInCluster> _remoteServers;
        private List<string> _remoteServerLocations;
        private ServiceRegistry _serviceRegistry;
        private List<ServerContainer> _servers;
        private FakeCommand _command;

        public AcceptanceTestsSteps()
        {
            _remoteServers = new List<ServerInCluster>();
            _serviceRegistry = new ServiceRegistry();
            _servers = new List<ServerContainer>();
        }

        public Timer GivenIHaveStartedMonitoring()
        {
            var timer = new Timer(x =>
            {
                Console.WriteLine("------------------------------------------------------------------------------------------------------");
                var rowNum = 0;
                foreach (var server in _servers.Select(s => s.Server))
                {
                    if (server.State is Leader)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                    }
                    else if (server.State is Follower)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                    }

                    Console.WriteLine($"{rowNum}. Id: {server.Id} State: {server.State} Term: {server.CurrentTerm} VotedFor: {server.VotedFor} CurrentTermVotes {server.CurrentTermVotes}");
                    rowNum++;
                }
            }, null, 1, 5000);
            return timer;
        }

        public void TheCommandIsPersistedToAllStateMachines(int index, int serversToCheck)
        {
            var stopWatch = Stopwatch.StartNew();
            var updated = new List<Guid>();

            while (stopWatch.ElapsedMilliseconds < 90000)
            {
                foreach (var server in _servers)
                {
                    var fakeStateMachine = (FakeStateMachine)server.StateMachine;

                    if (fakeStateMachine.Commands.Count > 0)
                    {
                        var command = (FakeCommand)fakeStateMachine.Commands[index];
                        command.Id.ShouldBe(_command.Id);
                        if (!updated.Contains(server.Server.Id))
                        {
                            updated.Add(server.Server.Id);
                        }
                    }
                }

                if (updated.Count == serversToCheck)
                {
                    break;
                }
            }

            updated.Count.ShouldBe(serversToCheck);
        }

        public void ACommandIsSentToTheLeader()
        {
            var leader = _servers.SingleOrDefault(x => x.Server.State is Leader);
            while(leader == null)
            {
                ThenANewLeaderIsElected();
                leader = _servers.SingleOrDefault(x => x.Server.State is Leader);
            }
            _command = new FakeCommand(Guid.NewGuid());
            var urlOfLeader = leader.ServerUrl;
            var json = JsonConvert.SerializeObject(_command);
            var httpContent = new StringContent(json);

            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(urlOfLeader);
                var response = httpClient.PostAsync("/command", httpContent).Result;
                response.EnsureSuccessStatusCode();
            }
        }

        public void ThenThatServerIsReceivingAndSendingMessages(string baseUrlOfServerToAssert)
        {
            var result = _servers.First(x => x.ServerUrl == baseUrlOfServerToAssert);

            result.Server.Id.ShouldNotBe(default(Guid));
            result.Server.CountOfRemoteServers.ShouldBe(6);
            var termMatchWithLeader = false;
            var stopWatch = Stopwatch.StartNew();
            while (stopWatch.ElapsedMilliseconds < 90000)
            {
                var leader = _servers.FirstOrDefault(x => x.Server.State is Leader);
                if (leader == null)
                {
                    continue;
                }
                if (leader.Server.CurrentTerm == result.Server.CurrentTerm)
                {
                    termMatchWithLeader = true;
                    break;
                }
            }

            termMatchWithLeader.ShouldBeTrue();
        }

        public void WhenIAddANewServer(string baseUrl)
        {
            GivenAServerIsRunning(baseUrl).Wait();
        }

        public void ThenTheOtherNodesAreFollowers(int expected)
        {
            var stopWatch = Stopwatch.StartNew();
            var fourFollowers = false;
            while (stopWatch.ElapsedMilliseconds < 90000)
            {
                var followers = _servers.Where(x => x.Server.State is Follower).ToList();
                if (followers.Count == expected)
                {
                    fourFollowers = true;
                    break;
                }
            }

            fourFollowers.ShouldBeTrue();
        }

        public void ThenANewLeaderIsElected()
        {
            var stopWatch = Stopwatch.StartNew();
            var newLeaderElected = false;

            while (stopWatch.ElapsedMilliseconds < 90000)
            {
                var leader = _servers.SingleOrDefault(x => x.Server.State is Leader);
                if (leader != null)
                {
                    newLeaderElected = true;
                    break;
                }
            }

            newLeaderElected.ShouldBeTrue();

            if (!newLeaderElected)
            {
                throw new Exception("no new leader");
            }

            Thread.Sleep(1000);
        }

        public void WhenTheLeaderDies()
        {
            var killedLeader = false;

            while (!killedLeader)
            {
                foreach (var serverContainer in _servers)
                {
                    if (serverContainer.Server.State is Leader)
                    {
                        serverContainer.MessageSender.Stop();
                        serverContainer.MessageBus.Stop();
                        serverContainer.WebHost.Dispose();
                        _remoteServers.Remove(serverContainer.ServerInCluster);
                        _servers.Remove(serverContainer);
                        killedLeader = true;
                        break;
                    }
                }
            }
        }

        public void GivenTheFollowingServersAreRunning(List<string> remoteServers)
        {
            _remoteServerLocations = remoteServers;

            var tasks = new Task[_remoteServerLocations.Count];
            for (int i = 0; i < tasks.Length; i++)
            {
                var remoteServerLocation = _remoteServerLocations[i];
                Thread.Sleep(500);
                tasks[i] = GivenAServerIsRunning(remoteServerLocation);
            }

            Task.WaitAll(tasks);
        }

        private async Task GivenAServerIsRunning(string baseUrl)
        {
            Server server = null;
            HttpClientMessageSender messageSender = null;
            ServerInCluster serverInCluster = null;
            InMemoryBus messageBus = null;
            IStateMachine stateMachine = null;

            var webHost = new WebHostBuilder()
                .UseUrls(baseUrl)
                .UseKestrel()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .ConfigureServices(x =>
                {
                })
                .Configure(app =>
                {
                    messageSender = new HttpClientMessageSender(_serviceRegistry);
                    messageBus = new InMemoryBus(messageSender);
                    stateMachine = new FakeStateMachine();
                    var logger = new ConsoleLogger("ConsoleLogger", (x, y) => true, true);

                    var result = app.UseRaftyForTesting(new Uri(baseUrl), messageSender, messageBus, stateMachine, 
                        _serviceRegistry, logger, _remoteServers);

                    server = result.server;
                    serverInCluster = result.serverInCluster;
                })
                .Build();

            webHost.Start();

            var serverContainer = new ServerContainer(webHost, server, baseUrl, messageSender, serverInCluster, messageBus, stateMachine);

            _servers.Add(serverContainer);
        }

        public void Dispose()
        {
            foreach (var serverContainer in _servers)
            {
                serverContainer.MessageSender.Stop();
                serverContainer.MessageBus.Stop();
                serverContainer.WebHost.Dispose();
                _remoteServers.Remove(serverContainer.ServerInCluster);
            }

            Thread.Sleep(1000);
        }

        public void ACommandIsSentToAFollower()
        {
            var leader = _servers.SingleOrDefault(x => x.Server.State is Leader);
            var follower = _servers.FirstOrDefault(x => x.Server.State is Follower);
            while (leader == null)
            {
                ThenANewLeaderIsElected();
                leader = _servers.SingleOrDefault(x => x.Server.State is Leader);
                follower = _servers.FirstOrDefault(x => x.Server.State is Follower);
            }
            _command = new FakeCommand(Guid.NewGuid());
            var urlOfLeader = follower.ServerUrl;
            var json = JsonConvert.SerializeObject(_command);
            var httpContent = new StringContent(json);

            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(urlOfLeader);
                var response = httpClient.PostAsync("/command", httpContent).Result;
                response.EnsureSuccessStatusCode();
            }

            //hacky delay
            var stopWatch = Stopwatch.StartNew();
            while (stopWatch.ElapsedMilliseconds < 1000)
            {
                
            }
        }
    }
}