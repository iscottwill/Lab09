using System;
using System.Threading;
using System.Threading.Tasks;
using SerialDataGUI.Models;

namespace SerialDataGUI.Services;

public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }

    event Action<byte[]>? DataReceived;
    event Action<string>? Faulted;
    event Action? Disconnected;

    Task ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default);
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}
