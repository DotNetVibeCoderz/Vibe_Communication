using System.Diagnostics.Metrics;

namespace VoipNet.Diagnostics;

/// <summary>
/// Performance counters published through <see cref="System.Diagnostics.Metrics"/> under the meter
/// name <c>VoipNet</c>. Watch them live with <c>dotnet-counters monitor --counters VoipNet</c> or
/// export them with OpenTelemetry.
/// </summary>
public static class VoipMetrics
{
    /// <summary>Meter name to subscribe to.</summary>
    public const string MeterName = "VoipNet";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    internal static readonly Counter<long> CallsStarted = Meter.CreateCounter<long>("voipnet.calls.started", "{call}", "Calls placed or received.");
    internal static readonly Counter<long> CallsAnswered = Meter.CreateCounter<long>("voipnet.calls.answered", "{call}", "Calls that reached the connected state.");
    internal static readonly Counter<long> CallsFailed = Meter.CreateCounter<long>("voipnet.calls.failed", "{call}", "Calls that ended before connecting.");
    internal static readonly UpDownCounter<long> ActiveCalls = Meter.CreateUpDownCounter<long>("voipnet.calls.active", "{call}", "Calls currently in progress.");
    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>("voipnet.call.duration", "s", "Connected duration of finished calls.");
    internal static readonly Histogram<double> CallMos = Meter.CreateHistogram<double>("voipnet.call.mos", "{score}", "Estimated MOS at the end of each call.");
    internal static readonly Histogram<double> PacketLoss = Meter.CreateHistogram<double>("voipnet.call.packet_loss", "%", "Packet loss at the end of each call.");
    internal static readonly Counter<long> Registrations = Meter.CreateCounter<long>("voipnet.registrations", "{registration}", "Registration results, tagged by state.");
}
