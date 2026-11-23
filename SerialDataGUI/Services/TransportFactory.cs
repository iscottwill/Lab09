using System;
using SerialDataGUI.Models;

namespace SerialDataGUI.Services;

public static class TransportFactory
{
    public static ITransport Create(TransportKind kind)
    {
        return kind switch
        {
            TransportKind.UsbUartLoopback => new SerialTransport(),
            TransportKind.Bluetooth => new BluetoothTransport(),
            TransportKind.Simulator => new SimulatorTransport(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transport kind."),
        };
    }
}
