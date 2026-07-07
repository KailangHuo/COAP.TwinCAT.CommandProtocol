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

## 公开 API

### SimpleCoapClient

调用方的主要入口。

| 成员 | 说明 |
|---|---|
| `Connect(string amsNetId, int amsPort)` | 连接 TwinCAT ADS Server。 |
| `Task<object> SendAndWaitAck(ICoapCommand command, TimeSpan? timeSpan = null)` | 发送命令，并返回一个在 ACK 满足、失败、冲突或超时时完成的 Task。 |
| `object GetVariableValue(string varName)` | 直接读取 PLC 变量值。 |
| `string GetServerStatusString()` | 读取 ADS Server 状态字符串。 |

### VariableWriteItem

表示一次 PLC 变量写入。

```csharp
var item = new VariableWriteItem("PC_GVAR.DO_TO_POS", true);
```

| 成员 | 说明 |
|---|---|
| `VarName` | 要写入的 PLC 变量名。 |
| `Value` | 要写入的值。 |

### CoapCommandItem

表示单变量写入命令。

```csharp
var command = new CoapCommandItem(writeItem, ack);
```

### CoapSumCommandItem

表示多变量写入命令。

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

定义命令结果如何被检查。

| 成员 | 说明 |
|---|---|
| `string AckVarName` | 作为命令结果的 PLC DI/AI 变量名。 |
| `bool IsMatched(byte[] rawData)` | 判断原始 ACK 数据是否满足条件。 |
| `object GetResultWithType(byte[] rawData)` | 将原始 ACK 字节转换为对应类型的结果。 |

当内置 predicate 模型不足以表达业务条件时，可以自行实现 `IAck`。

### AckPredicate<T>

内置 ACK 实现，适用于 unmanaged value type。

```csharp
IAck ack = new AckPredicate<bool>(
    "PC_GVAR.DI_AT_POS",
    PredicateType.Equals,
    true);
```

也可以提供自定义 predicate：

```csharp
IAck ack = new AckPredicate<int>(
    "PC_GVAR.AI_PRESSURE",
    value => value >= 10 && value <= 20,
    "pressure in expected range");
```

### PredicateType

| PredicateType | 含义 | 参数 |
|---|---|---|
| `Equals` | ACK 值等于目标值 | `arg1` |
| `NotEquals` | ACK 值不等于目标值 | `arg1` |
| `GreaterOrEqual` | ACK 值大于等于目标值 | `arg1` |
| `LessOrEqual` | ACK 值小于等于目标值 | `arg1` |
| `InBetween` | ACK 值在闭区间内 | `arg1`, `arg2` |

## 命令生命周期

在内部，一个命令会经历以下状态：

| 状态 | 含义 |
|---|---|
| Queued | 命令已被接收，但尚未派发。 |
| HandShakeSent | ACK notification 路径正在建立。 |
| Dispatched | DO/AO 写入已经发送到 PLC。 |
| Success | ACK predicate 已满足。 |
| Timeout | 超时前 ACK 未满足，包括最后一次主动读取后仍未满足。 |
| Collided | 已有其他命令正在使用同一个 ACK 变量。 |

同一时间只有一个 in-flight 命令可以使用同一个 ACK 变量。这样可以避免 notification 归属不明确。

## 项目结构

| 路径 | 用途 |
|---|---|
| `CoapClient/` | 类库源码。 |
| `CoapTestConsole/` | 用于手动实验的小型控制台项目。 |
| `CoapClient/BRIEF.md` | 维护者对项目意图的说明。 |

重要内部组件：

| 组件 | 职责 |
|---|---|
| `ThinAdsServer` | 封装 TwinCAT ADS 连接、符号查找、读、写和 notification。 |
| `CommandContext` | 保存单个命令的生命周期和完成状态。 |
| `CommandSender` | 串行化命令派发。 |
| `CommandHandler` | 处理 ADS notification，并路由到对应命令上下文。 |
| `CommandInFlightManager` | 防止 ACK 变量冲突。 |
| `TimeoutScheduler` | 调度命令超时回调。 |

## 环境要求

- .NET 8 SDK
- Beckhoff TwinCAT ADS 环境
- 运行 C# 程序的机器可以访问对应 ADS route

当前类库目标框架为 `net8.0`。

## 构建

```powershell
dotnet restore
dotnet build CoapClient.sln
```

## Roadmap

计划方向：

- Observation 模式：脱离单次命令生命周期的长期变量监听。
- 多 ACK 命令：一个命令可以依赖多个 ACK 变量。
- 面向真实 TwinCAT 环境的更多示例和集成测试。
- 更完整的自定义 `IAck` 文档。

## 参与贡献

这个项目欢迎具备以下经验的开发者参与：

- TwinCAT ADS 和 Beckhoff PLC 工作流
- C# async 编程
- 工业自动化命令模型
- 稳健的超时和 notification 处理
- 面向真实设备控制场景的文档和示例

适合贡献的方向：

- 改进公开 API 的易用性。
- 增加常见 DO/DI 和 AO/AI 工作流示例。
- 实现 Observation 模式。
- 设计并实现多 ACK 命令支持。
- 为命令生命周期边界情况增加测试。
- 改进错误信息和日志。

修改行为前，请始终记住这个核心设计规则：

```text
ACK observation path should be ready before the DO/AO command is sent.
```

也就是：ACK 监听路径应该先准备好，然后再下发 DO/AO 命令。

这条规则用于在 notification 延迟或高负载场景下保护命令生命周期的完整性。
