using System.Diagnostics;
using System.Threading.Channels;

namespace CoapClient
{
    internal class TimeoutScheduler : IDisposable
{
    public TimeoutScheduler(CancellationToken token)
    {
        // Unbounded on purpose.
        //
        // This channel is only a hand-off buffer between the caller thread and the worker, which
        // drains it into TimeoutMinHeap. The real bound on pending items is the number of in-flight
        // commands, and CommandInFlightManager already caps that at one per ACK variable.
        //
        // A bounded channel here was actively harmful: it was configured with
        // BoundedChannelFullMode.Wait, but the writer used TryWrite (which never waits), so the
        // Wait mode never took effect. Once 500 items were queued the write failed and
        // SendAndWaitAck threw InvalidOperationException *synchronously* - not as a task fault -
        // so callers that only awaited the returned task never saw it.
        this.TimeoutItemChannel = Channel.CreateUnbounded<ITimeoutItem>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        this.TimeoutMinHeap = new PriorityQueue<(ITimeoutItem Item, long DeadlineTicks), long>();
        this.WorkerEvent = new AutoResetEvent(false);
        this.WorkerTask = Task.Run(() => RunWorkerLoop(token), token);
    }

    private readonly Channel<ITimeoutItem> TimeoutItemChannel;

    private readonly PriorityQueue<(ITimeoutItem Item, long DeadlineTicks), long> TimeoutMinHeap;

    private readonly Task WorkerTask;

    private readonly AutoResetEvent WorkerEvent;

    private int Stopped;

    private void EnqueueTimeoutItem(ITimeoutItem item)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        TimeSpan? timeoutSpan = item.TimeSpan;
        if (!timeoutSpan.HasValue)
            throw new InvalidOperationException($"Received timeout item without TimeSpan. Item: {item}");

        long deadlineTicks = AddTimeoutToCurrentTimestamp(timeoutSpan.Value);
        var entry = (Item: item, DeadlineTicks: deadlineTicks);

        this.TimeoutMinHeap.Enqueue(entry, deadlineTicks);
        this.WorkerEvent.Set();
    }

    private void DrainIncomingItems()
    {
        while (this.TimeoutItemChannel.Reader.TryRead(out ITimeoutItem item)) 
        {
            if (item == null) break;
            this.EnqueueTimeoutItem(item);
        }
    }

    private void TriggerTimeout(ITimeoutItem item)
    {
        try
        {
            item.OnTimeout();
        }
        catch (Exception exception)
        {
            CoapLog.Error($"Timeout callback failed. Item: {item}", exception);
        }
    }

    public void ScheduleTimeout(ITimeoutItem timeoutItem)
    {
        if (timeoutItem == null)
            throw new ArgumentNullException(nameof(timeoutItem));

        if (Volatile.Read(ref this.Stopped) != 0)
            throw new ObjectDisposedException(nameof(TimeoutScheduler));

        // The channel is unbounded and its writer is never completed, so this cannot fail for
        // capacity reasons. Kept as a safety net: dropping the item silently would leave the
        // command without a timeout and hang the caller, so fail loudly instead.
        if (!this.TimeoutItemChannel.Writer.TryWrite(timeoutItem))
        {
            throw new InvalidOperationException(
                "TimeoutItemChannel rejected the item. The writer should never complete.");
        }

        this.WorkerEvent.Set();
    }

    /// <summary>
    /// Stops the worker, waits for it (bounded), and releases the pending command contexts.
    /// Idempotent.
    /// </summary>
    public void Stop(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref this.Stopped, 1) != 0)
            return;

        this.TimeoutItemChannel.Writer.TryComplete();
        this.WorkerEvent.Set();

        bool joined = false;
        try
        {
            joined = this.WorkerTask.Wait(timeout);
        }
        catch (AggregateException e)
        {
            CoapLog.Error("TimeoutScheduler worker ended with an exception.", e);
            joined = true;
        }

        if (!joined)
            CoapLog.Error("TimeoutScheduler worker did not stop within the dispose timeout.");

        // Completed contexts stay in the heap until their deadline; drop them on dispose.
        this.TimeoutMinHeap.Clear();
        while (this.TimeoutItemChannel.Reader.TryRead(out _))
        {
        }

        if (joined)
            this.WorkerEvent.Dispose();
    }

    public void Dispose()
    {
        this.Stop(TimeSpan.FromSeconds(2));
    }

    private void RunWorkerLoop(CancellationToken token)
    {
        try
        {
            this.WorkerLoop(token);
        }
        catch (Exception exception)
        {
            CoapLog.Error("TimeoutScheduler worker crashed.", exception);
            throw;
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        while (true)
        {
            if (token.IsCancellationRequested)
                return;

            this.DrainIncomingItems();

            if (this.TimeoutMinHeap.Count == 0)
            {
                this.WorkerEvent.WaitOne(100);
                continue;
            }

            var curEntry = this.TimeoutMinHeap.Peek();
            long nowTicks = Stopwatch.GetTimestamp();

            if (curEntry.DeadlineTicks <= nowTicks)
            {
                ITimeoutItem timeoutItem = this.TimeoutMinHeap.Dequeue().Item;
                this.TriggerTimeout(timeoutItem);
                continue;
            }

            int waitTime = GetWaitTimeFromTimestamp(curEntry.DeadlineTicks, nowTicks);
            this.WorkerEvent.WaitOne(waitTime);
        }
    }

    private static long AddTimeoutToCurrentTimestamp(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return Stopwatch.GetTimestamp();

        double deltaTicksDouble = timeout.TotalSeconds * Stopwatch.Frequency;

        if (deltaTicksDouble >= long.MaxValue)
            return long.MaxValue;

        long deltaTicks = (long)Math.Ceiling(deltaTicksDouble);
        long nowTicks = Stopwatch.GetTimestamp();

        if (long.MaxValue - nowTicks < deltaTicks)
            return long.MaxValue;

        return nowTicks + deltaTicks;
    }

    private static int GetWaitTimeFromTimestamp(long deadlineTicks, long nowTicks)
    {
        long remainTicks = deadlineTicks - nowTicks;
        if (remainTicks <= 0)
            return 0;

        double remainMilliseconds = (double)remainTicks * 1000.0 / Stopwatch.Frequency;

        if (remainMilliseconds >= int.MaxValue)
            return int.MaxValue;

        return (int)Math.Ceiling(remainMilliseconds);
    }
}
}