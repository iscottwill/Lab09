using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SerialDataGUI.Models;
using SerialDataProtocol;

namespace SerialDataGUI.Services;

public sealed class SimulatorTransport : TransportBase
{
    private readonly CommandFramer _commandFramer = new();
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;
    private bool[] _outputStates = new bool[ProtocolConstants.IoCount];
    private int _packetNumber;
    private double _phase;
    private volatile bool _connected;

    public override bool IsConnected => _connected;

    public override Task ConnectAsync(
        TransportEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        if (endpoint.Kind != TransportKind.Simulator)
        {
            throw new ArgumentException("SimulatorTransport requires a simulator endpoint.", nameof(endpoint));
        }

        if (_connected)
        {
            throw new InvalidOperationException("Simulator transport is already connected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        _connected = true;
        _cancellation = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cancellation.Token));

        return Task.CompletedTask;
    }

    public override Task WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Simulator transport is not connected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var text = Encoding.ASCII.GetString(data.Span);
        var errors = new List<string>();
        IReadOnlyList<CommandPacket> packets;

        lock (_writeLock)
        {
            packets = _commandFramer.Append(text, errors);
        }

        foreach (var error in errors)
        {
            OnFaulted($"Simulator command error: {error}");
        }

        foreach (var packet in packets)
        {
            _outputStates = packet.OutputStates;
        }

        return Task.CompletedTask;
    }

    public override async Task DisconnectAsync()
    {
        if (!_connected)
        {
            return;
        }

        _connected = false;

        var cancellation = _cancellation;
        _cancellation = null;

        var loopTask = _loopTask;
        _loopTask = null;

        cancellation?.Cancel();

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        cancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(ProtocolConstants.TelemetryIntervalMilliseconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                EmitTelemetry();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OnFaulted($"Simulator error: {ex.Message}");
            OnDisconnected();
        }
    }

    private void EmitTelemetry()
    {
        _phase += 0.18;

        var adcMillivolts = new int[ProtocolConstants.AdcCount];
        for (var i = 0; i < adcMillivolts.Length; i++)
        {
            var value = 1650 + (int)(Math.Sin(_phase + (i * 0.55)) * 1500);
            adcMillivolts[i] = Math.Clamp(value, 0, ProtocolConstants.AdcMaximumMillivolts);
        }

        var outputs = _outputStates;
        var inputStates = new[]
        {
            (_packetNumber / 20) % 2 == 0,
            outputs[0] || outputs[1],
            (_packetNumber / 10) % 2 == 1,
            outputs[3],
        };

        var frame = SerialProtocol.BuildTelemetry(_packetNumber, adcMillivolts, inputStates);
        _packetNumber = (_packetNumber + 1) % 1000;

        OnDataReceived(Encoding.ASCII.GetBytes(frame));
    }
}
