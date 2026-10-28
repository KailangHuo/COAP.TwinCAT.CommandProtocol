namespace CoapClient
{
    internal class CommandSender 
    {
        public CommandSender(ICommandDispatcher commandDispatcher)
        {
            this.ICommandDispatcher = commandDispatcher;
            this.CommandQueue = new SingleConsumerQueue<CommandContext>(this.QueuedCommandAction);
        }
    
        private SingleConsumerQueue<CommandContext> CommandQueue { get; }

        private ICommandDispatcher ICommandDispatcher { get; }
    
    
        public bool EnqueueDispatchCommand(CommandContext commandContext)
        {
            return this.CommandQueue.EnqueueItem(commandContext);
        }

        public void Stop(TimeSpan timeout)
        {
            this.CommandQueue.Stop(
                timeout,
                context => context.TrySetBusinessResult(
                    new ObjectDisposedException(nameof(SimpleCoapClient))));
        }

        private void QueuedCommandAction(CommandContext commandContext)
        {
            try
            {
                if (!commandContext.IsGenerationCurrent())
                {
                    CoapLog.Info($"<{commandContext.Command.AckVarName}> Command dropped: connection generation changed.");
                    commandContext.TrySetBusinessResult(new CoapNotConnectedException());
                    return;
                }

                if (!commandContext.TryBeginDispatch())
                {
                    CoapLog.Info($"<{commandContext.Command.AckVarName}> Command dispatch skipped");
                    return;
                }

                var command = commandContext.Command;
                switch (command) {
                    case CoapCommandItem:
                        CoapCommandItem item = (CoapCommandItem)command;
                        this.ICommandDispatcher.SendCommand(item.WriteItem);
                        break;

                    case CoapSumCommandItem:
                        CoapSumCommandItem sumItem = (CoapSumCommandItem)command;
                        this.ICommandDispatcher.SendCommand(sumItem.WriteItemArray);
                        break;
                }
                CoapLog.Info($"<{commandContext.Command.AckVarName}> Command Dispatched! ");
                commandContext.OnDispatched();
            }
            catch (Exception e)
            {
                CoapLog.Error(e.ToString());
                commandContext.TrySetBusinessResult(e);
            }
        
        }
    }
}