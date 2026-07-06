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
    
    
        public void EnqueueDispatchCommand(CommandContext commandContext)
        {
            this.CommandQueue.EnqueueItem(commandContext);
        }

        private void QueuedCommandAction(CommandContext commandContext)
        {
            try
            {
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