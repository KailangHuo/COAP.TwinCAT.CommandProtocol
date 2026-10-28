using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace CoapClient
{
    public class SimpleCoapClient : IDisposable
{
    public SimpleCoapClient()
    {
        this._lifecycleCts = new CancellationTokenSource();
        this.CommandInFlightManager = new CommandInFlightManager();
        this.ThinAdsServer = new ThinAdsServer(this.CommandInFlightManager, this._lifecycleCts.Token);
        this.CommandSender = new CommandSender(this.ThinAdsServer);
        this.TimeoutScheduler = new TimeoutScheduler(this._lifecycleCts.Token);
        this.ThinAdsServer.OnHandShakeSuccess += ThinAdsServerOnOnHandShakeSuccess;
        this.ThinAdsServer.ConnectionStateChanged += this.ThinAdsServerOnConnectionStateChanged;
    }

    private void ThinAdsServerOnOnHandShakeSuccess(CommandContext commandContext)
    {
        if (!this.CommandSender.EnqueueDispatchCommand(commandContext))
        {
            commandContext.TrySetBusinessResult(new ObjectDisposedException(nameof(SimpleCoapClient)));
        }
    }

    private readonly CancellationTokenSource _lifecycleCts;

    private ThinAdsServer ThinAdsServer;

    private CommandSender CommandSender;

    private CommandInFlightManager CommandInFlightManager;

    private TimeoutScheduler TimeoutScheduler;

    private int Disposed;

    public CoapConnectionState ConnectionState => this.ThinAdsServer.Snapshot.State;

    public CoapConnectionSnapshot ConnectionSnapshot => this.ThinAdsServer.Snapshot;

    public AdsState? LastKnownPlcState => this.ThinAdsServer.Snapshot.PlcState;

    private event EventHandler<CoapConnectionStateChangedEventArgs>? ConnectionStateChangedHandlers;

    public event EventHandler<CoapConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add => this.ConnectionStateChangedHandlers += value;
        remove => this.ConnectionStateChangedHandlers -= value;
    }

    private void ThinAdsServerOnConnectionStateChanged(
        object? sender,
        CoapConnectionStateChangedEventArgs e)
    {
        EventHandler<CoapConnectionStateChangedEventArgs>? handlers =
            this.ConnectionStateChangedHandlers;

        if (handlers == null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<CoapConnectionStateChangedEventArgs>)handler)(this, e);
            }
            catch (Exception ex)
            {
                CoapLog.Error("ConnectionStateChanged handler failed.", ex);
            }
        }
    }

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

            if (Volatile.Read(ref this.Disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SimpleCoapClient));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<object>(cancellationToken);
            }

            if (!this.ThinAdsServer.TryGetConnectedGeneration(out long generation))
            {
                return Task.FromException<object>(new CoapNotConnectedException());
            }

            CoapLog.Info($"<{command.AckVarName}> Enter SendAndWaitAckAsync");
            TimeSpan? effectiveTimeout = command.AckRequired
                ? timeSpan ?? command.Timeout ?? DefaultAckTimeout
                : null;

            CommandContext commandContext = new CommandContext()
            {
                Command = command,
                Generation = generation,
                IsGenerationValid = this.ThinAdsServer.IsGenerationCurrent,
                TimeSpan = effectiveTimeout,
                UnregisterCommandAction = this.CommandInFlightManager.UnregisterCommand,
                DeleteNotificationHandleAction =
                    handle => this.ThinAdsServer.DeleteDeviceNotification(handle, generation),
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
                if (!commandContext.IsGenerationCurrent())
                {
                    commandContext.TrySetBusinessResult(new CoapNotConnectedException());
                    return;
                }

                if (!this.CommandSender.EnqueueDispatchCommand(commandContext))
                {
                    commandContext.TrySetBusinessResult(
                        new ObjectDisposedException(nameof(SimpleCoapClient)));
                }

                return;
            }

            bool registered = this.CommandInFlightManager.RegisterCommand(commandContext);
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

            if (!commandContext.IsGenerationCurrent())
            {
                commandContext.TrySetBusinessResult(new CoapNotConnectedException());
                return;
            }

            //ISymbol ackSymbol = this.ThinAdsServer.GetSymbol()
            ResultValue<IAdsSymbol> symbolResult = await this.ThinAdsServer.ReadSymbolAsync(commandContext.Command.AckVarName,
                CancellationToken.None);

            if (!commandContext.IsGenerationCurrent())
            {
                commandContext.TrySetBusinessResult(new CoapNotConnectedException());
                return;
            }

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

                if (!commandContext.IsGenerationCurrent())
                    throw new CoapNotConnectedException();

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

            if (!commandContext.IsGenerationCurrent())
            {
                commandContext.TrySetBusinessResult(new CoapNotConnectedException());
                return;
            }

            commandContext.SetNotificationHandle(handle.Handle);
            if (commandContext.TryHandShake())
            {
                CoapLog.Info($"<{commandContext.Command.AckVarName}> Hand-Shack by Client-Read!");
                byte[] buffer = await this.ReadAckBytesAsync(commandContext);
                commandContext.SetValue(buffer);
                bool commandValidated = commandContext.TryValidCommandOnServerRecall();
                if (!commandValidated
                    && !this.CommandSender.EnqueueDispatchCommand(commandContext))
                {
                    commandContext.TrySetBusinessResult(
                        new ObjectDisposedException(nameof(SimpleCoapClient)));
                }
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

        if (!commandContext.IsGenerationCurrent())
            return buffer;

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
    /// The cache is loaded by <see cref="Connect"/> and rebuilt after a successful reconnect.
    /// It is not refreshed while the connection stays up (e.g. after an Online Change).
    /// </summary>
    /// <exception cref="ArgumentException">The variable name is null, empty, or whitespace.</exception>
    /// <exception cref="CoapNotConnectedException">The connection is not in the Connected state.</exception>
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
    /// The symbol table is loaded by <see cref="Connect"/> and rebuilt after a successful
    /// reconnect. References obtained from an older connection generation become stale after
    /// a reconnect - do not cache them across <see cref="ConnectionStateChanged"/> transitions.
    /// The table is not refreshed while the connection stays up (e.g. after an Online Change).
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.Disposed, 1) != 0)
            return;

        this.ThinAdsServer.BeginDispose();
        this.ThinAdsServer.ConnectionStateChanged -= this.ThinAdsServerOnConnectionStateChanged;
        this._lifecycleCts.Cancel();

        this.ThinAdsServer.WaitForMonitor(TimeSpan.FromSeconds(2));
        this.CommandSender.Stop(TimeSpan.FromSeconds(2));

        // Make old contexts' ADS cleanup a no-op before they are failed.
        this.ThinAdsServer.DetachConnection();

        this.CommandInFlightManager.Close(new ObjectDisposedException(nameof(SimpleCoapClient)));
        this.TimeoutScheduler.Stop(TimeSpan.FromSeconds(2));

        this.ThinAdsServer.CompleteDispose(TimeSpan.FromSeconds(2));

        this._lifecycleCts.Dispose();
    }
}
}