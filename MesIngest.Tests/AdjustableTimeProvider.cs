namespace MesIngest.Tests;

internal sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            _utcNow = utcNow.ToUniversalTime();
        }
    }
}
