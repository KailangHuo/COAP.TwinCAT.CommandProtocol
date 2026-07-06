using System.Diagnostics;
using System.Threading.Channels;

namespace CoapClient
{
    internal class TimeoutScheduler
{
    public TimeoutScheduler(CancellationToken token)
    {
        this.TimeoutItemChannel = Channel.CreateBounded<ITimeoutItem>(
            new BoundedChannelOptions(500)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        this.TimeoutMinHeap = new PriorityQueue<(ITimeoutItem Item, long DeadlineTicks), long>();
        this.WorkerEvent = new AutoResetEvent(false);
        this.WorkerTask = Task.Run(() => RunWorkerLoop(token), token);
    }

    private readonly Channel<ITimeoutItem> TimeoutItemChannel;

    private readonly PriorityQueue<(ITimeoutItem Item, long DeadlineTicks), long> TimeoutMinHeap;

    private readonly Task WorkerTask;

    private readonly AutoResetEvent WorkerEvent;

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

        bool success = this.TimeoutItemChannel.Writer.TryWrite(timeoutItem);
        if (!success)
        {
            throw new InvalidOperationException("Failed to enqueue timeout item into TimeoutItemChannel.");
        }

        this.WorkerEvent.Set();
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