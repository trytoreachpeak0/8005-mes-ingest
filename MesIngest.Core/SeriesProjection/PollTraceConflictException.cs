namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// A PollTraceId is an immutable idempotency identity and cannot be rebound to
/// another normalized round result.
/// </summary>
public sealed class PollTraceConflictException : InvalidOperationException
{
    public const string ConflictCode = "POLL_TRACE_CONTENT_CONFLICT";

    public PollTraceConflictException(string pollTraceId)
        : base(
            $"PollTraceId '{pollTraceId}' is already bound to different normalized round content "
            + $"under contract '{NewMesIngestContract.Version}'.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pollTraceId);
        PollTraceId = pollTraceId;
    }

    public string Code => ConflictCode;

    public string ContractVersion => NewMesIngestContract.Version;

    public string PollTraceId { get; }
}
