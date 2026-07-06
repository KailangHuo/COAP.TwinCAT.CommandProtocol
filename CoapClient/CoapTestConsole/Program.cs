// See https://aka.ms/new-console-template for more information

using CoapClient;

SimpleCoapClient client = new SimpleCoapClient();
client.Connect("[YOUR AMS-NET-ID]", 851);
Console.WriteLine("Connected");

// replace with your own DI var
var diName = "PC_GVAR.DI_AT_POS";

// replace with your own DO var
VariableWriteItem doTrueItem = new VariableWriteItem();
doTrueItem.VarName = "PC_GVAR.DO_TO_POS";
doTrueItem.Value = true;
IAck doAck = new AckPredicate<bool>(diName, PredicateType.Equals, true);
CoapCommandItem doTrueCommand = new CoapCommandItem(doTrueItem,  doAck);

VariableWriteItem doFalseItem = new VariableWriteItem();
doFalseItem.VarName = "PC_GVAR.DO_TO_POS";
doFalseItem.Value = true;
IAck do_false_Ack = new AckPredicate<bool>(diName, PredicateType.Equals, true);
CoapCommandItem doFalseCommand = new CoapCommandItem(doFalseItem,  do_false_Ack);

// high-frequency control of a do-di vars
for (int i = 0; i < 1000; i++)
{
    Console.WriteLine($"Sending : {doTrueItem.VarName} = {doTrueItem.Value}");
    var result = (bool)await client.SendAndWaitAck(doTrueCommand);
    Console.WriteLine($"{diName} has modified to {result}");
    
    Console.WriteLine($"Sending : {doFalseItem.VarName} = {doFalseItem.Value}");
    var _result = (bool)await client.SendAndWaitAck(doFalseCommand);
    Console.WriteLine($"{diName} has modified to {_result}");
}
