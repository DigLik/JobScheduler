using System.Runtime.CompilerServices;

namespace JobScheduler;

/// <summary>
/// Представляет планировщик задач, управляющий пулом рабочих потоков и распределением задач.
/// </summary>
public sealed unsafe class Scheduler : IDisposable
{
    private readonly JobPool _pool;
    internal readonly List<Worker> Workers;
    internal readonly List<WorkStealingDeque<JobHandle>> Queues;
    private int _nextWorkerIndex;
    private bool _isDisposed;

    /// <summary>
    /// Инициализирует новый экземпляр планировщика задач.
    /// </summary>
    /// <param name="threads">Количество рабочих потоков. Если передано значение 0 или меньше, используется количество логических процессоров системы.</param>
    public Scheduler(int threads = 0)
    {
        _pool = new JobPool();
        Workers = [];
        Queues = [];

        int amount = threads > 0 ? threads : Environment.ProcessorCount;

        for (int index = 0; index < amount; index++)
        {
            var worker = new Worker(this, index);
            Workers.Add(worker);
            Queues.Add(worker.Queue);
        }

        foreach (var worker in Workers) worker.Start();
    }

    /// <summary>
    /// Планирует единичную задачу.
    /// </summary>
    public JobHandle Schedule<T>(in T job, JobHandle parent = default) where T : struct, IJob
    {
        int id = _pool.RentId();
        int gen = Interlocked.Increment(ref _pool.Generations[id]);
        JobTypePool<T>.Payloads[id] = job;

        JobHandle actualParent = default;
        if (_pool.TryIncrementCounter(parent))
        {
            actualParent = parent;
        }

        _pool.SetupJob(id, gen, &JobTypePool<T>.Execute, actualParent);
        Flush(new JobHandle(id, gen));

        return new JobHandle(id, gen);
    }

    /// <summary>
    /// Планирует задачу на выполнение массива с авто-батчингом.
    /// </summary>
    public JobHandle Schedule<T>(in T job, int arrayLength, JobHandle parent = default) where T : struct, IJob
    {
        if (arrayLength <= 0) return parent;

        int batchSize = arrayLength / (Workers.Count * 4);
        if (batchSize < 128) batchSize = 128;

        int batches = (arrayLength + batchSize - 1) / batchSize;

        // Fast-Path: Массив не делится
        if (batches == 1)
        {
            int id = _pool.RentId();
            int gen = Interlocked.Increment(ref _pool.Generations[id]);

            ParallelJobTypePool<T>.Payloads[id] = job;
            ParallelJobTypePool<T>.StartIndices[id] = 0;
            ParallelJobTypePool<T>.EndIndices[id] = arrayLength;

            JobHandle actualParent = default;
            if (_pool.TryIncrementCounter(parent)) actualParent = parent;

            _pool.SetupJob(id, gen, &ParallelJobTypePool<T>.Execute, actualParent);
            Flush(new JobHandle(id, gen));

            return new JobHandle(id, gen);
        }

        // Standard-Path: Создание узла для батчей
        int groupId = _pool.RentId();
        int groupGen = Interlocked.Increment(ref _pool.Generations[groupId]);
        JobTypePool<EmptyJob>.Payloads[groupId] = default;

        JobHandle groupParent = default;
        if (_pool.TryIncrementCounter(parent)) groupParent = parent;

        _pool.SetupJob(groupId, groupGen, &JobTypePool<EmptyJob>.Execute, groupParent);
        var groupHandle = new JobHandle(groupId, groupGen);

        for (int i = 0; i < batches; i++)
        {
            int start = i * batchSize;
            int end = start + batchSize;
            if (end > arrayLength) end = arrayLength;

            _pool.TryIncrementCounter(groupHandle);

            int id = _pool.RentId();
            int gen = Interlocked.Increment(ref _pool.Generations[id]);

            ParallelJobTypePool<T>.Payloads[id] = job;
            ParallelJobTypePool<T>.StartIndices[id] = start;
            ParallelJobTypePool<T>.EndIndices[id] = end;

            _pool.SetupJob(id, gen, &ParallelJobTypePool<T>.Execute, groupHandle);
            Flush(new JobHandle(id, gen));
        }

        Flush(groupHandle);
        return groupHandle;
    }

    /// <summary>
    /// Ожидает завершения задачи, активно помогая её выполнять.
    /// </summary>
    public void Wait(JobHandle handle)
    {
        if (!handle.IsValid) return;

        ref var counter = ref _pool.Counters[handle.Index];
        var spin = new SpinWait();

        while (true)
        {
            long current = Volatile.Read(ref counter.Value);
            if ((int)(current >> 32) != handle.Generation) break;
            if ((int)current == 0) break;

            bool executedAny = false;
            for (int i = 0; i < Workers.Count; i++)
            {
                if (Workers[i].Queue.TrySteal(out var stolenJob))
                {
                    ExecuteJob(stolenJob);
                    Finish(stolenJob);
                    executedAny = true;
                }
                else if (Workers[i].IncomingQueue.TryDequeue(out var incomingJob))
                {
                    ExecuteJob(incomingJob);
                    Finish(incomingJob);
                    executedAny = true;
                }
            }

            if (!executedAny) spin.SpinOnce();
            else spin.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Flush(JobHandle jobHandle)
    {
        uint idx = (uint)Interlocked.Increment(ref _nextWorkerIndex);
        int workerIndex = (int)(idx % (uint)Workers.Count);

        Workers[workerIndex].IncomingQueue.Enqueue(jobHandle);
        Workers[workerIndex].WakeUp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteJob(JobHandle handle)
    {
        var execute = _pool.Jobs[handle.Index].Execute;
        if (execute != null) execute(handle.Index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Finish(JobHandle job)
    {
        JobHandle currentJob = job;

        while (currentJob.IsValid)
        {
            int id = currentJob.Index;
            long current = Volatile.Read(ref _pool.Counters[id].Value);
            long next;

            while (true)
            {
                if ((int)(current >> 32) != currentJob.Generation) return;
                next = current - 1;
                long actual = Interlocked.CompareExchange(ref _pool.Counters[id].Value, next, current);
                if (actual == current) break;
                current = actual;
            }

            if ((int)next != 0) return;

            JobHandle parent = _pool.Parents[id];
            _pool.Return(id);

            currentJob = parent;
        }
    }

    /// <summary>
    /// Освобождает ресурсы, используемые планировщиком задач, и останавливает все рабочие потоки.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        foreach (var worker in Workers) worker.Stop();
    }
}