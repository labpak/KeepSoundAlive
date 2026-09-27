using System.IO.Pipes;

namespace KeepSoundAlive.Services;

/// <summary>
/// Makes sure only one copy runs per user. A second launch asks the running copy to show its window and exits;
/// this is also how the window is brought back on desktops without a system tray.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = "KeepSoundAlive-" + Sanitize(Environment.UserName);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    public event Action? ActivationRequested;

    /// <summary>Returns null if another instance is already running (after asking it to show itself).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, Id, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalRunningInstance();
            return null;
        }

        var instance = new SingleInstance(mutex);
        _ = instance.ListenAsync();
        return instance;
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
        }
        catch
        {
            // The other instance may be starting up or shutting down; nothing else to do.
        }
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                ActivationRequested?.Invoke(); // the connection itself is the signal
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    private static string Sanitize(string value) =>
        new(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on a different thread than acquired; the OS cleans up on exit anyway.
        }

        _mutex.Dispose();
    }
}
