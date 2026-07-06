# CoapClient

[English](README.md) | [中文](README_CN.md)

COAP means **Command Oriented Ads Protocol**.

CoapClient is a command-oriented C# middleware for Beckhoff TwinCAT ADS.

It turns the common PLC interaction pattern of "write DO/AO, then poll DI/AI" into an asynchronous command lifecycle based on ADS notifications.

> Note: this project is not an implementation of the IETF CoAP protocol. In this project, COAP stands for Command Oriented Ads Protocol.

## Why This Project Exists

In many upper-level automation applications, one complete action is modeled like this:

1. The C# application writes a DO/AO variable to start an action.
2. A PLC task executes the action.
3. The PLC updates a DI/AI variable to expose the result.
4. The C# application repeatedly reads the DI/AI variable until it reaches the expected value or times out.

That works, but it pushes polling, timeout checks, command collision handling, and notification timing issues into every caller.

CoapClient wraps this into a command model:

```text
Create command
-> register DI/AI notification
-> complete notification handshake
-> write DO/AO
-> receive ADS notification
-> validate ACK
-> clean up notification handle
-> complete the command task
```

The caller describes what should be written and what ACK condition should be observed. CoapClient manages the lifecycle.

## Design Goal

CoapClient is designed for PLC workflows that can be expressed as:

```text
DO/AO starts a command.
DI/AI represents the command result.
```

It is especially useful when upper-level software should issue commands without manually implementing DI/AI polling loops.

## Communication Flow

For a command with ACK, CoapClient follows this flow:

| Step | Behavior | Purpose |
|---|---|---|
| 1 | Register a notification handle for the ACK DI/AI variable | Observe the result before sending the command |
| 2 | Wait for the first notification frame or actively read the value | Avoid losing the initial handshake under high load |
| 3 | Write the DO/AO command variable | Start the PLC action only after the ACK path is ready |
| 4 | PLC executes the action | Handled by the PLC task |
| 5 | PLC changes the DI/AI result variable | Triggers ADS `OnChange` notification |
| 6 | CoapClient validates the ACK predicate | Decide whether the command succeeded |
| 7 | Remove the notification handle | End the command lifecycle |
| 8 | If timeout occurs, actively read the ACK once more | Reduce false timeouts caused by notification timing edges |

The handshake step is important. Under high load, the first notification frame after registering a handle may be missed. CoapClient actively reads the ACK variable during registration and again after timeout to keep the command lifecycle as complete as possible.

## Quick Start

```csharp
using CoapClient;

var client = new SimpleCoapClient();
client.Connect("YOUR_AMS_NET_ID", 851);

var write = new VariableWriteItem("PC_GVAR.DO_TO_POS", true);
var ack = new AckPredicate<bool>(
    "PC_GVAR.DI_AT_POS",
    PredicateType.Equals,
    true);

var command = new CoapCommandItem(write, ack);

object result = await client.SendAndWaitAck(
    command,
    TimeSpan.FromSeconds(3));
```

This command means:

```text
Write PC_GVAR.DO_TO_POS = true.
Listen to PC_GVAR.DI_AT_POS.
Complete the command when PC_GVAR.DI_AT_POS == true.
Timeout after 3 seconds.
```

## Public API

### SimpleCoapClient

Main entry point for callers.

| Member | Description |
|---|---|
| `Connect(string amsNetId, int amsPort)` | Connects to the TwinCAT ADS server. |
| `Task<object> SendAndWaitAck(ICoapCommand command, TimeSpan? timeSpan = null)` | Sends a command and returns a task that completes when the ACK is satisfied, fails, collides, or times out. |
| `object GetVariableValue(string varName)` | Reads a PLC variable value directly. |
| `string GetServerStatusString()` | Reads the ADS server state as a string. |

### VariableWriteItem

Represents one PLC variable write.

```csharp
var item = new VariableWriteItem("PC_GVAR.DO_TO_POS", true);
```

| Member | Description |
|---|---|
| `VarName` | PLC variable name to write. |
| `Value` | Value to write. |

### CoapCommandItem

Represents a single-variable command.

```csharp
var command = new CoapCommandItem(writeItem, ack);
```

### CoapSumCommandItem

Represents a multi-variable write command.

```csharp
var command = new CoapSumCommandItem(
    new[]
    {
        new VariableWriteItem("PC_GVAR.DO_A", true),
        new VariableWriteItem("PC_GVAR.DO_B", false),
    },
    ack);
```

### IAck

Defines how a command result is checked.

| Member | Description |
|---|---|
| `string AckVarName` | PLC DI/AI variable used as the command result. |
| `bool IsMatched(byte[] rawData)` | Returns whether the raw ACK value satisfies the condition. |
| `object GetResultWithType(byte[] rawData)` | Converts the raw ACK bytes into a typed result. |

Implement `IAck` when the built-in predicate model is not enough.

### AckPredicate<T>

Built-in ACK implementation for unmanaged value types.

```csharp
IAck ack = new AckPredicate<bool>(
    "PC_GVAR.DI_AT_POS",
    PredicateType.Equals,
    true);
```

You can also provide a custom predicate:

```csharp
IAck ack = new AckPredicate<int>(
    "PC_GVAR.AI_PRESSURE",
    value => value >= 10 && value <= 20,
    "pressure in expected range");
```

### PredicateType

| PredicateType | Meaning | Arguments |
|---|---|---|
| `Equals` | ACK value equals the expected value | `arg1` |
| `NotEquals` | ACK value does not equal the expected value | `arg1` |
| `GreaterOrEqual` | ACK value is greater than or equal to the expected value | `arg1` |
| `LessOrEqual` | ACK value is less than or equal to the expected value | `arg1` |
| `InBetween` | ACK value is within a closed interval | `arg1`, `arg2` |

## Command Lifecycle

Internally, a command moves through these states:

| State | Meaning |
|---|---|
| Queued | The command has been accepted but not dispatched. |
| HandShakeSent | The ACK notification path is being established. |
| Dispatched | The DO/AO write has been sent to the PLC. |
| Success | The ACK predicate matched. |
| Timeout | The ACK did not match before timeout, including the final active read. |
| Collided | Another command is already using the same ACK variable. |

Only one in-flight command can use the same ACK variable at the same time. This avoids ambiguous notification ownership.

## Project Structure

| Path | Purpose |
|---|---|
| `CoapClient/` | Library source code. |
| `CoapTestConsole/` | Small console project for manual experiments. |
| `CoapClient/BRIEF.md` | Maintainer notes describing the project intent. |

Important internal components:

| Component | Responsibility |
|---|---|
| `ThinAdsServer` | Wraps TwinCAT ADS connection, symbol lookup, read, write, and notifications. |
| `CommandContext` | Holds one command lifecycle and completion state. |
| `CommandSender` | Serializes command dispatch. |
| `CommandHandler` | Handles ADS notifications and routes them to command contexts. |
| `CommandInFlightManager` | Prevents ACK variable collisions. |
| `TimeoutScheduler` | Schedules command timeout callbacks. |

## Requirements

- .NET 8 SDK
- Beckhoff TwinCAT ADS environment
- Reachable ADS route from the machine running the C# application

The library currently targets `net8.0`.

## Build

```powershell
dotnet restore
dotnet build CoapClient.sln
```

## Roadmap

Planned directions:

- Observation mode: long-lived variable observation outside a single command lifecycle.
- Multi-ACK commands: one command can depend on multiple ACK variables.
- Stronger examples and integration tests around real TwinCAT setups.
- More complete documentation for custom `IAck` implementations.

## Contributing

This project is looking for contributors who have experience with:

- TwinCAT ADS and Beckhoff PLC workflows
- C# async programming
- Industrial automation command models
- Robust timeout and notification handling
- Documentation and examples for real machine-control scenarios

Useful contribution areas:

- Improve the public API ergonomics.
- Add examples for common DO/DI and AO/AI workflows.
- Implement Observation mode.
- Design and implement multi-ACK command support.
- Add tests for command lifecycle edge cases.
- Improve error messages and logging.

Before changing behavior, please keep the core design rule in mind:

```text
The ACK observation path should be ready before the DO/AO command is sent.
```

This rule protects command lifecycle integrity under notification delay or high-load scenarios.
