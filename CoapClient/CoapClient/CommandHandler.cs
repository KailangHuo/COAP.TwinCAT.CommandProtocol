using TwinCAT.Ads;

namespace CoapClient
{
    internal class CommandHandler
    {
        public CommandHandler()
        {
            this.NotificationQueue = new SingleConsumerQueue<INotification>(OnNotificationReceived);
        }

        private SingleConsumerQueue<INotification> NotificationQueue;

        public bool EnqueueNotification(INotification iNotification)
        {
            return this.NotificationQueue.EnqueueItem(iNotification);
        }

        public void Stop(TimeSpan timeout)
        {
            this.NotificationQueue.Stop(timeout, null);
        }
    
        private void OnNotificationReceived(INotification e)
        {
            CoapLog.Info($"Handle Ads-Notification Command! handle : {e.Handle}");
            try
            {
                if (e.UserData == null) throw new ArgumentNullException($"notification.UserData is NULL!");
                CommandContext commandContext = (CommandContext)e.UserData;

                // Do not let a notification from an old connection mutate a command that
                // belongs to a newer epoch.
                if (!commandContext.IsGenerationCurrent())
                {
                    CoapLog.Info($"Dropping stale notification for <{commandContext.Command.AckVarName}>.");
                    return;
                }

                commandContext.SetNotificationHandle(e.Handle);
                if (commandContext.IsFinished())
                {
                    commandContext.TryCleanupNotificationHandle(e.Handle);
                    return;
                }
                commandContext.SetValue(e.Data.ToArray());
                bool commandValidated = commandContext.TryValidCommandOnServerRecall();
                if (commandValidated) CoapLog.Info($"ACK-ed! {commandContext.Command.AckVarName} : raw-{e.Data}");
                if (commandValidated) return;

                bool isHandShakeFrame = commandContext.TryHandShake();
                if (isHandShakeFrame) this.HandShakeTriggered?.Invoke(this, commandContext);
            }
            catch (Exception ex)
            {
                if (e.UserData is CommandContext commandContext)
                {
                    commandContext.TrySetBusinessResult(ex);
                }
                CoapLog.Error("Errored! " + ex.Message);
            }
        }

        public event EventHandler<CommandContext> HandShakeTriggered;



    }
}