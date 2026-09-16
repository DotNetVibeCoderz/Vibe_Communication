using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace VoipNet.AI.Agents;

/// <summary>
/// Tools that let the model act on the call itself: transfer it to a colleague, end it, press keys
/// on an IVR, or put the caller on hold. Add them to <see cref="ChatOptions.Tools"/> (the agent
/// does that for you when <see cref="VoiceAgentOptions.EnableCallControlTools"/> is on).
/// </summary>
public static class CallControlTools
{
    /// <summary>Creates the call control tools for one call.</summary>
    /// <param name="call">Call to control.</param>
    /// <param name="agent">Agent that owns the call, used for spoken announcements.</param>
    public static IReadOnlyList<AITool> Create(VoipCall call, VoiceAgent? agent = null)
    {
        ArgumentNullException.ThrowIfNull(call);

        [Description("Transfer the caller to a colleague, department or phone number. Use it when the caller asks for a human or the request is outside your remit.")]
        async Task<string> TransferCall(
            [Description("Destination, for example sip:support@company.com or an extension such as 2001.")] string destination,
            [Description("One short sentence spoken to the caller before the transfer.")] string announcement = "")
        {
            if (agent is not null)
            {
                var done = await agent.HandOffAsync(destination, announcement).ConfigureAwait(false);
                return done ? $"Transferring the caller to {destination}." : "The call is no longer active.";
            }

            if (!call.IsActive)
            {
                return "The call is no longer active.";
            }

            call.Transfer(destination);
            return $"Transferring the caller to {destination}.";
        }

        [Description("End the call politely. Say goodbye first; the call hangs up once you stop speaking.")]
        string EndCall([Description("Why the call is ending, for logs.")] string reason = "")
        {
            if (agent is not null)
            {
                agent.ShouldHangUp = true;
                return "The call will end after this answer.";
            }

            call.Hangup();
            return "Call ended.";
        }

        [Description("Send DTMF key presses, for example to navigate another system's menu.")]
        string SendDigits([Description("Digits to press, such as 1234#.")] string digits)
        {
            if (!call.IsActive)
            {
                return "The call is no longer active.";
            }

            call.SendDtmf(digits);
            return $"Sent {digits}.";
        }

        [Description("Put the caller on hold or take them off hold.")]
        string SetHold([Description("True to hold, false to resume.")] bool hold)
        {
            if (!call.IsActive)
            {
                return "The call is no longer active.";
            }

            call.SetHold(hold);
            return hold ? "The caller is on hold." : "The caller is back.";
        }

        [Description("Read the current call quality: packet loss, jitter and estimated mean opinion score.")]
        string GetCallQuality()
        {
            var stats = call.GetStatistics();
            return $"MOS {stats.Mos:F1}, loss {stats.LossPercent:F1}%, jitter {stats.JitterMs:F0} ms, codec payload type {stats.PayloadType}.";
        }

        return
        [
            AIFunctionFactory.Create(TransferCall, "transfer_call"),
            AIFunctionFactory.Create(EndCall, "end_call"),
            AIFunctionFactory.Create(SendDigits, "send_dtmf"),
            AIFunctionFactory.Create(SetHold, "set_hold"),
            AIFunctionFactory.Create(GetCallQuality, "get_call_quality"),
        ];
    }
}
