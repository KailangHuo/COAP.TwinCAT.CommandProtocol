using System.Collections.Concurrent;

namespace CoapClient
{
    public class SingleConsumerQueue<T>
    {
        public SingleConsumerQueue(Action<T> consumerAction)
        {
            this._consumerAction = consumerAction;
            this._queue = new ConcurrentQueue<T>();
            this._semaphore = new SemaphoreSlim(0);
            Start(new  CancellationTokenSource().Token);
        }

        private ConcurrentQueue<T> _queue;
    
        private SemaphoreSlim _semaphore;

        private Action<T> _consumerAction;

        private void Start(CancellationToken ct)
        {
        
            var task = Task.Run(()=>WorkerAsyncTask(ct), ct);

            _ = task.ContinueWith(
                t => CoapLog.Error($"Worker faulted: {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        public void EnqueueItem(T item)
        {
            this._queue.Enqueue(item);
            this._semaphore.Release();
        }

        private async Task WorkerAsyncTask(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await this._semaphore.WaitAsync(ct).ConfigureAwait(false);
                
                    if (this._queue.TryDequeue(out var item))
                    {
                        this._consumerAction?.Invoke(item);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException("Consumer Work Loop 已取消");
            }
            catch (Exception e)
            {
                CoapLog.Error(e.Message);
                throw;
            }
        }

    }
}