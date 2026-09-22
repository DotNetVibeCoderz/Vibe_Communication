using System.Diagnostics;

namespace VoipNet.Diagnostics;

/// <summary>
/// Distributed tracing for calls, published through <see cref="System.Diagnostics.Activity"/> under
/// the source name <c>VoipNet</c>. Subscribe with OpenTelemetry
/// (<c>.WithTracing(t =&gt; t.AddSource(VoipTelemetry.ActivitySourceName))</c>) or with an
/// <see cref="ActivityListener"/>. Nothing is recorded while no listener is attached.
/// </summary>
/// <remarks>
/// A call is one span: it opens when the call is placed or arrives and closes when the call ends,
/// carrying the SIP identifiers, the codec and the quality the call finished with. An outbound call
/// continues whatever activity placed it, so a call shows up under the request that triggered it.
/// Voice agents add a child span per turn.
/// </remarks>
public static class VoipTelemetry
{
    /// <summary>Activity source name to subscribe to.</summary>
    public const string ActivitySourceName = "VoipNet";

    internal static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    /// <summary>Span name for a call, from the first SIP message to the last.</summary>
    public const string CallSpan = "sip.call";

    /// <summary>Span name for one turn of a voice agent: listen, think, speak.</summary>
    public const string AgentTurnSpan = "voip.agent.turn";

    /// <summary>True when something is listening, so callers can skip work that only feeds a trace.</summary>
    public static bool Enabled => Source.HasListeners();

    internal static Activity? StartCall(VoipCall call)
    {
        var activity = Source.StartActivity(CallSpan, call.IsOutgoing ? ActivityKind.Client : ActivityKind.Server);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("sip.direction", call.IsOutgoing ? "outbound" : "inbound");
        activity.SetTag("sip.remote_uri", call.RemoteUri);
        activity.SetTag("voipnet.call_id", call.Id);
        return activity;
    }

    /// <summary>Closes a call's span with how the call actually went.</summary>
    internal static void EndCall(VoipCall call, Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("sip.status_code", call.LastStatusCode);
        if (!string.IsNullOrEmpty(call.Codec))
        {
            activity.SetTag("voip.codec", call.Codec);
        }

        if (call.ConnectedAt is null)
        {
            // A call that never connected is only a failure if the other side refused it; a caller
            // hanging up first is an ordinary outcome.
            activity.SetStatus(call.LastStatusCode >= 400 ? ActivityStatusCode.Error : ActivityStatusCode.Ok, call.LastStatusCode >= 400 ? $"call failed with {call.LastStatusCode}" : null);
        }
        else
        {
            activity.SetTag("voip.duration_seconds", Math.Round(call.Duration.TotalSeconds, 3));
            if (call.FinalStatistics is { } stats)
            {
                activity.SetTag("voip.mos", Math.Round(stats.Mos, 2));
                activity.SetTag("voip.loss_percent", Math.Round(stats.LossPercent, 2));
                activity.SetTag("voip.jitter_ms", Math.Round(stats.JitterMs, 2));
            }

            activity.SetStatus(ActivityStatusCode.Ok);
        }

        activity.Dispose();
    }

    /// <summary>Opens a span for one agent turn, as a child of the call's span when there is one.</summary>
    /// <param name="call">Call the turn belongs to.</param>
    /// <param name="model">Model answering the turn, if known.</param>
    /// <returns>The span, or <c>null</c> when nothing is listening.</returns>
    public static Activity? StartAgentTurn(VoipCall? call, string? model = null)
    {
        var parent = call?.Activity?.Context ?? default;
        var activity = Source.StartActivity(AgentTurnSpan, ActivityKind.Internal, parent);
        if (activity is not null && !string.IsNullOrEmpty(model))
        {
            activity.SetTag("gen_ai.request.model", model);
        }

        return activity;
    }
}
