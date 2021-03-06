using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace Rafty
{
    using System.Threading;
    using System.Threading.Tasks;

    public class HttpClientMessageSender : IMessageSender
    {
        private readonly IServiceRegistry _serviceRegistry;
        private Server _server;
        private readonly Dictionary<Type, Action<IMessage>> _sendToSelfHandlers;
        private bool _stopSendingMessages;
        private bool _sleeping;
        private readonly string _appendEntriesUrl;
        private readonly string _requestVoteUrl;
        private readonly string _commandUrl;

        public HttpClientMessageSender(IServiceRegistry serviceRegistry, string raftyBasePath = null)
        {
            var urlConfig = RaftyUrlConfig.Get(raftyBasePath);

            _appendEntriesUrl = urlConfig.appendEntriesUrl;
            _requestVoteUrl = urlConfig.requestVoteUrl;
            _commandUrl = urlConfig.commandUrl;

            _serviceRegistry = serviceRegistry;
            _sendToSelfHandlers = new Dictionary<Type, Action<IMessage>>
            {
                {typeof(BecomeCandidate), x => _server.Receive((BecomeCandidate) x)},
                {typeof(SendHeartbeat), x => _server.Receive((SendHeartbeat) x)},
                {typeof(Command), x => _server.Receive((Command) x)},
            };
        }

        public async Task<AppendEntriesResponse> Send(AppendEntries appendEntries)
        {
            try
            {
                var serverToSendMessageTo = _serviceRegistry.Get(RaftyServiceDiscoveryName.Get()).First(x => x.Id == appendEntries.FollowerId);
                var json = JsonConvert.SerializeObject(appendEntries);
                var httpContent = new StringContent(json);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using (var httpClient = new HttpClient())
                {
                    httpClient.BaseAddress = serverToSendMessageTo.Location;
                    var response = await httpClient.PostAsync(_appendEntriesUrl, httpContent);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    var appendEntriesResponse = JsonConvert.DeserializeObject<AppendEntriesResponse>(content);
                    return appendEntriesResponse;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                throw;
            }
        }

        public async Task<RequestVoteResponse> Send(RequestVote requestVote)
        {
            try
            {
                var serverToSendMessageTo = _serviceRegistry.Get(RaftyServiceDiscoveryName.Get()).First(x => x.Id == requestVote.VoterId);
                var json = JsonConvert.SerializeObject(requestVote);
                var httpContent = new StringContent(json);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using (var httpClient = new HttpClient())
                {
                    httpClient.BaseAddress = serverToSendMessageTo.Location;
                    var response = await httpClient.PostAsync(_requestVoteUrl, httpContent);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    var requestVoteResponse = JsonConvert.DeserializeObject<RequestVoteResponse>(content);
                    return requestVoteResponse;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                throw;
            }
        }

        public async Task<SendLeaderCommandResponse> Send(ICommand command, Guid leaderId)
        {
            try
            {
                var serverToSendMessageTo = _serviceRegistry.Get(RaftyServiceDiscoveryName.Get()).First(x => x.Id == leaderId);
                var json = JsonConvert.SerializeObject(command);
                var httpContent = new StringContent(json);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using (var httpClient = new HttpClient())
                {
                    httpClient.BaseAddress = serverToSendMessageTo.Location;
                    var response = await httpClient.PostAsync(_commandUrl, httpContent);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    var requestVoteResponse = JsonConvert.DeserializeObject<SendLeaderCommandResponse>(content);
                    return requestVoteResponse;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                throw;
            }
        }

        public void Send(SendToSelf message)
        {
            if (_stopSendingMessages)
            {
                return;
            }

            try
            {
                _sleeping = true;
                Thread.Sleep(message.DelaySeconds * 1000);
                _sleeping = false;
                var typeOfMessage = message.Message.GetType();
                var handler = _sendToSelfHandlers[typeOfMessage];
                handler(message.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        public void Stop()
        {
            while (_sleeping)
            {
                
            }
            _stopSendingMessages = true;
        }

        public void SetServer(Server server)
        {
            _server = server;
        }


    }
}