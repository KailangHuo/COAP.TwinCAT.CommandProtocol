using System.Collections.Concurrent;

namespace CoapClient
{
    internal class CommandInFlightManager
    {
        public CommandInFlightManager()
        {
            this.AckVarContexts = new ConcurrentDictionary<string, CommandContext>();
        }

        private readonly ConcurrentDictionary<string, CommandContext> AckVarContexts;

        private int Closed;

        /// <summary>
        /// Registers the context for its ACK variable. A stale context (older generation) may be
        /// replaced by a newer one; a context from the same generation collides.
        /// </summary>
        public bool RegisterCommand(CommandContext commandContext)
        {
            if (Volatile.Read(ref this.Closed) != 0)
                return false;

            string ackVarName = commandContext.Command.AckVarName;

            while (true)
            {
                if (this.AckVarContexts.TryAdd(ackVarName, commandContext))
                    return true;

                if (!this.AckVarContexts.TryGetValue(ackVarName, out CommandContext? existing))
                    continue;

                // A RouteCommand that read the previous snapshot can register after a loss.
                // Only replace an older epoch; same-generation means a real collision.
                if (existing.Generation < commandContext.Generation
                    && this.AckVarContexts.TryUpdate(ackVarName, commandContext, existing))
                {
                    return true;
                }

                return false;
            }
        }

        public void UnregisterCommand(CommandContext commandContext)
        {
            string ackVarName = commandContext.Command.AckVarName;

            // Only the owner may remove the entry; a newer epoch may already own it.
            if (this.AckVarContexts.TryGetValue(ackVarName, out CommandContext? existing)
                && ReferenceEquals(existing, commandContext))
            {
                this.AckVarContexts.TryRemove(ackVarName, out _);
            }
        }

        /// <summary>Releases all ACK variable claims after a connection loss.</summary>
        public void Clear()
        {
            this.AckVarContexts.Clear();
        }

        /// <summary>
        /// Stops accepting registrations and faults every in-flight command. Used by Dispose.
        /// </summary>
        public void Close(Exception error)
        {
            Interlocked.Exchange(ref this.Closed, 1);

            foreach (CommandContext commandContext in this.AckVarContexts.Values)
            {
                commandContext.TrySetBusinessResult(error);
            }

            this.AckVarContexts.Clear();
        }
    }
}
