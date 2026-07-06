namespace CoapClient
{
    public interface IAck
    {
        bool IsMatched(byte[] tAckValue);
    
        string AckVarName { get; }

        object GetResultWithType(byte[] rawData);
    }
}