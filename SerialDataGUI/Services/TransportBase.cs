using System;
using System.Threading;
using System.Threading.Tasks;
using SerialDataGUI.Models;

namespace SerialDataGUI.Services;

public abstract class TransportBase : ITransport
{
    public abstract bool IsConnected { get; }

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Faulted;
    public event Action? Disconnected;

    public abstract Task ConnectAsync(
        TransportEndpoint endpoint,
        CancellationToken cancellationToken = default);

    public abstract Task WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    public abstract Task DisconnectAsync();

    protected void OnDataReceived(byte[] data)
    {
        DataReceived?.Invoke(data);
    }

    protected void OnFaulted(string message)
    {
        Faulted?.Invoke(message);
    }

    protected void OnDisconnected()
    {
        Disconnected?.Invoke();
    }

    public virtual async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
