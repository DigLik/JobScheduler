using System.Collections.Concurrent;

namespace JobScheduler;

internal sealed class Worker
{
    private readonly int _workerId;
    private readonly Thread _thread;
    private readonly Scheduler _scheduler;
    private readonly CancellationTokenSource _cts;
    private readonly AutoResetEvent _signal;

    public volatile int IsSleeping;

    public readonly ConcurrentQueue<JobHandle> IncomingQueue = new();
    public readonly WorkStealingDeque<JobHandle> Queue = new(64);

    public Worker(Scheduler scheduler, int id)
    {
        _workerId = id;
        _scheduler = scheduler;
        _cts = new CancellationTokenSource();
        _signal = new AutoResetEvent(false);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"JobWorker_{id}"
        };
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _cts.Cancel();
        IsSleeping = 0;
        _signal.Set();
        _thread.Join();
        _signal.Dispose();
    }

    public void WakeUp()
    {
        if (IsSleeping == 1)
        {
            IsSleeping = 0;
            _signal.Set();
        }
    }

    private void Run()
    {
        var token = _cts.Token;
        int idleSpins = 0;

        while (!token.IsCancellationRequested)
        {
            bool processedAny = false;
            int transferred = 0;

            while (transferred < 32 && IncomingQueue.TryDequeue(out var incomingJob))
            {
                Queue.PushBottom(incomingJob);
                transferred++;
            }

            if (Queue.TryPopBottom(out var job))
            {
                _scheduler.ExecuteJob(job);
                _scheduler.Finish(job);
                processedAny = true;
                idleSpins = 0;
            }
            else
            {
                for (int i = 0; i < _scheduler.Queues.Count; i++)
                {
                    if (i == _workerId) continue;

                    if (_scheduler.Queues[i].TrySteal(out job))
                    {
                        _scheduler.ExecuteJob(job);
                        _scheduler.Finish(job);
                        processedAny = true;
                        idleSpins = 0;
                        break;
                    }
                }
            }

            if (!processedAny)
            {
                if (idleSpins < 100)
                {
                    Thread.SpinWait(10);
                    idleSpins++;
                }
                else if (idleSpins < 500)
                {
                    Thread.Yield();
                    idleSpins++;
                }
                else
                {
                    IsSleeping = 1;
                    if (IncomingQueue.IsEmpty && Queue.Size() == 0)
                    {
                        _signal.WaitOne(2);
                    }
                    IsSleeping = 0;
                }
            }
        }
    }
}