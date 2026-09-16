using System.Buffers.Binary;
using System.Net;

namespace VoipNet.Diagnostics;

/// <summary>Quality report for one RTP stream (one SSRC on one path).</summary>
public sealed class RtpStreamReport
{
    /// <summary>Synchronisation source identifier.</summary>
    public uint Ssrc { get; init; }

    /// <summary>Sender address.</summary>
    public required IPEndPoint Source { get; init; }

    /// <summary>Receiver address.</summary>
    public required IPEndPoint Destination { get; init; }

    /// <summary>Payload type seen most often.</summary>
    public int PayloadType { get; internal set; }

    /// <summary>Name of <see cref="PayloadType"/> when it is a static type.</summary>
    public string Codec => PayloadType switch
    {
        0 => "PCMU",
        3 => "GSM",
        4 => "G723",
        8 => "PCMA",
        9 => "G722",
        13 => "CN",
        18 => "G729",
        _ => $"dynamic({PayloadType})",
    };

    /// <summary>Packets received.</summary>
    public long Packets { get; internal set; }

    /// <summary>Packets expected from the sequence range.</summary>
    public long Expected { get; internal set; }

    /// <summary>Packets missing from the sequence range.</summary>
    public long Lost => Math.Max(Expected - Packets, 0);

    /// <summary>Loss as a percentage.</summary>
    public double LossPercent => Expected == 0 ? 0 : Lost * 100.0 / Expected;

    /// <summary>Packets that arrived with a lower sequence number than one already seen.</summary>
    public long OutOfOrder { get; internal set; }

    /// <summary>Duplicate sequence numbers.</summary>
    public long Duplicates { get; internal set; }

    /// <summary>Final RFC 3550 inter-arrival jitter, in milliseconds.</summary>
    public double JitterMs { get; internal set; }

    /// <summary>Largest jitter observed, in milliseconds.</summary>
    public double MaxJitterMs { get; internal set; }

    /// <summary>Largest gap between consecutive packets, in milliseconds.</summary>
    public double MaxDeltaMs { get; internal set; }

    /// <summary>Time from first to last packet.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>Payload bytes received.</summary>
    public long Bytes { get; internal set; }

    /// <summary>Marker bits seen, which usually indicate talk spurts.</summary>
    public long Markers { get; internal set; }

    /// <summary>Estimated MOS from loss and jitter (simplified E-model).</summary>
    public double Mos
    {
        get
        {
            var ie = Codec is "PCMU" or "PCMA" or "G722" ? 0.0 : 10.0;
            var bpl = 25.1;
            var ieEff = ie + ((95 - ie) * LossPercent / (LossPercent + bpl));
            var delay = 40 + (JitterMs * 2);
            var id = (0.024 * delay) + (delay > 177.3 ? 0.11 * (delay - 177.3) : 0);
            var r = Math.Clamp(93.2 - id - ieEff, 0, 100);
            return Math.Clamp(1 + (0.035 * r) + (7e-6 * r * (r - 60) * (100 - r)), 1, 4.5);
        }
    }
}

/// <summary>
/// Detects RTP streams in a sequence of UDP datagrams and measures loss, jitter and ordering.
/// Feed it packets from a capture file or from a live socket.
/// </summary>
public sealed class RtpStreamAnalyzer
{
    private readonly Dictionary<(uint Ssrc, string Path), State> _streams = [];

    /// <summary>Clock rate used for jitter when the payload type is dynamic.</summary>
    public int DefaultClockRate { get; set; } = 8000;

    /// <summary>Processes one datagram. Non-RTP payloads are ignored.</summary>
    /// <param name="datagram">The datagram.</param>
    /// <returns>True when the datagram was RTP.</returns>
    public bool Add(UdpDatagram datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        var data = datagram.Payload.Span;
        if (data.Length < 12 || data[0] >> 6 != 2)
        {
            return false;
        }

        var payloadType = data[1] & 0x7F;
        if (payloadType is >= 72 and <= 76)
        {
            return false; // RTCP multiplexed on the same port
        }

        var sequence = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var key = (ssrc, $"{datagram.Source}>{datagram.Destination}");

        if (!_streams.TryGetValue(key, out var state))
        {
            state = new State(new RtpStreamReport { Ssrc = ssrc, Source = datagram.Source, Destination = datagram.Destination });
            _streams[key] = state;
        }

        state.Update(datagram.Timestamp, sequence, timestamp, payloadType, data[1] >> 7 == 1, data.Length - 12, DefaultClockRate);
        return true;
    }

    /// <summary>Reports for every stream seen, ordered by packet count.</summary>
    public IReadOnlyList<RtpStreamReport> Reports() =>
        _streams.Values.Select(s => s.Report).OrderByDescending(r => r.Packets).ToList();

    /// <summary>Analyses a capture file.</summary>
    /// <param name="path">Path to a libpcap file.</param>
    public static IReadOnlyList<RtpStreamReport> AnalyzeFile(string path)
    {
        var analyzer = new RtpStreamAnalyzer();
        foreach (var datagram in PcapReader.ReadUdp(path))
        {
            analyzer.Add(datagram);
        }

        return analyzer.Reports();
    }

    private sealed class State(RtpStreamReport report)
    {
        private readonly HashSet<int> _recent = [];
        private readonly Queue<int> _recentOrder = new();
        private readonly Dictionary<int, long> _payloadTypes = [];
        private long _baseSequence = -1;
        private long _highestSequence;
        private int _cycles;
        private DateTimeOffset _first;
        private DateTimeOffset _lastArrival;
        private double? _lastTransit;
        private double _jitter;

        public RtpStreamReport Report { get; } = report;

        public void Update(DateTimeOffset arrival, ushort sequence, uint timestamp, int payloadType, bool marker, int payloadBytes, int defaultRate)
        {
            var r = Report;
            r.Packets++;
            r.Bytes += payloadBytes;
            if (marker)
            {
                r.Markers++;
            }

            _payloadTypes[payloadType] = _payloadTypes.GetValueOrDefault(payloadType) + 1;
            r.PayloadType = _payloadTypes.MaxBy(p => p.Value).Key;

            if (_baseSequence < 0)
            {
                _baseSequence = sequence;
                _highestSequence = sequence;
                _first = arrival;
            }
            else
            {
                var delta = (short)(sequence - (ushort)_highestSequence);
                if (delta > 0)
                {
                    if (sequence < (ushort)_highestSequence)
                    {
                        _cycles++;
                    }

                    _highestSequence = sequence;
                }
                else if (delta < 0)
                {
                    r.OutOfOrder++;
                }

                var gap = (arrival - _lastArrival).TotalMilliseconds;
                r.MaxDeltaMs = Math.Max(r.MaxDeltaMs, gap);
            }

            var extended = ((long)_cycles << 16) | sequence;
            if (!_recent.Add((int)(extended & 0x7FFFFFFF)))
            {
                r.Duplicates++;
                r.Packets--;
            }
            else
            {
                _recentOrder.Enqueue((int)(extended & 0x7FFFFFFF));
                if (_recentOrder.Count > 512)
                {
                    _recent.Remove(_recentOrder.Dequeue());
                }
            }

            var extendedHighest = ((long)_cycles << 16) | (ushort)_highestSequence;
            r.Expected = extendedHighest - _baseSequence + 1;

            // RFC 3550 A.8 using the codec clock rate (G.722 is clocked at 8 kHz).
            var clock = payloadType is 0 or 3 or 4 or 8 or 9 or 18 ? 8000 : defaultRate;
            var transit = (arrival - _first).TotalSeconds - (timestamp / (double)clock);
            if (_lastTransit is { } last)
            {
                var d = Math.Abs(transit - last);
                if (d < 1)
                {
                    _jitter += (d - _jitter) / 16;
                }
            }

            _lastTransit = transit;
            _lastArrival = arrival;
            r.JitterMs = _jitter * 1000;
            r.MaxJitterMs = Math.Max(r.MaxJitterMs, r.JitterMs);
            r.Duration = arrival - _first;
        }
    }
}
