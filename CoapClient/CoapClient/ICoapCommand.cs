namespace CoapClient
{
    public interface ICoapCommand
    {
        bool AckRequired { get; }
    
        int CycleTime { get; }
    
        Type ValueType { get; }
    
        int MaxDelayTime { get; }
    
        string AckVarName { get; }
    
        TimeSpan? Timeout { get; }

        bool ValidateAck(byte[] rawAckData);
    
        object GetResult(byte[] rawData);
    }
}