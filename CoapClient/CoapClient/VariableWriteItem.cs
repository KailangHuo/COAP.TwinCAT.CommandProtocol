namespace CoapClient
{
    public class VariableWriteItem
    {
        public VariableWriteItem()
        {
            
        }

        public VariableWriteItem(string varName, object value)
        {
            this.VarName = varName;
            this.Value = value;
        }
        
        public string VarName;
        
        public object Value;
    }
}