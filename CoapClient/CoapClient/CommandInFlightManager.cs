using System.Collections.Concurrent;

namespace CoapClient
{
    internal class CommandInFlightManager
    {
        public CommandInFlightManager()
        {
            this.AckVarSet = new ConcurrentDictionary<string, bool>();
        }

        private ConcurrentDictionary<string, bool> AckVarSet;

        public bool RegisterCommand(string ackVarName)
        {
            return this.AckVarSet.TryAdd(ackVarName, true);
        }
    
        private void UnregisterAckVar(string ackVarName)
        {
            this.AckVarSet.TryRemove(ackVarName, out _);
        }

        public void UnregisterCommand(CommandContext commandContext)
        {
            this.UnregisterAckVar(commandContext.Command.AckVarName);
        }
    }
}