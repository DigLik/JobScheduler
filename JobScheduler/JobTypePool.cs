using System.Runtime.CompilerServices;

namespace JobScheduler;

internal static class JobTypePool<T> where T : struct, IJob
{
    public static readonly T[] Payloads = new T[JobPool.MaxJobs];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Execute(int index)
    {
        Payloads[index].Execute(0);
        Payloads[index] = default;
    }
}