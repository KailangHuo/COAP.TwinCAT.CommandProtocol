# CoapClient 待办 Issue

> 记录类库 `CoapClient`（NuGet 包 `COAP.TwinCAT.CommandProtocol`）中**已知但尚未处理**的缺陷。
>
> 已处理的内容不在此列出，见文末「已修对照表」。下列问题按严重度排列。

---

## ISSUE-1 无 `IDisposable`，ADS 连接与句柄永不释放

**位置**：`CoapClient/CoapClient/SimpleCoapClient.cs`、`CoapClient/CoapClient/ThinAdsServer.cs`

**现象**：两个类都没有实现 `IDisposable`，也没有 `Disconnect`。内部持有的 `AdsClient`
（`ThinAdsServer.cs:13`）在对象生命周期结束后不会被释放，下层 ADS 连接与已注册句柄一并泄漏。

**影响**：TwinCAT 的 ADS 句柄总数上限约 **8191**（另见每个端口 550 个通知的上限）。
反复 `new SimpleCoapClient()` 并丢弃会逐步耗尽句柄，最终报
`no more ADS instances available (8191 are in use)`。长时间运行的进程会因此失败。

**建议**：让 `SimpleCoapClient` 实现 `IDisposable`，在 `Dispose` 中按顺序释放
未完成的命令、`TimeoutScheduler`、最后 `AdsClient`。`ThinAdsServer` 同样实现。
注意 `TimeoutScheduler` 的 worker 线程需要可停止（见 ISSUE-3）。

---

## ISSUE-2 `Connect` 是空 catch-rethrow，失败时无任何日志

**位置**：`CoapClient/CoapClient/ThinAdsServer.cs:48-51`

```csharp
catch (Exception e)
{
    throw;                     // e 从未使用（编译警告 CS0168）
}
```

**影响**：连接失败（AMS NetId 写错、路由不可达、PLC 未启动）时**不产生任何日志**。
使用者只能拿到裸的 ADS 异常，无法从日志侧判断失败发生在哪一步
（`AdsClient.Connect` 还是 `ReadState` 还是符号表加载）。

**建议**：至少 `CoapLog.Error(...)` 记录后再 `throw;`；或删除这个无意义的 try/catch，
改为在 `SimpleCoapClient.Connect` 统一记录。

---

## ISSUE-3 `SingleConsumerQueue` 的取消分支是死代码

**位置**：`CoapClient/CoapClient/SingleConsumerQueue.cs:12, 41, 52-54`

```csharp
Start(new CancellationTokenSource().Token);   // :12 建完即丢，从不 Cancel
...
while (!ct.IsCancellationRequested)           // :41 永远为真
...
catch (OperationCanceledException)            // :52 永不触发
{
    throw new OperationCanceledException("Consumer Work Loop 已取消");
}
```

**影响**：该队列的 worker 线程**无法停止**。与 ISSUE-1 相关 —— 即使补了 `IDisposable`，
这个后台循环也不会退出，进程/线程无法干净收尾。

**建议**：把 `CancellationTokenSource` 提升为字段并在 `Dispose` 时 `Cancel()`，
让 worker 能随宿主一起停止。

---

## ISSUE-4 另有两种语义仍挤在 `InvalidOperationException`

**位置**：
| 位置 | 语义 |
| --- | --- |
| `ThinAdsServer.cs:123` | 调用方在 `CoapSumCommandItem` 中传了重复符号 |
| `AckPredicateFactory.cs:66` | ACK 值类型不支持比较（用了排序类谓词） |

**影响**：`Collided`、`SymbolNotFound`、**未连接**都已拆出独立类型
（`CoapCommandCollisionException` / `CoapSymbolNotFoundException` / `CoapNotConnectedException`），
但上述两处仍是裸 `InvalidOperationException`，调用方依旧只能靠消息文本区分。

**建议**：按同一种模式补类型。优先级低，可按需处理，不必一次做完。

---

## ISSUE-5 `CommandHandler` 把消息当 `paramName` 传给 `ArgumentNullException`

**位置**：`CoapClient/CoapClient/CommandHandler.cs:24`

```csharp
if (e.UserData == null) throw new ArgumentNullException($"notification.UserData is NULL!");
```

**现象**：`ArgumentNullException` 的单字符串构造函数中，该字符串是**参数名**而非消息。于是：

- `ex.ParamName` = `"notification.UserData is NULL!"` —— 声称存在这个名字的参数；
- `ex.Message` = 默认的 `"Value cannot be null."` —— **真正的诊断信息丢失**。

而且 `UserData` 并不是该方法的参数（签名是 `OnNotificationReceived(INotification e)`），
连 `ParamName` 的语义都对不上。

**影响**：该站点被 `CommandHandler.cs:40-47` 捕获并吞掉（`e.UserData is CommandContext`
在 null 时为假），到不了调用方，所以危害有限。但按此写法排查日志时看不到有效信息。

**建议**：改为 `InvalidOperationException`，消息写清上下文
（handle、变量名等），例如：
```csharp
throw new InvalidOperationException(
    $"Notification received with null UserData. Handle: {e.Handle}");
```

---

## ISSUE-6 测试代码硬编码内网 IP

**位置**：`CoapClient/CoapTestConsole/Program.cs:14`

```csharp
client.Connect("172.17.137.1.1.1", 851);
```

**影响**：个人环境的 AMS NetId 已随提交 `9d104a8` 进入仓库。对他人无意义，
且暴露内网拓扑信息。

**建议**：从环境变量或本地配置文件读取；在 `ConnectInfo.md`（当前为空文件）中
记录配置方式，并考虑把该文件加入 `.gitignore`。

---

## ISSUE-7 实现细节被声明为 `public`

**位置**：`CoapClient/CoapClient/SingleConsumerQueue.cs:5`、`CoapClient/CoapClient/ITimeOutItem.cs:3`

`SingleConsumerQueue<T>` 与 `ITimeoutItem` 都是内部实现细节，却声明为 `public`，
会进入公开 API 表面（且无任何文档）。

**影响**：使用者可能误以为这些是可依赖的公开契约；一旦改动就是破坏性变更。

**建议**：改为 `internal`。因为当前 NuGet 包（0.2.0）已经公开了它们，严格来说
这是个破坏性变更，需要配合一次 minor 版本升级说明。

---

## 附：已修对照表

仅供对照，不算未决项。

| 问题 | 处理 |
| --- | --- |
| `Collided` 与 `SymbolNotFound` 同为 `InvalidOperationException` | 拆为 `CoapCommandCollisionException` / `CoapSymbolNotFoundException`（均继承 `InvalidOperationException` 以保持兼容） |
| 省略 `timeSpan` 时无超时，命令永久挂起 | 新增 `SimpleCoapClient.DefaultAckTimeout`（1 分钟）兜底，`AckRequired` 的命令必有超时 |
| Success 终态因 `GetResult` 抛异常而 fault，调用方可能误重试 | 新增 `CoapResultConversionException`，显式说明"命令已成功、PLC 动作已发生，勿盲目重试" |
| `SendAndWaitAck` 未校验 `command == null` / 空 `AckVarName` | 补 `ArgumentNullException` / `ArgumentException` 守卫 |
| `RouteCommand` 未检查 `symbolResult`，失败时 NRE | 补守卫，抛 `CoapSymbolNotFoundException` 并记录 ADS 错误码 |
| 超时队列背压会从 `SendAndWaitAck` **同步抛** | 改无界队列（真正的上限是在途命令数，已被 `CommandInFlightManager` 约束）；写入失败仅作安全网保留 |
| `GetSymbol` 缺符号表 null 守卫（未连接时 NRE） | 抽出共享 `RequireSymbolTable()`，`GetSymbol` / `VariableExists` 共用；未连接抛 `CoapNotConnectedException` |
| `TrySetBusinessResult` 在 cleanup 失败时丢弃原业务异常 | 改为 `AggregateException` 合并，与 `TryFinish` 的既有写法一致 |
| `GetServerStatusString` 失败时不记日志 | 补齐与其他公开方法一致的 try/catch + `CoapLog.Error` |

