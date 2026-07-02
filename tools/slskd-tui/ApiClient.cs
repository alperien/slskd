using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SlskdTui;

public class ApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ApiClient(string baseUrl, string username, string password)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var auth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{username}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
    }

    public async Task<(bool Connected, string Username)> GetApplicationStateAsync()
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonElement>("api/v0/application", JsonOpts);
            var server = json.GetProperty("server");
            var state = server.GetProperty("state").GetString() ?? "";
            var user = json.GetProperty("user");
            var username = user.GetProperty("username").GetString() ?? "?";
            return (state == "Connected", username);
        }
        catch
        {
            return (false, "?");
        }
    }

    public async Task<List<Transfer>> GetTransfersAsync()
    {
        try
        {
            var users = await _http.GetFromJsonAsync<List<JsonElement>>("api/v0/transfers/downloads", JsonOpts);
            if (users == null) return [];

            var transfers = new List<Transfer>();

            foreach (var user in users)
            {
                var username = user.GetProperty("username").GetString() ?? "?";
                var dirs = user.GetProperty("directories").EnumerateArray();

                foreach (var dir in dirs)
                {
                    var files = dir.GetProperty("files").EnumerateArray();
                    foreach (var file in files)
                    {
                        var t = JsonSerializer.Deserialize<Transfer>(file.GetRawText(), JsonOpts);
                        if (t != null)
                        {
                            if (t.Username == null) t.Username = username;
                            transfers.Add(t);
                        }
                    }
                }
            }

            return transfers;
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<LogEntry>> GetRecentLogsAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<LogEntry>>("api/v0/logs", JsonOpts);
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }
}
