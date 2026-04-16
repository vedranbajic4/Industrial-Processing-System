namespace ConsoleApp1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

public class MainClass
{
    public static int maxQueueSize = 100;
    public static int workerCount = 5;

    // returns the job list
    public static List<Job> ParseXML(string path)
    {
        XElement xmlData = XElement.Load(path);

        maxQueueSize = int.Parse(xmlData.Element("MaxQueueSize").Value);
        workerCount  = int.Parse(xmlData.Element("WorkerCount").Value);

        List<Job> jobs = (from job in xmlData.Descendants("Job")
                          select new Job
                          {
                              Id       = Guid.NewGuid(),  // generate unique Id
                              Type     = (JobType)Enum.Parse(typeof(JobType), job.Attribute("Type").Value),
                              Payload  = job.Attribute("Payload").Value,
                              Priority = int.Parse(job.Attribute("Priority")?.Value ?? "3") // default 3 if missing
                          }).ToList();

        Console.WriteLine($"[CONFIG] Workers: {workerCount}, MaxQueue: {maxQueueSize}, Initial jobs: {jobs.Count}");
        return jobs;
    }

    public static void Main(string[] args)
    {
        // 1. Parse config + initial job list
        List<Job> initialJobs = ParseXML("../SystemConfig.xml");

        // 2. Create ProcessingSystem - workers start sleeping inside
        var system = new ProcessingSystem(workerCount, maxQueueSize);

        // 3. Submit initial jobs from XML
        foreach (Job job in initialJobs)
        {
            try
            {
                JobHandle? handle = system.Submit(job);
                if (handle == null)
                    Console.WriteLine($"[MAIN] Queue full, skipped initial job {job.Id}");
                else
                    Console.WriteLine($"[MAIN] Submitted initial job {job.Id} (Type={job.Type}, Priority={job.Priority})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MAIN] Failed to submit job: {ex.Message}");
            }
        }

        // 4. Start producer threads - generate random jobs forever
        for (int i = 0; i < workerCount; i++)
        {
            int threadIndex = i; // capture for lambda
            Thread producer = new Thread(() => ProducerThread(system, threadIndex));
            producer.Name = $"producer-{threadIndex}";
            producer.IsBackground = true;
            producer.Start();
        }

        Console.WriteLine("[MAIN] System running. Press ENTER to stop.");
        Console.ReadLine();
    }

    // Each producer thread runs this - generates random jobs indefinitely
    private static void ProducerThread(ProcessingSystem system, int index)
    {
        Random rng = new Random(index * 137); // different seed per thread

        while (true)
        {
            try
            {
                JobType type = rng.Next(2) == 0 ? JobType.Prime : JobType.IO;

                string payload = type == JobType.Prime
                    ? $"numbers:{rng.Next(100, 20000)},threads:{rng.Next(1, 9)}"
                    : $"delay:{rng.Next(100, 2000)}";

                Job job = new Job
                {
                    Id       = Guid.NewGuid(),
                    Type     = type,
                    Payload  = payload,
                    Priority = rng.Next(1, 6)  // 1=highest, 5=lowest
                };

                JobHandle? handle = system.Submit(job);

                if (handle == null)
                    Console.WriteLine($"[{Thread.CurrentThread.Name}] Queue full, job skipped.");
                else
                    Console.WriteLine($"[{Thread.CurrentThread.Name}] Submitted {job.Type} job, priority={job.Priority}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Thread.CurrentThread.Name}] Error: {ex.Message}");
            }

            Thread.Sleep(rng.Next(200, 1500)); // wait random time before next job
        }
    }
}