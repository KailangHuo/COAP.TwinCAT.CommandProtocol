# CoapClient

[English](README.md) | [中文](README_CN.md)

COAP 是 **Command Oriented Ads Protocol** 的缩写。

CoapClient 是一个面向 Beckhoff TwinCAT ADS 的 C# 命令式通信中间件。

它把常见的 PLC 交互方式“写 DO/AO，然后轮询 DI/AI”封装成基于 ADS Notification 的异步命令生命周期。

> 注意：本项目不是 IETF CoAP 协议的实现。在本项目中，COAP 表示 Command Oriented Ads Protocol。

## 命令时序

下图展示了 CoapClient 如何先准备 ACK 监听路径，再写入命令变量，并在 PLC 发布预期 ACK 后完成命令。

![CoapClient 命令时序](SequenceDiagram.png)

## 项目为什么存在

在很多自动化上层软件中，一个完整动作通常被建模为：

1. C# 上层软件写入一个 DO/AO 变量，用来启动动作。
2. PLC Task 执行动作。
3. PLC 修改一个 DI/AI 变量，用来表示动作结果。
4. C# 上层软件不断读取 DI/AI，直到它达到预期值或等待超时。

这种方式可以工作，但轮询、超时判断、命令冲突处理、通知时序问题都会散落在调用方代码里。

CoapClient 将这个流程封装为命令模型：

```text
创建命令
-> 注册 DI/AI notification
-> 完成 notification 握手
-> 写入 DO/AO
-> 接收 ADS notification
-> 校验 ACK
-> 清理 notification handle
-> 完成命令 Task
```

调用方只需要描述“要写什么变量”和“什么 ACK 条件代表命令完成”，命令生命周期由 CoapClient 管理。

## 设计目标

CoapClient 适用于可以被表达为以下模型的 PLC 工作流：

```text
DO/AO 表示命令开始。
DI/AI 表示命令结果。
```

它适合用于希望上层软件以命令方式操作 PLC，而不是在业务代码里手写 DI/AI 轮询循环的场景。

## 通信流程

对于带 ACK 的命令，CoapClient 遵循以下流程：

| 步骤 | 行为 | 目的 |
|---|---|---|
| 1 | 为 ACK DI/AI 变量注册 notification handle | 在命令下发前先建立结果监听 |
| 2 | 等待首帧 notification 或主动读取当前值 | 避免高负载下首帧丢失导致握手失败 |
| 3 | 写入 DO/AO 命令变量 | 确保 ACK 路径准备好后再启动 PLC 动作 |
| 4 | PLC 执行动作 | 由 PLC Task 处理 |
| 5 | PLC 修改 DI/AI 结果变量 | 触发 ADS `OnChange` notification |
| 6 | CoapClient 校验 ACK predicate | 判断命令是否成功 |
| 7 | 移除 notification handle | 结束命令生命周期 |
| 8 | 如果发生超时，再主动读取一次 ACK | 降低 notification 时序边界导致的误超时 |

握手步骤非常重要。在高负载场景下，注册 handle 后的首帧 notification 可能丢失。CoapClient 会在注册监听时主动读取 ACK 变量，并在超时后再次主动读取一次，从而尽量保证命令生命周期完整。

## 快速开始

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

这条命令表达的是：

```text
写入 PC_GVAR.DO_TO_POS = true。
监听 PC_GVAR.DI_AT_POS。
当 PC_GVAR.DI_AT_POS == true 时认为命令完成。
3 秒后超时。
```

## 超时

每个需要 ACK 的命令都有超时。取值顺序如下：

```text
1. 传给 SendAndWaitAck 的 timeSpan 参数
2. ICoapCommand.Timeout（命令自身提供的超时）
3. SimpleCoapClient.DefaultAckTimeout —— 当前为 1 分钟
```

第三步很关键：`CoapCommandItem` 与 `CoapSumCommandItem` **不会**填充 `ICoapCommand.Timeout`，
所以省略 `timeSpan` 时会落到 `DefaultAckTimeout`。如果需要别的上限，要么显式传 `timeSpan`，
要么继承 `AbstractCoapCommand` 并在自己的构造函数里设置 `Timeout`。

不需要 ACK 的命令（`AckRequired == false`）不会安排超时 —— 写命令下发后即完成。

## 取消（Cancellation）

`SendAndWaitAck` 可接受一个可选的 `CancellationToken`。当 token 被触发时，命令 Task 会以取消状态完成，而不是继续等待 ACK 或超时。

如果在调用 `SendAndWaitAck` 时 token 已经被取消，返回的 Task 会立刻取消，不会触碰 PLC。

```csharp
using System.Threading;
using CoapClient;

var client = new SimpleCoapClient();
client.Connect("YOUR_AMS_NET_ID", 851);

var write = new VariableWriteItem("PC_GVAR.DO_TO_POS", true);
var ack = new AckPredicate<bool>("PC_GVAR.DI_AT_POS", PredicateType.Equals, true);
var command = new CoapCommandItem(write, ack);

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
try
{
    object result = await client.SendAndWaitAck(command, cancellationToken: cts.Token);
    Console.WriteLine($"命令完成: {result}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("命令已取消。");
}
```

取消是协作式的：CoapClient 会停止等待并清理 notification handle，但**不会**撤销已经下发给 PLC 的 DO/AO 写入。

注意这个例子靠 `CancellationTokenSource` 来限制等待时间 —— 它没有传 `timeSpan`。如果没有这个
token，命令会等到 `DefaultAckTimeout` 才超时。

## 公开 API

### SimpleCoapClient

调用方的主要入口。

| 成员 | 说明 |
|---|---|
| `Connect(string amsNetId, int amsPort)` | 连接 TwinCAT ADS Server，并加载符号表。 |
| `Task<object> SendAndWaitAck(ICoapCommand command, TimeSpan? timeSpan = null, CancellationToken cancellationToken = default)` | 下发命令，返回的 Task 在 ACK 满足、失败、冲突、取消或超时时完成。 |
| `bool VariableExists(string varName)` | 用本地缓存的符号表判断变量是否存在，不走 ADS 往返。 |
| `object GetVariableValue(string varName)` | 直接读取一个 PLC 变量。 |
| `ISymbol? GetVariableSymbol(string varName)` | 查询变量的符号元数据 —— 声明类型、类型分类、字节大小、成员。本地查找，不走 ADS 往返。变量不存在时返回 `null`。 |
| `object[] GetSumVariableValue(string[] varNames)` | 用一次 ADS sum 读批量读取多个变量，返回值与 `varNames` 顺序对齐。 |
| `string GetServerStatusString()` | 读取 ADS Server 状态（字符串形式）。 |
| `static readonly TimeSpan DefaultAckTimeout` | ACK 超时的兜底值，当前为 1 分钟。见[超时](#超时)。 |

参数校验：`command` 为 `null` 时抛 `ArgumentNullException`；命令需要 ACK 但 `AckVarName`
为空时抛 `ArgumentException`。两者都在**取消检查之前**抛出 —— 即使 token 已被取消，
非法命令也会立刻报错。

一次往返批量读取：

```csharp
object[] values = client.GetSumVariableValue(
    new[] { "PC_GVAR.DI_AT_POS", "PC_GVAR.AI_PRESSURE" });
bool atPos = (bool)values[0];
```

### 检查变量是否存在

`VariableExists` 只查本地缓存的符号表来判断变量名是否为 PLC 所知 ——
每次调用都不产生 ADS 往返。可以对任意多个名字反复调用。

```csharp
if (!client.VariableExists("PC_GVAR.DO_TO_POS"))
{
    Console.WriteLine("该变量不在当前 PLC 工程中。");
}
```

行为与约束：

| 情况 | 结果 |
|---|---|
| 变量存在于已加载的符号表 | `true` |
| 变量不存在 | `false` |
| `varName` 为 null、空或纯空白 | 抛 `ArgumentException` |
| 尚未调用 `Connect` | 抛 `CoapNotConnectedException` |

符号表在 `Connect` 时加载，**不会自动刷新**。PLC 程序重新下载后，请重新 `Connect`
再依赖此检查 —— 否则过期的符号表可能报告一个已经不存在的变量。

### 获取变量的类型

`GetVariableSymbol` 返回变量的符号元数据 —— 和 `VariableExists` 是同一次本地查找，
只是把返回值从布尔换成了符号本身。

```csharp
ISymbol? symbol = client.GetVariableSymbol("PC_GVAR.DI_AT_POS");
if (symbol is null)
{
    Console.WriteLine("该变量不在当前 PLC 工程中。");
    return;
}

symbol.TypeName               // "BOOL" —— PLC 声明的类型名
symbol.ByteSize               // 1
symbol.DataType.Category      // DataTypeCategory.Primitive
symbol.DataType.IsPrimitive   // true
symbol.SubSymbols             // 成员（结构体、数组类型）
```

要拿到 PLC 类型对应的 .NET 类型，把 `symbol.DataType` 交给 TwinCAT ADS 的解析器：

```csharp
// using TwinCAT.TypeSystem;
DataTypeResolverExtension.TryGetManagedType(symbol.DataType, out Type? managed);
```

行为与 `VariableExists` 一致：变量不存在返回 `null`，名字为 null/空抛 `ArgumentException`，
未 `Connect` 时抛 `CoapNotConnectedException`。

两点需要注意：

- **返回的 `ISymbol` 是符号表内的活引用，不是副本。** 请只读使用。
- **类型信息是 `Connect` 时刻的快照，不会刷新。** 这比"过期的存在性"更危险：
  过期的**存在性**最多让你查到一个已经不存在的名字；而过期的**类型**会让你用错误的方式
  解析正确的数据，**静默产生错值**。PLC 重新下载后务必重连再依赖它。

这个 API 只是查询，**不参与命令生命周期** —— 既不能影响 `SendAndWaitAck`，也不受它影响。

### VariableWriteItem

表示一次 PLC 变量写入。

```csharp
var item = new VariableWriteItem("PC_GVAR.DO_TO_POS", true);
```

| 成员 | 说明 |
|---|---|
| `VarName` | 要写入的 PLC 变量名。 |
| `Value` | 要写入的值。 |

### ICoapCommand

`SendAndWaitAck` 所操作的契约。可以直接实现，但更推荐继承 `AbstractCoapCommand`（见下）。

| 成员 | 说明 |
|---|---|
| `bool AckRequired` | 为 `false` 时命令是「发了不管」：写命令下发后 Task 立即完成，不注册 notification、不校验 ACK、也没有超时。 |
| `string AckVarName` | 用于观察结果的 DI/AI 变量。`AckRequired` 为 `true` 时必填。 |
| `int CycleTime` | ADS Server 采样 ACK 变量变化的时间间隔，单位**毫秒**。会传给 `NotificationSettings`。 |
| `int MaxDelayTime` | 传给 `NotificationSettings` 的最大延迟，单位**毫秒**。`0` 表示关闭该机制。 |
| `TimeSpan? Timeout` | 可选的命令级 ACK 超时。当 `SendAndWaitAck` 未传 `timeSpan` 时生效。 |
| `Type ValueType` | 声明的类型信息。目前类库既不填充也不读取 —— 视为未使用。 |
| `bool ValidateAck(byte[] rawAckData)` | 判断原始 ACK 字节是否满足命令条件。 |
| `object GetResult(byte[] rawData)` | 把原始 ACK 字节转换成 `SendAndWaitAck` 返回的对象。 |

> 这里的 `CycleTime` 与 `MaxDelayTime` 单位是**毫秒**，因为对应的是
> `NotificationSettings(AdsTransMode, int, int)` 重载。而原始 ADS 协议
> （`AdsNotificationAttrib`）用的是 100 ns 单位 —— 不要把两边的数值直接照搬。

### AbstractCoapCommand

自定义命令的基类。它实现了 `ICoapCommand`，并把 ACK 处理委托给一个 `IAck` 实例，
所以派生类只需要提供数据和各属性。

```csharp
public class MyCommand : AbstractCoapCommand
{
    public MyCommand(VariableWriteItem write, IAck ack)
    {
        this.WriteItem = write;
        this.AckPredicate = ack;
        this.AckRequired = true;
        this.AckVarName = ack.AckVarName;
        this.CycleTime = 2;
        this.MaxDelayTime = 0;
        this.Timeout = TimeSpan.FromSeconds(5);   // protected set
    }

    public VariableWriteItem WriteItem { get; }
}
```

| 成员 | 说明 |
|---|---|
| `AckPredicate` | `ValidateAck` / `GetResult` 使用的 `IAck`。 |
| `AckRequired`、`CycleTime`、`MaxDelayTime`、`AckVarName`、`Timeout` | 均为 `protected set`，派生类可赋值。 |
| `ValidateAck(byte[])` | `AckRequired` 为 `false` 时返回 `true`；否则委托给 `AckPredicate.IsMatched`。 |
| `GetResult(byte[])` | `AckRequired` 为 `false` 时返回 `true`；否则委托给 `AckPredicate.GetResultWithType`。 |

### CoapCommandItem

单变量命令，派生自 `AbstractCoapCommand`。

```csharp
// 省略 ack（或传 null）=> AckRequired 保持 false => 发了不管
var command = new CoapCommandItem(writeItem);

// 带 ACK；cycleTime 默认 2 ms
var command = new CoapCommandItem(writeItem, ack);
var command = new CoapCommandItem(writeItem, ack, cycleTime: 10);
```

`ack` 传 `null` 是受支持的「只写不等结果」用法。

### CoapSumCommandItem

多变量写命令，通过 ADS sum 写下发。

```csharp
var command = new CoapSumCommandItem(
    new[]
    {
        new VariableWriteItem("PC_GVAR.DO_A", true),
        new VariableWriteItem("PC_GVAR.DO_B", false),
    },
    ack);
```

构造函数形式与 `CoapCommandItem` 相同，含可选的 `cycleTime`。

写入的变量必须互不相同 —— 出现重复变量会在下发时抛 `InvalidOperationException`。

### IAck

定义命令结果如何校验。

| 成员 | 说明 |
|---|---|
| `string AckVarName` | 用作命令结果的 PLC DI/AI 变量。 |
| `bool IsMatched(byte[] rawData)` | 返回原始 ACK 值是否满足条件。 |
| `object GetResultWithType(byte[] rawData)` | 把原始 ACK 字节转换成有类型的返回值。 |

当内置 predicate 模型不够用时，实现 `IAck`。

### AckPredicate<T>

内置的 ACK 实现，适用于 unmanaged 值类型。

```csharp
IAck ack = new AckPredicate<bool>(
    "PC_GVAR.DI_AT_POS",
    PredicateType.Equals,
    true);
```

也可以传入自定义 predicate：

```csharp
IAck ack = new AckPredicate<int>(
    "PC_GVAR.AI_PRESSURE",
    value => value >= 10 && value <= 20,
    "pressure in expected range");
```

### PredicateType

| PredicateType | 含义 | 参数 |
|---|---|---|
| `Equals` | ACK 值等于预期值 | `arg1` |
| `NotEquals` | ACK 值不等于预期值 | `arg1` |
| `GreaterOrEqual` | ACK 值大于等于预期值 | `arg1` |
| `LessOrEqual` | ACK 值小于等于预期值 | `arg1` |
| `InBetween` | ACK 值落在闭区间内 | `arg1`、`arg2` |

三个排序类 predicate 要求 ACK 值类型支持比较；对不支持比较的值类型使用它们会抛
`InvalidOperationException`。

## 命令生命周期

内部而言，一条命令会经历以下状态：

| 状态 | 含义 |
|---|---|
| Queued | 命令已被接受，尚未下发。 |
| HandShakeSent | 正在建立 ACK notification 路径。 |
| Dispatched | DO/AO 写命令已下发到 PLC。 |
| Success | ACK predicate 匹配。 |
| Timeout | 超时前 ACK 未匹配，包含最后一次主动读取。 |
| Collided | 同一 ACK 变量上已有其它命令在使用。 |
| Cancelled | 命令通过 `CancellationToken` 被取消。 |

同一时刻，同一个 ACK 变量只能有一条在途命令。这避免了 notification 归属的歧义。

## 异常

`SendAndWaitAck` 通过返回的 Task 报告所有终态 —— 要么是 ACK 值，要么是下列异常之一。
注意有少数失败是**同步抛出**的（在下表中单独说明）。

| 异常 | 含义 |
|---|---|
| `TimeoutException` | 超时前 ACK predicate 未被满足。消息里带最后一次已知值。 |
| `CoapCommandCollisionException` | 该 ACK 变量已被另一条命令占用。带 `AckVarName`。 |
| `OperationCanceledException`（实际为 `TaskCanceledException`）| 命令通过 `CancellationToken` 取消。 |
| `CoapResultConversionException` | **命令已成功，但 Task 仍然 faulted。** 见下。 |
| `CoapSymbolNotFoundException` | 变量不在已加载的符号表中。带 `VarName`。 |
| `CoapNotConnectedException` | 尚未调用 `Connect`。 |
| ADS 异常（`AdsErrorException` 等）| 由底层 `Beckhoff.TwinCAT.Ads` 层原样抛出。 |

同步抛出：

- `SendAndWaitAck` 传入 `null` 命令 → `ArgumentNullException`。
- `SendAndWaitAck` 传入需要 ACK 但 `AckVarName` 为空的命令 → `ArgumentException`。
- `Connect` 原样重抛 ADS 层报出的异常。

### `CoapResultConversionException` —— 不要盲目重试

这是唯一一个表示命令**已经成功**的异常。ACK predicate 已满足，PLC 侧动作**已经发生** ——
但随后 `ICoapCommand.GetResult` 在把原始 ACK 字节转换成结果对象时抛了异常。

如果调用方把它当命令失败处理并重新下发写入，PLC 动作会**被执行第二次**。
应该改用 `RawAckData`（PLC 实际报出的字节）和 `InnerException`（`GetResult` 抛出的原因）来排查。

```csharp
try
{
    object result = await client.SendAndWaitAck(command);
}
catch (CoapResultConversionException ex)
{
    // PLC 动作已经发生了。先排查，不要盲目重试。
    Console.WriteLine($"ACK 已满足（{ex.AckVarName}），但结果解析失败。");
    Console.WriteLine($"原始 ACK 字节: {Convert.ToHexString(ex.RawAckData ?? Array.Empty<byte>())}");
    Console.WriteLine($"原因: {ex.InnerException?.Message}");
}
catch (CoapCommandCollisionException ex)
{
    Console.WriteLine($"{ex.AckVarName} 上已有命令在途。");
}
catch (CoapSymbolNotFoundException ex)
{
    Console.WriteLine($"{ex.VarName} 不在当前 PLC 工程中。");
}
catch (CoapNotConnectedException)
{
    Console.WriteLine("请先调用 Connect()。");
}
```

### 向后兼容

`CoapCommandCollisionException`、`CoapSymbolNotFoundException`、`CoapNotConnectedException`
都继承自 `InvalidOperationException`，因此按旧版本写法捕获 `InvalidOperationException` 的
调用方仍然可用。但新代码应优先捕获具体类型 —— 只用基类型无法区分这几种情况。

## 日志

类库通过 **log4net** 输出日志，使用独立的 repository 与 logger：

```text
repository : CoapProjectRepository
logger     : [COAP]
```

repository 在内部完成配置，因此无需额外添加 log4net 配置即可看到日志。`Connect`、命令下发、
ACK 校验、handle 清理、超时处理都会写日志；失败会在异常抛出前先记录。

## 并发

- **不同的 ACK 变量**可以并发使用。每条命令持有自己的 notification handle，通知回调线程把
  工作交给单一消费者队列，因此不同命令的 ACK 处理是串行的，不会交错。
- **同一个 ACK 变量**不能有两条在途命令。第二条会立刻以 `CoapCommandCollisionException` 失败。
- 不带 ACK 的命令通过串行化的 sender 下发。

## 生命周期

`SimpleCoapClient` **目前尚未**实现 `IDisposable`，也没有 `Disconnect`。底层 ADS 连接会随实例
一直持有。因此请**每个应用创建一个 client 并复用**，不要按命令创建 ——
反复创建会泄漏 ADS 连接与句柄，而 TwinCAT 的句柄总数上限约 8191。

对应的工作项记录在 `CoapTestConsole/Issue.md`。

## 项目结构

路径均相对仓库根目录。

| 路径 | 用途 |
|---|---|
| `CoapClient/CoapClient/` | 类库源码。 |
| `CoapClient/CoapTestConsole/` | 用于手工实验的控制台工程。 |
| `CoapClient/BRIEF.md` | 维护者说明，描述项目意图。 |
| `CoapClient/CoapTestConsole/Issue.md` | 已知未决缺陷。 |
| `CoapClient/CoapTestConsole/CoapClient_KnownIssues.md` | 异常语义上的坑，供下游使用方参考。 |

重要的内部组件：

| 组件 | 职责 |
|---|---|
| `ThinAdsServer` | 封装 TwinCAT ADS 连接、符号查找、读写与 notification。 |
| `CommandContext` | 承载单条命令的生命周期与完成状态。 |
| `CommandSender` | 串行化命令下发。 |
| `CommandHandler` | 处理 ADS notification 并路由到对应的 command context。 |
| `CommandInFlightManager` | 防止 ACK 变量冲突。 |
| `TimeoutScheduler` | 调度命令超时回调。 |

`SingleConsumerQueue<T>` 与 `ITimeoutItem` 属于实现细节，目前却是 `public` 声明 ——
请勿依赖。见 `Issue.md`。

## 环境要求

- .NET 8 SDK
- Beckhoff TwinCAT ADS 环境
- 运行 C# 应用程序的机器到 PLC 的 ADS 路由可达

类库当前目标框架为 `net8.0`。

## 构建

```powershell
dotnet restore
dotnet build CoapClient.sln
```

## Roadmap

计划中的方向：

- Observation 模式：脱离单次命令生命周期的长期变量监听。
- 多 ACK 命令：一条命令可以依赖多个 ACK 变量。
- 针对真实 TwinCAT 环境的更完整示例与集成测试。
- 更完整的自定义 `IAck` 实现文档。

## 参与贡献

本项目希望寻找具备以下经验的贡献者：

- TwinCAT ADS 与 Beckhoff PLC 工作流
- C# 异步编程
- 工业自动化命令模型
- 健壮的超时与 notification 处理
- 面向真实机器控制场景的文档与示例

可以参与的方向：

- 改进公开 API 的易用性。
- 为常见的 DO/DI 与 AO/AI 工作流补充示例。
- 实现 Observation 模式。
- 设计与实现多 ACK 命令支持。
- 为命令生命周期的边界情况补充测试。
- 改进错误信息与日志。

改动行为之前，请牢记核心设计规则：

```text
ACK 监听路径必须在 DO/AO 命令下发之前就绪。
```

这条规则保证了在 notification 延迟或高负载场景下命令生命周期的完整性。
