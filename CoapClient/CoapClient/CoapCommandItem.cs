namespace CoapClient
{
    public class CoapCommandItem : AbstractCoapCommand, ICoapCommand
    {
        public CoapCommandItem(VariableWriteItem targetVarWriteItemItem, IAck ackPredicate = null) 
            : this(targetVarWriteItemItem, ackPredicate, 2)
        {
           
        }
        
        public CoapCommandItem(VariableWriteItem targetVarWriteItemItem, IAck ackPredicate, int cycleTime)
        {
            this.CycleTime = cycleTime;
            this.WriteItem = targetVarWriteItemItem;
            if (ackPredicate != null)
            {
                this.AckRequired = true;
                this.AckPredicate = ackPredicate;
                this.AckVarName = ackPredicate.AckVarName;
            }
            
        }

        public VariableWriteItem WriteItem { get; }
    }
}