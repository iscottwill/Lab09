using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using SerialDataGUI.Models;

namespace SerialDataGUI.Services;

public sealed class SerialTransport : TransportBase
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialPort? _port;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;

    public override bool IsConnected => _port?.IsOpen == true;

    public override Task ConnectAsync(
        TransportEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        if (endpoint.Kind != TransportKind.UsbUartLoopback)
        {
            throw new ArgumentException("SerialTransport requires a USB/UART endpoint.", nameof(endpoint));
        }

        if (IsConnected)
        {
            throw new InvalidOperationException("Serial transport is already connected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var portName = string.IsNullOrWhiteSpace(endpoint.Address)
            ? "/dev/ttyUSB0"
            : endpoint.Address.Trim();

        var port = new SerialPort(
            portName,
            endpoint.BaudRate,
            Parity.None,
            8,
            StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 500,
            WriteTimeout = 2000,
        };

        try
        {
            port.Open();
        }
        catch
        {
            port.Dispose();
            throw;
        }

        _port = port;
        _readCancellation = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(port, _readCancellation.Token));

        return Task.CompletedTask;
    }

    public override async Task WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            throw new InvalidOperationException("Serial transport is not connected.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async Task DisconnectAsync()
    {
        var port = _port;
        _port = null;

        var cancellation = _readCancellation;
        _readCancellation = null;

        var readTask = _readTask;
        _readTask = null;

        cancellation?.Cancel();

        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch
            {
            }

            port.Dispose();
        }

        if (readTask is not null)
        {
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        cancellation?.Dispose();
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await port.BaseStream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                OnDataReceived(data);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OnFaulted($"Serial read error: {ex.Message}");
            OnDisconnected();
        }
    }
}
