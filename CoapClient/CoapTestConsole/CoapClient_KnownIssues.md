# CoapClient 已知问题

> 记录底层类库 `CoapClient`（NuGet 包 `COAP.TwinCAT.CommandProtocol`）在**异常语义**上的
> 坑，以及中间件为此做的额外处理。升级类库版本时，请逐条重验。

---

## 1. 「命令冲突」与「变量不存在」抛的是同一个异常

### 现象

写命令（`SendAndWaitAck`）的四种终态，类库用**异常类型**来区分：

| 终态 | 类库的表现 |
| --- | --- |
| Success | 正常返回 |
| Timeout | `TimeoutException` |
| Cancelled | `OperationCanceledException`（`TaskCanceledException` 是其子类） |
| **Collided** | `InvalidOperationException`，消息里含 `"Collided"` |

问题在最后一行：**`ThinAdsServer.GetSymbol` 在找不到符号时，抛的也是 `InvalidOperationException`。**

于是这两件毫不相干的事，在类型上无法区分：

| 真实原因 | 异常消息（形如） |
| --- | --- |
| 命令冲突（同一 ACK 变量已有在途命令） | `Command Collided! Ack: <var>` |
| **变量不存在** | `Symbol '<var>' was not found.` |

### 为什么危险

它不是一个"错误没被捕获"的问题，而是一个**错误被张冠李戴**的问题。

如果按最自然的写法：

```csharp
catch (InvalidOperationException) → Collided        // ✗ 错的
```

那么「变量路径写错了」会被报告成「命令冲突，请稍后再试」。

后果是**误导排查方向**：用户看到"冲突"会去重试，而真实原因是这个变量在 PLC 里根本不存在 ——
重试多少次都不会成功，而且几乎不会有人想到去查路径。

### 中间件的处理

用**异常过滤器按消息文本**把两者分开（`PlcGateway.cs` 的 `WriteAndWaitAckAsync`）：

```csharp
catch (InvalidOperationException e) when (e.Message.Contains("Collided"))
{
    return Result.Success(new { status = WriteStatus.Collided, path = request.Path, ackPath });
}
catch (Exception e)
{
    // 落到这里的包括 "Symbol 'X' was not found." —— 如实上报，
    // 让前端看到真实原因，而不是被归为命令冲突。
    return Result.Failure("ADS_WRITE_FAILED", $"写入失败：{e.Message}", e);
}
```

要点：**兜底分支不能省**。不写 `when` 过滤器而直接捕获 `InvalidOperationException`，
就等于把"变量不存在"错判成"冲突"。

### 代价：这是与消息文本的耦合点

判断依据是 `e.Message.Contains("Collided")` —— 一个**字符串**。

- 类库换版本、消息文案改了（例如改成 `collision`、本地化、或加了前缀），
  这个过滤器会**静默失效**，"变量不存在"又会被误判成"命令冲突"，而且不会有任何编译或运行错误提示；
- 所以升级 `COAP.TwinCAT.CommandProtocol` 时，**必须**专门验这一条（见下方验证步骤）。

### 怎么验（升级类库后必做）

1. **变量不存在**：写一个**确定不存在**的路径 → 应当得到
   `ADS_WRITE_FAILED`，消息里含 `was not found`，**不应**得到 `Collided`；
2. **命令冲突**：对同一个 ACK 变量并发发起两条写入 → 应当得到 `Collided`。

---

## 2. 相关：读取路径上不能把异常降级成"全不存在"

同一类异常的另一个坑，出现在**只读**路径上。

`ExistsMany` / `ReadMany` 在符号表尚未加载时也会撞上 `InvalidOperationException`。
此时的诱惑是"**降级返回：全部不存在**"，让流程继续走下去。

**不要这么做。** 后果是界面会把每一行都判成"变量缺失"、整表禁用 ——
比一条明确的错误提示糟糕得多：用户面对的是一个看起来"数据本身全坏了"的界面，
而真实原因只是"还没连上"。

正确做法是**如实返回一个明确错误**：

```csharp
catch (InvalidOperationException e)
{
    return Result.Failure("NOT_CONNECTED", "PLC 尚未连接或符号表未加载", e, retryable: true);
}
```

（`ReadMany` 的情况还要多一重：`InvalidOperationException` 也可能是"某个符号不存在"
—— 即"批量读是全有或全无"那条约束被违反，调用方没有预筛。两种情况都如实上报。）

---

## 涉及位置

| 位置 | 说明 |
| --- | --- |
| `CoapMiddleWare/CoapMiddleWare/PlcGateway.cs` | `WriteAndWaitAckAsync`（冲突判定）、`ExistsMany`、`ReadMany` 的异常处理 |
| `CoapMiddleWare/HANDOFF.md` §5.1 | 同一问题的另一份记录（面向中间件开发者） |
| `Middleware_API.md` §7.4 | 错误码与终态的协议侧说明 |

> 本文只记录**类库异常语义**这一类问题；升级类库时若发现新的同类坑，追加到这里。
