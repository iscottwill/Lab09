using System;
using SerialDataProtocol;

namespace SerialDataGUI.Models;

public sealed record TransportEndpoint(
    TransportKind Kind,
    string Address,
    int BaudRate = ProtocolConstants.BaudRate)
{
    public static TransportEndpoint For(TransportKind kind)
    {
        return kind switch
        {
            TransportKind.UsbUartLoopback => new TransportEndpoint(kind, "/dev/ttyUSB0"),
            TransportKind.Bluetooth => new TransportEndpoint(kind, "MeadowSolarIan"),
            TransportKind.Simulator => new TransportEndpoint(kind, "simulator"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transport kind."),
        };
    }
}
