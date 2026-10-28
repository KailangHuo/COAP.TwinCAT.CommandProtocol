using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

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

    /// <summary>
    /// Timeout applied to an ACK-required command when neither the <c>timeSpan</c> argument nor
    /// <see cref="ICoapCommand.Timeout"/> supplies one.
    /// <para>
    /// Without this fallback, omitting <c>timeSpan</c> on a command whose ACK never arrives would
    /// leave the returned task pending forever — the built-in command types
    /// (<see cref="CoapCommandItem"/>, <see cref="CoapSumCommandItem"/>) never populate
    /// <see cref="ICoapCommand.Timeout"/>, so the fallback chain previously ended in <c>null</c>.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromMinutes(1);

    public void Connect(string amsNetId, int amsPort)
    {
        this.ThinAdsServer.Connect(amsNetId, amsPort);
    }

    public Task<object> SendAndWaitAck(ICoapCommand command, TimeSpan? timeSpan = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (command.AckRequired && string.IsNullOrWhiteSpace(command.AckVarName))
            {
                throw new ArgumentException(
                    "Command requires an ACK but its AckVarName is null or empty.", nameof(command));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<object>(cancellationToken);
            }

            CoapLog.Info($"<{command.AckVarName}> Enter SendAndWaitAckAsync");
            TimeSpan? effectiveTimeout = command.AckRequired
                ? timeSpan ?? command.Timeout ?? DefaultAckTimeout
                : null;

            CommandContext commandContext = new CommandContext()
            {
                Command = command,
                TimeSpan = effectiveTimeout,
                UnregisterCommandAction = this.CommandInFlightManager.UnregisterCommand,
                DeleteNotificationHandleAction = this.ThinAdsServer.DeleteDeviceNotification,
                TimeoutAckReadAction = this.ReadAckBytesAsync,
            };

            CoapLog.Info($"<{command.AckVarName}> CommandContext created");

            // effectiveTimeout is never null here: the AckRequired branch above always resolves to
            // something, ending in DefaultAckTimeout. Every ACK-required command therefore has a
            // timeout scheduled and cannot hang forever.
            if (command.AckRequired)
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
            ResultValue<IAdsSymbol> symbolResult = await this.ThinAdsServer.ReadSymbolAsync(commandContext.Command.AckVarName,
                CancellationToken.None);

            // Without this guard a failed read leaves symbolResult.Value null, and the failure
            // surfaces later as a bare NullReferenceException from symbol.ByteSize / ReadAckBytesAsync.
            // The overwhelming cause of ReadSymbolAsync failing on a name is that the name is not in
            // the PLC symbol table, so it is reported as CoapSymbolNotFoundException. The underlying
            // ADS error code is logged here for cases where that assumption does not hold
            // (e.g. the connection went down).
            if (symbolResult.Failed || symbolResult.Value == null)
            {
                CoapLog.Error($"<{commandContext.Command.AckVarName}> ReadSymbolAsync failed. " +
                              $"AdsErrorCode: {symbolResult.ErrorCode}");
                throw new CoapSymbolNotFoundException(commandContext.Command.AckVarName);
            }

            IAdsSymbol symbol = symbolResult.Value;
            commandContext.AckSymbol = symbol;
            CoapLog.Info($"<{commandContext.Command.AckVarName}> AdsSymbol Acquired!");
        
            NotificationSettings settings = new NotificationSettings(
                AdsTransMode.OnChange, 
                commandContext.Command.CycleTime, 
                commandContext.Command.MaxDelayTime);
        
            // 握手
            ResultHandle handle = await this.ThinAdsServer.AddDeviceNotificationAsync(
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
        ResultReadBytes result = await this.ThinAdsServer.ReadAsync(
            symbol.IndexGroup,
            symbol.IndexOffset,
            buffer.Length,
            CancellationToken.None);

        // Preserve the previous Memory<byte> overload semantics: the caller always gets a
        // buffer of symbol.ByteSize, left zero-filled when the read fails.
        if (result.Succeeded)
        {
            int copyLength = Math.Min(result.Data.Length, buffer.Length);
            result.Data.Span[..copyLength].CopyTo(buffer);
        }

        return buffer;
    }
    
    /// <summary>
    /// Checks whether the given PLC variable exists in the locally cached symbol table.
    /// This is a local lookup only; no ADS round trip is performed.
    /// The cache is loaded by <see cref="Connect"/> and is not refreshed automatically,
    /// so re-connect after the PLC application is re-downloaded.
    /// </summary>
    /// <exception cref="ArgumentException">The variable name is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Not connected yet; the symbol table has not been loaded.</exception>
    public bool VariableExists(string varName)
    {
        try
        {
            return this.ThinAdsServer.VariableExists(varName);
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            throw;
        }
    }

    /// <summary>
    /// Looks up a PLC variable in the locally cached symbol table and returns its metadata -
    /// declared type name, type category, byte size and members. Local lookup only; no ADS round trip.
    /// <para>
    /// The returned <see cref="ISymbol"/> is a live reference into the symbol table, not a copy.
    /// Treat it as read-only. For the .NET equivalent of the PLC type, pass
    /// <c>symbol.DataType</c> to the TwinCAT ADS type resolver.
    /// </para>
    /// <para>
    /// The symbol table is loaded by <see cref="Connect"/> and is <b>not refreshed automatically</b>.
    /// After the PLC application is re-downloaded, the type information may be stale - and stale
    /// type information is more dangerous than a stale existence check, because it can make a caller
    /// parse correct data the wrong way. Re-connect before relying on it.
    /// </para>
    /// </summary>
    /// <returns>The symbol, or <c>null</c> when the variable is not part of the current PLC project.</returns>
    /// <exception cref="ArgumentException">The variable name is null, empty, or whitespace.</exception>
    /// <exception cref="CoapNotConnectedException">Not connected yet; the symbol table has not been loaded.</exception>
    public ISymbol? GetVariableSymbol(string varName)
    {
        try
        {
            return this.ThinAdsServer.FindSymbol(varName);
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            throw;
        }
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
        try
        {
            return this.ThinAdsServer.GetServerStatus();
        }
        catch (Exception e)
        {
            CoapLog.Error(e.ToString());
            throw;
        }
    }
    
}
}