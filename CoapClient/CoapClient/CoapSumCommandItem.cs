namespace CoapClient
{
    public class CoapSumCommandItem : AbstractCoapCommand, ICoapCommand
    {
        public CoapSumCommandItem(VariableWriteItem[] targetVariables, IAck ackPredicate = null)
            : this(targetVariables, ackPredicate, 2){}
        
        public CoapSumCommandItem(VariableWriteItem[] targetVarItems, IAck ackPredicate, int cycleTime)
        {
            this.CycleTime = cycleTime;
            this.WriteItemArray = targetVarItems;
            if (ackPredicate != null)
            {
                this.AckPredicate = ackPredicate;
                this.AckRequired = true;
                this.AckVarName = ackPredicate.AckVarName;
            }
        }
        
        public VariableWriteItem[] WriteItemArray { get; }
    
    }
}