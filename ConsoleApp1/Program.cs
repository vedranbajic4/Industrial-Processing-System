namespace ConsoleApp1;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Xml.Linq;

public class MainClass
{
    public static int maxQueueSize = 100; // default value, can be overridden by XML config
    public static int workerCount = 5; // default value, can be overridden by XML config

    // Method to parse XML configuration and populate job list
    public static void parseXML(string path)
    {
        //Console.WriteLine("[DEBUG] Parsing XML.");
        var jobs = new List<Job>();
        XElement xmlData = XElement.Load(path);
        
        maxQueueSize = int.Parse(xmlData.Element("MaxQueueSize").Value);
        workerCount = int.Parse(xmlData.Element("WorkerCount").Value);

        jobs = (from job in xmlData.Descendants("Job")
                select new Job
                {
                    Type = (JobType)Enum.Parse(typeof(JobType), job.Attribute("Type").Value),
                    Payload = job.Attribute("Payload").Value
                }).ToList();
        /*
        Console.WriteLine($"[DEBUG] Parsed {jobs.Count} jobs from XML. MaxQueueSize: {maxQueueSize}, WorkerCount: {workerCount}");
        foreach (var job in jobs)
        {
            Console.WriteLine($"Job Type: {job.Type}, Payload: {job.Payload}");
        }
        Console.WriteLine("~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~");*/
    }

    public static void Main(string[] args)
    {
        parseXML("../SystemConfig.xml");

    }
}

