namespace CoapClient
{
    public interface ITimeoutItem
    {
        TimeSpan? TimeSpan { get; set; }
    
        /// <summary>
        /// Thread Switched Already, so no need to worry about thread safety here. Just implement the timeout logic.
        /// </summary>
        void OnTimeout();

    
    }
}