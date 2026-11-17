using System.IO;
using NAudio.Wave;

namespace VoiceInput;

internal sealed class AudioCapture : IDisposable
{
    private readonly WaveInEvent input;
    private readonly WaveFileWriter writer;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private bool disposed;
    private Exception? writeError;

    public AudioCapture(string path)
    {
        input = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
        try { writer = new WaveFileWriter(path, input.WaveFormat); }
        catch { input.Dispose(); throw; }
        input.DataAvailable += OnData;
        input.RecordingStopped += (_, e) =>
        {
            lock (gate)
            {
                var error = writeError ?? e.Exception;
                try { writer.Dispose(); }
                catch (Exception ex) { error ??= ex; }
                if (error is null) stopped.TrySetResult();
                else stopped.TrySetException(error);
            }
        };
    }

    public Task Completion => stopped.Task;

    public void Start() => input.StartRecording();
    public void Stop() => input.StopRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (gate)
        {
            if (disposed || writeError is not null) return;
            try { writer.Write(e.Buffer, 0, e.BytesRecorded); }
            catch (Exception ex) { writeError = ex; }
        }
        if (writeError is not null) input.StopRecording();
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; }
        try { input.Dispose(); }
        finally { lock (gate) { writer.Dispose(); } }
    }
}
