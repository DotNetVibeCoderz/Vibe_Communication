namespace Rumble.Net;

/// <summary>Error codes reported by the native core.</summary>
public enum RumbleErrorCode
{
    /// <summary>No error.</summary>
    None = 0,
    /// <summary>An argument was invalid.</summary>
    InvalidArgument = -1,
    /// <summary>The client is not connected.</summary>
    NotConnected = -2,
    /// <summary>Network or protocol failure.</summary>
    Network = -3,
    /// <summary>Audio device or codec failure.</summary>
    Audio = -4,
    /// <summary>TLS or certificate failure.</summary>
    Tls = -5,
    /// <summary>Invalid JSON crossed the native boundary.</summary>
    Json = -6,
    /// <summary>A panic occurred in native code (bug).</summary>
    Panic = -7,
    /// <summary>The operation is not supported in the current mode.</summary>
    Unsupported = -8,
    /// <summary>The operation timed out.</summary>
    Timeout = -9,
    /// <summary>Internal error.</summary>
    Internal = -10,
}

/// <summary>Base exception for Rumble.Net failures.</summary>
public class RumbleException : Exception
{
    /// <summary>Creates a new exception.</summary>
    public RumbleException(RumbleErrorCode code, string message)
        : base(message)
    {
        ErrorCode = code;
    }

    /// <summary>Creates a new exception with an inner exception.</summary>
    public RumbleException(RumbleErrorCode code, string message, Exception? inner)
        : base(message, inner)
    {
        ErrorCode = code;
    }

    /// <summary>The native error code.</summary>
    public RumbleErrorCode ErrorCode { get; }
}

/// <summary>Thrown when connecting fails or the server rejects the client.</summary>
public sealed class RumbleConnectionException : RumbleException
{
    /// <summary>Creates a new exception.</summary>
    public RumbleConnectionException(string message, string? rejectType = null)
        : base(RumbleErrorCode.Network, message)
    {
        RejectType = rejectType;
    }

    /// <summary>Server reject reason type (e.g. <c>WrongServerPw</c>), when rejected.</summary>
    public string? RejectType { get; }
}

/// <summary>Thrown when the server denies a request.</summary>
public sealed class RumblePermissionDeniedException : RumbleException
{
    /// <summary>Creates a new exception.</summary>
    public RumblePermissionDeniedException(Events.PermissionDeniedEvent denial)
        : base(RumbleErrorCode.InvalidArgument, denial.Reason ?? $"Permission denied ({denial.DenyType})")
    {
        Denial = denial;
    }

    /// <summary>Details of the denial.</summary>
    public Events.PermissionDeniedEvent Denial { get; }
}
