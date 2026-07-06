using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace CoapClient
{
    internal interface IAdsServer
    {
        public delegate void CommandContextDelegate(CommandContext commandContext);
    
        AdsClient AdsClient { get; }
    
        ISymbol GetSymbol(string varName);
    
        uint AddDeviceNotification(string varName, NotificationSettings notiSettings, object userData);
    
        void Connect(string amsNetId, int amsPort);
    
        void DeleteDeviceNotification(uint handleIndex);
    
        /*void SendCommand(VariableWriteItem variableWriteItem);

        void SendCommand(VariableWriteItem[] variableWriteItems);*/

        object GetVariableValue(string varName);

        string GetServerStatus();

        event CommandContextDelegate OnHandShakeSuccess;

    }
}