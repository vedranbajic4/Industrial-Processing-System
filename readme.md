# 🏭 Industrial Processing System

A thread-safe, async, event-driven industrial job processing system built in C# (.NET 10), implementing the **Producer-Consumer pattern** with priority queues, parallel job execution, and automated XML reporting.

---

## 📋 Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Class Diagram](#class-diagram)
- [How It Works](#how-it-works)
- [Concurrency & Threading](#concurrency--threading)
- [Events & Pub-Sub](#events--pub-sub)
- [Async & Parallel Execution](#async--parallel-execution)
- [Reports](#reports)
- [Configuration](#configuration)
- [How to Run](#how-to-run)

---

## 🔍 Overview

This system simulates an industrial job dispatcher. Jobs arrive from multiple producer threads, are queued by priority, and consumed by a fixed pool of worker threads. Each job is executed asynchronously with configurable timeout, retry, reporting, and logging settings loaded from `SystemConfig.xml`.

**Key features:**
- Priority-based job queue (lower number = higher priority)
- Configurable worker thread count and max queue size via XML
- Two job types: `Prime` (CPU-bound, parallel) and `IO` (blocking I/O simulation)
- Idempotent job submission — same `Guid` is never processed twice
- Event-driven logging (`JobCompleted`, `JobFailed`)
- Automatic XML report generation with configurable interval and retention
- Thread-safe throughout using `lock` and `Monitor`

---

## 🏗️ Architecture

The system follows the classic **Producer-Consumer** pattern, extended with priorities and async execution:

```
┌─────────────────────────────────────────────────────────────────┐
│                          MAIN PROGRAM                           │
│                                                                 │
│   ┌─────────────┐   ┌─────────────┐   ┌─────────────┐         │
│   │ producer-0  │   │ producer-1  │   │ producer-N  │  ...    │
│   └──────┬──────┘   └──────┬──────┘   └──────┬──────┘         │
│          │                 │                  │                 │
│          └─────────────────┴──────────────────┘                │
│                            │ Submit(job)                        │
│                            ▼                                    │
│          ┌─────────────────────────────────┐                   │
│          │     ProcessingSystem             │                   │
│          │                                  │                   │
│          │  PriorityQueue (lock-protected)  │                   │
│          │  [p=1] job_A                     │                   │
│          │  [p=2] job_C                     │                   │
│          │  [p=3] job_B                     │                   │
│          │                                  │                   │
│          │  Monitor.Pulse() on each Submit  │                   │
│          └──────────────┬───────────────────┘                   │
│                         │ Monitor.Wait() → dequeue              │
│          ┌──────────────┼───────────────────┐                   │
│          ▼              ▼                   ▼                   │
│      worker-0       worker-1           worker-N                 │
│    ExecuteJob()   ExecuteJob()        ExecuteJob()              │
│         │               │                  │                    │
│         └───────────────┴──────────────────┘                   │
│                         │ tcs.SetResult()                       │
│                         ▼                                       │
│               JobCompleted / JobFailed                          │
│               Events fire → async log write                     │
└─────────────────────────────────────────────────────────────────┘
```

---

## 🗂️ Class Diagram

```
┌──────────────────────────────────┐
│             Job                  │
├──────────────────────────────────┤
│ + Id       : Guid                │
│ + Type     : JobType             │
│ + Payload  : string              │
│ + Priority : int                 │
└──────────────────────────────────┘
              │ used by
              ▼
┌──────────────────────────────────┐       ┌──────────────────────┐
│        ProcessingSystem          │       │      JobHandle        │
├──────────────────────────────────┤       ├──────────────────────┤
│ - _queue        : PriorityQueue  │       │ + Id     : Guid       │
│ - _queueLock    : object         │       │ + Result : Task<int>  │
│ - _submittedIds : HashSet<Guid>  │       └──────────────────────┘
│ - _completedJobs: List<Record>   │              ▲
│ - _maxQueueSize : int            │              │ returned by
│ - _reportIndex  : int            │              │
├──────────────────────────────────┤       Submit(Job) : JobHandle?
│ + Submit(job)   : JobHandle?     │
│ + GetTopJobs(n) : IEnumerable    │
│ + GetJob(id)    : Job?           │
│ + GenerateReport()               │
│ - WorkerLoop()                   │
│ - ExecuteJob()                   │
│ - RunJobLogic()                  │
│ - ParsePrime()                   │
│ - ParseIO()                      │
│ - LogToFile()                    │
│ - ReportLoop()                   │
├──────────────────────────────────┤
│ + event JobCompleted             │
│ + event JobFailed                │
└──────────────────────────────────┘

┌──────────────────────────────────┐
│       CompletedJobRecord         │
├──────────────────────────────────┤
│ + Job        : Job               │
│ + Result     : int               │
│ + Failed     : bool              │
│ + ElapsedMs  : double            │
└──────────────────────────────────┘

┌──────────────┐
│   JobType    │
├──────────────┤
│   Prime      │
│   IO         │
└──────────────┘
```

---

## ⚙️ How It Works

### Job Submission Flow

1. A producer thread calls `system.Submit(job)`
2. `Submit` acquires `lock(_queueLock)`
3. If queue is full (`Count >= _maxQueueSize`) → returns `null`, job rejected
4. If `job.Id` was already submitted → returns `null` (idempotency)
5. A `TaskCompletionSource<int>` (TCS) is created — this is the "promise"
6. Job + TCS are enqueued into the `PriorityQueue` keyed by `job.Priority`
7. `Monitor.Pulse()` wakes one sleeping worker
8. `Submit` returns a `JobHandle` containing `tcs.Task` — the caller can `await` it

### Worker Execution Flow

1. Worker calls `Monitor.Wait(_queueLock)` — releases the lock and sleeps
2. When woken by `Pulse()`, worker re-acquires the lock
3. Worker dequeues the **highest-priority** job (lowest Priority number)
4. Lock is released — other threads can now submit
5. Worker calls `ExecuteJob()` — runs job logic with a 2-second timeout
6. On success: `tcs.SetResult(value)` → the caller's `await` resumes
7. On timeout/failure: retries up to 3 times, then logs `ABORT`

### Timeout & Retry Logic

```
Attempt 1 → jobTask.Wait(2s) → timeout → retry
Attempt 2 → jobTask.Wait(2s) → timeout → retry
Attempt 3 → jobTask.Wait(2s) → timeout → ABORT logged, TCS set to exception
```

A job fails if it takes longer than 2 seconds to complete.

---

## 🔒 Concurrency & Threading

All shared state is protected using C#'s `lock` keyword combined with `Monitor` for thread signaling.

### `lock` + `Monitor.Wait` / `Monitor.Pulse`

```csharp
// Worker sleeps here (releases lock while waiting)
lock (_queueLock)
{
    while (_queue.Count == 0)
        Monitor.Wait(_queueLock);   // releases lock, sleeps
    _queue.TryDequeue(out item, out _);
}

// Producer wakes one worker
lock (_queueLock)
{
    _queue.Enqueue((job, tcs), job.Priority);
    Monitor.Pulse(_queueLock);      // wakes one waiting thread
}
```

### Why `while` and Not `if`

The condition check uses `while (_queue.Count == 0)` — not `if`. When `Pulse()` fires, **all waiting workers compete** to re-acquire the lock. Only one wins; the others must re-check and go back to sleep. Using `if` would cause a worker to dequeue from an already-empty queue.

### Thread Roles

| Thread | Count | Role |
|---|---|---|
| `worker-N` | `WorkerThreads` from XML | Consume jobs from the priority queue |
| `producer-N` | `ProducerThreads` from XML | Generate and submit random jobs |
| `report-thread` | 1 | Generate XML reports using `ReportIntervalSeconds` |
| `Main thread` | 1 | Reads config, submits initial jobs, keeps app alive |

---

## 📡 Events & Pub-Sub

The system uses C# `event` with `Action<T>` delegates — a built-in publish-subscribe mechanism.

### Event Declarations

```csharp
public event Action<Job, int>? JobCompleted;
public event Action<Job>?      JobFailed;
```

### Subscriptions (in constructor)

```csharp
JobCompleted += (job, result) =>
    Task.Run(() => LogToFile($"[{DateTime.Now}][COMPLETED] {job.Id}, Result={result}"));

JobFailed += (job) =>
    Task.Run(() => LogToFile($"[{DateTime.Now}][FAILED] {job.Id}"));
```

The `+=` operator subscribes a **lambda expression** to the event. When the worker fires `JobCompleted?.Invoke(job, result)`, every subscribed handler runs. `Task.Run()` ensures the log write happens **asynchronously** — the worker thread is never blocked waiting for file I/O.

Multiple subscribers can be added (`+=`) and removed (`-=`) at any time, making the system extensible without modifying `ProcessingSystem`.

---

## ⚡ Async & Parallel Execution

### `Task` and `TaskCompletionSource`

`Task<int>` represents a future value — the result of a job that hasn't finished yet. `TaskCompletionSource<int>` (TCS) is the manual controller of that Task:

```
Submit() creates TCS           Worker calls tcs.SetResult(42)
    │                                      │
    ├── returns tcs.Task ◄── caller awaits │
    │   (not completed yet)                │
    │                                      │
    └──────────── Task completes here ─────┘
                  caller's await resumes with 42
```

This decouples **when a job is submitted** from **when it completes**, which is exactly what a queued system requires.

### Parallel Prime Calculation

`Prime` jobs use `Parallel.For` to distribute prime-checking across multiple threads:

```csharp
Parallel.For(2, limit + 1,
    new ParallelOptions { MaxDegreeOfParallelism = threadCount },
    i => { if (IsPrime(i)) Interlocked.Increment(ref count); });
```

`Interlocked.Increment` performs an **atomic** increment — no `lock` needed because it's a single CPU instruction that cannot be interrupted mid-execution. Thread count is clamped to `[1, 8]` as specified.

### IO Simulation

`IO` jobs use `Thread.Sleep` to simulate blocking I/O (e.g., reading from a sensor at an address). This is intentional — it simulates real hardware latency.

---

## 📊 Reports

Every 60 seconds, a LINQ report is generated and saved as XML.

### LINQ Aggregation

```csharp
var reportData = snapshot
    .GroupBy(r => r.Job.Type)
    .OrderBy(g => g.Key.ToString())
    .Select(g => new {
        Type      = g.Key.ToString(),
        Completed = g.Count(r => !r.Failed),
        Failed    = g.Count(r => r.Failed),
        AvgTimeMs = g.Where(r => !r.Failed).Select(r => r.ElapsedMs).Average()
    });
```

### Report Output Format

```xml
<Report GeneratedAt="2026-04-16 19:30:00" TotalJobs="47">
  <JobStats>
    <JobType Type="IO"    Completed="18" Failed="3" AvgTimeMs="412.50" />
    <JobType Type="Prime" Completed="23" Failed="3" AvgTimeMs="876.20" />
  </JobStats>
</Report>
```

### Circular Buffer (Last 10 Reports)

Reports are saved as `report_0.xml` through `report_9.xml` inside a `reports/` directory. A simple modulo counter handles the rotation:

```
Minute  1 → reports/report_0.xml
Minute  2 → reports/report_1.xml
...
Minute 10 → reports/report_9.xml
Minute 11 → reports/report_0.xml  ← overwrites oldest
Minute 12 → reports/report_1.xml  ← overwrites second oldest
```

---

## 🗃️ Configuration

The system is fully configured via `SystemConfig.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<SystemConfig>
    <WorkerThreads>5</WorkerThreads>
    <ProducerThreads>5</ProducerThreads>
    <MaxQueueSize>100</MaxQueueSize>
    <RetryCount>3</RetryCount>
    <JobTimeoutSeconds>2</JobTimeoutSeconds>
    <ReportIntervalSeconds>60</ReportIntervalSeconds>
    <MaxReports>10</MaxReports>
    <LogFolder>..</LogFolder>
    <LogFileName>log.txt</LogFileName>
    <ReportsFolder>../reports</ReportsFolder>
    <Jobs>
        <Job Type="Prime" Payload="numbers:10_000,threads:3" Priority="1"/>
        <Job Type="Prime" Payload="numbers:20_000,threads:2" Priority="2"/>
        <Job Type="IO"    Payload="delay:1_000"              Priority="3"/>
        <Job Type="IO"    Payload="delay:3_000"              Priority="3"/>
        <Job Type="IO"    Payload="delay:15_000"             Priority="3"/>
    </Jobs>
</SystemConfig>
```

| Field | Description |
|---|---|
| `WorkerThreads` | Number of worker threads to spawn |
| `ProducerThreads` | Number of producer threads to spawn |
| `MaxQueueSize` | Maximum jobs allowed in the queue at once — new jobs are rejected beyond this |
| `RetryCount` | Number of times a worker retries a failed or timed-out job |
| `JobTimeoutSeconds` | Per-attempt timeout before a job is retried |
| `ReportIntervalSeconds` | Delay between automatic report generations |
| `MaxReports` | Number of rotating report files to keep |
| `LogFolder` / `LogFileName` | Output location for runtime log writes |
| `ReportsFolder` | Output location for generated XML reports |
| `Job.Type` | `Prime` or `IO` |
| `Job.Priority` | Integer — lower = higher priority (1 is processed before 5) |
| `Job.Payload` | For Prime: `numbers:N,threads:T` — For IO: `delay:Ms` |

Underscore separators in numbers (`10_000`) are automatically stripped during parsing.

---

## 🚀 How to Run

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Git

### Clone & Run

```bash
git clone https://github.com/YOUR_USERNAME/Industrial-Processing-System.git
cd Industrial-Processing-System/ConsoleApp1
dotnet run
```

### Project Structure

```
Industrial-Processing-System/
├── ConsoleApp1/
│   ├── Program.cs              ← Entry point, XML parsing, producer threads
│   ├── ProcessingSystem.cs     ← Core system: queue, workers, events, reports
│   ├── Job.cs                  ← Job data model (Id, Type, Payload, Priority)
│   ├── JobHandle.cs            ← Async result handle (Id, Task<int>)
│   ├── JobType.cs              ← Enum: Prime, IO
│   ├── CompletedJobRecord.cs   ← Record of finished jobs for reporting
│   ├── PayloadParser.cs        ← Static payload parsing for Prime and IO jobs
│   └── Industrial Processing System.csproj
├── SystemConfig.xml            ← Configuration file
├── log.txt                     ← Generated at runtime
└── reports/
    ├── report_0.xml            ← Generated every 60 seconds (circular, max 10)
    ├── report_1.xml
    └── ...
```

### Expected Output

```
[CONFIG] Workers: 5, MaxQueue: 100, Initial jobs: 5
[SYSTEM] Started 5 workers.
[MAIN] Submitted initial job ... (Type=Prime, Priority=1)
[worker-0] Processing Prime job ...
[producer-1] Submitted IO job, priority=3
[REPORT] Written to reports/report_0.xml (12 total jobs tracked)
```

---

## 🧪 Testing Notes

For time-independent testing (as required), `TaskCompletionSource` is used instead of `Thread.Sleep` for waiting on results. Tests can call `tcs.SetResult()` manually without waiting for real time to pass.

To trigger a report immediately without waiting 60 seconds, call:

```csharp
system.GenerateReport(); // make public temporarily during testing
```

---

## 👨‍💻 Author

Course: SNUS — Kolokvijum 1, April 2026
