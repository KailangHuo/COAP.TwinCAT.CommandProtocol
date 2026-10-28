using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace CoapClient
{
    /// <summary>
    /// Connection state exposed to callers. <see cref="Connected"/> means the transport,
    /// the symbol table and the PLC Run state are all ready; symbols may be used and new
    /// commands may be submitted.
    /// </summary>
    public enum CoapConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Recovering,
        Disposed,
    }

    /// <summary>
    /// Immutable view of the connection at one point in time. State, symbol table, generation,
    /// PLC state and the internal ADS connection are published together, so a reader can never
    /// observe a new state paired with an old symbol table.
    /// </summary>
    public sealed class CoapConnectionSnapshot
    {
        private CoapConnectionSnapshot(
            CoapConnectionState state,
            ISymbolCollection<ISymbol>? symbols,
            long generation,
            AdsState? plcState,
            Exception? lastError,
            AdsConnection? connection)
        {
            this.State = state;
            this.Symbols = symbols;
            this.Generation = generation;
            this.PlcState = plcState;
            this.LastError = lastError;
            this.Connection = connection;
        }

        public CoapConnectionState State { get; }

        /// <summary>Non-null only while <see cref="State"/> is <see cref="CoapConnectionState.Connected"/>.</summary>
        public ISymbolCollection<ISymbol>? Symbols { get; }

        /// <summary>Increments whenever an existing connected epoch is invalidated or a new one starts.</summary>
        public long Generation { get; }

        /// <summary>Last known PLC state, or null when the transport is unreachable.</summary>
        public AdsState? PlcState { get; }

        /// <summary>Last connection error, primarily useful while <see cref="State"/> is Recovering.</summary>
        public Exception? LastError { get; }

        internal AdsConnection? Connection { get; }

        internal static CoapConnectionSnapshot Disconnected(long generation, Exception? error = null)
        {
            return new CoapConnectionSnapshot(
                CoapConnectionState.Disconnected, null, generation, null, error, null);
        }

        internal static CoapConnectionSnapshot Connecting(long generation)
        {
            return new CoapConnectionSnapshot(
                CoapConnectionState.Connecting, null, generation, null, null, null);
        }

        internal static CoapConnectionSnapshot Recovering(
            long generation,
            AdsState? plcState,
            Exception? error,
            AdsConnection? connection)
        {
            return new CoapConnectionSnapshot(
                CoapConnectionState.Recovering, null, generation, plcState, error, connection);
        }

        internal static CoapConnectionSnapshot Connected(
            long generation,
            ISymbolCollection<ISymbol> symbols,
            AdsConnection connection,
            AdsState plcState)
        {
            ArgumentNullException.ThrowIfNull(symbols);
            ArgumentNullException.ThrowIfNull(connection);

            return new CoapConnectionSnapshot(
                CoapConnectionState.Connected, symbols, generation, plcState, null, connection);
        }

        internal static CoapConnectionSnapshot Disposed(long generation)
        {
            return new CoapConnectionSnapshot(
                CoapConnectionState.Disposed, null, generation, null, null, null);
        }
    }

    public sealed class CoapConnectionStateChangedEventArgs : EventArgs
    {
        public CoapConnectionStateChangedEventArgs(
            CoapConnectionSnapshot previousSnapshot,
            CoapConnectionSnapshot snapshot)
        {
            this.PreviousSnapshot = previousSnapshot;
            this.Snapshot = snapshot;
        }

        public CoapConnectionSnapshot PreviousSnapshot { get; }

        public CoapConnectionSnapshot Snapshot { get; }

        public CoapConnectionState OldState => this.PreviousSnapshot.State;

        public CoapConnectionState NewState => this.Snapshot.State;

        public long Generation => this.Snapshot.Generation;

        public AdsState? PlcState => this.Snapshot.PlcState;

        public Exception? Exception => this.Snapshot.LastError;
    }
}
