using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SerialDataProtocol;

public static class ProtocolConstants
{
    public const string StartMarker = "###";
    public const int BaudRate = 115200;
    public const int TelemetryPayloadLength = 34;
    public const int TelemetryFrameLength = 37;
    public const int CommandPayloadLength = 7;
    public const int CommandFrameLength = 10;
    public const int AdcCount = 6;
    public const int IoCount = 4;
    public const int AdcMaximumMillivolts = 3300;
    public const int TelemetryIntervalMilliseconds = 100;
}


// A sealed class indicates that no other class can inherit from this class. 
public sealed class TelemetryPacket
{
    public TelemetryPacket(
        int packetNumber,
        int[] adcMillivolts,
        bool[] inputStates,
        int checksum)
    {
        PacketNumber = packetNumber;
        AdcMillivolts = adcMillivolts;
        InputStates = inputStates;
        Checksum = checksum;
    }

    public int PacketNumber { get; }
    public int[] AdcMillivolts { get; }
    public bool[] InputStates { get; }
    public int Checksum { get; }

    public string InputStateText
    {
        //The getter method for returning the input states (D02 - D05) as a string rather than boolen values
        get
        {
            var chars = new char[InputStates.Length];
            for (var i = 0; i < InputStates.Length; i++)
            {
                chars[i] = InputStates[i] ? '1' : '0';
            }

            return new string(chars);
        }
    }
}

public sealed class CommandPacket
{
    public CommandPacket(bool[] outputStates, int checksum)
    {
        OutputStates = outputStates;
        Checksum = checksum;
    }

    public bool[] OutputStates { get; }
    public int Checksum { get; }

    //returns the Output state (D08 - D11) as text rather than an array of booleans
    public string OutputStateText
    {
        get
        {
            var chars = new char[OutputStates.Length];
            for (var i = 0; i < OutputStates.Length; i++)
            {
                chars[i] = OutputStates[i] ? '1' : '0';
            }

            return new string(chars);
        }
    }
}

public static class SerialProtocol
{
    //Based on the input data, a checksum is computed. 
    public static int ComputeChecksum(string payload)
    {
        if (payload == null)
        {
            //nameof is a c# expression that evaluates at compile time, which makes it more flexible for Exceptions if the code is refactored
            throw new ArgumentNullException(nameof(payload));
        }

        var sum = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            sum += payload[i];
        }

        return sum % 1000;
    }

    //The input telemetry packet is built. 
    public static string BuildTelemetry(int packetNumber, int[] adcMillivolts, bool[] inputStates)
    {
        if (packetNumber < 0 || packetNumber > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(packetNumber));
        }

        if (adcMillivolts == null || adcMillivolts.Length != ProtocolConstants.AdcCount)
        {
            throw new ArgumentException("Exactly six ADC values are required.", nameof(adcMillivolts));
        }

        if (inputStates == null || inputStates.Length != ProtocolConstants.IoCount)
        {
            throw new ArgumentException("Exactly four input states are required.", nameof(inputStates));
        }
       
        //StringBuilder is used to avoid the heavy overhead of string concatenation (since strings are immutable)
        var payload = new StringBuilder(ProtocolConstants.TelemetryPayloadLength);
        //Begin with "###"
        payload.Append(ProtocolConstants.StartMarker);
        //   Append the packet number. The Invarient Culture option ensures that, on any machine, 
        //   The three digits will appear as 3-digit (D3) ASCII characters
        payload.Append(packetNumber.ToString("D3", CultureInfo.InvariantCulture));
        
        // Append the ADC values to the packet
        for (var i = 0; i < ProtocolConstants.AdcCount; i++)
        {
            // Clamp the incoming milivolts to the accepted values
            var value = Clamp(adcMillivolts[i], 0, ProtocolConstants.AdcMaximumMillivolts);
            // Same as above, although this time specifying 4 digit (D4) ASCII characters 
            payload.Append(value.ToString("D4", CultureInfo.InvariantCulture));
        }

        //  Append the Digital Values to the packet
        for (var i = 0; i < ProtocolConstants.IoCount; i++)
        {
            payload.Append(inputStates[i] ? '1' : '0');
        }

        //  Compute the checksum, and append it to the packet
        var checksum = ComputeChecksum(payload.ToString());
        payload.Append(checksum.ToString("D3", CultureInfo.InvariantCulture));
        payload.Append("\r\n");

        //  Return the completed Packet
        return payload.ToString();
    }

    //Build Output Command to change the LED states. 
    public static string BuildCommand(bool[] outputStates)
    {
        
        //
        if (outputStates == null || outputStates.Length != ProtocolConstants.IoCount)
        {
            throw new ArgumentException("Exactly four output states are required.", nameof(outputStates));
        }

        var payload = new StringBuilder(ProtocolConstants.CommandPayloadLength);
        payload.Append(ProtocolConstants.StartMarker);

        for (var i = 0; i < ProtocolConstants.IoCount; i++)
        {
            payload.Append(outputStates[i] ? '1' : '0');
        }

        var checksum = ComputeChecksum(payload.ToString());
        payload.Append(checksum.ToString("D3", CultureInfo.InvariantCulture));
        return payload.ToString();
    }


    // Parse the incoming Telemetry from the meadow
    public static bool TryParseTelemetry(
        string frame,
        out TelemetryPacket? packet,
        out string error)
    {
        packet = null;
        error = string.Empty;

        // reject null
        if (frame == null)
        {
            error = "Frame is null.";
            return false;
        }

        // reject improper frame length
        if (frame.Length != ProtocolConstants.TelemetryFrameLength)
        {
            error = $"Expected {ProtocolConstants.TelemetryFrameLength} characters, got {frame.Length}.";
            return false;
        }

        // reject frames with no start marker
        if (!frame.StartsWith(ProtocolConstants.StartMarker, StringComparison.Ordinal))
        {
            error = "Missing start marker.";
            return false;
        }

        // Parse the packetNumber
        if (!TryParseNumber(frame, 3, 3, 0, 999, out var packetNumber, out error))
        {
            return false;
        }

        // Parse the ADC values
        var adcValues = new int[ProtocolConstants.AdcCount];
        for (var i = 0; i < ProtocolConstants.AdcCount; i++)
        {
            if (!TryParseNumber(
                    frame,
                    6 + (i * 4),
                    4,
                    0,
                    ProtocolConstants.AdcMaximumMillivolts,
                    out adcValues[i],
                    out error))
            {
                error = $"ADC{i}: {error}";
                return false;
            }
        }

        // Parse Digital IO
        var inputs = new bool[ProtocolConstants.IoCount];
        for (var i = 0; i < ProtocolConstants.IoCount; i++)
        {
            var value = frame[30 + i];
            if (value != '0' && value != '1')
            {
                error = $"Input bit {i} is not 0 or 1.";
                return false;
            }

            inputs[i] = value == '1';
        }


        //Parse Checksum
        if (!TryParseNumber(frame, 34, 3, 0, 999, out var checksum, out error))
        {
            error = $"Checksum: {error}";
            return false;
        }

        //Validate Checksum
        var expected = ComputeChecksum(frame.Substring(0, ProtocolConstants.TelemetryPayloadLength));
        if (checksum != expected)
        {
            error = $"Checksum mismatch: got {checksum:D3}, expected {expected:D3}.";
            return false;
        }

        // Return a TelemetryPacket object if successful
        packet = new TelemetryPacket(packetNumber, adcValues, inputs, checksum);
        return true;
    }


    public static bool TryParseCommand(
        string frame,
        out CommandPacket? packet,
        out string error)
    {
        packet = null;
        error = string.Empty;

        if (frame == null)
        {
            error = "Frame is null.";
            return false;
        }

        if (frame.Length != ProtocolConstants.CommandFrameLength)
        {
            error = $"Expected {ProtocolConstants.CommandFrameLength} characters, got {frame.Length}.";
            return false;
        }

        if (!frame.StartsWith(ProtocolConstants.StartMarker, StringComparison.Ordinal))
        {
            error = "Missing start marker.";
            return false;
        }

        var outputs = new bool[ProtocolConstants.IoCount];
        for (var i = 0; i < ProtocolConstants.IoCount; i++)
        {
            var value = frame[3 + i];
            if (value != '0' && value != '1')
            {
                error = $"Output bit {i} is not 0 or 1.";
                return false;
            }

            outputs[i] = value == '1';
        }

        if (!TryParseNumber(frame, 7, 3, 0, 999, out var checksum, out error))
        {
            error = $"Checksum: {error}";
            return false;
        }

        var expected = ComputeChecksum(frame.Substring(0, ProtocolConstants.CommandPayloadLength));
        if (checksum != expected)
        {
            error = $"Checksum mismatch: got {checksum:D3}, expected {expected:D3}.";
            return false;
        }

        packet = new CommandPacket(outputs, checksum);
        return true;
    }


    private static bool TryParseNumber(
        string text,
        int offset,
        int length,
        int minimum,
        int maximum,
        out int value,
        out string error)
    {
        value = 0;
        error = string.Empty;

        for (var i = 0; i < length; i++)
        {
            var c = text[offset + i];
            if (c < '0' || c > '9')
            {
                error = "Contains a non-digit character.";
                return false;
            }
        }

        if (!int.TryParse(
                text.Substring(offset, length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value))
        {
            error = "Could not parse number.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"Value {value} is outside {minimum}-{maximum}.";
            return false;
        }

        return true;
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        if (value > maximum)
        {
            return maximum;
        }

        return value;
    }
}

public sealed class TelemetryFramer
{
    private readonly StringBuilder _buffer = new StringBuilder();

    public void Reset()
    {
        _buffer.Clear();
    }

    public IReadOnlyList<string> Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        _buffer.Append(text);
        var lines = new List<string>();

        while (true)
        {
            var current = _buffer.ToString();
            var newlineIndex = current.IndexOf('\n');
            if (newlineIndex < 0)
            {
                break;
            }

            var line = current.Substring(0, newlineIndex);
            if (line.EndsWith("\r", StringComparison.Ordinal))
            {
                line = line.Substring(0, line.Length - 1);
            }

            lines.Add(line);
            _buffer.Remove(0, newlineIndex + 1);
        }

        if (_buffer.Length > 4096)
        {
            _buffer.Clear();
        }

        return lines;
    }
}

public sealed class CommandFramer
{
    private readonly StringBuilder _buffer = new StringBuilder();

    public void Reset()
    {
        _buffer.Clear();
    }

    public IReadOnlyList<CommandPacket> Append(string text)
    {
        return Append(text, null);
    }

    public IReadOnlyList<CommandPacket> Append(string text, List<string>? errors)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _buffer.Append(text);
        }

        var packets = new List<CommandPacket>();

        while (true)
        {
            var current = _buffer.ToString();
            var startIndex = current.IndexOf(ProtocolConstants.StartMarker, StringComparison.Ordinal);

            if (startIndex < 0)
            {
                if (_buffer.Length > 2)
                {
                    _buffer.Remove(0, _buffer.Length - 2);
                }

                break;
            }

            if (startIndex > 0)
            {
                _buffer.Remove(0, startIndex);
                current = _buffer.ToString();
            }

            if (current.Length < ProtocolConstants.CommandFrameLength)
            {
                break;
            }

            var candidate = current.Substring(0, ProtocolConstants.CommandFrameLength);
            if (SerialProtocol.TryParseCommand(candidate, out var packet, out var error))
            {
                packets.Add(packet!);
                _buffer.Remove(0, ProtocolConstants.CommandFrameLength);
            }
            else
            {
                errors?.Add($"frame='{candidate}' {error}");
                _buffer.Remove(0, 1);
            }
        }

        if (_buffer.Length > 4096)
        {
            _buffer.Clear();
        }

        return packets;
    }
}
