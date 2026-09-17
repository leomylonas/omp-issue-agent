namespace IssueAgent.Host;

public sealed class ReadinessState
{
    private int initialized;

    public bool IsInitialized => Volatile.Read(ref initialized) == 1;

    public void MarkInitialized() => Interlocked.Exchange(ref initialized, 1);
}
