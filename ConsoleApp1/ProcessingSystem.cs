namespace ConsoleApp1;
public class ProcessingSystem
{
    // lock for synchronizing access to the queue
    private readonly object _lock = new();
    // queue to hold the jobs and their associated TaskCompletionSource(results), priority is determined by the job's priority
    private readonly PriorityQueue<(Job, TaskCompletionSource<int>), int> _queue = new();
    private readonly int _maxQueueSize;
    // idempotency tracking to ensure that the same job is not processed multiple times
    private readonly HashSet<int> _submittedIds = new();

    // For reports, track completed jobs in memory
    private readonly List<CompletedJobRecord> _completedJobs = new();
    private readonly object _completedLock = new object();

    // Events
    public event Action<Job, int>? JobCompleted;
    public event Action<Job>?     JobFailed;

    // Log file lock
    private readonly object _logLock = new object();


    // --- Constructor ---
    // Called ONCE from Main. Spins up worker threads immediately.
    public ProcessingSystem(int workerCount, int maxQueueSize)
    {
        _maxQueueSize = maxQueueSize;

        // Subscribe events for logging
        JobCompleted += (job, result) =>
            Task.Run(() => LogToFile($"[{DateTime.Now}][COMPLETED] {job.Id}, Result={result}"));
        JobFailed += (job) =>
            Task.Run(() => LogToFile($"[{DateTime.Now}][FAILED] {job.Id}"));

        // Start worker threads - they immediately go to sleep waiting for jobs
        for (int i = 0; i < workerCount; i++)
        {
            Thread worker = new Thread(WorkerLoop);
            worker.Name        = $"worker-{i}";
            worker.IsBackground = true;
            worker.Start();
        }

        Console.WriteLine($"[SYSTEM] Started {workerCount} workers.");
    }

    // --- Submit ---
    public JobHandle? Submit(Job job)
    {
        lock (_lock)
        {
            // Queue full - reject
            if (_queue.Count >= _maxQueueSize)
                return null; 

            // Already submitted - idempotency
            if (_submittedIds.Contains(job.Id))
                return null; 

            _submittedIds.Add(job.Id);

            var tcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue((job, tcs), job.Priority);

            Monitor.Pulse(_lock); // Wake ONE sleeping worker

            return new JobHandle { Id = job.Id, Result = tcs.Task };
        }
    }

    // --- Worker Loop ---
    // Each worker thread runs this forever
    private void WorkerLoop()
    {
        while (true)
        {
            (Job job, TaskCompletionSource<int> tcs) item;

            lock (_lock)
            {
                // Sleep (releases lock!) until Submit() calls Pulse()
                while (_queue.Count == 0)
                    Monitor.Wait(_lock);

                _queue.TryDequeue(out item, out _);
                // Lock releases here automatically (end of lock block)
            }

            // Execute OUTSIDE the lock so other threads can Submit() while we work
            ExecuteJob(item.job, item.tcs);
        }
    }

    // --- Execute with retry ---
    private void ExecuteJob(Job job, TaskCompletionSource<int> tcs)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var jobTask = Task.Run(() => RunJobLogic(job));
                bool finished = jobTask.Wait(TimeSpan.FromSeconds(2)); // 2s timeout

                if (finished)
                {
                    int result = jobTask.Result;

                    lock (_completedLock)
                        _completedJobs.Add(new CompletedJobRecord(job, result, false));

                    tcs.SetResult(result);
                    JobCompleted?.Invoke(job, result);
                    return; // success
                }

                Console.WriteLine($"[{Thread.CurrentThread.Name}] Job {job.Id} timed out (attempt {attempt})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Thread.CurrentThread.Name}] Job {job.Id} failed (attempt {attempt}): {ex.Message}");
            }
        }

        // All 3 attempts failed
        LogToFile($"[{DateTime.Now}][ABORT] {job.Id}");

        lock (_completedLock)
            _completedJobs.Add(new CompletedJobRecord(job, 0, failed: true));

        tcs.TrySetException(new Exception("Job aborted after 3 failures"));
        JobFailed?.Invoke(job);
    }

    // --- Job logic (the actual work) ---
    //TODO: ovo popraviti ne valja parsing payloada
    private int RunJobLogic(Job job)
    {
        if (job.Type == JobType.IO) // Simulate blocking I/O by sleeping for the specified duration in milliseconds
        {
            int delay = int.Parse(job.Payload);
            Thread.Sleep(delay); // intentional blocking simulation
            return new Random().Next(0, 101);
        }
        else // Prime
        {
            var parts = job.Payload.Split(',');
            int limit = int.Parse(parts[0]);
            int threadCount = Math.Clamp(int.Parse(parts[1]), 1, 8);

            int count = 0;
            Parallel.For(2, limit + 1,
                new ParallelOptions { MaxDegreeOfParallelism = threadCount },
                i => { if (IsPrime(i)) Interlocked.Increment(ref count); });

            return count;
        }
    }

    // --- Additional methods ---
    public IEnumerable<Job> GetTopJobs(int n)
    {
        lock (_queueLock)
            return _queue.UnorderedItems
                         .OrderBy(x => x.Priority)
                         .Take(n)
                         .Select(x => x.Element.Item1)  // x.Element is (Job, TaskCompletionSource<int>) and Item1 = Job
                         .ToList();
    }

    public Job? GetJob(Guid id)
    {
        // if job is in working queue
        lock (_queueLock)
        {
            var inQueue = _queue.UnorderedItems
                                .FirstOrDefault(x => x.Element.Item1.Id == id);
            if (inQueue.Element.Item1 != null)
                return inQueue.Element.Item1;
        }
        // if job is completed
        lock (_completedLock)
            return _completedJobs.FirstOrDefault(r => r.Job.Id == id)?.Job;
    }

    // --- Helpers ---
    private bool IsPrime(int n)
    {
        if (n < 2) return false;
        for (int i = 2; i * i <= n; i++)
            if (n % i == 0) return false;
        return true;
    }

    private void LogToFile(string message)
    {
        lock (_logLock)
            File.AppendAllText("../log.txt", message + "\n");
    }
}