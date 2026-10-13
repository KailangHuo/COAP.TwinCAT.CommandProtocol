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
            AdsClient = new AdsClient();
            this.CommandHandler = new CommandHandler();
            this.CommandHandler.HandShakeTriggered += CommandHandlerOnHandShakeTriggered;

            this.AdsClient.AdsNotification += AdsClientOnAdsNotification;
        }

        private void CommandHandlerOnHandShakeTriggered(object? sender, CommandContext e)
        {
            this.OnHandShakeSuccess?.Invoke(e);
        }

        private void AdsClientOnAdsNotification(object? sender, AdsNotificationEventArgs e)
        {
            CoapLog.Info("Ads-Notification received!");
            if (e.UserData == null) return;
            if (e.UserData is CommandContext) this.CommandHandler.EnqueueNotification(e);
            // TODO:Observer
        }

        public AdsClient AdsClient { get; }

        private ISymbolCollection<ISymbol> SymbolCollection;

        private CommandHandler CommandHandler;

        public void Connect(string amsNetId, int port)
        {
            try
            {
                this.AdsClient.Connect(amsNetId, port);
                StateInfo info = this.AdsClient.ReadState();
                ISymbolLoader loader = SymbolLoaderFactory.Create(this.AdsClient, SymbolLoaderSettings.Default);
                this.SymbolCollection = loader.Symbols;
            }
            catch (Exception e)
            {
                throw;
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

            return this.SymbolCollection.TryGetInstance(varName, out ISymbol symbol) ? symbol : null;
        }

        public void SendCommand(VariableWriteItem variableWriteItem)
        {
            string varName = variableWriteItem.VarName;
            object newVal = variableWriteItem.Value;
            ISymbol symbol = this.GetSymbol(varName);
            CoapLog.Info($"Write Var: {varName}, {newVal}");
            this.AdsClient.WriteValue(symbol, newVal);
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
            SumSymbolWrite sumSymbolWrite = new SumSymbolWrite(this.AdsClient, symbolCollection);
            CoapLog.Info($"SumSymbolWrite mapping sequence: {string.Join(", ", logMappings)}");
            sumSymbolWrite.Write(valueList.ToArray());
        }

        public object GetVariableValue(string varName)
        {
            ISymbol symbol = this.GetSymbol(varName);
            return this.AdsClient.ReadValue(symbol);
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
            SumSymbolRead sumRead = new SumSymbolRead(this.AdsClient, symbols);
            return sumRead.Read();
        }

        public string GetServerStatus()
        {
            return this.AdsClient.ReadState().AdsState.ToString();
        }


        public event IAdsServer.CommandContextDelegate? OnHandShakeSuccess;
    }
}