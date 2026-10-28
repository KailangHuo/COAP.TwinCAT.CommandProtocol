using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace CoapClient
{
    /// <summary>
/// 生命周期示意:
/// 1. 创建 -> 无Ack -> 立刻排队发, 发走时setResult
/// 2. 创建 -> 有Ack -> 握手 -> 握手回调经过OnServerRecall -> 判断是否Valid, Valid的话直接TryFinish, 否 -> 设置握手判断为已握手, 然后发送真实的命令 -> 命令回执经过OnServerRecall -> 判断valid, 如真则finish, 如假没通过则继续等下一次回调, 直到有回执或者超时
///
/// 超时概念示意:
/// 超时时会将超时时的状态和最后一次的值一起抛出, 但是不会阻拦命令的生命周期, 只是后续不再处理
/// 超时时不做任何清理, 不关心硬件层的状态, 由调用方决定如何处理
///
/// 线程安全性:
/// HandShake, EnqueueCommand, OnServerRecall处于同一线性处理流程, 彼此不会并发
/// 只有 OnTimeout可能与上述流程并发, 但Timeout的处理只会设置状态, 不会修改命令的生命周期, 也不会清理资源, 因此不会与上述流程冲突
/// </summary>
internal class CommandContext : ITimeoutItem
{
    public CommandContext()
    {
        this.AckTaskSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.NotificationValueRawByteArr = null;
        this.FinishingGate = 0;
        this.HandShakeGate = 0;
        this.DispatchGate = 0;
        this.InFlightCleanupGate = 0;
        this.NotificationCleanupGate = 0;
        this.Stage = (int)CommandContextStage.Queued;
    }

    private int Stage;

    private int FinishingGate;
    
    private int HandShakeGate;

    private int DispatchGate;

    private int InFlightCleanupGate;

    private int NotificationCleanupGate;

    public ICoapCommand Command = null!;

    /// <summary>Connection generation captured when the command was accepted.</summary>
    public long Generation;

    /// <summary>
    /// Returns true while the captured generation is still the current connected epoch.
    /// Null means "not generation-aware", which keeps non-COAP test/diagnostic paths working.
    /// </summary>
    public Func<long, bool>? IsGenerationValid;

    public ISymbol Symbol = null!;

    public IAdsSymbol AckSymbol = null!;

    public bool IsGenerationCurrent()
    {
        return this.IsGenerationValid == null || this.IsGenerationValid(this.Generation);
    }
    
    public TaskCompletionSource<object> AckTaskSource;

    private uint NotificationHandle;

    private byte[] NotificationValueRawByteArr;

    public Action<CommandContext>? UnregisterCommandAction { get; set; }

    public Action<uint>? DeleteNotificationHandleAction { get; set; }

    public Func<CommandContext, Task<byte[]>>? TimeoutAckReadAction { get; set; }

    public TimeSpan? TimeSpan { get; set; }

    public CancellationToken CancelToken { get; set; }

    public CancellationTokenRegistration CancelRegistration { get; set; }

    public void SetNotificationHandle(uint handleIndex)
    {
        Volatile.Write(ref this.NotificationHandle, handleIndex);
    }

    public void SetValue(byte[] valueByteArr)
    {
        Volatile.Write(ref this.NotificationValueRawByteArr, valueByteArr);
    }

    public void CommandCollides()
    {
        this.TryFinish(CommandFinishResult.Collided);
    }

    public void RegisterCancellation(CancellationToken cancellationToken)
    {
        this.CancelToken = cancellationToken;
        if (cancellationToken.IsCancellationRequested)
        {
            this.TryFinish(CommandFinishResult.Cancelled);
            return;
        }

        this.CancelRegistration = cancellationToken.Register(
            () => this.TryFinish(CommandFinishResult.Cancelled));
    }

    // Complete the command with a business-layer failure without crashing worker threads.
    public void TrySetBusinessResult(Exception exception)
    {
        if (Interlocked.CompareExchange(ref this.FinishingGate, 1, 0) != 0) return;

        try
        {
            this.TryCleanupResources();
        }
        catch (Exception cleanupException)
        {
            // Report both. Previously only the cleanup failure was surfaced and the original
            // business exception was discarded, so the caller never learned the real reason the
            // command failed. TryFinish merges the same way - keep the two consistent.
            CoapLog.Error($"<{this.Command.AckVarName}> Cleanup failed while reporting a command failure.",
                          cleanupException);
            this.AckTaskSource.TrySetException(new AggregateException(exception, cleanupException));
            return;
        }

        this.AckTaskSource.TrySetException(exception);
    }

    public void OnHandShake()
    {
        Volatile.Write(ref this.Stage, (int)CommandContextStage.HandShakeSent);
    }
    
    public void OnDispatch()
    {
        Volatile.Write(ref this.Stage, (int)CommandContextStage.Dispatched);
    }

    public bool TryBeginDispatch()
    {
        if (this.IsFinished()) return false;
        if (Interlocked.CompareExchange(ref this.DispatchGate, 1, 0) != 0) return false;

        this.OnDispatch();
        return true;
    }
    
    public void OnDispatched()
    {
        if (!this.Command.AckRequired)
        {
            Volatile.Write(ref this.NotificationValueRawByteArr, BitConverter.GetBytes(true));
            this.TryFinish(CommandFinishResult.Success);
        }
    }

    public void OnTimeout()
    {
        if (!this.Command.AckRequired) return;
        _ = this.VerifyAckBeforeTimeoutAsync().ContinueWith(
            t =>
            {
                if (t.Exception != null)
                    CoapLog.Error("Timeout ACK verification task failed.", t.Exception);
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task VerifyAckBeforeTimeoutAsync()
    {
        if (this.IsFinished()) return;

        try
        {
            if (!this.IsGenerationCurrent())
            {
                this.TryFinish(CommandFinishResult.Timeout);
                return;
            }

            if (this.TimeoutAckReadAction != null)
            {
                CoapLog.Info($"<{this.Command.AckVarName}> timeout in receiving, Client-Read triggered!");
                byte[] rawBytes = await this.TimeoutAckReadAction(this).ConfigureAwait(false);

                // The connection may have changed while the read was in flight.
                if (!this.IsGenerationCurrent())
                {
                    this.TryFinish(CommandFinishResult.Timeout);
                    return;
                }

                this.SetValue(rawBytes);

                if (this.TryValidCommandOnServerRecall()) return;
            }

            this.TryFinish(CommandFinishResult.Timeout);
        }
        catch (Exception e)
        {
            this.TryFinish(CommandFinishResult.Timeout, e);
        }
    }

    public bool TryHandShake()
    {
        if (this.IsFinished()) return false;
        CoapLog.Info($"<{this.Command.AckVarName}> Hand-Shack!");
        return Interlocked.CompareExchange(ref this.HandShakeGate, 1, 0) == 0;
    }
    
    public bool TryValidCommandOnServerRecall()
    {
        if (this.IsFinished())
        {
            this.TryCleanupNotificationHandle();
            return false;
        }

        // A late notification from an old connection must not complete a command that belongs
        // to a newer generation. Do not touch the handle: the current connection may have
        // reused that handle number.
        if (!this.IsGenerationCurrent())
            return false;

        if (this.ValidateAck())
        {
            this.TryFinish(CommandFinishResult.Success);
            return true;
        }

        return false;
    }

    public void TryCleanupResources()
    {
        this.CancelRegistration.Dispose();
        this.TryCleanupInFlight();
        this.TryCleanupNotificationHandle();
    }

    public void TryCleanupNotificationHandle(uint handleIndex)
    {
        if (!this.Command.AckRequired || handleIndex == 0) return;
        if (!this.IsGenerationCurrent()) return;
        if (Interlocked.CompareExchange(ref this.NotificationCleanupGate, 1, 0) != 0) return;
        CoapLog.Info("Try remove handle...");
        this.DeleteNotificationHandleAction?.Invoke(handleIndex);
        CoapLog.Info("Handle removed");
    }

    private void TryCleanupInFlight()
    {
        if (!this.Command.AckRequired) return;
        if (Interlocked.CompareExchange(ref this.InFlightCleanupGate, 1, 0) != 0) return;
        this.UnregisterCommandAction?.Invoke(this);
    }

    private void TryCleanupNotificationHandle()
    {
        uint handleIndex = Volatile.Read(ref this.NotificationHandle);
        this.TryCleanupNotificationHandle(handleIndex);
    }

    private bool ValidateAck()
    {
        byte[] value = Volatile.Read(ref this.NotificationValueRawByteArr);
        return this.Command.ValidateAck(value);
    }
    
    private void TryFinish(CommandFinishResult result, Exception? triggerException = null)
    {
        if (Interlocked.CompareExchange(ref this.FinishingGate, 1, 0) != 0) return;
        this.CancelRegistration.Dispose();
        CoapLog.Info("CommandContext TryFinish");

        Exception? finishException = triggerException;
        if (result != CommandFinishResult.Collided)
        {
            try
            {
                this.TryCleanupResources();
            }
            catch (Exception e)
            {
                finishException = finishException == null
                    ? e
                    : new AggregateException(finishException, e);
            }
        }

        CommandContextStage stage = (CommandContextStage)Volatile.Read(ref this.Stage);

        switch (result)
        {
            case CommandFinishResult.Success:
                object valueObj = null;
                byte[] rawAckBytes = Volatile.Read(ref this.NotificationValueRawByteArr);

                if (finishException == null)
                {
                    try
                    {
                        valueObj = this.Command.GetResult(rawAckBytes);
                    }
                    catch (Exception e)
                    {
                        // The ACK predicate was already satisfied, so the PLC-side action has
                        // happened. Report a dedicated type so the caller cannot mistake this for a
                        // failed command and retry a write that already took effect.
                        CoapLog.Error($"<{this.Command.AckVarName}> Ack satisfied but GetResult threw. " +
                                      $"The PLC-side action has already taken effect - do not retry blindly.");
                        this.AckTaskSource.TrySetException(
                            new CoapResultConversionException(this.Command.AckVarName, rawAckBytes, e));
                        return;
                    }
                }

                if (finishException != null)
                {
                    this.AckTaskSource.TrySetException(finishException);
                    return;
                }
                CoapLog.Info("CommandContext TrySetResult");
                this.AckTaskSource.TrySetResult(valueObj);
                CoapLog.Info("CommandContext SetResult-ed");
                break;
            case CommandFinishResult.Timeout:
                string lastValueMessage = "NULL";
                byte[] lastRawBytes = Volatile.Read(ref this.NotificationValueRawByteArr);
                if (lastRawBytes != null)
                {
                    try
                    {
                        object lastValueObj = this.Command.GetResult(lastRawBytes);
                        lastValueMessage = lastValueObj?.ToString() ?? "NULL";
                    }
                    catch (Exception e)
                    {
                        lastValueMessage = $"Unable to parse last value: {e.Message}";
                    }
                }

                this.AckTaskSource.TrySetException(new TimeoutException($"Timeout! Stage at {stage}. " +
                                                                        $"Ack: {this.Command.AckVarName}. " +
                                                                        $"LastValue: {lastValueMessage}",
                    finishException));
                break;
            case CommandFinishResult.Collided:
                this.AckTaskSource.TrySetException(new CoapCommandCollisionException(this.Command.AckVarName));
                break;
            case CommandFinishResult.Cancelled:
                this.AckTaskSource.TrySetCanceled(this.CancelToken);
                break;
        }
        
    }

    public bool IsFinished()
    {
        return Volatile.Read(ref this.FinishingGate) != 0;
    }

}

internal enum CommandContextStage
{
    Queued,
    HandShakeSent,
    Dispatched,
}

internal enum CommandFinishResult
{
    Success,
    Timeout,
    Collided,
    Cancelled,
}
}