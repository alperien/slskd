using Spectre.Console;

namespace SlskdTui;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5030";
        var username = args.Length > 1 ? args[1] : "slskd";
        var password = args.Length > 2 ? args[2] : "slskd";

        var api = new ApiClient(baseUrl, username, password);
        var autoReplaceLog = new List<(string Message, string? Level)>();
        var downloadStates = new Dictionary<Guid, string?>();


        await AnsiConsole.Live(new Panel("[[connecting...]]").Header("slskd-tui"))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                while (true)
                {
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                        break;

                    var app = await api.GetApplicationStateAsync();
                    var transfers = await api.GetTransfersAsync();
                    var logs = await api.GetRecentLogsAsync();

                    foreach (var log in logs)
                    {
                        if (log.Message is { } m && m.Contains("Auto-replace", StringComparison.OrdinalIgnoreCase))
                            autoReplaceLog.Add((m, log.Level));
                    }

                    if (autoReplaceLog.Count > 100)
                        autoReplaceLog.RemoveRange(0, autoReplaceLog.Count - 100);

                    foreach (var t in transfers)
                    {
                        if (downloadStates.TryGetValue(t.Id, out var prev) && prev != t.State)
                            autoReplaceLog.Add(($"[DOWNLOAD] [{t.Username ?? "?"}/{ShortName(t.Filename)}] {prev} => {t.State}", "INF"));
                        downloadStates[t.Id] = t.State;
                    }

                    var downloads = transfers
                        .Where(t => t.Direction == "Download")
                        .OrderByDescending(t => t.State == "InProgress" ? 0 : t.State == "Queued,Remotely" ? 1 : 2)
                        .ThenBy(t => t.Username)
                        .ToList();

                    var active = downloads.Count(t => t.State is "InProgress" or "Initializing" or "Queued,Remotely" or "Requested" or "Queued,Locally");
                    var succeeded = downloads.Count(t => t.State == "Completed,Succeeded");
                    var errored = downloads.Count(t => t.State == "Completed,Errored");

                    var transferTable = new Table().Border(TableBorder.Simple).HideHeaders();
                    transferTable.AddColumns("File", "State", "Progress", "Speed", "User");

                    foreach (var t in downloads.Take(15))
                    {
                        var state = t.State switch
                        {
                            "InProgress" => $"[yellow]{t.State}[/]",
                            "Queued,Remotely" => $"[blue]{t.State}[/]",
                            "Completed,Errored" => $"[red]{t.State}[/]",
                            "Completed,Succeeded" => $"[green]{t.State}[/]",
                            _ => (t.State ?? "?").EscapeMarkup()
                        };
                        var progress = t.State switch
                        {
                            "InProgress" when t.Size > 0 => $"{t.BytesTransferred * 100 / t.Size}%",
                            "Queued,Remotely" when t.PlaceInQueue.HasValue => $"#{t.PlaceInQueue}",
                            _ => "-"
                        };
                        var speed = t.AverageSpeed > 0 ? $"{t.AverageSpeed / 1024:F0} KB/s" : "";
                        var c = t.State == "Completed,Errored" ? "red" : t.State == "InProgress" ? "yellow" : "default";
                        transferTable.AddRow(
                            $"[{c}]{ShortName(t.Filename).EscapeMarkup()}[/]",
                            $"[{c}]{state}[/]",
                            $"[{c}]{progress}[/]",
                            $"[{c}]{speed}[/]",
                            $"[{c}]{(t.Username ?? "?").EscapeMarkup()}[/]");
                    }

                    var replaceLines = autoReplaceLog
                        .TakeLast(12)
                        .Select(l =>
                        {
                            var msg = Truncate(l.Message, 90).EscapeMarkup();
                            return
                                l.Message.Contains("found") || l.Message.Contains("enqueued") ? $"[green]✓[/] {msg}" :
                                l.Message.Contains("failed") || l.Message.Contains("no suitable") ? $"[red]✗[/] {msg}" :
                                l.Message.Contains("searching") ? $"[blue]~[/] {msg}" :
                                $"  {msg}";
                        });

                    var replacePanel = new Panel(
                        new Markup(string.Join("\n", replaceLines.DefaultIfEmpty("(no auto-replace activity)"))))
                        .Header("Auto-Replace")
                        .Border(BoxBorder.Rounded);

                    var transferPanel = new Panel(transferTable)
                        .Header($"Downloads: {active} active, {succeeded} succeeded, {errored} errored")
                        .Border(BoxBorder.Rounded);

                    var statusLine = app.Connected
                        ? $"[green]● Connected[/]  user: {app.Username}"
                        : $"[red]● Disconnected[/]";

                    var now = DateTime.Now;
                    var updateInfo = $"[dim]last update: {now:HH:mm:ss}  press ESC to exit[/]";

                    var layout = new Rows(
                        new Markup($"{statusLine}  {updateInfo}"),
                        transferPanel,
                        replacePanel
                    );

                    ctx.UpdateTarget(new Panel(layout).Border(BoxBorder.None));
                    ctx.Refresh();
                    await Task.Delay(2000);
                }
            });

        return 0;
    }

    private static string ShortName(string? path, int maxLen = 30)
    {
        if (path == null) return "?";
        var name = Path.GetFileName(path);
        return name.Length <= maxLen ? name : name[..(maxLen - 3)] + "...";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
