using System.Collections.Concurrent;

namespace CoapClient
{
    public class SingleConsumerQueue<T> : IDisposable
    {
        public SingleConsumerQueue(Action<T> consumerAction)
        {
            this._consumerAction = consumerAction;
            this._queue = new ConcurrentQueue<T>();
            this._semaphore = new SemaphoreSlim(0);
            this._cts = new CancellationTokenSource();
            this._worker = Task.Run(() => this.WorkerAsyncTask(this._cts.Token), this._cts.Token);

            _ = this._worker.ContinueWith(
                t => CoapLog.Error($"Worker faulted: {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private readonly object _gate = new();

        private readonly ConcurrentQueue<T> _queue;

        private readonly SemaphoreSlim _semaphore;

        private readonly CancellationTokenSource _cts;

        private readonly Task _worker;

        private readonly Action<T> _consumerAction;

        private int _accepting = 1;

        /// <summary>Queues an item. Returns false once the queue has been stopped.</summary>
        public bool EnqueueItem(T item)
        {
            lock (this._gate)
            {
                if (this._accepting == 0)
                    return false;

                this._queue.Enqueue(item);
                this._semaphore.Release();
                return true;
            }
        }

        /// <summary>
        /// Stops the worker, waits for it (bounded), then drains remaining items to
        /// <paramref name="onDropped"/>. Idempotent.
        /// </summary>
        public void Stop(TimeSpan timeout, Action<T>? onDropped)
        {
            lock (this._gate)
            {
                if (this._accepting == 0)
                    return;

                this._accepting = 0;
            }

            this._cts.Cancel();

            try
            {
                // Wake the worker if it is blocked on WaitAsync.
                this._semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // The semaphore is only used as a signal; ignore.
            }

            bool joined = false;
            try
            {
                joined = this._worker.Wait(timeout);
            }
            catch (AggregateException e)
            {
                // A faulted worker is already terminated.
                CoapLog.Error("Queue worker ended with an exception.", e);
                joined = true;
            }

            if (!joined)
                CoapLog.Error("Queue worker did not stop within the dispose timeout.");

            while (this._queue.TryDequeue(out T? item))
            {
                try
                {
                    onDropped?.Invoke(item!);
                }
                catch (Exception e)
                {
                    CoapLog.Error("Dropping a queued item failed.", e);
                }
            }

            // Only release OS resources once the worker is definitely gone. If the join timed
            // out, leave them alive; the process is shutting down or the worker is stuck.
            if (joined)
            {
                this._cts.Dispose();
                this._semaphore.Dispose();
            }
        }

        public void Dispose()
        {
            this.Stop(TimeSpan.FromSeconds(2), null);
        }

        private async Task WorkerAsyncTask(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await this._semaphore.WaitAsync(ct).ConfigureAwait(false);

                    if (this._queue.TryDequeue(out T? item))
                    {
                        this._consumerAction?.Invoke(item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception e)
            {
                CoapLog.Error(e.Message);
                throw;
            }
        }
    }
}
