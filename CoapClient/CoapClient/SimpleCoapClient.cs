using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;

namespace CoapClient
{
    public class SimpleCoapClient
{
    public SimpleCoapClient()
    {
        ThinAdsServer thinAdsServer = new ThinAdsServer();
        this.ThinAdsServer = thinAdsServer;
        this.CommandSender = new CommandSender(thinAdsServer);
        this.CommandInFlightManager = new CommandInFlightManager();
        this.TimeoutScheduler = new TimeoutScheduler(CancellationToken.None);
        this.ThinAdsServer.OnHandShakeSuccess += ThinAdsServerOnOnHandShakeSuccess;
        
    }

    private void ThinAdsServerOnOnHandShakeSuccess(CommandContext commandContext)
    {
        this.CommandSender.EnqueueDispatchCommand(commandContext);
    }

    private ThinAdsServer ThinAdsServer;

    private CommandSender CommandSender;
    
    private CommandInFlightManager CommandInFlightManager;
    
    private TimeoutScheduler TimeoutScheduler;

    public void Connect(string amsNetId, int amsPort)
    {
        this.ThinAdsServer.Connect(amsNetId, amsPort);
    }

    public Task<object> SendAndWaitAck(ICoapCommand command, TimeSpan? timeSpan = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<object>(cancellationToken);
            }

            CoapLog.Info($"<{command.AckVarName}> Enter SendAndWaitAckAsync");
            TimeSpan? effectiveTimeout = command.AckRequired ? timeSpan ?? command.Timeout : null;

            CommandContext commandContext = new CommandContext()
            {
                Command = command,
                TimeSpan = effectiveTimeout,
                UnregisterCommandAction = this.CommandInFlightManager.UnregisterCommand,
                DeleteNotificationHandleAction = this.ThinAdsServer.AdsClient.DeleteDeviceNotification,
                TimeoutAckReadAction = this.ReadAckBytesAsync,
            };

            CoapLog.Info($"<{command.AckVarName}> CommandContext created");

            if (command.AckRequired && effectiveTimeout != null)
            {
                this.TimeoutScheduler.ScheduleTimeout(commandContext);
            }

            commandContext.RegisterCancellation(cancellationToken);

            //this.TryWriteCommandContext(commandContext);
            _ = this.RouteCommand(commandContext);
            return commandContext.AckTaskSource.Task;
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            throw;
        }
    }
    
    private async Task RouteCommand(CommandContext commandContext)
    {
        try
        {
            if (!commandContext.Command.AckRequired)
            {
                this.CommandSender.EnqueueDispatchCommand(commandContext);
                return;
            }
        
            bool registered = this.CommandInFlightManager.RegisterCommand(commandContext.Command.AckVarName);
            if (!registered)
            {
                CoapLog.Error($"<{commandContext.Command.AckVarName}> Command Collides!");
                commandContext.CommandCollides();
                return;
            }

            if (commandContext.IsFinished())
            {
                commandContext.TryCleanupResources();
                return;
            }
        
            //ISymbol ackSymbol = this.ThinAdsServer.GetSymbol()
            ResultValue<IAdsSymbol> symbolResult = await this.ThinAdsServer.AdsClient.ReadSymbolAsync(commandContext.Command.AckVarName, 
                CancellationToken.None);
            IAdsSymbol symbol = symbolResult.Value;
            commandContext.AckSymbol = symbol;
            CoapLog.Info($"<{commandContext.Command.AckVarName}> AdsSymbol Acquired!");
        
            NotificationSettings settings = new NotificationSettings(
                AdsTransMode.OnChange, 
                commandContext.Command.CycleTime, 
                commandContext.Command.MaxDelayTime);
        
            // 握手
            ResultHandle handle = await this.ThinAdsServer.AdsClient.AddDeviceNotificationAsync(
                commandContext.Command.AckVarName,
                symbol.ByteSize,
                settings,
                commandContext,
                default
                );
        
            commandContext.SetNotificationHandle(handle.Handle);
            if (commandContext.TryHandShake())
            {
                CoapLog.Info($"<{commandContext.Command.AckVarName}> Hand-Shack by Client-Read!");
                byte[] buffer = await this.ReadAckBytesAsync(commandContext);
                commandContext.SetValue(buffer);
                bool commandValidated = commandContext.TryValidCommandOnServerRecall();
                if (!commandValidated) this.CommandSender.EnqueueDispatchCommand(commandContext);
            }
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            commandContext.TrySetBusinessResult(e);
        }
       
    }

    private async Task<byte[]> ReadAckBytesAsync(CommandContext commandContext)
    {
        IAdsSymbol symbol = commandContext.AckSymbol;
        byte[] buffer = new byte[symbol.ByteSize];
        await this.ThinAdsServer.AdsClient.ReadAsync(
            symbol.IndexGroup,
            symbol.IndexOffset,
            buffer.AsMemory(),
            CancellationToken.None);

        return buffer;
    }
    
    public object GetVariableValue(string varName)
    {
        try
        {
            return this.ThinAdsServer.GetVariableValue(varName);
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            throw;
        }

    }

    /// <summary>
    /// Reads several PLC variables in a single ADS sum read.
    /// The returned array is aligned with <paramref name="varNames"/> in the same order.
    /// </summary>
    public object[] GetSumVariableValue(string[] varNames)
    {
        try
        {
            return this.ThinAdsServer.GetSumVariableValue(varNames);
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            throw;
        }
    }

    public string GetServerStatusString()
    {
        return this.ThinAdsServer.GetServerStatus();
    }
    
}
}