using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SerialDataGUI.Models;
using InTheHand.Bluetooth;
using Linux.Bluetooth.Extensions;
using LinuxBluetooth = Linux.Bluetooth;

namespace SerialDataGUI.Services;

public sealed class BluetoothTransport : TransportBase
{
    private const ushort ServiceId = 125;
    private const string ServiceUuid = "0000007d-0000-1000-8000-00805f9b34fb";
    private const int MaximumWriteLength = 40;
    private const int RequestedMtu = 100;

    private static readonly Guid CharacteristicUuid =
        Guid.Parse("5a5a5a56-69ab-4a49-a2f2-de5b292458f3");

    private static readonly string CharacteristicUuidText = CharacteristicUuid.ToString();

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateLock = new();

    private BluetoothDevice? _device;
    private RemoteGattServer? _gatt;
    private GattCharacteristic? _characteristic;

    private LinuxBluetooth.Adapter? _linuxAdapter;
    private LinuxBluetooth.Device? _linuxDevice;
    private LinuxBluetooth.GattCharacteristic? _linuxCharacteristic;
    private int _linuxConnected;

    private bool _disconnecting;

    public override bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                if (_linuxDevice is not null)
                {
                    return Volatile.Read(ref _linuxConnected) == 1;
                }

                return _gatt?.IsConnected == true;
            }
        }
    }

    public override async Task ConnectAsync(
        TransportEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        if (endpoint.Kind != TransportKind.Bluetooth)
        {
            throw new ArgumentException("BluetoothTransport requires a Bluetooth endpoint.", nameof(endpoint));
        }

        if (IsConnected)
        {
            throw new InvalidOperationException("Bluetooth transport is already connected.");
        }

        var address = string.IsNullOrWhiteSpace(endpoint.Address)
            ? "MeadowSolarIan"
            : endpoint.Address.Trim();

        if (OperatingSystem.IsLinux())
        {
            await ConnectLinuxAsync(address, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ConnectInTheHandAsync(address, cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            await WriteLinuxAsync(data, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteInTheHandAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DisconnectAsync()
    {
        LinuxBluetooth.Adapter? linuxAdapter;
        LinuxBluetooth.Device? linuxDevice;
        LinuxBluetooth.GattCharacteristic? linuxCharacteristic;

        BluetoothDevice? device;
        RemoteGattServer? gatt;
        GattCharacteristic? characteristic;

        lock (_stateLock)
        {
            _disconnecting = true;

            linuxAdapter = _linuxAdapter;
            linuxDevice = _linuxDevice;
            linuxCharacteristic = _linuxCharacteristic;
            _linuxAdapter = null;
            _linuxDevice = null;
            _linuxCharacteristic = null;
            Volatile.Write(ref _linuxConnected, 0);

            device = _device;
            gatt = _gatt;
            characteristic = _characteristic;
            _device = null;
            _gatt = null;
            _characteristic = null;
        }

        try
        {
            if (linuxCharacteristic is not null)
            {
                linuxCharacteristic.Value -= OnLinuxCharacteristicValue;

                try
                {
                    await linuxCharacteristic.StopNotifyAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                linuxCharacteristic.Dispose();
            }

            if (linuxDevice is not null)
            {
                linuxDevice.Disconnected -= OnLinuxDeviceDisconnected;

                try
                {
                    await linuxDevice.DisconnectAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                linuxDevice.Dispose();
            }

            linuxAdapter?.Dispose();

            if (characteristic is not null)
            {
                characteristic.CharacteristicValueChanged -= OnCharacteristicValueChanged;

                try
                {
                    await characteristic.StopNotificationsAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (device is not null)
            {
                device.GattServerDisconnected -= OnGattServerDisconnected;
            }

            try
            {
                gatt?.Disconnect();
            }
            catch
            {
            }

            device?.Dispose();
        }
        finally
        {
            lock (_stateLock)
            {
                _disconnecting = false;
            }
        }
    }

    private async Task ConnectLinuxAsync(string address, CancellationToken cancellationToken)
    {
        LinuxBluetooth.Adapter? adapter = null;
        LinuxBluetooth.Device? device = null;
        LinuxBluetooth.GattCharacteristic? characteristic = null;

        try
        {
            var adapters = await LinuxBluetooth.BlueZManager
                .GetAdaptersAsync()
                .ConfigureAwait(false);

            foreach (var candidate in adapters)
            {
                try
                {
                    if (await candidate.GetAsync<bool>("Powered").ConfigureAwait(false))
                    {
                        adapter = candidate;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (adapter is null)
            {
                throw new InvalidOperationException("Bluetooth adapter is not available or powered on.");
            }

            await adapter.StartDiscoveryAsync().ConfigureAwait(false);

            try
            {
                await Task.Delay(ScanTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await adapter.StopDiscoveryAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            var devices = await adapter.GetDevicesAsync().ConfigureAwait(false);
            device = await FindLinuxDeviceAsync(devices, address).ConfigureAwait(false);

            if (device is null)
            {
                throw new InvalidOperationException(
                    $"Bluetooth device '{address}' was not found. Discovered: "
                    + await DescribeLinuxDevicesAsync(devices).ConfigureAwait(false)
                    + ".");
            }

            await device.ConnectAsync().ConfigureAwait(false);
            await WaitForLinuxPropertyAsync(
                device,
                "ServicesResolved",
                true,
                ConnectionTimeout,
                cancellationToken).ConfigureAwait(false);

            var service = await device.GetServiceAsync(ServiceUuid).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"GATT service 0x{ServiceId:X4} was not found.");

            characteristic = await service
                .GetCharacteristicAsync(CharacteristicUuidText)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"GATT characteristic {CharacteristicUuid} was not found.");

            characteristic.Value += OnLinuxCharacteristicValue;
            device.Disconnected += OnLinuxDeviceDisconnected;
            await characteristic.StartNotifyAsync().ConfigureAwait(false);

            lock (_stateLock)
            {
                _linuxAdapter = adapter;
                _linuxDevice = device;
                _linuxCharacteristic = characteristic;
                Volatile.Write(ref _linuxConnected, 1);
            }
        }
        catch
        {
            if (characteristic is not null)
            {
                characteristic.Value -= OnLinuxCharacteristicValue;

                try
                {
                    await characteristic.StopNotifyAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                characteristic.Dispose();
            }

            if (device is not null)
            {
                device.Disconnected -= OnLinuxDeviceDisconnected;

                try
                {
                    await device.DisconnectAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                device.Dispose();
            }

            adapter?.Dispose();
            throw;
        }
    }

    private async Task ConnectInTheHandAsync(string address, CancellationToken cancellationToken)
    {
        if (!await Bluetooth.GetAvailabilityAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Bluetooth is not available or powered on.");
        }

        BluetoothDevice device;
        try
        {
            var devices = await Bluetooth.ScanForDevicesAsync(
                new RequestDeviceOptions
                {
                    AcceptAllDevices = true,
                    Timeout = ScanTimeout,
                },
                cancellationToken).ConfigureAwait(false);

            device = FindInTheHandDevice(devices, address)
                ?? throw new InvalidOperationException(
                    $"Bluetooth device '{address}' was not found. Discovered: {DescribeInTheHandDevices(devices)}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Bluetooth scan failed: {ex.Message}", ex);
        }

        var gatt = device.Gatt;
        GattCharacteristic? characteristic = null;

        try
        {
            await gatt.ConnectAsync().ConfigureAwait(false);

            try
            {
                await gatt.RequestMtuAsync(RequestedMtu).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Linux/BlueZ may not support explicit MTU negotiation.
            }

            var service = await gatt
                .GetPrimaryServiceAsync(BluetoothUuid.FromShortId(ServiceId))
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"GATT service 0x{ServiceId:X4} was not found.");

            characteristic = await service
                .GetCharacteristicAsync(BluetoothUuid.FromGuid(CharacteristicUuid))
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"GATT characteristic {CharacteristicUuid} was not found.");

            characteristic.CharacteristicValueChanged += OnCharacteristicValueChanged;
            device.GattServerDisconnected += OnGattServerDisconnected;
            await characteristic.StartNotificationsAsync().ConfigureAwait(false);

            lock (_stateLock)
            {
                _device = device;
                _gatt = gatt;
                _characteristic = characteristic;
            }
        }
        catch
        {
            if (characteristic is not null)
            {
                characteristic.CharacteristicValueChanged -= OnCharacteristicValueChanged;
            }

            device.GattServerDisconnected -= OnGattServerDisconnected;

            try
            {
                gatt.Disconnect();
            }
            catch
            {
            }

            device.Dispose();
            throw;
        }
    }

    private async Task WriteLinuxAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        LinuxBluetooth.GattCharacteristic? characteristic;
        lock (_stateLock)
        {
            characteristic = _linuxCharacteristic;
        }

        if (characteristic is null || !IsConnected)
        {
            throw new InvalidOperationException("Bluetooth transport is not connected.");
        }

        var bytes = data.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        if (bytes.Length > MaximumWriteLength)
        {
            throw new ArgumentException(
                $"Bluetooth writes are limited to {MaximumWriteLength} bytes.",
                nameof(data));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await characteristic
                .WriteValueAsync(
                    bytes,
                    new Dictionary<string, object> { ["type"] = "request" })
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteInTheHandAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        GattCharacteristic? characteristic;
        lock (_stateLock)
        {
            characteristic = _characteristic;
        }

        if (characteristic is null || !IsConnected)
        {
            throw new InvalidOperationException("Bluetooth transport is not connected.");
        }

        var bytes = data.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        if (bytes.Length > MaximumWriteLength)
        {
            throw new ArgumentException(
                $"Bluetooth writes are limited to {MaximumWriteLength} bytes.",
                nameof(data));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await characteristic.WriteValueWithResponseAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task OnLinuxCharacteristicValue(
        LinuxBluetooth.GattCharacteristic sender,
        LinuxBluetooth.GattCharacteristicValueEventArgs e)
    {
        if (e.Value is { Length: > 0 } value)
        {
            OnDataReceived(value);
        }

        return Task.CompletedTask;
    }

    private Task OnLinuxDeviceDisconnected(
        LinuxBluetooth.Device sender,
        LinuxBluetooth.BlueZEventArgs e)
    {
        lock (_stateLock)
        {
            if (_disconnecting)
            {
                return Task.CompletedTask;
            }
        }

        _ = HandleUnexpectedLinuxDisconnectAsync();
        return Task.CompletedTask;
    }

    private async Task HandleUnexpectedLinuxDisconnectAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        OnDisconnected();
    }

    private void OnCharacteristicValueChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        if (e.Error is not null)
        {
            OnFaulted($"Bluetooth notification error: {e.Error.Message}");
            return;
        }

        if (e.Value is { Length: > 0 } value)
        {
            OnDataReceived(value);
        }
    }

    private void OnGattServerDisconnected(object? sender, EventArgs e)
    {
        lock (_stateLock)
        {
            if (_disconnecting)
            {
                return;
            }
        }

        _ = HandleUnexpectedInTheHandDisconnectAsync();
    }

    private async Task HandleUnexpectedInTheHandDisconnectAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        OnDisconnected();
    }

    private static async Task<LinuxBluetooth.Device?> FindLinuxDeviceAsync(
        IReadOnlyList<LinuxBluetooth.Device> devices,
        string address)
    {
        foreach (var device in devices)
        {
            var properties = await device.GetPropertiesAsync().ConfigureAwait(false);

            if (string.Equals(properties.Address, address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(properties.Name, address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(properties.Alias, address, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    private static async Task<string> DescribeLinuxDevicesAsync(
        IReadOnlyList<LinuxBluetooth.Device> devices)
    {
        if (devices.Count == 0)
        {
            return "none";
        }

        var descriptions = new List<string>();
        foreach (var device in devices.Take(10))
        {
            var properties = await device.GetPropertiesAsync().ConfigureAwait(false);
            var name = properties.Name;
            var address = properties.Address ?? "unknown";
            descriptions.Add(string.IsNullOrWhiteSpace(name)
                ? address
                : $"{name} ({address})");
        }

        return string.Join(", ", descriptions);
    }

    private static BluetoothDevice? FindInTheHandDevice(
        IReadOnlyCollection<BluetoothDevice> devices,
        string address)
    {
        foreach (var device in devices)
        {
            if (string.Equals(device.Id, address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Name, address, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    private static string DescribeInTheHandDevices(IReadOnlyCollection<BluetoothDevice> devices)
    {
        if (devices.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ", ",
            devices
                .Select(device => string.IsNullOrWhiteSpace(device.Name)
                    ? device.Id
                    : $"{device.Name} ({device.Id})")
                .Take(10));
    }

    private static async Task WaitForLinuxPropertyAsync<T>(
        LinuxBluetooth.Device device,
        string propertyName,
        T expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = await device.GetAsync<T>(propertyName).ConfigureAwait(false);
            if (EqualityComparer<T>.Default.Equals(value, expected))
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for Bluetooth property '{propertyName}'.");
    }
}
