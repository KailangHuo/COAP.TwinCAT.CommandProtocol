using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace CoapClient
{
    internal class ThinAdsServer : ICommandDispatcher
    {
        public ThinAdsServer()
        {
            this.CommandHandler = new CommandHandler();
            this.CommandHandler.HandShakeTriggered += CommandHandlerOnHandShakeTriggered;
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

        private AdsSession? Session;

        private AdsConnection? Connection;

        private string? ConnectedNetId;

        private int ConnectedPort;

        private ISymbolCollection<ISymbol> SymbolCollection = null!;

        private CommandHandler CommandHandler;

        private AdsConnection RequireConnection()
        {
            AdsConnection? connection = this.Connection;
            if (connection == null)
                throw new CoapNotConnectedException();

            return connection;
        }

        public void Connect(string amsNetId, int port)
        {
            try
            {
                bool sameTarget = this.Session != null
                                  && this.ConnectedNetId == amsNetId
                                  && this.ConnectedPort == port;

                if (sameTarget)
                {
                    // AdsConnection.ConnectAsync returns early when already connected, while
                    // AdsClient.Connect always re-established. Disconnect first to keep the
                    // previous "Connect always re-establishes" behaviour.
                    if (this.Session!.IsConnected)
                        this.Session.Disconnect();

                    this.Session.Connect();
                }
                else
                {
                    this.DisposeSession();

                    SessionSettings settings = SessionSettings.Default;
                    // ResurrectionTime is shared with the session's FailFast window: after a
                    // recognised loss, calls fail fast for this long instead of waiting for the
                    // ADS timeout. Keep the ADS.NET default (21s) so the failure latency matches
                    // the previous AdsClient path. Within that window the session may also try a
                    // handle-only resurrection; it never reconnects the transport or re-registers
                    // notifications.
                    settings.ResurrectionTime = SessionSettings.DefaultResurrectionTime;

                    this.Session = new AdsSession(amsNetId, port, settings);
                    this.Session.Connect();
                    this.ConnectedNetId = amsNetId;
                    this.ConnectedPort = port;
                }

                AdsConnection? connection = this.Session!.Connection;
                if (connection == null)
                    throw new CoapNotConnectedException();

                this.Connection = connection;

                // Connect can be called more than once; subscribe idempotently.
                connection.AdsNotification -= ConnectionOnAdsNotification;
                connection.AdsNotification += ConnectionOnAdsNotification;

                // Kept from the previous implementation: this call is what verifies that the
                // target port is actually reachable (IsConnected only reflects the local port).
                connection.ReadState();

                ISymbolLoader loader = SymbolLoaderFactory.Create(connection, SymbolLoaderSettings.Default);
                this.SymbolCollection = loader.Symbols;
            }
            catch (Exception e)
            {
                CoapLog.Error("Connect failed.", e);
                throw;
            }
        }

        private void DisposeSession()
        {
            AdsConnection? connection = this.Connection;
            if (connection != null)
                connection.AdsNotification -= ConnectionOnAdsNotification;

            this.Connection = null;

            AdsSession? session = this.Session;
            this.Session = null;
            this.ConnectedNetId = null;
            this.ConnectedPort = 0;
            this.SymbolCollection = null!;

            if (session != null)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception e)
                {
                    CoapLog.Error("Disposing the previous ADS session failed.", e);
                }
            }
        }

        /// <summary>
        /// Guards every symbol-table lookup. Kept in one place so the "not connected" check cannot
        /// drift between callers again — previously <see cref="VariableExists"/> had it while
        /// <see cref="GetSymbol"/> did not, so reading a variable on an unconnected client surfaced
        /// as a bare <see cref="NullReferenceException"/> instead of a meaningful error.
        /// </summary>
        private void RequireSymbolTable()
        {
            if (this.SymbolCollection == null)
                throw new CoapNotConnectedException();
        }

        public ISymbol GetSymbol(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                throw new ArgumentException("Variable name is null or empty.", nameof(varName));

            this.RequireSymbolTable();

            if (!SymbolCollection.Contains(varName))
                throw new CoapSymbolNotFoundException(varName);

            ISymbol symbol = SymbolCollection[varName];
            return symbol;
        }

        /// <summary>
        /// Checks the locally cached symbol table for the given instance path.
        /// No ADS round trip is performed.
        /// </summary>
        public bool VariableExists(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                throw new ArgumentException("Variable name is null or empty.", nameof(varName));

            this.RequireSymbolTable();

            return this.SymbolCollection.Contains(varName);
        }

        /// <summary>
        /// Looks up a symbol without throwing when the variable is unknown.
        /// <para>
        /// Companion to <see cref="VariableExists"/> and <see cref="GetSymbol"/>: same local
        /// symbol-table lookup, no ADS round trip. Use this when the caller wants the symbol's
        /// metadata - declared type, byte size, members - rather than just its existence.
        /// </para>
        /// </summary>
        /// <returns>The symbol, or <c>null</c> when the name is not in the symbol table.</returns>
        public ISymbol? FindSymbol(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                throw new ArgumentException("Variable name is null or empty.", nameof(varName));

            this.RequireSymbolTable();

            return this.SymbolCollection.TryGetInstance(varName, out ISymbol? symbol) ? symbol : null;
        }

        public void SendCommand(VariableWriteItem variableWriteItem)
        {
            string varName = variableWriteItem.VarName;
            object newVal = variableWriteItem.Value;
            ISymbol symbol = this.GetSymbol(varName);
            CoapLog.Info($"Write Var: {varName}, {newVal}");
            this.RequireConnection().WriteValue(symbol, newVal);
        }

        public void SendCommand(VariableWriteItem[] variableWriteItems)
        {
            // 不可有相同的symbol，否则会被当成一个symbol写入，导致写入失败
            HashSet<ISymbol> symbolSet = new HashSet<ISymbol>();
            List<ISymbol> symbolList = new List<ISymbol>();
            List<object> valueList = new List<object>();
            List<string> logMappings = new List<string>();
            foreach (var item in variableWriteItems)
            {
                string varName = item.VarName;
                object varValue = item.Value;
                ISymbol symbolInfo = this.GetSymbol(varName);
                symbolSet.Add(symbolInfo);
                symbolList.Add(symbolInfo);
                valueList.Add(varValue);
                logMappings.Add($"{varName}={varValue}");
            }

            if (symbolSet.Count != symbolList.Count)
            {
                throw new InvalidOperationException(
                    "Duplicate symbols found in variable write items. Each variable must be unique."
                    );
            }

            SymbolCollection symbolCollection = new SymbolCollection();
            symbolCollection.AddRange(symbolList);
            SumSymbolWrite sumSymbolWrite = new SumSymbolWrite(this.RequireConnection(), symbolCollection);
            CoapLog.Info($"SumSymbolWrite mapping sequence: {string.Join(", ", logMappings)}");
            sumSymbolWrite.Write(valueList.ToArray());
        }

        public object GetVariableValue(string varName)
        {
            ISymbol symbol = this.GetSymbol(varName);
            return this.RequireConnection().ReadValue(symbol);
        }

        public object[] GetSumVariableValue(string[] varNames)
        {
            if (varNames == null)
                throw new ArgumentNullException(nameof(varNames));
            if (varNames.Length == 0)
                return Array.Empty<object>();

            List<ISymbol> symbols = new List<ISymbol>(varNames.Length);
            foreach (string varName in varNames)
                symbols.Add(this.GetSymbol(varName));

            // Resolve name -> symbol -> ADS address, then read all in a single SUM_READ round-trip.
            // Read() returns the values in the same order as varNames.
            SumSymbolRead sumRead = new SumSymbolRead(this.RequireConnection(), symbols);
            return sumRead.Read();
        }

        public string GetServerStatus()
        {
            return this.RequireConnection().ReadState().AdsState.ToString();
        }

        internal Task<ResultValue<IAdsSymbol>> ReadSymbolAsync(string varName, CancellationToken cancellationToken)
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
            return this.RequireConnection().ReadAsync(indexGroup, indexOffset, readLength, cancellationToken);
        }

        internal void DeleteDeviceNotification(uint handle)
        {
            // Cleanup can run after a failed connect; there is nothing to delete in that case.
            this.Connection?.DeleteDeviceNotification(handle);
        }


        public event IAdsServer.CommandContextDelegate? OnHandShakeSuccess;
    }
}