using System.Runtime.CompilerServices;

namespace JobScheduler;

internal static class ParallelJobTypePool<T> where T : struct, IJob
{
    public static readonly T[] Payloads = new T[JobPool.MaxJobs];
    public static readonly int[] StartIndices = new int[JobPool.MaxJobs];
    public static readonly int[] EndIndices = new int[JobPool.MaxJobs];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Execute(int index)
    {
        int start = StartIndices[index];
        int end = EndIndices[index];
        ref T job = ref Payloads[index];

        for (int i = start; i < end; i++)
            job.Execute(i);

        Payloads[index] = default;
    }
}