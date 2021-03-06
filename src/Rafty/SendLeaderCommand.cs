using System;
using Newtonsoft.Json;

namespace Rafty
{
    public class SendLeaderCommand : Message
    {
        public SendLeaderCommand(ICommand command, Guid leaderId)
        {
            Command = command;
            LeaderId = leaderId;
        }

        [JsonConstructor]
        public SendLeaderCommand(FakeCommand command, Guid leaderId)
        {
            Command = command;
            LeaderId = leaderId;
        }

        public ICommand Command { get; private set; }
        public Guid LeaderId { get; private set; }
    }
}