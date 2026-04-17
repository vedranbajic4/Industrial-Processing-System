namespace ConsoleApp1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

public class ProcessingSystem
{
    // lock for synchronizing access to the queue
    private readonly object _queueLock = new();
    // queue to hold the jobs and their associated TaskCompletionSource(results), priority is determined by the job's priority
    private readonly PriorityQueue<(Job, TaskCompletionSource<int>), int> _queue = new();
    // idempotency tracking to ensure that the same job is not processed multiple times
    private readonly HashSet<Guid> _submittedIds = new();

    // For reports, track completed jobs in memory
    private readonly List<CompletedJobRecord> _completedJobs = new();
    private readonly object _completedLock = new object();

    // Events
    public event Action<Job, int>? JobCompleted;
    public event Action<Job>? JobFailed;

    // Log file lock
    private readonly object _logLock = new object();

    private int _reportIndex = 0;          // tracks which file slot to write (0-9)

    // --- Constructor ---
    // Called ONCE from Main. Spins up worker threads immediately.
    public ProcessingSystem()
    {
        // Subscribe events for logging
        JobCompleted += (job, result) =>
            Task.Run(() => LogToFile($"[{DateTime.Now}][COMPLETED] {job.Id}, Result={result}"));
        JobFailed += (job) =>
            Task.Run(() => LogToFile($"[{DateTime.Now}][FAILED] {job.Id}"));

        // Start worker threads - they immediately go to sleep waiting for jobs
        for (int i = 0; i < SystemConfiguration.WorkerThreads; i++)
        {
            Thread worker = new Thread(WorkerLoop);
            worker.Name        = $"worker-{i}";
            worker.IsBackground = true;
            worker.Start();
        }

        // Start report generation — fires every 60 seconds
        Thread reportThread = new Thread(ReportLoop);
        reportThread.Name = "report-thread";
        reportThread.IsBackground = true;
        reportThread.Start();
            
        Console.WriteLine($"[SYSTEM] Started {SystemConfiguration.WorkerThreads} workers.");
    }

    // --- Submit ---
    public JobHandle? Submit(Job job)
    {
        lock (_queueLock)
        {
            // Queue full - reject
            if (_queue.Count >= SystemConfiguration.MaxQueueSize)
                return null; 

            // Already submitted - idempotency
            if (_submittedIds.Contains(job.Id))
                return null; 

            _submittedIds.Add(job.Id);

            var tcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue((job, tcs), job.Priority);

            Monitor.Pulse(_queueLock); // Wake ONE sleeping worker

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

            lock (_queueLock)
            {
                // Sleep (releases lock!) until Submit() calls Pulse()
                while (_queue.Count == 0)
                    Monitor.Wait(_queueLock);

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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int attempt = 1; attempt <= SystemConfiguration.RetryCount; attempt++)
        {
            try
            {
                var jobTask = Task.Run(() => RunJobLogic(job));
                bool finished = jobTask.Wait(TimeSpan.FromSeconds(SystemConfiguration.JobTimeoutSeconds));

                if (finished)
                {
                    stopwatch.Stop();
                    int result = jobTask.Result;

                    lock (_completedLock)
                        _completedJobs.Add(new CompletedJobRecord(job, result, false, stopwatch.Elapsed.TotalMilliseconds));

                    tcs.TrySetResult(result);
                    JobCompleted?.Invoke(job, result);
                    return;
                }

                Console.WriteLine($"[{Thread.CurrentThread.Name}] Job {job.Id} timed out (attempt {attempt}/{SystemConfiguration.RetryCount})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Thread.CurrentThread.Name}] Job {job.Id} threw (attempt {attempt}/{SystemConfiguration.RetryCount}): {ex.Message}");
            }
        }

        // All retries failed
        stopwatch.Stop();
        LogToFile($"[{DateTime.Now}][ABORT] {job.Id}");

        lock (_completedLock)
            _completedJobs.Add(new CompletedJobRecord(job, 0, failed: true, stopwatch.Elapsed.TotalMilliseconds));

        tcs.TrySetException(new Exception($"Job aborted after {SystemConfiguration.RetryCount} failures"));
        JobFailed?.Invoke(job);
    }

    // --- Job logic (the actual work) ---
    private int RunJobLogic(Job job)
    {
        if (job.Type == JobType.IO)
        {
            int delay = PayloadParser.ParseIO(job.Payload);
            Thread.Sleep(delay);
            return new Random().Next(0, 101);
        }
        else // Prime
        {
            var (limit, threadCount) = PayloadParser.ParsePrime(job.Payload);

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
        {
            Directory.CreateDirectory(SystemConfiguration.LogFolder);
            File.AppendAllText(SystemConfiguration.LogFilePath, message + Environment.NewLine);
        }
    }
    // --- Report generation ---
    private void ReportLoop()
    {
        // Create reports directory if it doesn't exist
        Directory.CreateDirectory(SystemConfiguration.ReportsFolder);

        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(SystemConfiguration.ReportIntervalSeconds));
            GenerateReport();
        }
    }

    private void GenerateReport()
    {
        List<CompletedJobRecord> snapshot;

        // Take a snapshot so we don't hold the lock during LINQ
        lock (_completedLock)
            snapshot = new List<CompletedJobRecord>(_completedJobs);

        // LINQ grouping by JobType
        var reportData = snapshot
            .GroupBy(r => r.Job.Type)
            .OrderBy(g => g.Key.ToString())
            .Select(g => new
            {
                Type         = g.Key.ToString(),
                Completed    = g.Count(r => !r.Failed),
                Failed       = g.Count(r => r.Failed),
                AvgTimeMs    = g.Where(r => !r.Failed)
                                .Select(r => r.ElapsedMs)
                                .DefaultIfEmpty(0)
                                .Average()
            })
            .ToList();

        // Build XML
        XDocument doc = new XDocument(
            new XElement("Report",
                new XAttribute("GeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                new XAttribute("TotalJobs", snapshot.Count),
                new XElement("JobStats",
                    reportData.Select(r =>
                        new XElement("JobType",
                            new XAttribute("Type",          r.Type),
                            new XAttribute("Completed",     r.Completed),
                            new XAttribute("Failed",        r.Failed),
                            new XAttribute("AvgTimeMs",     Math.Round(r.AvgTimeMs, 2))
                        )
                    )
                )
            )
        );

        // Circular file naming: report_0.xml ... report_9.xml
        // _reportIndex % MaxReports means after the last file, we overwrite the oldest one
        string fileName = Path.Combine(SystemConfiguration.ReportsFolder, $"report_{_reportIndex % SystemConfiguration.MaxReports}.xml");
        _reportIndex++;

        doc.Save(fileName);
        Console.WriteLine($"[REPORT] Written to {fileName} ({snapshot.Count} total jobs tracked)");
    }
}