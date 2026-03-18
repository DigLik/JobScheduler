using System.Runtime.InteropServices;

namespace JobScheduler;

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct JobCounter
{
    [FieldOffset(0)]
    public int Value;
}

internal sealed unsafe class JobPool
{
    public const int MaxJobs = 262144;
    public const int MaxEdges = MaxJobs * 4;

    public struct JobMetadata
    {
        public delegate* managed<int, void> Execute;
    }

    public readonly JobMetadata[] Jobs = new JobMetadata[MaxJobs];
    public readonly int[] Parents = new int[MaxJobs];
    public readonly JobCounter[] Counters = new JobCounter[MaxJobs];

    public readonly int[] HeadEdge = new int[MaxJobs];
    public readonly int[] EdgeTargets = new int[MaxEdges];
    public readonly int[] NextEdge = new int[MaxEdges];

    private long _freeJobsHead;
    private readonly int[] _freeJobsNext = new int[MaxJobs];

    private long _freeEdgesHead;
    private readonly int[] _freeEdgesNext = new int[MaxEdges];

    public JobPool()
    {
        for (int i = 0; i < MaxJobs - 1; i++) _freeJobsNext[i] = i + 1;
        _freeJobsNext[MaxJobs - 1] = -1;
        _freeJobsHead = 0;

        for (int i = 0; i < MaxEdges - 1; i++) _freeEdgesNext[i] = i + 1;
        _freeEdgesNext[MaxEdges - 1] = -1;
        _freeEdgesHead = 0;

        for (int i = 0; i < MaxJobs; i++) HeadEdge[i] = -1;
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

    public void SetupJob(int id, delegate* managed<int, void> execute, int parentId)
    {
        Jobs[id].Execute = execute;
        Parents[id] = parentId;
        Counters[id].Value = 1;
        HeadEdge[id] = -1;
    }

    public void Return(int id)
    {
        Jobs[id].Execute = null;

        int currentEdge = HeadEdge[id];
        while (currentEdge != -1)
        {
            int nextEdge = NextEdge[currentEdge];

            long edgeHead, edgeNext;
            do
            {
                edgeHead = Volatile.Read(ref _freeEdgesHead);
                _freeEdgesNext[currentEdge] = (int)(edgeHead & 0xFFFFFFFF);
                long aba = (edgeHead >> 32) + 1;
                edgeNext = (aba << 32) | (uint)currentEdge;
            } while (Interlocked.CompareExchange(ref _freeEdgesHead, edgeNext, edgeHead) != edgeHead);

            currentEdge = nextEdge;
        }
        HeadEdge[id] = -1;

        long jobHead, jobNext;
        do
        {
            jobHead = Volatile.Read(ref _freeJobsHead);
            _freeJobsNext[id] = (int)(jobHead & 0xFFFFFFFF);
            long aba = (jobHead >> 32) + 1;
            jobNext = (aba << 32) | (uint)id;
        } while (Interlocked.CompareExchange(ref _freeJobsHead, jobNext, jobHead) != jobHead);
    }

    public void AddDependency(int dependOn, int dependency)
    {
        long edgeHead, edgeNext;
        do
        {
            edgeHead = Volatile.Read(ref _freeEdgesHead);
            int index = (int)(edgeHead & 0xFFFFFFFF);
            if (index == -1) throw new InvalidOperationException("Пул ребер исчерпан.");
            int nextIndex = _freeEdgesNext[index];
            long aba = (edgeHead >> 32) + 1;
            edgeNext = (aba << 32) | (uint)nextIndex;
        } while (Interlocked.CompareExchange(ref _freeEdgesHead, edgeNext, edgeHead) != edgeHead);

        int edgeIdx = (int)(edgeHead & 0xFFFFFFFF);
        EdgeTargets[edgeIdx] = dependency;

        while (true)
        {
            int currentHead = HeadEdge[dependOn];
            NextEdge[edgeIdx] = currentHead;

            if (Interlocked.CompareExchange(ref HeadEdge[dependOn], edgeIdx, currentHead) == currentHead)
                break;
        }
    }
}