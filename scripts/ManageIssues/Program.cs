using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
var repo = Environment.GetEnvironmentVariable("REPO") ?? "ExarKun1983/IntelligentTradingSimulator";
var dryRun = string.Equals(Environment.GetEnvironmentVariable("DRY_RUN"), "1", StringComparison.OrdinalIgnoreCase);

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("ERROR: GITHUB_TOKEN environment variable is required.");
    return 1;
}

var repoParts = repo.Split('/');
if (repoParts.Length != 2)
{
    Console.Error.WriteLine("ERROR: REPO must be in 'owner/repo' format.");
    return 1;
}

var owner = repoParts[0];
var repoName = repoParts[1];

var labelColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["backtesting"] = "1D76DB",
    ["benchmark"] = "5319E7",
    ["risk-management"] = "D93F0B",
    ["strategy"] = "0E8A16",
    ["advanced-factors"] = "FBCA04",
    ["ai"] = "C5DEF5",
    ["reporting"] = "006B75",
    ["documentation"] = "0075CA",
    ["persistence"] = "BFDADC",
    ["infrastructure"] = "7057FF",
    ["market-data"] = "A2EEEF",
    ["core-logic"] = "E99695",
    ["application"] = "F9D0C4",
    ["analytics"] = "D4C5F9",
    ["configuration"] = "B60205",
    ["allocation"] = "FEF2C0",
    ["ranking"] = "C2E0C6",
    ["execution"] = "E4E669",
    ["fundamentals"] = "5319E7",
    ["export"] = "0052CC",
    ["developer-experience"] = "CFD3D7",
    ["architecture"] = "8B5CF6",
    ["data-model"] = "BFD4F2",
    ["training-data"] = "FCE94F",
    ["explainability"] = "FF9F1C",
    ["evaluation"] = "2ECC71",
    ["scoring"] = "FFB347",
    ["milestone-1"] = "000000",
    ["milestone-2"] = "111111",
    ["milestone-3"] = "222222",
    ["milestone-4"] = "333333",
    ["milestone-5"] = "444444",
    ["milestone-6"] = "555555"
};

var issueConfig = new Dictionary<int, IssueConfig>
{
    [2] = new(new[] { "backtesting", "persistence", "milestone-1" }),
    [3] = new(new[] { "backtesting", "persistence", "milestone-1" }, "Depends on #2"),
    [4] = new(new[] { "backtesting", "core-logic", "milestone-1" }, "Blocked by #2, #3, #5, #6"),
    [5] = new(new[] { "backtesting", "market-data", "milestone-1" }),
    [6] = new(new[] { "backtesting", "application", "milestone-1" }, "Depends on #5"),
    [7] = new(new[] { "backtesting", "reporting", "milestone-1" }, "Blocked by #4 and uses data introduced in #3"),
    [8] = new(new[] { "benchmark", "market-data", "milestone-2" }),
    [9] = new(new[] { "benchmark", "reporting", "milestone-2" }, "Depends on #8"),
    [10] = new(new[] { "benchmark", "analytics", "milestone-2" }, "Depends on #8 and #9"),
    [11] = new(new[] { "risk-management", "strategy", "milestone-2" }),
    [12] = new(new[] { "risk-management", "allocation", "milestone-2" }),
    [13] = new(new[] { "risk-management", "analytics", "milestone-2" }, "Depends on #3"),
    [14] = new(new[] { "risk-management", "analytics", "milestone-2" }, "Depends on #13"),
    [15] = new(new[] { "reporting", "benchmark", "risk-management", "milestone-2" }, "Blocked by #9, #10, #13, and #14"),
    [16] = new(new[] { "strategy", "configuration", "milestone-3" }),
    [17] = new(new[] { "strategy", "persistence", "milestone-3" }, "Depends on #16"),
    [18] = new(new[] { "strategy", "configuration", "milestone-3" }, "Depends on #16"),
    [19] = new(new[] { "strategy", "configuration", "milestone-3" }, "Depends on #16 and #18"),
    [20] = new(new[] { "strategy", "backtesting", "milestone-3" }, "Blocked by #19 and requires backtest execution from #4"),
    [21] = new(new[] { "reporting", "strategy", "milestone-3" }, "Depends on #20"),
    [22] = new(new[] { "reporting", "analytics", "milestone-3" }, "Depends on #21"),
    [23] = new(new[] { "benchmark", "strategy", "reporting", "milestone-3" }, "Depends on #21 and benchmark issues #8, #9, #10"),
    [24] = new(new[] { "documentation", "strategy", "milestone-3" }),
    [25] = new(new[] { "advanced-factors", "analytics", "milestone-4" }),
    [26] = new(new[] { "advanced-factors", "scoring", "milestone-4" }, "Depends on #25"),
    [27] = new(new[] { "advanced-factors", "analytics", "milestone-4" }),
    [28] = new(new[] { "advanced-factors", "scoring", "milestone-4" }, "Depends on #27"),
    [29] = new(new[] { "advanced-factors", "benchmark", "milestone-4" }),
    [30] = new(new[] { "advanced-factors", "scoring", "milestone-4" }, "Depends on #29"),
    [31] = new(new[] { "advanced-factors", "analytics", "milestone-4" }),
    [32] = new(new[] { "advanced-factors", "scoring", "milestone-4" }, "Blocked by #25, #27, #29, #31 and should integrate scoring behavior from #26, #28, #30"),
    [33] = new(new[] { "advanced-factors", "reporting", "milestone-4" }, "Depends on #32"),
    [34] = new(new[] { "advanced-factors", "ranking", "milestone-4" }, "Depends on #32"),
    [35] = new(new[] { "configuration", "advanced-factors", "milestone-4" }, "Depends on #16 and should align with #32"),
    [36] = new(new[] { "documentation", "advanced-factors", "milestone-4" }),
    [37] = new(new[] { "risk-management", "execution", "milestone-5" }, "Depends on #39"),
    [38] = new(new[] { "risk-management", "execution", "milestone-5" }, "Depends on #39"),
    [39] = new(new[] { "execution", "persistence", "milestone-5" }),
    [40] = new(new[] { "advanced-factors", "fundamentals", "milestone-5" }),
    [41] = new(new[] { "fundamentals", "infrastructure", "milestone-5" }, "Depends on #40"),
    [42] = new(new[] { "advanced-factors", "scoring", "milestone-5" }, "Blocked by #40 and #41"),
    [43] = new(new[] { "reporting", "export", "milestone-5" }),
    [44] = new(new[] { "reporting", "export", "milestone-5" }),
    [45] = new(new[] { "reporting", "export", "milestone-5" }, "Depends on strategy comparison reporting from #21"),
    [46] = new(new[] { "documentation", "developer-experience", "milestone-5" }),
    [47] = new(new[] { "documentation", "architecture", "milestone-5" }),
    [48] = new(new[] { "ai", "architecture", "milestone-6" }),
    [49] = new(new[] { "ai", "data-model", "milestone-6" }, "Follows architecture abstraction introduced in #48"),
    [50] = new(new[] { "ai", "persistence", "milestone-6" }, "Depends on #49"),
    [51] = new(new[] { "ai", "training-data", "milestone-6" }, "Depends on #50"),
    [52] = new(new[] { "ai", "training-data", "export", "milestone-6" }, "Blocked by #50 and #51"),
    [53] = new(new[] { "ai", "explainability", "milestone-6" }, "Depends on factor/feature transparency from #33 and feature modeling from #49"),
    [54] = new(new[] { "ai", "explainability", "reporting", "milestone-6" }, "Depends on reporting from #15 and strategy comparison from #21"),
    [55] = new(new[] { "ai", "configuration", "milestone-6" }, "Depends on #48"),
    [56] = new(new[] { "ai", "architecture", "milestone-6" }, "Blocked by #48, #49, and #55"),
    [57] = new(new[] { "ai", "evaluation", "milestone-6" }, "Depends on #52 and #56"),
    [58] = new(new[] { "ai", "strategy", "milestone-6" }, "Depends on strategy comparison outputs and evaluation groundwork from #57"),
    [59] = new(new[] { "documentation", "ai", "architecture", "milestone-6" })
};

using var httpClient = new HttpClient
{
    BaseAddress = new Uri("https://api.github.com/")
};
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IssueManager", "1.0"));

Console.WriteLine($"Repository: {repo}");
Console.WriteLine($"Dry run: {dryRun}");

var allLabels = issueConfig.Values
    .SelectMany(x => x.Labels)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(x => x)
    .ToList();

var existingLabels = await GetExistingLabelsAsync();

foreach (var label in allLabels)
{
    await EnsureLabelAsync(label, existingLabels);
    await Task.Delay(100);
}

foreach (var entry in issueConfig.OrderBy(x => x.Key))
{
    var issueNumber = entry.Key;
    var config = entry.Value;

    await VerifyIssueExistsAsync(issueNumber);
    await SetIssueLabelsAsync(issueNumber, config.Labels);
    await Task.Delay(100);

    if (!string.IsNullOrWhiteSpace(config.Comment))
    {
        await EnsureCommentAsync(issueNumber, config.Comment!);
        await Task.Delay(100);
    }
}

Console.WriteLine("Done.");
return 0;

async Task<HashSet<string>> GetExistingLabelsAsync()
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var page = 1;

    while (true)
    {
        var response = await SendAsync(HttpMethod.Get, $"repos/{owner}/{repoName}/labels?per_page=100&page={page}");
        var body = await response.Content.ReadAsStringAsync();
        var labels = JsonSerializer.Deserialize<List<LabelDto>>(body, JsonOptions) ?? new();

        if (labels.Count == 0)
        {
            break;
        }

        foreach (var label in labels)
        {
            if (!string.IsNullOrWhiteSpace(label.Name))
            {
                result.Add(label.Name);
            }
        }

        if (labels.Count < 100)
        {
            break;
        }

        page++;
    }

    return result;
}

async Task EnsureLabelAsync(string name, HashSet<string> existing)
{
    if (existing.Contains(name))
    {
        return;
    }

    var payload = new
    {
        name,
        color = labelColors.TryGetValue(name, out var color) ? color : "CCCCCC",
        description = $"Managed by script: {name}"
    };

    if (dryRun)
    {
        Console.WriteLine($"[DRY_RUN] Create label: {name}");
        existing.Add(name);
        return;
    }

    var response = await SendAsync(HttpMethod.Post, $"repos/{owner}/{repoName}/labels", payload, allowConflict: true);
    if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.UnprocessableEntity)
    {
        existing.Add(name);
        Console.WriteLine($"Ensured label: {name}");
        return;
    }

    await EnsureSuccessAsync(response);
}

async Task VerifyIssueExistsAsync(int issueNumber)
{
    var response = await SendAsync(HttpMethod.Get, $"repos/{owner}/{repoName}/issues/{issueNumber}");
    await EnsureSuccessAsync(response);
}

async Task SetIssueLabelsAsync(int issueNumber, IReadOnlyCollection<string> labels)
{
    if (dryRun)
    {
        Console.WriteLine($"[DRY_RUN] Set labels on #{issueNumber}: {string.Join(", ", labels)}");
        return;
    }

    var payload = new { labels };
    var response = await SendAsync(HttpMethod.Put, $"repos/{owner}/{repoName}/issues/{issueNumber}/labels", payload);
    await EnsureSuccessAsync(response);
    Console.WriteLine($"Set labels on #{issueNumber}: {string.Join(", ", labels)}");
}

async Task EnsureCommentAsync(int issueNumber, string body)
{
    var comments = await GetCommentsAsync(issueNumber);
    if (comments.Any(x => string.Equals(x.Body?.Trim(), body.Trim(), StringComparison.Ordinal)))
    {
        Console.WriteLine($"Comment already exists on #{issueNumber}: {body}");
        return;
    }

    if (dryRun)
    {
        Console.WriteLine($"[DRY_RUN] Add comment on #{issueNumber}: {body}");
        return;
    }

    var payload = new { body };
    var response = await SendAsync(HttpMethod.Post, $"repos/{owner}/{repoName}/issues/{issueNumber}/comments", payload);
    await EnsureSuccessAsync(response);
    Console.WriteLine($"Added comment on #{issueNumber}: {body}");
}

async Task<List<CommentDto>> GetCommentsAsync(int issueNumber)
{
    var result = new List<CommentDto>();
    var page = 1;

    while (true)
    {
        var response = await SendAsync(HttpMethod.Get, $"repos/{owner}/{repoName}/issues/{issueNumber}/comments?per_page=100&page={page}");
        var body = await response.Content.ReadAsStringAsync();
        var comments = JsonSerializer.Deserialize<List<CommentDto>>(body, JsonOptions) ?? new();

        if (comments.Count == 0)
        {
            break;
        }

        result.AddRange(comments);

        if (comments.Count < 100)
        {
            break;
        }

        page++;
    }

    return result;
}

async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? payload = null, bool allowConflict = false)
{
    using var request = new HttpRequestMessage(method, url);

    if (payload is not null)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
    }

    var response = await httpClient.SendAsync(request);
    if (allowConflict && response.StatusCode == HttpStatusCode.UnprocessableEntity)
    {
        return response;
    }

    return response;
}

static async Task EnsureSuccessAsync(HttpResponseMessage response)
{
    if (response.IsSuccessStatusCode)
    {
        return;
    }

    var content = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"GitHub API request failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{content}");
}

static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

internal sealed record IssueConfig(IReadOnlyList<string> Labels, string? Comment = null);

internal sealed class LabelDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class CommentDto
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }
}
