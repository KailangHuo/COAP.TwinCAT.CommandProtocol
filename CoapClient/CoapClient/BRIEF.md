# BRIEF

这是一个对当前项目的简述, 阅读这个的AGENT应该以这个简述结合代码来理解项目
1. 这是一个用来给C# 上层软件和 TwinCAT Server沟通的中间件
2. 这个中间件的主要能力是将传统的 "上层软件修改DO, 轮询底层DI变更" 的通信形式替换成了 以PLC使用"AdsNotification" 回调上层的通信形式
3. 基本上这是一种将 "同步非阻塞" 替换为 "异步非阻塞"的一种通信形式
4. 将通信形式由状态同步变更为了命令式调用, 具有命令的生命周期, 具体的表现形式见下文:
5. AGENT在写README的时候需要列出调用方可以使用的接口函数, 可以使用继承的基类以及可以实现的接口, 在AckPredicate中还是用了PredicateType, 也需要列一张表格说明
6. 需要强调: 注册SERVER的监听handle时会存在高负载下首帧丢失的情况, 所以需要在注册监听时主动读取来争抢握手权, 确保握手后才下发命令, 在获取DI回调时也在超时后会主动读取, 以确保命令生命周期完整
***

## 前提假设
使用条件必须满足如下形式:
一个完整的操作动作: 上层软件修改 DO, PLC task做出相应执行后修改DI.
以DO作为命令开始, DI作为命令执行结果
***

## COAP的通信流程
上层通过COAP向PLC传递以下信息: 要修改哪个DO, 监听哪个DI变更, 且DI为何值.
发COAP命令的整体流程:
1. 注册对应的 DI/AI 监听句柄
2. 等待Server回推监听注册成功的首帧握手回调, 高负载下可能丢失首帧, 所以并发主动read该值来确保握手
3. 握手成功以后下发 DO/AO 命令
4. PLC 做动
5. PLC 做动成功后修改对应的 DI/AI, 触发OnChange变更, 调用AdsNotification推给上层
6. 上层获得notification, 校验 DI/AI 为预期值后销毁句柄, 命令周期结束

使用了 async/await 异步编码来确保整体流程的异步特性, 从而维护命令的顺序性
见示例代码
***

## 示例代码
```
// replace with your own DI var
var diName = "PC_GVAR.DI_AT_POS";

// replace with your own DO var
VariableWriteItem doTrueItem = new VariableWriteItem();
doTrueItem.VarName = "PC_GVAR.DO_TO_POS";
doTrueItem.Value = true;
IAck doAck = new AckPredicate<bool>(diName, PredicateType.Equals, true);
CoapCommandItem doTrueCommand = new CoapCommandItem(doTrueItem,  doAck);
```
***

## 后续的发展
后续会有更多的特性:
1. 长期监听某一变量的Observation模式
2. 可以给一个命令设置多个ACK的多ACK模式
***
