using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace CoapClient
{
    internal class ThinAdsServer : ICommandDispatcher, IDisposable
    {
        private const int ProbeIntervalMs = 1000;
        private const int ProbeTimeoutMs = 1000;
        private const int ReconnectAttemptTimeoutMs = 5000;
        private static readonly int[] BackoffMs = { 1000, 2000, 5000 };

        public ThinAdsServer(CommandInFlightManager commandInFlightManager, CancellationToken lifecycleToken)
        {
            this.CommandInFlightManager = commandInFlightManager;
            this.LifecycleToken = lifecycleToken;
            this.CommandHandler = new CommandHandler();
            this.CommandHandler.HandShakeTriggered += CommandHandlerOnHandShakeTriggered;
        }

        private readonly CommandInFlightManager CommandInFlightManager;

        private readonly CancellationToken LifecycleToken;

        private readonly CommandHandler CommandHandler;

        private readonly object StateLock = new();

        private readonly SemaphoreSlim ConnectGate = new(1, 1);

        private readonly SemaphoreSlim Wake = new(0);

        private CoapConnectionSnapshot _snapshot = CoapConnectionSnapshot.Disconnected(0);

        private AdsSession? Session;

        private AdsConnection? Connection;

        private string? ConnectedNetId;

        private int ConnectedPort;

        private Task? MonitorTask;

        private long Generation;

        private int BackoffIndex;

        private int Disposed;

        private int Completed;

        private int ManualConnectInProgress;

        private int MonitorStarted;

        internal CoapConnectionSnapshot Snapshot => Volatile.Read(ref this._snapshot);

        internal long CurrentGeneration => this.Snapshot.Generation;

        internal event EventHandler<CoapConnectionStateChangedEventArgs>? ConnectionStateChanged;

        internal bool IsGenerationCurrent(long generation)
        {
            CoapConnectionSnapshot snapshot = this.Snapshot;
            return snapshot.State == CoapConnectionState.Connected
                   && snapshot.Generation == generation;
        }

        internal bool TryGetConnectedGeneration(out long generation)
        {
            CoapConnectionSnapshot snapshot = this.Snapshot;
            if (snapshot.State != CoapConnectionState.Connected)
            {
                generation = 0;
                return false;
            }

            generation = snapshot.Generation;
            return true;
        }

        private void CommandHandlerOnHandShakeTriggered(object? sender, CommandContext e)
        {
            this.OnHandShakeSuccess?.Invoke(e);
        }

        private void ConnectionOnAdsNotification(object? sender, AdsNotificationEventArgs e)
        {
            CoapLog.Info("Ads-Notification received!");
            if (e.UserData == null) return;
            if (e.UserData is CommandContext) this.CommandHandler.EnqueueNotification(e);
            // TODO:Observer
        }

        public void Connect(string amsNetId, int port)
        {
            if (Volatile.Read(ref this.Disposed) != 0)
                throw new ObjectDisposedException(nameof(SimpleCoapClient));

            if (Volatile.Read(ref this.ManualConnectInProgress) != 0)
                throw new InvalidOperationException(
                    "Connect cannot be called re-entrantly from a connection state handler.");

            if (Volatile.Read(ref this.MonitorStarted) != 0)
            {
                if (this.ConnectedNetId == amsNetId && this.ConnectedPort == port)
                    return;

                throw new InvalidOperationException(
                    "This client is already connected to a different AMS target. " +
                    "Create a new SimpleCoapClient for that target.");
            }

            Interlocked.Exchange(ref this.ManualConnectInProgress, 1);
            CoapConnectionStateChangedEventArgs? pendingEvent = null;

            try
            {
                long generation = Interlocked.Increment(ref this.Generation);
                this.CommandInFlightManager.Clear();

                pendingEvent = this.SwapSnapshot(CoapConnectionSnapshot.Connecting(generation));
                this.Raise(pendingEvent);
                pendingEvent = null;

                this.ConnectGate.Wait();
                try
                {
                    if (Volatile.Read(ref this.Disposed) != 0)
                        throw new ObjectDisposedException(nameof(SimpleCoapClient));

                    this.DisposeSession();

                    SessionSettings settings = SessionSettings.Default;
                    // ResurrectionTime is shared with the session's FailFast window. Keeping the
                    // default (21s) preserves fast failure after a recognised loss.
                    settings.ResurrectionTime = SessionSettings.DefaultResurrectionTime;

                    this.Session = new AdsSession(amsNetId, port, settings);
                    this.Session.Connect();
                    this.ConnectedNetId = amsNetId;
                    this.ConnectedPort = port;

                    AdsConnection? connection = this.Session.Connection;
                    if (connection == null)
                        throw new CoapNotConnectedException();

                    this.Connection = connection;
                    this.AttachNotificationHandler(connection);

                    // Preserve the previous Connect semantics: this round trip is what verifies
                    // that the target port is reachable.
                    StateInfo state = connection.ReadState();
                    if (state.AdsState != AdsState.Run)
                        throw new CoapNotConnectedException();

                    ISymbolCollection<ISymbol> symbols = LoadSymbols(connection);
                    this.BackoffIndex = 0;

                    pendingEvent = this.SwapSnapshot(
                        CoapConnectionSnapshot.Connected(generation, symbols, connection, state.AdsState));
                }
                finally
                {
                    this.ConnectGate.Release();
                }

                this.StartMonitor();

                // A concurrent Dispose may already have published Disposed; do not emit a
                // stale Connected event after it.
                if (Volatile.Read(ref this.Disposed) == 0)
                    this.Raise(pendingEvent);

                pendingEvent = null;
            }
            catch (Exception e)
            {
                this.DisposeSession();

                long failureGeneration = Interlocked.Increment(ref this.Generation);
                pendingEvent = this.SwapSnapshot(
                    CoapConnectionSnapshot.Disconnected(failureGeneration, e));
                this.Raise(pendingEvent);

                CoapLog.Error("Connect failed.", e);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref this.ManualConnectInProgress, 0);
                this.Wake.Release();
            }
        }

        private void AttachNotificationHandler(AdsConnection connection)
        {
            connection.AdsNotification -= this.ConnectionOnAdsNotification;
            connection.AdsNotification += this.ConnectionOnAdsNotification;
        }

        internal void DetachConnection()
        {
            AdsConnection? connection = this.Connection;
            if (connection != null)
                connection.AdsNotification -= this.ConnectionOnAdsNotification;

            this.Connection = null;
        }

        private void DisposeSession()
        {
            this.DetachConnection();

            AdsSession? session = this.Session;
            this.Session = null;
            this.ConnectedNetId = null;
            this.ConnectedPort = 0;

            if (session != null)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception e)
                {
                    CoapLog.Error("Disposing the ADS session failed.", e);
                }
            }
        }

        private static ISymbolCollection<ISymbol> LoadSymbols(AdsConnection connection)
        {
            ISymbolLoader loader = SymbolLoaderFactory.Create(connection, SymbolLoaderSettings.Default);
            return loader.Symbols;
        }

        private void StartMonitor()
        {
            if (Volatile.Read(ref this.Disposed) != 0)
                return;

            if (Interlocked.CompareExchange(ref this.MonitorStarted, 1, 0) != 0)
                return;

            this.MonitorTask = Task.Run(() => this.MonitorLoopAsync(this.LifecycleToken));
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            int unknownCount = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (Volatile.Read(ref this.ManualConnectInProgress) != 0)
                    {
                        await this.WaitForWakeAsync(100, token).ConfigureAwait(false);
                        continue;
                    }

                    CoapConnectionSnapshot snapshot = this.Snapshot;

                    if (snapshot.State == CoapConnectionState.Connected)
                    {
                        bool acquired = await this.ConnectGate
                            .WaitAsync(ProbeIntervalMs, token)
                            .ConfigureAwait(false);

                        if (!acquired)
                            continue;

                        try
                        {
                            snapshot = this.Snapshot;
                            if (snapshot.State == CoapConnectionState.Connected
                                && snapshot.Connection != null)
                            {
                                ProbeResult probe = await this
                                    .ProbeAsync(snapshot.Connection, token)
                                    .ConfigureAwait(false);

                                this.HandleConnectedProbe(probe, ref unknownCount);
                            }
                        }
                        finally
                        {
                            this.ConnectGate.Release();
                        }

                        await this.WaitForWakeAsync(ProbeIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    int delay = BackoffMs[Math.Min(this.BackoffIndex, BackoffMs.Length - 1)];
                    this.BackoffIndex++;
                    await this.WaitForWakeAsync(delay, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested
                        || Volatile.Read(ref this.ManualConnectInProgress) != 0)
                    {
                        continue;
                    }

                    await this.ConnectGate.WaitAsync(token).ConfigureAwait(false);

                    try
                    {
                        await this.TryRecoverAsync(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        this.ConnectGate.Release();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    CoapLog.Error("Connection monitor iteration failed.", e);

                    if (Volatile.Read(ref this.ManualConnectInProgress) == 0)
                        this.UpdateRecovering(null, e);

                    try
                    {
                        await this.WaitForWakeAsync(ProbeIntervalMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }

        private async Task WaitForWakeAsync(int milliseconds, CancellationToken token)
        {
            await this.Wake.WaitAsync(milliseconds, token).ConfigureAwait(false);
        }

        private async Task<ProbeResult> ProbeAsync(
            AdsConnection connection,
            CancellationToken token)
        {
            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(ProbeTimeoutMs);

            try
            {
                ResultReadDeviceState result = await connection
                    .ReadStateAsync(cts.Token)
                    .ConfigureAwait(false);

                if (result.Succeeded)
                {
                    return result.State.AdsState == AdsState.Run
                        ? new ProbeResult(ProbeKind.Run, result.State.AdsState, null)
                        : new ProbeResult(ProbeKind.NotRunning, result.State.AdsState, null);
                }

                Exception error = new InvalidOperationException(
                    $"ADS ReadState failed: {result.ErrorCode}");

                return IsTransportError(result.ErrorCode)
                    ? new ProbeResult(ProbeKind.Unreachable, null, error)
                    : new ProbeResult(ProbeKind.Unknown, null, error);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new ProbeResult(ProbeKind.Unknown, null, null);
            }
            catch (Exception e)
            {
                return e is AdsException
                    ? new ProbeResult(ProbeKind.Unreachable, null, e)
                    : new ProbeResult(ProbeKind.Unknown, null, e);
            }
        }

        private static bool IsTransportError(AdsErrorCode errorCode)
        {
            // Values from AdsErrorCode: TargetPortNotFound 0x6, TargetMachineNotFound 0x7,
            // PortNotConnected 0xD, PortDisabled 0x12, RouterFragmentTimeout 0x50C,
            // ClientPortNotOpen 0x748.
            return errorCode switch
            {
                (AdsErrorCode)0x006 => true,
                (AdsErrorCode)0x007 => true,
                (AdsErrorCode)0x00D => true,
                (AdsErrorCode)0x012 => true,
                (AdsErrorCode)0x50C => true,
                (AdsErrorCode)0x748 => true,
                _ => false,
            };
        }

        private void HandleConnectedProbe(ProbeResult probe, ref int unknownCount)
        {
            switch (probe.Kind)
            {
                case ProbeKind.Run:
                    unknownCount = 0;
                    return;

                case ProbeKind.NotRunning:
                    unknownCount = 0;
                    this.TransitionToRecovering(probe.PlcState, null);
                    return;

                case ProbeKind.Unreachable:
                    unknownCount = 0;
                    this.TransitionToRecovering(null, probe.Error);
                    return;

                default:
                    unknownCount++;
                    if (unknownCount >= 2)
                    {
                        unknownCount = 0;
                        this.TransitionToRecovering(null, probe.Error);
                    }

                    return;
            }
        }

        private void TransitionToRecovering(AdsState? plcState, Exception? error)
        {
            long generation = Interlocked.Increment(ref this.Generation);
            this.CommandInFlightManager.Clear();
            this.BackoffIndex = 0;

            CoapConnectionSnapshot next = CoapConnectionSnapshot.Recovering(
                generation,
                plcState,
                error,
                this.Connection);

            CoapLog.Info($"Connection lost; entering Recovering (generation {generation}).");

            CoapConnectionStateChangedEventArgs? args = this.SwapSnapshot(next);
            this.Raise(args);
        }

        private void UpdateRecovering(AdsState? plcState, Exception? error)
        {
            CoapConnectionSnapshot current = this.Snapshot;

            if (current.State == CoapConnectionState.Disposed)
                return;

            if (current.State == CoapConnectionState.Connected)
            {
                this.CommandInFlightManager.Clear();
                this.BackoffIndex = 0;
            }

            long generation = current.State == CoapConnectionState.Recovering
                ? current.Generation
                : Interlocked.Increment(ref this.Generation);

            CoapConnectionSnapshot next = CoapConnectionSnapshot.Recovering(
                generation,
                plcState ?? current.PlcState,
                error,
                this.Connection);

            CoapConnectionStateChangedEventArgs? args = this.SwapSnapshot(next);
            this.Raise(args);
        }

        private async Task TryRecoverAsync(CancellationToken token)
        {
            CoapConnectionSnapshot snapshot = this.Snapshot;
            if (snapshot.State == CoapConnectionState.Disposed || token.IsCancellationRequested)
                return;

            // If the existing transport still answers, do not tear it down. This is the
            // PLC STOP/Config case: wait for Run, then reload symbols.
            AdsConnection? existing = snapshot.Connection ?? this.Connection;
            if (existing != null)
            {
                ProbeResult probe = await this.ProbeAsync(existing, token).ConfigureAwait(false);

                if (probe.Kind == ProbeKind.Run)
                {
                    this.PublishConnected(existing, probe.PlcState ?? AdsState.Run);
                    return;
                }

                if (probe.Kind == ProbeKind.NotRunning)
                {
                    this.UpdateRecovering(probe.PlcState, null);
                    return;
                }
            }

            using CancellationTokenSource attemptCts =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptCts.CancelAfter(ReconnectAttemptTimeoutMs);

            try
            {
                AdsConnection? connection = await this
                    .RecreateSessionAsync(attemptCts.Token)
                    .ConfigureAwait(false);

                if (connection == null)
                    throw new CoapNotConnectedException();

                ProbeResult probe = await this.ProbeAsync(connection, token).ConfigureAwait(false);

                if (probe.Kind == ProbeKind.Run)
                {
                    this.PublishConnected(connection, probe.PlcState ?? AdsState.Run);
                }
                else
                {
                    this.UpdateRecovering(probe.PlcState, probe.Error);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                CoapLog.Error("Reconnect attempt failed.", e);
                this.UpdateRecovering(null, e);
            }
        }

        /// <summary>
        /// Disposes the old session and creates a fresh one. Reusing the old AdsSession after a
        /// local disconnect proved unreliable; a fresh session reproduces the initial-connect
        /// path and gives the monitor a known-good transport.
        /// </summary>
        private async Task<AdsConnection?> RecreateSessionAsync(CancellationToken token)
        {
            string? netId = this.ConnectedNetId;
            int port = this.ConnectedPort;

            if (string.IsNullOrWhiteSpace(netId))
                return null;

            this.DisposeSession();

            // DisposeSession cleared the target; restore it so a failed attempt can be retried.
            this.ConnectedNetId = netId;
            this.ConnectedPort = port;

            AdsSession? session = null;

            try
            {
                SessionSettings settings = SessionSettings.Default;
                settings.ResurrectionTime = SessionSettings.DefaultResurrectionTime;

                session = new AdsSession(netId, port, settings);
                await session.ConnectAsync(token).ConfigureAwait(false);

                AdsConnection? connection = session.Connection;
                if (connection == null)
                    throw new CoapNotConnectedException();

                this.Session = session;
                this.Connection = connection;
                this.AttachNotificationHandler(connection);

                return connection;
            }
            catch
            {
                session?.Dispose();
                throw;
            }
        }

        private void PublishConnected(AdsConnection connection, AdsState plcState)
        {
            try
            {
                ISymbolCollection<ISymbol> symbols = LoadSymbols(connection);

                CoapConnectionSnapshot current = this.Snapshot;
                if (current.State == CoapConnectionState.Disposed)
                    return;

                this.Connection = connection;
                this.BackoffIndex = 0;

                CoapConnectionSnapshot next = CoapConnectionSnapshot.Connected(
                    current.Generation,
                    symbols,
                    connection,
                    plcState);

                CoapLog.Info($"Connection recovered (generation {current.Generation}).");

                CoapConnectionStateChangedEventArgs? args = this.SwapSnapshot(next);
                this.Raise(args);
            }
            catch (Exception e)
            {
                this.UpdateRecovering(plcState, e);
            }
        }

        private CoapConnectionStateChangedEventArgs? SwapSnapshot(CoapConnectionSnapshot next)
        {
            lock (this.StateLock)
            {
                if (Volatile.Read(ref this.Disposed) != 0
                    && next.State != CoapConnectionState.Disposed)
                {
                    return null;
                }

                CoapConnectionSnapshot previous = this._snapshot;
                Volatile.Write(ref this._snapshot, next);

                if (previous.State == next.State)
                    return null;

                return new CoapConnectionStateChangedEventArgs(previous, next);
            }
        }

        private void Raise(CoapConnectionStateChangedEventArgs? args)
        {
            if (args == null)
                return;

            EventHandler<CoapConnectionStateChangedEventArgs>? handlers =
                this.ConnectionStateChanged;

            if (handlers == null)
                return;

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<CoapConnectionStateChangedEventArgs>)handler)(this, args);
                }
                catch (Exception e)
                {
                    CoapLog.Error("ConnectionStateChanged handler failed.", e);
                }
            }
        }

        internal bool BeginDispose()
        {
            if (Interlocked.Exchange(ref this.Disposed, 1) != 0)
                return false;

            long generation = Interlocked.Increment(ref this.Generation);
            CoapConnectionStateChangedEventArgs? args =
                this.SwapSnapshot(CoapConnectionSnapshot.Disposed(generation));

            this.Raise(args);
            this.Wake.Release();
            return true;
        }

        internal void WaitForMonitor(TimeSpan timeout)
        {
            Task? monitorTask = this.MonitorTask;
            if (monitorTask == null)
                return;

            try
            {
                if (!monitorTask.Wait(timeout))
                    CoapLog.Error("Connection monitor did not stop within the dispose timeout.");
            }
            catch (AggregateException e)
            {
                CoapLog.Error("Connection monitor ended with an exception.", e);
            }
        }

        internal void CompleteDispose(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref this.Completed, 1) != 0)
                return;

            bool acquired = false;
            try
            {
                acquired = this.ConnectGate.Wait(timeout);
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                this.WaitForMonitor(timeout);
                this.CommandHandler.Stop(timeout);
                this.DetachConnection();
                this.DisposeSession();
            }
            finally
            {
                if (acquired)
                    this.ConnectGate.Release();
            }
        }

        public void Dispose()
        {
            if (!this.BeginDispose())
                return;

            this.CompleteDispose(TimeSpan.FromSeconds(2));
        }

        private CoapConnectionSnapshot RequireConnectedSnapshot()
        {
            CoapConnectionSnapshot snapshot = this.Snapshot;

            if (snapshot.State != CoapConnectionState.Connected
                || snapshot.Connection == null
                || snapshot.Symbols == null)
            {
                throw new CoapNotConnectedException();
            }

            return snapshot;
        }

        private AdsConnection RequireConnection()
        {
            return this.RequireConnectedSnapshot().Connection!;
        }

        private static ISymbol? FindSymbolIn(
            ISymbolCollection<ISymbol> symbols,
            string varName)
        {
            return symbols.TryGetInstance(varName, out ISymbol? symbol) ? symbol : null;
        }

        public ISymbol GetSymbol(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                throw new ArgumentException("Variable name is null or empty.", nameof(varName));

            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();

            return FindSymbolIn(snapshot.Symbols!, varName)
                   ?? throw new CoapSymbolNotFoundException(varName);
        }

        /// <summary>
        /// Checks the locally cached symbol table for the given instance path.
        /// No ADS round trip is performed.
        /// </summary>
        public bool VariableExists(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                throw new ArgumentException("Variable name is null or empty.", nameof(varName));

            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();
            return snapshot.Symbols!.Contains(varName);
        }

        /// <summary>
        /// Looks up a symbol without throwing when the variable is unknown.
        /// </summary>
        /// <returns>The symbol, or <c>null</c> when the name is not in the symbol table.</returns>
        public ISymbol? FindSymbol(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                throw new ArgumentException("Variable name is null or empty.", nameof(varName));

            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();
            return FindSymbolIn(snapshot.Symbols!, varName);
        }

        public void SendCommand(VariableWriteItem variableWriteItem)
        {
            string varName = variableWriteItem.VarName;
            object newVal = variableWriteItem.Value;

            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();
            ISymbol symbol = FindSymbolIn(snapshot.Symbols!, varName)
                             ?? throw new CoapSymbolNotFoundException(varName);

            CoapLog.Info($"Write Var: {varName}, {newVal}");
            snapshot.Connection!.WriteValue(symbol, newVal);
        }

        public void SendCommand(VariableWriteItem[] variableWriteItems)
        {
            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();

            // 不可有相同的symbol，否则会被当成一个symbol写入，导致写入失败
            HashSet<ISymbol> symbolSet = new HashSet<ISymbol>();
            List<ISymbol> symbolList = new List<ISymbol>();
            List<object> valueList = new List<object>();
            List<string> logMappings = new List<string>();

            foreach (var item in variableWriteItems)
            {
                string varName = item.VarName;
                object varValue = item.Value;
                ISymbol symbolInfo = FindSymbolIn(snapshot.Symbols!, varName)
                                     ?? throw new CoapSymbolNotFoundException(varName);

                symbolSet.Add(symbolInfo);
                symbolList.Add(symbolInfo);
                valueList.Add(varValue);
                logMappings.Add($"{varName}={varValue}");
            }

            if (symbolSet.Count != symbolList.Count)
            {
                throw new InvalidOperationException(
                    "Duplicate symbols found in variable write items. Each variable must be unique.");
            }

            SymbolCollection symbolCollection = new SymbolCollection();
            symbolCollection.AddRange(symbolList);

            SumSymbolWrite sumSymbolWrite = new SumSymbolWrite(snapshot.Connection!, symbolCollection);
            CoapLog.Info($"SumSymbolWrite mapping sequence: {string.Join(", ", logMappings)}");
            sumSymbolWrite.Write(valueList.ToArray());
        }

        public object GetVariableValue(string varName)
        {
            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();
            ISymbol symbol = FindSymbolIn(snapshot.Symbols!, varName)
                             ?? throw new CoapSymbolNotFoundException(varName);

            return snapshot.Connection!.ReadValue(symbol);
        }

        public object[] GetSumVariableValue(string[] varNames)
        {
            if (varNames == null)
                throw new ArgumentNullException(nameof(varNames));
            if (varNames.Length == 0)
                return Array.Empty<object>();

            CoapConnectionSnapshot snapshot = this.RequireConnectedSnapshot();

            List<ISymbol> symbols = new List<ISymbol>(varNames.Length);
            foreach (string varName in varNames)
            {
                symbols.Add(FindSymbolIn(snapshot.Symbols!, varName)
                            ?? throw new CoapSymbolNotFoundException(varName));
            }

            // Resolve name -> symbol -> ADS address, then read all in a single SUM_READ round-trip.
            // Read() returns the values in the same order as varNames.
            SumSymbolRead sumRead = new SumSymbolRead(snapshot.Connection!, symbols);
            return sumRead.Read();
        }

        public string GetServerStatus()
        {
            CoapConnectionSnapshot snapshot = this.Snapshot;

            if (snapshot.State == CoapConnectionState.Disposed)
                throw new ObjectDisposedException(nameof(SimpleCoapClient));

            // Prefer the last known PLC state; fall back to the COAP connection state when the
            // transport is unreachable (LastKnownPlcState is null).
            if (snapshot.PlcState.HasValue)
                return snapshot.PlcState.Value.ToString();

            return snapshot.State.ToString();
        }

        internal Task<ResultValue<IAdsSymbol>> ReadSymbolAsync(
            string varName,
            CancellationToken cancellationToken)
        {
            return this.RequireConnection().ReadSymbolAsync(varName, cancellationToken);
        }

        internal Task<ResultHandle> AddDeviceNotificationAsync(
            string varName,
            int dataSize,
            NotificationSettings settings,
            object userData,
            CancellationToken cancellationToken)
        {
            return this.RequireConnection().AddDeviceNotificationAsync(
                varName,
                dataSize,
                settings,
                userData,
                cancellationToken);
        }

        internal Task<ResultReadBytes> ReadAsync(
            uint indexGroup,
            uint indexOffset,
            int readLength,
            CancellationToken cancellationToken)
        {
            // The Memory<byte> overload on AdsConnection is marked obsolete; the length-based
            // overload is the supported async read.
            return this.RequireConnection()
                .ReadAsync(indexGroup, indexOffset, readLength, cancellationToken);
        }

        internal void DeleteDeviceNotification(uint handle, long generation)
        {
            CoapConnectionSnapshot snapshot = this.Snapshot;

            if (snapshot.State != CoapConnectionState.Connected
                || snapshot.Generation != generation
                || snapshot.Connection == null)
            {
                return;
            }

            snapshot.Connection.DeleteDeviceNotification(handle);
        }

        private enum ProbeKind
        {
            Run,
            NotRunning,
            Unreachable,
            Unknown,
        }

        private readonly record struct ProbeResult(
            ProbeKind Kind,
            AdsState? PlcState,
            Exception? Error);

        public event IAdsServer.CommandContextDelegate? OnHandShakeSuccess;
    }
}
