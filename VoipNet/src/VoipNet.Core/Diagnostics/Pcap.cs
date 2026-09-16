using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VoipNet.Diagnostics;

/// <summary>A UDP datagram extracted from a capture.</summary>
/// <param name="Timestamp">Capture time.</param>
/// <param name="Source">Sender address.</param>
/// <param name="Destination">Receiver address.</param>
/// <param name="Payload">UDP payload.</param>
public sealed record UdpDatagram(DateTimeOffset Timestamp, IPEndPoint Source, IPEndPoint Destination, ReadOnlyMemory<byte> Payload);

/// <summary>Reads UDP datagrams from classic libpcap files (Ethernet, raw IP, Linux cooked and loopback).</summary>
public static class PcapReader
{
    private const uint MagicMicro = 0xA1B2C3D4;
    private const uint MagicNano = 0xA1B23C4D;

    /// <summary>Enumerates the UDP datagrams in a capture file.</summary>
    /// <param name="path">Path to a <c>.pcap</c> file.</param>
    public static IEnumerable<UdpDatagram> ReadUdp(string path)
    {
        using var stream = File.OpenRead(path);
        foreach (var datagram in ReadUdp(stream))
        {
            yield return datagram;
        }
    }

    /// <summary>Enumerates the UDP datagrams in a capture stream.</summary>
    /// <param name="stream">Capture data.</param>
    public static IEnumerable<UdpDatagram> ReadUdp(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[24];
        if (stream.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24)
        {
            yield break;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var bigEndian = false;
        bool nano;
        if (magic is MagicMicro or MagicNano)
        {
            nano = magic == MagicNano;
        }
        else
        {
            var swapped = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (swapped is not (MagicMicro or MagicNano))
            {
                throw new InvalidDataException("Not a libpcap file (pcapng is not supported; convert it with editcap -F pcap).");
            }

            bigEndian = true;
            nano = swapped == MagicNano;
        }

        var linkType = Read32(header.AsSpan(20), bigEndian);
        var record = new byte[16];
        while (stream.ReadAtLeast(record, 16, throwOnEndOfStream: false) == 16)
        {
            var seconds = Read32(record, bigEndian);
            var fraction = Read32(record.AsSpan(4), bigEndian);
            var captured = (int)Read32(record.AsSpan(8), bigEndian);
            if (captured is < 0 or > 1 << 24)
            {
                yield break;
            }

            var packet = new byte[captured];
            if (stream.ReadAtLeast(packet, captured, throwOnEndOfStream: false) < captured)
            {
                yield break;
            }

            var ticks = nano ? fraction / 100 : fraction * 10;
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks);
            if (Parse(packet, linkType, timestamp) is { } datagram)
            {
                yield return datagram;
            }
        }
    }

    private static uint Read32(ReadOnlySpan<byte> data, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);

    private static UdpDatagram? Parse(byte[] packet, uint linkType, DateTimeOffset timestamp)
    {
        int offset;
        switch (linkType)
        {
            case 1: // Ethernet
                if (packet.Length < 14)
                {
                    return null;
                }

                offset = 14;
                var etherType = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12));
                if (etherType == 0x8100 && packet.Length >= 18)
                {
                    etherType = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16));
                    offset = 18;
                }

                if (etherType is not (0x0800 or 0x86DD))
                {
                    return null;
                }

                break;
            case 0: // BSD loopback
                offset = 4;
                break;
            case 101 or 228 or 229: // raw IP
                offset = 0;
                break;
            case 113: // Linux cooked
                offset = 16;
                break;
            default:
                return null;
        }

        if (packet.Length < offset + 20)
        {
            return null;
        }

        var version = packet[offset] >> 4;
        IPAddress source, destination;
        int udp;
        if (version == 4)
        {
            if (packet[offset + 9] != 17)
            {
                return null;
            }

            udp = offset + ((packet[offset] & 0x0F) * 4);
            source = new IPAddress(packet.AsSpan(offset + 12, 4));
            destination = new IPAddress(packet.AsSpan(offset + 16, 4));
        }
        else if (version == 6 && packet.Length >= offset + 40)
        {
            if (packet[offset + 6] != 17)
            {
                return null;
            }

            udp = offset + 40;
            source = new IPAddress(packet.AsSpan(offset + 8, 16));
            destination = new IPAddress(packet.AsSpan(offset + 24, 16));
        }
        else
        {
            return null;
        }

        if (packet.Length < udp + 8)
        {
            return null;
        }

        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udp));
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udp + 2));
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udp + 4)) - 8, packet.Length - udp - 8);
        if (length < 0)
        {
            return null;
        }

        return new UdpDatagram(
            timestamp,
            new IPEndPoint(source, sourcePort),
            new IPEndPoint(destination, destinationPort),
            packet.AsMemory(udp + 8, length));
    }
}

/// <summary>
/// Writes datagrams to a libpcap file with synthetic IPv4/UDP headers, so SIP traces from the
/// engine open directly in Wireshark with the SIP dissector.
/// </summary>
public sealed class PcapWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly Lock _gate = new();
    private ushort _ipId;

    /// <summary>Creates a capture file.</summary>
    /// <param name="path">Destination path.</param>
    public PcapWriter(string path)
    {
        _stream = File.Create(path);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicMicroseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 65535);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 101); // LINKTYPE_RAW
        _stream.Write(header);
    }

    private const uint MagicMicroseconds = 0xA1B2C3D4;

    /// <summary>Appends a UDP datagram.</summary>
    /// <param name="source">Sender.</param>
    /// <param name="destination">Receiver.</param>
    /// <param name="payload">UDP payload.</param>
    /// <param name="timestamp">Capture time; now when omitted.</param>
    public void WriteUdp(IPEndPoint source, IPEndPoint destination, ReadOnlySpan<byte> payload, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var src = source.Address.MapToIPv4().GetAddressBytes();
        var dst = destination.Address.MapToIPv4().GetAddressBytes();
        var total = 20 + 8 + payload.Length;
        var packet = new byte[total];

        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)total);
        packet[8] = 64;
        packet[9] = (byte)ProtocolType.Udp;
        src.CopyTo(packet, 12);
        dst.CopyTo(packet, 16);

        lock (_gate)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), _ipId++);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), (ushort)source.Port);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)destination.Port);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(8 + payload.Length));
            payload.CopyTo(packet.AsSpan(28));

            var time = timestamp ?? DateTimeOffset.UtcNow;
            Span<byte> record = stackalloc byte[16];
            var unixMicros = (time - DateTimeOffset.UnixEpoch).Ticks / 10;
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(unixMicros / 1_000_000));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)(unixMicros % 1_000_000));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)total);
            _stream.Write(record);
            _stream.Write(packet);
        }
    }

    /// <summary>Records every SIP message of a client (enables <see cref="VoipClientOptions.TraceSip"/> semantics).</summary>
    /// <param name="client">Client to trace. Its options must have <c>TraceSip</c> enabled.</param>
    public void Attach(VoipClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var local = ParseEndPoint(client.LocalAddress) ?? new IPEndPoint(IPAddress.Loopback, 5060);
        client.SipTrace += (_, e) =>
        {
            var remote = ParseEndPoint(e.RemoteEndPoint) ?? new IPEndPoint(IPAddress.Loopback, 5060);
            var bytes = System.Text.Encoding.UTF8.GetBytes(e.Message);
            if (e.Outgoing)
            {
                WriteUdp(local, remote, bytes);
            }
            else
            {
                WriteUdp(remote, local, bytes);
            }
        };
    }

    private static IPEndPoint? ParseEndPoint(string value) =>
        IPEndPoint.TryParse(value, out var endPoint) ? endPoint : null;

    private static ushort Checksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (var i = 0; i < header.Length; i += 2)
        {
            sum += (uint)((header[i] << 8) | (i + 1 < header.Length ? header[i + 1] : 0));
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>Flushes and closes the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Flush();
            _stream.Dispose();
        }
    }
}
