namespace ConsoleApp1;
public class Job
{
    public Guid Id { get; set; }
    public JobType Type { get; set; }
    public required string Payload { get; set; }    // will be parsed
    public int Priority { get; set; } // lower number = higher priority
}