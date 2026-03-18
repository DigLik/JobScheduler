using System.Runtime.CompilerServices;

namespace JobScheduler;

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
    /// <param name="threads">Количество рабочих потоков. Если 0, используется количество логических ядер процессора.</param>
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
    /// Планирует единичную задачу для выполнения.
    /// </summary>
    /// <typeparam name="T">Тип структуры задачи.</typeparam>
    /// <param name="job">Данные задачи.</param>
    /// <param name="dependency">Опциональная задача, завершения которой необходимо дождаться перед запуском.</param>
    /// <returns>Дескриптор запланированной задачи.</returns>
    public JobHandle Schedule<T>(in T job, JobHandle dependency = default) where T : struct, IJob
    {
        int id = _pool.RentId();
        JobTypePool<T>.Payloads[id] = job;

        int parentId = dependency.IsValid ? dependency.Index : -1;
        if (parentId != -1)
            Interlocked.Increment(ref _pool.Counters[parentId].Value);

        _pool.SetupJob(id, &JobTypePool<T>.Execute, parentId);
        Flush(id);

        return new JobHandle(id);
    }

    /// <summary>
    /// Планирует параллельное выполнение задачи для массива данных.
    /// Автоматически разделяет нагрузку на оптимальные батчи (чанки) для максимальной производительности.
    /// </summary>
    /// <typeparam name="T">Тип структуры задачи.</typeparam>
    /// <param name="job">Данные задачи.</param>
    /// <param name="arrayLength">Общее количество элементов для обработки.</param>
    /// <param name="dependency">Опциональная задача, завершения которой необходимо дождаться перед запуском.</param>
    /// <returns>Дескриптор, представляющий всю группу параллельных задач.</returns>
    public JobHandle Schedule<T>(in T job, int arrayLength, JobHandle dependency = default) where T : struct, IJob
    {
        if (arrayLength <= 0) return dependency;

        int batchSize = arrayLength / Workers.Count;
        if (batchSize < 128) batchSize = 128;

        int batches = (arrayLength + batchSize - 1) / batchSize;

        if (batches == 1)
        {
            int id = _pool.RentId();
            ParallelJobTypePool<T>.Payloads[id] = job;
            ParallelJobTypePool<T>.StartIndices[id] = 0;
            ParallelJobTypePool<T>.EndIndices[id] = arrayLength;

            int parentId = dependency.IsValid ? dependency.Index : -1;
            if (parentId != -1) Interlocked.Increment(ref _pool.Counters[parentId].Value);

            _pool.SetupJob(id, &ParallelJobTypePool<T>.Execute, parentId);
            Flush(id);

            return new JobHandle(id);
        }

        int groupId = _pool.RentId();
        JobTypePool<EmptyJob>.Payloads[groupId] = default;

        int pId = dependency.IsValid ? dependency.Index : -1;
        if (pId != -1)
            Interlocked.Increment(ref _pool.Counters[pId].Value);

        _pool.SetupJob(groupId, &JobTypePool<EmptyJob>.Execute, pId);

        for (int i = 0; i < batches; i++)
        {
            int start = i * batchSize;
            int end = start + batchSize;
            if (end > arrayLength) end = arrayLength;

            Interlocked.Increment(ref _pool.Counters[groupId].Value);
            int id = _pool.RentId();

            ParallelJobTypePool<T>.Payloads[id] = job;
            ParallelJobTypePool<T>.StartIndices[id] = start;
            ParallelJobTypePool<T>.EndIndices[id] = end;

            _pool.SetupJob(id, &ParallelJobTypePool<T>.Execute, groupId);
            Flush(id);
        }

        Flush(groupId);
        return new JobHandle(groupId);
    }

    /// <summary>
    /// Блокирует текущий поток до полного завершения указанной задачи.
    /// Во время ожидания текущий поток активно помогает выполнять задачи из очереди.
    /// </summary>
    /// <param name="handle">Дескриптор задачи для ожидания.</param>
    public void Wait(JobHandle handle)
    {
        if (!handle.IsValid) return;

        ref var counter = ref _pool.Counters[handle.Index];
        SpinWait spin = new SpinWait();

        while (Volatile.Read(ref counter.Value) > 0)
        {
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
    internal void Flush(int jobId)
    {
        uint idx = (uint)Interlocked.Increment(ref _nextWorkerIndex);
        int workerIndex = (int)(idx % (uint)Workers.Count);

        Workers[workerIndex].IncomingQueue.Enqueue(new JobHandle(jobId));
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
        int id = job.Index;
        if (Interlocked.Decrement(ref _pool.Counters[id].Value) != 0) return;

        int parent = _pool.Parents[id];
        if (parent != -1) Finish(new JobHandle(parent));

        int currentEdge = _pool.HeadEdge[id];
        while (currentEdge != -1)
        {
            int target = _pool.EdgeTargets[currentEdge];
            Flush(target);
            currentEdge = _pool.NextEdge[currentEdge];
        }

        _pool.Return(id);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        foreach (var worker in Workers) worker.Stop();
    }
}