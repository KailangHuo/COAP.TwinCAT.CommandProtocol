using System.Diagnostics;
using CoapClient;
using TwinCAT.TypeSystem;

/// <summary>
/// Manual smoke tests for the AdsClient -> AdsSession transport swap.
/// These run against a real TwinCAT target and write to simulation variables only.
/// </summary>
internal static class ReplacementTests
{
    private const string AmsNetId = "172.17.137.1.1.1";
    private const int AmsPort = 851;

    // Same variable used as DO and DI, so one write completes one command lifecycle.
    private const string TestVar = "Simulation.C_BR1IdleSpeed[2].Mode";
    private const string TestVar2 = "Simulation.C_BR1IdleSpeed[1].Mode";

    // Read-only BOOL/BYTE witness that will not change during the test. Used to force
    // an ACK that can never be satisfied (timeout) and to hold a command in flight (collision).
    private const string WitnessVar = "Global_Variables.EC_DEVICE_STATE_MASK";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private static int _passed;
    private static int _failed;
    private static int _skipped;

    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("COAP AdsSession replacement smoke tests");
        Console.WriteLine($"Target: {AmsNetId}:{AmsPort}");
        Console.WriteLine($"DO/DI : {TestVar}");
        Console.WriteLine(new string('-', 72));

        SimpleCoapClient client = new SimpleCoapClient();
        try
        {
            client.Connect(AmsNetId, AmsPort);
            Console.WriteLine("Connect: OK");
            Console.WriteLine();
        }
        catch (Exception e)
        {
            Console.WriteLine("Connect failed: " + e);
            return 1;
        }

        if (!client.VariableExists(TestVar))
        {
            Console.WriteLine($"Test variable not found in the PLC symbol table: {TestVar}");
            return 2;
        }

        bool hasTestVar2 = client.VariableExists(TestVar2);
        bool hasWitness = client.VariableExists(WitnessVar);
        byte impossibleWitnessValue = hasWitness
            ? GetImpossibleValue(client)
            : (byte)0;

        // Remember the original values so the suite leaves the simulation exactly as it found it.
        bool originalTestVar = (bool)client.GetVariableValue(TestVar);
        bool originalTestVar2 = hasTestVar2 && (bool)client.GetVariableValue(TestVar2);

        try
        {
            await RunTest("Repeated Connect (same target)", async () =>
            {
                client.Connect(AmsNetId, AmsPort);
                client.Connect(AmsNetId, AmsPort);
                await SetBoolAsync(client, TestVar, false, CommandTimeout);
                Assert((bool)client.GetVariableValue(TestVar) == false, "readback should be false");
            });

            await RunTest("Server status", () =>
            {
                string status = client.GetServerStatusString();
                Assert(!string.IsNullOrWhiteSpace(status), "status should not be empty");
                Console.WriteLine("       PLC state: " + status);
                return Task.CompletedTask;
            });

            await RunTest("Symbol table lookups", () =>
            {
                Assert(client.VariableExists(TestVar), "known variable should exist");
                Assert(!client.VariableExists("Simulation.DoesNotExist_XYZ"), "unknown variable should not exist");

                ISymbol? known = client.GetVariableSymbol(TestVar);
                Assert(known != null, "GetVariableSymbol should return the known symbol");
                Assert(client.GetVariableSymbol("Simulation.DoesNotExist_XYZ") == null,
                    "GetVariableSymbol should return null for an unknown name");

                object value = client.GetVariableValue(TestVar);
                Assert(value is bool, "GetVariableValue should return bool, got " + value.GetType().Name);
                return Task.CompletedTask;
            });

            await RunTest("Unknown variable throws CoapSymbolNotFoundException", () =>
            {
                try
                {
                    client.GetVariableValue("Simulation.DoesNotExist_XYZ");
                    throw new Exception("expected CoapSymbolNotFoundException");
                }
                catch (CoapSymbolNotFoundException)
                {
                }

                return Task.CompletedTask;
            });

            await RunTest("Null command rejected", async () =>
            {
                try
                {
                    await client.SendAndWaitAck(null!);
                    throw new Exception("expected ArgumentNullException");
                }
                catch (ArgumentNullException)
                {
                }
            });

            await RunTest("Empty ACK variable rejected", async () =>
            {
                CoapCommandItem command = new CoapCommandItem(
                    new VariableWriteItem(TestVar, true),
                    new AckPredicate<bool>("", PredicateType.Equals, true));

                try
                {
                    await client.SendAndWaitAck(command);
                    throw new Exception("expected ArgumentException");
                }
                catch (ArgumentException)
                {
                }
            });

            await RunTest("ACK lifecycle: false -> true", async () =>
            {
                await SetBoolAsync(client, TestVar, false, CommandTimeout);
                object result = await SetBoolAsync(client, TestVar, true, CommandTimeout);
                Assert((bool)result, "command result should be true");
                Assert((bool)client.GetVariableValue(TestVar), "PLC value should be true");
            });

            await RunTest("ACK lifecycle: true -> false", async () =>
            {
                object result = await SetBoolAsync(client, TestVar, false, CommandTimeout);
                Assert(!(bool)result, "command result should be false");
                Assert(!(bool)client.GetVariableValue(TestVar), "PLC value should be false");
            });

            await RunTest("Non-ACK command", async () =>
            {
                await SetBoolAsync(client, TestVar, false, CommandTimeout);

                object result = await WriteWithoutAckAsync(client, TestVar, true);
                Assert(result is true, "non-ACK command should complete with true");
                Assert((bool)client.GetVariableValue(TestVar), "PLC value should be true");

                await WriteWithoutAckAsync(client, TestVar, false);
                Assert(!(bool)client.GetVariableValue(TestVar), "PLC value should be false");
            });

            if (hasTestVar2)
            {
                await RunTest("Sum command lifecycle + batch read", async () =>
                {
                    await SetBoolAsync(client, TestVar, false, CommandTimeout);
                    await SetBoolAsync(client, TestVar2, false, CommandTimeout);

                    VariableWriteItem[] writes =
                    {
                        new VariableWriteItem(TestVar, true),
                        new VariableWriteItem(TestVar2, true),
                    };
                    CoapSumCommandItem command = new CoapSumCommandItem(
                        writes,
                        new AckPredicate<bool>(TestVar2, PredicateType.Equals, true));

                    object result = await client.SendAndWaitAck(command, CommandTimeout);
                    Assert((bool)result, "sum command result should be true");

                    object[] values = client.GetSumVariableValue(new[] { TestVar, TestVar2 });
                    Assert(values.Length == 2, "batch read should return two values");
                    Assert((bool)values[0] && (bool)values[1], "both batch-read values should be true");

                    // Reset both variables through one sum write.
                    VariableWriteItem[] resets =
                    {
                        new VariableWriteItem(TestVar, false),
                        new VariableWriteItem(TestVar2, false),
                    };
                    CoapSumCommandItem resetCommand = new CoapSumCommandItem(
                        resets,
                        new AckPredicate<bool>(TestVar2, PredicateType.Equals, false));

                    await client.SendAndWaitAck(resetCommand, CommandTimeout);
                });
            }
            else
            {
                Skip("Sum command lifecycle + batch read", TestVar2 + " not found");
            }

            if (hasWitness)
            {
                await RunTest("ACK timeout", async () =>
                {
                    await SetBoolAsync(client, TestVar, false, CommandTimeout);

                    CoapCommandItem command = new CoapCommandItem(
                        new VariableWriteItem(TestVar, true),
                        new AckPredicate<byte>(WitnessVar, PredicateType.Equals, impossibleWitnessValue));

                    Stopwatch stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await client.SendAndWaitAck(command, TimeSpan.FromSeconds(1));
                        throw new Exception("expected TimeoutException");
                    }
                    catch (TimeoutException)
                    {
                    }

                    stopwatch.Stop();
                    Assert(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(700),
                        "timeout returned too early: " + stopwatch.Elapsed);
                });

                await RunTest("ACK cancellation", async () =>
                {
                    await SetBoolAsync(client, TestVar, false, CommandTimeout);

                    CoapCommandItem command = new CoapCommandItem(
                        new VariableWriteItem(TestVar, true),
                        new AckPredicate<byte>(WitnessVar, PredicateType.Equals, impossibleWitnessValue));

                    using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                    try
                    {
                        await client.SendAndWaitAck(command, TimeSpan.FromSeconds(10), cts.Token);
                        throw new Exception("expected OperationCanceledException");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });

                await RunTest("Duplicate ACK variable collision", async () =>
                {
                    await SetBoolAsync(client, TestVar, false, CommandTimeout);

                    AckPredicate<byte> ack = new AckPredicate<byte>(
                        WitnessVar,
                        PredicateType.Equals,
                        impossibleWitnessValue);

                    CoapCommandItem first = new CoapCommandItem(new VariableWriteItem(TestVar, true), ack);
                    CoapCommandItem second = new CoapCommandItem(new VariableWriteItem(TestVar, true), ack);

                    using CancellationTokenSource cts = new CancellationTokenSource();
                    Task<object> firstTask = client.SendAndWaitAck(first, TimeSpan.FromSeconds(10), cts.Token);

                    await Task.Delay(50);

                    try
                    {
                        await client.SendAndWaitAck(second, TimeSpan.FromSeconds(2));
                        throw new Exception("expected CoapCommandCollisionException");
                    }
                    catch (CoapCommandCollisionException)
                    {
                    }
                    finally
                    {
                        cts.Cancel();
                    }

                    try
                    {
                        await firstTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            }
            else
            {
                Skip("ACK timeout", WitnessVar + " not found");
                Skip("ACK cancellation", WitnessVar + " not found");
                Skip("Duplicate ACK variable collision", WitnessVar + " not found");
            }
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Restoring test variables...");
            await TryRestoreAsync(client, TestVar, originalTestVar);
            if (hasTestVar2)
                await TryRestoreAsync(client, TestVar2, originalTestVar2);
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"Passed: {_passed}  Failed: {_failed}  Skipped: {_skipped}");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunTest(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine("       " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"[SKIP] {name}: {reason}");
    }

    private static async Task<object> SetBoolAsync(
        SimpleCoapClient client,
        string varName,
        bool value,
        TimeSpan timeout)
    {
        CoapCommandItem command = new CoapCommandItem(
            new VariableWriteItem(varName, value),
            new AckPredicate<bool>(varName, PredicateType.Equals, value));

        return await client.SendAndWaitAck(command, timeout);
    }

    private static async Task<object> WriteWithoutAckAsync(
        SimpleCoapClient client,
        string varName,
        bool value)
    {
        CoapCommandItem command = new CoapCommandItem(
            new VariableWriteItem(varName, value),
            null!);

        return await client.SendAndWaitAck(command, CommandTimeout);
    }

    private static byte GetImpossibleValue(SimpleCoapClient client)
    {
        byte current = Convert.ToByte(client.GetVariableValue(WitnessVar));
        return current == byte.MaxValue ? (byte)0 : byte.MaxValue;
    }

    private static async Task TryRestoreAsync(SimpleCoapClient client, string varName, bool originalValue)
    {
        try
        {
            await SetBoolAsync(client, varName, originalValue, TimeSpan.FromSeconds(2));
            Console.WriteLine($"  {varName} = {originalValue} (restored)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  {varName} restore failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
