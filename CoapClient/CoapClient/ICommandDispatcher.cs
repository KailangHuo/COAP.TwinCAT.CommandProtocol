namespace CoapClient
{
    internal interface ICommandDispatcher
    {
        void SendCommand(VariableWriteItem variableWriteItem);

        void SendCommand(VariableWriteItem[] variableWriteItems);
    }
}