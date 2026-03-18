using BenchmarkDotNet.Attributes;

using MyJobScheduler = JobScheduler.Scheduler;
using ZeroAllocJobScheduler = Schedulers.JobScheduler;

namespace JobScheduler.Benchmarks;

public struct CalculationJob : IJob
{
    public int Iterations;

    public readonly void Execute(int _)
    {
        double result = 0;
        for (int i = 0; i < Iterations; i++)
            result += double.Sqrt(i);
    }
}

public class ZeroAllocJobSchedulerCalculationJob : Schedulers.IJob
{
    public int Iterations;

    public void Execute()
    {
        double result = 0;
        for (int i = 0; i < Iterations; i++)
            result += double.Sqrt(i);
    }
}

[MemoryDiagnoser]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class SchedulerBenchmark
{
    private MyJobScheduler _scheduler = null!;
    private ZeroAllocJobScheduler _oldScheduler = null!;

    private ZeroAllocJobSchedulerCalculationJob _oldParentJob = null!;
    private ZeroAllocJobSchedulerCalculationJob[] _oldCachedJobs = null!;

    [Params(100, 1000)]
    public int JobsCount;

    [GlobalSetup(Target = nameof(JobScheduler_New))]
    public void SetupNew() => _scheduler = new MyJobScheduler();

    [GlobalCleanup(Target = nameof(JobScheduler_New))]
    public void CleanupNew() => _scheduler?.Dispose();

    [GlobalSetup(Target = nameof(ZeroAllocJobScheduler_Original))]
    public void SetupOld()
    {
        var config = new ZeroAllocJobScheduler.Config();
        _oldScheduler = new ZeroAllocJobScheduler(in config);

        _oldParentJob = new ZeroAllocJobSchedulerCalculationJob { Iterations = 0 };
        _oldCachedJobs = new ZeroAllocJobSchedulerCalculationJob[JobsCount];
        for (int i = 0; i < JobsCount; i++)
            _oldCachedJobs[i] = new ZeroAllocJobSchedulerCalculationJob { Iterations = 100 };
    }

    [GlobalCleanup(Target = nameof(ZeroAllocJobScheduler_Original))]
    public void CleanupOld() => _oldScheduler?.Dispose();

    [Benchmark(Baseline = true)]
    public void StandardParallelFor()
    {
        Parallel.For(0, JobsCount, _ =>
        {
            var job = new CalculationJob { Iterations = 100 };
            job.Execute(0);
        });
    }

    [Benchmark]
    public async Task StandardTasksWhenAll()
    {
        var tasks = new Task[JobsCount];
        for (int i = 0; i < JobsCount; i++)
            tasks[i] = Task.Run(() =>
            {
                var job = new CalculationJob { Iterations = 100 };
                job.Execute(0);
            });
        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public void JobScheduler_New()
    {
        var job = new CalculationJob { Iterations = 100 };
        var handle = _scheduler.Schedule(in job, JobsCount);
        _scheduler.Wait(handle);
    }

    [Benchmark]
    public void ZeroAllocJobScheduler_Original()
    {
        var parentHandle = _oldScheduler.Schedule(_oldParentJob);

        for (int i = 0; i < JobsCount; i++)
            _ = _oldScheduler.Schedule(_oldCachedJobs[i], parentHandle);

        _oldScheduler.Flush();
        parentHandle.Complete();
    }
}