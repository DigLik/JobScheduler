using System.Runtime.InteropServices;

namespace JobScheduler;

[StructLayout(LayoutKind.Sequential)]
internal struct JobCounter
{
    public long Value;
}

internal sealed unsafe class JobPool
{
    public const int MaxJobs = 262144;

    public struct JobMetadata
    {
        public delegate* managed<int, void> Execute;
    }

    public readonly JobMetadata[] Jobs = new JobMetadata[MaxJobs];
    public readonly JobHandle[] Parents = new JobHandle[MaxJobs];
    public readonly JobCounter[] Counters = new JobCounter[MaxJobs];
    public readonly int[] Generations = new int[MaxJobs];

    private long _freeJobsHead;
    private readonly int[] _freeJobsNext = new int[MaxJobs];

    public JobPool()
    {
        for (int i = 0; i < MaxJobs - 1; i++) _freeJobsNext[i] = i + 1;
        _freeJobsNext[MaxJobs - 1] = -1;
        _freeJobsHead = 0;
    }

    public int RentId()
    {
        long head, next;
        do
        {
            head = Volatile.Read(ref _freeJobsHead);
            int index = (int)(head & 0xFFFFFFFF);
            if (index == -1) throw new InvalidOperationException("Пул задач исчерпан.");
            int nextIndex = _freeJobsNext[index];
            long aba = (head >> 32) + 1;
            next = (aba << 32) | (uint)nextIndex;
        } while (Interlocked.CompareExchange(ref _freeJobsHead, next, head) != head);

        return (int)(head & 0xFFFFFFFF);
    }

    public void SetupJob(int id, int gen, delegate* managed<int, void> execute, JobHandle parent)
    {
        Jobs[id].Execute = execute;
        Parents[id] = parent;
        Counters[id].Value = ((long)gen << 32) | 1;
    }

    public void Return(int id)
    {
        Jobs[id].Execute = null;
        Parents[id] = default;

        long jobHead, jobNext;
        do
        {
            jobHead = Volatile.Read(ref _freeJobsHead);
            _freeJobsNext[id] = (int)(jobHead & 0xFFFFFFFF);
            long aba = (jobHead >> 32) + 1;
            jobNext = (aba << 32) | (uint)id;
        } while (Interlocked.CompareExchange(ref _freeJobsHead, jobNext, jobHead) != jobHead);
    }

    public bool TryIncrementCounter(JobHandle handle)
    {
        if (!handle.IsValid) return false;
        long current = Volatile.Read(ref Counters[handle.Index].Value);
        while (true)
        {
            if ((int)(current >> 32) != handle.Generation) return false;
            if ((int)current == 0) return false;
            long next = current + 1;
            long actual = Interlocked.CompareExchange(ref Counters[handle.Index].Value, next, current);
            if (actual == current) return true;
            current = actual;
        }
    }
}