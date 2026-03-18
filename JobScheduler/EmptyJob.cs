namespace JobScheduler;

internal struct EmptyJob : IJob
{
    public readonly void Execute(int index) { }
}