namespace SlskdTui;

public class Transfer
{
    public Guid Id { get; set; }
    public string? Username { get; set; }
    public string? Filename { get; set; }
    public long Size { get; set; }
    public long BytesTransferred { get; set; }
    public double AverageSpeed { get; set; }
    public string? State { get; set; }
    public int? PlaceInQueue { get; set; }
    public string? Direction { get; set; }
    public int ReplacementAttempts { get; set; }
    public string? Exception { get; set; }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string? Level { get; set; }
    public string? Message { get; set; }
}
