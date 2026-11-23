using SerialDataProtocol;
using Meadow;
using Meadow.Devices;
using Meadow.Gateways.Bluetooth;
using Meadow.Hardware;
using Meadow.Units;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeadowBleDevice;

public class MeadowApp : App<F7FeatherV2>
{
    //ServiceID, DeviceName, and CharacteristicUUid are used to match and identify this device
    private const ushort ServiceId = 125;
    private const string DeviceName = "MeadowSolarIan";
    private const string CharacteristicUuid = "5a5a5a56-69ab-4a49-a2f2-de5b292458f3";
    private const int CharacteristicMaxLength = 100;

    private static readonly TimeSpan AdcSampleInterval = TimeSpan.Zero;
    private static readonly Voltage AdcReference = new Voltage(3.3, Voltage.UnitType.Volts);

    private readonly CommandFramer _commandFramer = new CommandFramer();
    private readonly object _commandLock = new object();

    private IAnalogInputPort[] _adc = null!;
    private IDigitalInputPort[] _inputs = null!;
    private IDigitalOutputPort[] _outputs = null!;
    private CharacteristicString _dataCharacteristic = null!;
    private Definition _definition = null!;

    private int _packetNumber;
    private int _commandValidInterval;
    private int _commandInvalidInterval;
    private int _commandValidTotal;
    private int _commandInvalidTotal;
    private int _notifyErrorCount;

    public override Task Initialize()
    {
        _adc = new IAnalogInputPort[]
        {
            Device.CreateAnalogInputPort(Device.Pins.A00, 1, AdcSampleInterval, AdcReference),
            Device.CreateAnalogInputPort(Device.Pins.A01, 1, AdcSampleInterval, AdcReference),
            Device.CreateAnalogInputPort(Device.Pins.A02, 1, AdcSampleInterval, AdcReference),
            Device.CreateAnalogInputPort(Device.Pins.A03, 1, AdcSampleInterval, AdcReference),
            Device.CreateAnalogInputPort(Device.Pins.A04, 1, AdcSampleInterval, AdcReference),
            Device.CreateAnalogInputPort(Device.Pins.A05, 1, AdcSampleInterval, AdcReference)
        };

        _outputs = new IDigitalOutputPort[]
        {
            Device.CreateDigitalOutputPort(Device.Pins.D08),
            Device.CreateDigitalOutputPort(Device.Pins.D09),
            Device.CreateDigitalOutputPort(Device.Pins.D10),
            Device.CreateDigitalOutputPort(Device.Pins.D11)
        };

        _inputs = new IDigitalInputPort[]
        {
            Device.CreateDigitalInputPort(Device.Pins.D02, ResistorMode.InternalPullUp),
            Device.CreateDigitalInputPort(Device.Pins.D03, ResistorMode.InternalPullUp),
            Device.CreateDigitalInputPort(Device.Pins.D04, ResistorMode.InternalPullUp),
            Device.CreateDigitalInputPort(Device.Pins.D05, ResistorMode.InternalPullUp)
        };

        _dataCharacteristic = new CharacteristicString(
            "SolarData",
            CharacteristicUuid,
            CharacteristicPermission.Read | CharacteristicPermission.Write,
            CharacteristicProperty.Read
                | CharacteristicProperty.Write
                | CharacteristicProperty.WriteNoResponse
                | CharacteristicProperty.Notify,
            CharacteristicMaxLength);

        _dataCharacteristic.ValueSet += OnCharacteristicValueSet;

        //Create a new Bluetooth Gateway Service
        var service = new Service("SolarService", ServiceId, _dataCharacteristic);
        _definition = new Definition(DeviceName, service);

        Device.BluetoothAdapter.ServerStarting += (_, _) => Resolver.Log.Info("BLE server starting.");
        Device.BluetoothAdapter.ServerStarted += (_, _) => Resolver.Log.Info("BLE server started.");
        Device.BluetoothAdapter.ServerStopping += (_, _) => Resolver.Log.Info("BLE server stopping.");
        Device.BluetoothAdapter.ServerStopped += (_, _) => Resolver.Log.Info("BLE server stopped.");
        Device.BluetoothAdapter.ClientConnected += (_, _) =>
        {
            lock (_commandLock)
            {
                _commandFramer.Reset();
            }

            Resolver.Log.Info("BLE client connected.");
        };
        Device.BluetoothAdapter.ClientDisconnected += (_, _) => Resolver.Log.Info("BLE client disconnected.");

        var started = Device.BluetoothAdapter.StartBluetoothServer(_definition);
        if (started)
        {
            Resolver.Log.Info(
                $"BLE server started: device='{DeviceName}', service={ServiceId}, characteristic={CharacteristicUuid}.");
        }
        else
        {
            Resolver.Log.Error("BLE server failed to start.");
        }

        Resolver.Log.Info("ADC: A00-A05; inputs: D02-D05; outputs: D08-D11.");

        return base.Initialize();
    }

    public override Task Run()
    {
        Resolver.Log.Info("Run...");

        _ = Task.Run(TelemetryLoop);
        _ = Task.Run(SummaryLoop);

        return base.Run();
    }

    private async Task TelemetryLoop()
    {
        while (true)
        {
            try
            {
                var adcMillivolts = new int[ProtocolConstants.AdcCount];
                for (var i = 0; i < adcMillivolts.Length; i++)
                {
                    var voltage = await _adc[i].Read();
                    adcMillivolts[i] = (int)Math.Round(voltage.Volts * 1000.0);
                }

                var inputStates = new bool[ProtocolConstants.IoCount];
                for (var i = 0; i < inputStates.Length; i++)
                {
                    inputStates[i] = _inputs[i].State;
                }

                var frame = SerialProtocol.BuildTelemetry(_packetNumber, adcMillivolts, inputStates);
                _packetNumber = (_packetNumber + 1) % 1000;
                _dataCharacteristic.SetValue(frame);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _notifyErrorCount) <= 3)
                {
                    Resolver.Log.Error($"BLE telemetry notify: {ex.Message}");
                }
            }

            await Task.Delay(ProtocolConstants.TelemetryIntervalMilliseconds);
        }
    }

    private async Task SummaryLoop()
    {
        while (true)
        {
            await Task.Delay(5000);

            var valid = Interlocked.Exchange(ref _commandValidInterval, 0);
            var invalid = Interlocked.Exchange(ref _commandInvalidInterval, 0);
            var totalValid = Volatile.Read(ref _commandValidTotal);
            var totalInvalid = Volatile.Read(ref _commandInvalidTotal);

            Resolver.Log.Info(
                $"BLE commands: {valid} valid, {invalid} invalid in 5s; "
                + $"totals {totalValid} valid, {totalInvalid} invalid.");
        }
    }

    private void OnCharacteristicValueSet(ICharacteristic characteristic, object value)
    {
        if (value is not string text)
        {
            Interlocked.Increment(ref _commandInvalidTotal);
            Interlocked.Increment(ref _commandInvalidInterval);
            Resolver.Log.Error($"BLE command RX FAIL: unexpected value type '{value?.GetType().Name ?? "null"}'.");
            return;
        }

        var errors = new List<string>();
        IReadOnlyList<CommandPacket> packets;

        lock (_commandLock)
        {
            packets = _commandFramer.Append(text, errors);
        }

        foreach (var error in errors)
        {
            Interlocked.Increment(ref _commandInvalidTotal);
            Interlocked.Increment(ref _commandInvalidInterval);
            Resolver.Log.Error($"BLE command RX FAIL: {error}.");
        }

        foreach (var packet in packets)
        {
            ApplyCommand(packet);
        }
    }

    private void ApplyCommand(CommandPacket packet)
    {
        for (var i = 0; i < _outputs.Length; i++)
        {
            _outputs[i].State = packet.OutputStates[i];
        }

        Interlocked.Increment(ref _commandValidTotal);
        Interlocked.Increment(ref _commandValidInterval);
        Resolver.Log.Info($"BLE command RX PASS: outputs={packet.OutputStateText} checksum={packet.Checksum:D3}.");
    }
}
