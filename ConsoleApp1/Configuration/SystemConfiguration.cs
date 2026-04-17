namespace ConsoleApp1;

using System.Xml.Linq;

public static class SystemConfiguration
{
    
    public static string SystemConfigPath { get; } = "../SystemConfig.xml"; // path to the initial XML config file
    public static string LogFolder { get; private set; } = "../logs"; // folder for logs
    public static string LogFileName { get; private set; } = "log.txt"; // file name for logs
    
    public static string ReportsFolder { get; private set; } = "../reports"; // folder for reports

    public static int RetryCount { get; private set; } = 3;     // how many times a failed job should be retried before giving up
    public static int WorkerThreads { get; private set; } = 5;  // how many worker threads to start
    public static int MaxQueueSize { get; private set; } = 100; // maximum number of jobs that can be waiting in the queue
    public static int JobTimeoutSeconds { get; private set; } = 2; // how many seconds before a job times out
    public static int ReportIntervalSeconds { get; private set; } = 10; // interval in seconds for generating reports (one minute)
    public static int MaxReports { get; private set; } = 10; // maximum number of reports to keep
    public static int PrimePayloadMinLimit { get; private set; } = 100; // minimum limit for prime number calculation
    public static int PrimePayloadMaxLimit { get; private set; } = 20000; // maximum limit for prime number calculation
    public static int PrimePayloadMinThreads { get; private set; } = 1; // minimum number of threads for prime calculation
    public static int PrimePayloadMaxThreads { get; private set; } = 8; // maximum number of threads for prime calculation
    public static int IoDelayMinMs { get; private set; } = 100; // minimum delay for IO jobs in milliseconds
    public static int IoDelayMaxMs { get; private set; } = 2000; // maximum delay for IO jobs in milliseconds

    public static string LogFilePath => Path.Combine(LogFolder, LogFileName);

    public static XElement LoadFromXml(string path)
    {
        XElement xmlData = XElement.Load(path);

        WorkerThreads = ReadPositiveInt(
            xmlData,
            "WorkerThreads",
            ReadPositiveInt(xmlData, "WorkerCount", WorkerThreads));

        MaxQueueSize = ReadPositiveInt(xmlData, "MaxQueueSize", MaxQueueSize);
        RetryCount = ReadPositiveInt(xmlData, "RetryCount", RetryCount);
        JobTimeoutSeconds = ReadPositiveInt(xmlData, "JobTimeoutSeconds", JobTimeoutSeconds);
        ReportIntervalSeconds = ReadPositiveInt(xmlData, "ReportIntervalSeconds", ReportIntervalSeconds);
        MaxReports = ReadPositiveInt(xmlData, "MaxReports", MaxReports);

        LogFolder = ReadString(xmlData, "LogFolder", LogFolder);
        LogFileName = ReadString(xmlData, "LogFileName", LogFileName);
        ReportsFolder = ReadString(xmlData, "ReportsFolder", ReportsFolder);

        PrimePayloadMinLimit = ReadPositiveInt(xmlData, "PrimePayloadMinLimit", PrimePayloadMinLimit);
        PrimePayloadMaxLimit = Math.Max(
            PrimePayloadMinLimit,
            ReadPositiveInt(xmlData, "PrimePayloadMaxLimit", PrimePayloadMaxLimit));

        PrimePayloadMinThreads = ReadPositiveInt(xmlData, "PrimePayloadMinThreads", PrimePayloadMinThreads);
        PrimePayloadMaxThreads = Math.Max(
            PrimePayloadMinThreads,
            ReadPositiveInt(xmlData, "PrimePayloadMaxThreads", PrimePayloadMaxThreads));

        IoDelayMinMs = ReadNonNegativeInt(xmlData, "IoDelayMinMs", IoDelayMinMs);
        IoDelayMaxMs = Math.Max(IoDelayMinMs, ReadNonNegativeInt(xmlData, "IoDelayMaxMs", IoDelayMaxMs));

        return xmlData;
    }

    private static int ReadPositiveInt(XElement root, string elementName, int fallback)
    {
        string? value = root.Element(elementName)?.Value;
        return int.TryParse(value, out int parsedValue) && parsedValue > 0
            ? parsedValue
            : fallback;
    }

    private static int ReadNonNegativeInt(XElement root, string elementName, int fallback)
    {
        string? value = root.Element(elementName)?.Value;
        return int.TryParse(value, out int parsedValue) && parsedValue >= 0
            ? parsedValue
            : fallback;
    }

    private static string ReadString(XElement root, string elementName, string fallback)
    {
        string? value = root.Element(elementName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}