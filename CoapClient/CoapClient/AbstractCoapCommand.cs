namespace CoapClient
{
    public abstract class AbstractCoapCommand : ICoapCommand
    {
        public AbstractCoapCommand()
        {
        
        }

        public bool AckRequired { get; protected set;}
    
        public IAck AckPredicate { get; protected set; }

        public int CycleTime { get; protected set;}

        public Type ValueType { get; }

        public int MaxDelayTime { get; protected set;}
    
        public string AckVarName { get; protected set;}
    
        public TimeSpan? Timeout { get; protected set;}

        public bool ValidateAck(byte[] rawAckByteArr)
        {
            if (!this.AckRequired) return true;
            return this.AckPredicate.IsMatched(rawAckByteArr);
        }

        public object GetResult(byte[] rawData)
        {
            if (!this.AckRequired) return true;
            return this.AckPredicate.GetResultWithType(rawData);
        }
    }
}