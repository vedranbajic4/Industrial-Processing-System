public class CompletedJobRecord
{
    public Job Job           { get; set; }
    public int Result        { get; set; }
    public bool Failed       { get; set; }
    public double ElapsedMs  { get; set; } // needed for the LINQ report later

    public CompletedJobRecord(Job job, int result, bool failed, double elapsedMs = 0)
    {
        Job       = job;
        Result    = result;
        Failed    = failed;
        ElapsedMs = elapsedMs;
    }
}