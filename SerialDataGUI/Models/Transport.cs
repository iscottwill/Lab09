namespace SerialDataGUI.Models;

//Enumarate two distinct kinds of transport for the drop down menus
public enum TransportKind
{
    UsbUartLoopback,
    Bluetooth,
    Simulator,
}

//Every object has a build in ToString() function. This overrides that, in order to return a friendly string, rather than the Kind and the Name. 
public sealed record TransportOption(TransportKind Kind, string DisplayName)
{
    public override string ToString() => DisplayName;
} 
