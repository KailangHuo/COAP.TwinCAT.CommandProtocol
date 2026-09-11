// See https://aka.ms/new-console-template for more information

using CoapClient;
using PlcVars;

SimpleCoapClient client = new SimpleCoapClient();
var variable = PLC_IO_Variable.Simulation_ARR_BR1_Roller_dry;
client.Connect("172.17.137.1.1.1", 851);
Console.WriteLine("Connected");

// replace with your own DI var
var diName = "Simulation.C_BR1IdleSpeed[2].Mode";

// replace with your own DO var
VariableWriteItem doTrueItem = new VariableWriteItem();
doTrueItem.VarName = diName;
doTrueItem.Value = true;
IAck doAck = new AckPredicate<bool>(diName, PredicateType.Equals, true);
CoapCommandItem doTrueCommand = new CoapCommandItem(doTrueItem,  doAck);

var _result = (bool)await client.SendAndWaitAck(doTrueCommand);
Console.WriteLine($"{diName} has modified to {_result}");


/*VariableWriteItem doFalseItem = new VariableWriteItem();
doFalseItem.VarName = "PC_GVAR.DO_TO_POS";
doFalseItem.Value = true;
IAck do_false_Ack = new AckPredicate<bool>(diName, PredicateType.Equals, true);
CoapCommandItem doFalseCommand = new CoapCommandItem(doFalseItem,  do_false_Ack);*/

// high-frequency control of a do-di vars
/*for (int i = 0; i < 1000; i++)
{
    Console.WriteLine($"Sending : {doTrueItem.VarName} = {doTrueItem.Value}");
    var result = (bool)await client.SendAndWaitAck(doTrueCommand);
    Console.WriteLine($"{diName} has modified to {result}");
    
    Console.WriteLine($"Sending : {doFalseItem.VarName} = {doFalseItem.Value}");
    var _result = (bool)await client.SendAndWaitAck(doFalseCommand);
    Console.WriteLine($"{diName} has modified to {_result}");
}*/

// Cancellation demo: cancel the wait instead of waiting for a timeout.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
try
{
    object cancelledResult = await client.SendAndWaitAck(doTrueCommand, cancellationToken: cts.Token);
    Console.WriteLine($"Command completed: {cancelledResult}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Command was cancelled as expected.");
}
