namespace CoapClient
{
    /// <summary>
    /// Another command is already in flight on the same ACK variable, so this command
    /// could not take ownership of the ACK notification.
    /// <para>
    /// Derives from <see cref="InvalidOperationException"/> so that callers written against
    /// earlier versions (which threw <see cref="InvalidOperationException"/> here) keep catching it.
    /// </para>
    /// </summary>
    public sealed class CoapCommandCollisionException : InvalidOperationException
    {
        public CoapCommandCollisionException(string ackVarName)
            : base($"Command Collided! Ack: {ackVarName}")
        {
            this.AckVarName = ackVarName;
        }

        /// <summary>ACK variable that was already owned by another in-flight command.</summary>
        public string AckVarName { get; }
    }

    /// <summary>
    /// The variable name is not present in the loaded symbol table.
    /// <para>
    /// Derives from <see cref="InvalidOperationException"/> so that callers written against
    /// earlier versions (which threw <see cref="InvalidOperationException"/> here) keep catching it.
    /// </para>
    /// </summary>
    public sealed class CoapSymbolNotFoundException : InvalidOperationException
    {
        public CoapSymbolNotFoundException(string varName)
            : base($"Symbol '{varName}' was not found.")
        {
            this.VarName = varName;
        }

        /// <summary>Variable name that was not found in the symbol table.</summary>
        public string VarName { get; }
    }

    /// <summary>
    /// The ADS symbol table has not been loaded, which means <see cref="SimpleCoapClient.Connect"/>
    /// has not been called (or did not complete successfully).
    /// <para>
    /// Distinct from <see cref="CoapSymbolNotFoundException"/>: the variable may well exist in the
    /// PLC, we simply have no symbol table to look it up in yet. Callers should treat this as a
    /// retryable "not connected" condition rather than as a bad variable name.
    /// </para>
    /// <para>
    /// Derives from <see cref="InvalidOperationException"/> so that callers written against earlier
    /// versions keep catching it.
    /// </para>
    /// </summary>
    public sealed class CoapNotConnectedException : InvalidOperationException
    {
        public CoapNotConnectedException()
            : base("The ADS symbol table has not been loaded. Call Connect() before querying variables.")
        {
        }
    }

    /// <summary>
    /// The command reached its <b>Success</b> terminal — the ACK predicate was satisfied and the
    /// PLC-side action has therefore already taken effect — but the ACK value could not be turned
    /// into a result object (<see cref="ICoapCommand.GetResult"/> threw).
    /// <para>
    /// <b>Do not retry blindly.</b> The distinction matters: a caller that treats this as a command
    /// failure and re-issues the write will execute the PLC action a second time. Use
    /// <see cref="RawAckData"/> to inspect what the PLC actually reported.
    /// </para>
    /// <para>
    /// This is distinct from an ACK failure — here the command did succeed.
    /// </para>
    /// </summary>
    public sealed class CoapResultConversionException : Exception
    {
        public CoapResultConversionException(string ackVarName, byte[]? rawAckData, Exception innerException)
            : base($"Command succeeded (ACK satisfied) but the result could not be produced. " +
                   $"Ack: {ackVarName}. The PLC-side action has already taken effect - do not retry blindly.",
                   innerException)
        {
            this.AckVarName = ackVarName;
            this.RawAckData = rawAckData;
        }

        /// <summary>ACK variable whose predicate was satisfied.</summary>
        public string AckVarName { get; }

        /// <summary>Raw ACK bytes as received, or <c>null</c> when unavailable.</summary>
        public byte[]? RawAckData { get; }
    }
}
