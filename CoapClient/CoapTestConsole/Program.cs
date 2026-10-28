// Manual smoke tests for the AdsClient -> AdsSession transport swap.
// Run with: dotnet run --project CoapClient/CoapTestConsole
// The tests connect to the AMS target configured in ReplacementTests.cs and
// only write variables under the PLC's Simulation namespace.

return await ReplacementTests.RunAsync(args);
