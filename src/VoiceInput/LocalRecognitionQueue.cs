namespace VoiceInput;

internal sealed class LocalRecognitionQueue
{
    private readonly object gate = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int waitingVoice;
    private bool running;

    internal async Task<T> RunAsync<T>(bool voice, Action onWait, Func<Task<T>> work, CancellationToken token)
    {
        bool waiting = voice;
        if (voice) lock (gate) waitingVoice++;
        bool acquired = false;
        try
        {
            bool reported = false;
            while (!acquired)
            {
                token.ThrowIfCancellationRequested();
                Task signal;
                lock (gate)
                {
                    if (!running && (voice || waitingVoice == 0))
                    {
                        acquired = running = true;
                        if (waiting) waitingVoice--;
                        waiting = false;
                    }
                    signal = changed.Task;
                }
                if (acquired) break;
                if (!reported) onWait();
                reported = true;
                await signal.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            return await work();
        }
        finally
        {
            lock (gate)
            {
                if (waiting) waitingVoice--;
                if (acquired) running = false;
                changed.TrySetResult();
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
