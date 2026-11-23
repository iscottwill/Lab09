using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialDataGUI.Models;
using SerialDataGUI.Services;
using SerialDataProtocol;

namespace SerialDataGUI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const int MaximumHistoryLength = 100;

    private readonly TelemetryFramer _telemetryFramer = new();
    private readonly List<string> _history = [];
    private ITransport? _transport;

    public IReadOnlyList<TransportOption> TransportOptions { get; } =
    [
        new(TransportKind.Simulator, "Simulator"),
        new(TransportKind.UsbUartLoopback, "USB / UART (FTDI)"),
        new(TransportKind.Bluetooth, "Bluetooth"),
    ];

    [ObservableProperty] public partial TransportOption SelectedTransport { get; set; }
    [ObservableProperty] public partial string Endpoint { get; set; } = "";
    [ObservableProperty] public partial bool IsEndpointEditable { get; set; }
    [ObservableProperty] public partial bool CanChangeTransport { get; set; } = true;
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string ConnectionButtonText { get; set; } = "Connect";
    [ObservableProperty] public partial string ConnectionStatus { get; set; } = "Disconnected";
    [ObservableProperty] public partial string LastError { get; set; } = "";

    [ObservableProperty] public partial bool D08 { get; set; }
    [ObservableProperty] public partial bool D09 { get; set; }
    [ObservableProperty] public partial bool D10 { get; set; }
    [ObservableProperty] public partial bool D11 { get; set; }

    [ObservableProperty] public partial bool ShowHistory { get; set; }

    [ObservableProperty] public partial string RawPacketSend { get; set; } = "";
    [ObservableProperty] public partial string RawPacketReceived { get; set; } = "";
    [ObservableProperty] public partial string RawPacketsReceived { get; set; } = "";

    [ObservableProperty] public partial string PacketNumber { get; set; } = "---";
    [ObservableProperty] public partial string InputStateText { get; set; } = "----";
    [ObservableProperty] public partial int ReceivedPacketCount { get; set; }
    [ObservableProperty] public partial int InvalidPacketCount { get; set; }

    [ObservableProperty] public partial string A0 { get; set; } = "...";
    [ObservableProperty] public partial string A1 { get; set; } = "...";
    [ObservableProperty] public partial string A2 { get; set; } = "...";
    [ObservableProperty] public partial string A3 { get; set; } = "...";
    [ObservableProperty] public partial string A4 { get; set; } = "...";
    [ObservableProperty] public partial string A5 { get; set; } = "...";

    public MainViewModel()
    {
        SelectedTransport = TransportOptions[0];
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var transport = _transport;
        if (transport is null || !transport.IsConnected)
        {
            LastError = "Connect before sending.";
            return;
        }

        try
        {
            var command = SerialProtocol.BuildCommand([D08, D09, D10, D11]) + "\r\n";
            await transport.WriteAsync(Encoding.ASCII.GetBytes(command));
            RawPacketSend = command.TrimEnd('\r', '\n');
            LastError = "";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private bool CanSend()
    {
        return IsConnected;
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (IsConnected)
            {
                await DisconnectTransportAsync(true);
            }
            else
            {
                await ConnectTransportAsync();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ConnectionStatus = "Disconnected";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanToggleConnection()
    {
        return !IsBusy;
    }

    private async Task ConnectTransportAsync()
    {
        var option = SelectedTransport;
        var endpoint = option.Kind == TransportKind.Simulator
            ? TransportEndpoint.For(option.Kind)
            : new TransportEndpoint(option.Kind, Endpoint.Trim());

        if (string.IsNullOrWhiteSpace(endpoint.Address))
        {
            LastError = "Endpoint is required.";
            return;
        }

        var transport = TransportFactory.Create(option.Kind);
        transport.DataReceived += OnTransportDataReceived;
        transport.Faulted += OnTransportFaulted;
        transport.Disconnected += OnTransportDisconnected;

        _transport = transport;
        LastError = "";
        ConnectionStatus = $"Connecting to {option.DisplayName}...";

        try
        {
            await transport.ConnectAsync(endpoint);
            IsConnected = true;
            ConnectionStatus = $"Connected: {option.DisplayName}";
            ResetTelemetry();
        }
        catch (Exception ex)
        {
            _transport = null;
            DetachTransport(transport);
            await transport.DisposeAsync();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            LastError = ex.Message;
        }
    }

    private async Task DisconnectTransportAsync(bool updateStatus)
    {
        var transport = _transport;
        _transport = null;

        if (transport is null)
        {
            IsConnected = false;
            if (updateStatus)
            {
                ConnectionStatus = "Disconnected";
            }

            return;
        }

        DetachTransport(transport);

        try
        {
            await transport.DisconnectAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        await transport.DisposeAsync();
        IsConnected = false;

        if (updateStatus)
        {
            ConnectionStatus = "Disconnected";
        }
    }

    private void DetachTransport(ITransport transport)
    {
        transport.DataReceived -= OnTransportDataReceived;
        transport.Faulted -= OnTransportFaulted;
        transport.Disconnected -= OnTransportDisconnected;
    }

    private void OnTransportDataReceived(byte[] data)
    {
        Dispatcher.UIThread.Post(() => ProcessReceivedData(data));
    }

    private void ProcessReceivedData(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data);

        foreach (var line in _telemetryFramer.Append(text))
        {
            if (SerialProtocol.TryParseTelemetry(line, out var packet, out var error))
            {
                ApplyTelemetry(packet!, line);
            }
            else
            {
                InvalidPacketCount++;
                LastError = error;
            }
        }
    }

    private void ApplyTelemetry(TelemetryPacket packet, string rawFrame)
    {
        PacketNumber = packet.PacketNumber.ToString("D3", CultureInfo.InvariantCulture);
        InputStateText = packet.InputStateText;
        A0 = $"{packet.AdcMillivolts[0]} mV";
        A1 = $"{packet.AdcMillivolts[1]} mV";
        A2 = $"{packet.AdcMillivolts[2]} mV";
        A3 = $"{packet.AdcMillivolts[3]} mV";
        A4 = $"{packet.AdcMillivolts[4]} mV";
        A5 = $"{packet.AdcMillivolts[5]} mV";
        RawPacketReceived = rawFrame;
        ReceivedPacketCount++;

        _history.Add(rawFrame);
        if (_history.Count > MaximumHistoryLength)
        {
            _history.RemoveAt(0);
        }

        if (ShowHistory)
        {
            RawPacketsReceived = string.Join(Environment.NewLine, _history);
        }
    }

    private void OnTransportFaulted(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LastError = message;
            ConnectionStatus = "Transport fault";
        });
    }

    private void OnTransportDisconnected()
    {
        Dispatcher.UIThread.Post(() => _ = HandleTransportDisconnectedAsync());
    }

    private async Task HandleTransportDisconnectedAsync()
    {
        if (_transport is null)
        {
            return;
        }

        await DisconnectTransportAsync(false);
        ConnectionStatus = "Disconnected: transport lost";
    }

    private void ResetTelemetry()
    {
        _telemetryFramer.Reset();
        _history.Clear();
        PacketNumber = "---";
        InputStateText = "----";
        RawPacketReceived = "";
        RawPacketsReceived = "";
        ReceivedPacketCount = 0;
        InvalidPacketCount = 0;
        A0 = "...";
        A1 = "...";
        A2 = "...";
        A3 = "...";
        A4 = "...";
        A5 = "...";
    }

    partial void OnSelectedTransportChanged(TransportOption value)
    {
        Endpoint = TransportEndpoint.For(value.Kind).Address;
        UpdateEndpointEditState();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectionButtonText = value ? "Disconnect" : "Connect";
        CanChangeTransport = !value;
        UpdateEndpointEditState();
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowHistoryChanged(bool value)
    {
        if (value)
        {
            RawPacketsReceived = string.Join(Environment.NewLine, _history);
        }
    }

    private void UpdateEndpointEditState()
    {
        IsEndpointEditable = !IsConnected && SelectedTransport.Kind != TransportKind.Simulator;
    }
}
