using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GitHubRepositoryExplorer.Models;

namespace GitHubRepositoryExplorer.Services;

public sealed class GitHubArchiveService
{
    private readonly HttpClient _http = new();
    private readonly HttpClient _webHttp = new();
    private readonly DatabaseService _db;
    public GitHubArchiveService(DatabaseService db)
    {
        _db=db;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubRepositoryExplorer/0.6");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _webHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubRepositoryExplorer/0.5 (public source downloader)");
        _webHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    }

    public async Task<RepositoryRecord> ArchiveAsync(string inputUrl,string outputRoot,string? token,IProgress<string> progress,CancellationToken ct)
    {
        if(!Uri.TryCreate(inputUrl.Trim(),UriKind.Absolute,out var uri) || !uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a standard https://github.com/owner/repository URL.");
        var parts=uri.AbsolutePath.Trim('/').Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length<2) throw new InvalidOperationException("URL must include an owner and repository name.");
        var owner=parts[0]; var repo=parts[1].EndsWith(".git",StringComparison.OrdinalIgnoreCase)?parts[1][..^4]:parts[1];
        SetAuthorization(token);
        var api=$"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        progress.Report("Reading repository metadata...");
        using var response=await _http.GetAsync(api,ct); response.EnsureSuccessStatusCode();
        var json=await response.Content.ReadAsStringAsync(ct); using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        var id=root.GetProperty("id").GetInt64().ToString(); var full=root.GetProperty("full_name").GetString()??$"{owner}/{repo}";
        var branch=root.TryGetProperty("default_branch",out var b)?b.GetString()??"main":"main";
        var target=Path.Combine(outputRoot,Sanitize(full.Replace('/','_'))); Directory.CreateDirectory(target);
        var record=new RepositoryRecord {
            RepositoryId=id,FullName=full,Url=root.GetProperty("html_url").GetString()??inputUrl,
            Description=root.TryGetProperty("description",out var d)&&d.ValueKind!=JsonValueKind.Null?d.GetString()??"": "",
            Language=root.TryGetProperty("language",out var l)&&l.ValueKind!=JsonValueKind.Null?l.GetString()??"": "",
            Stars=root.TryGetProperty("stargazers_count",out var s)?s.GetInt64():0,Forks=root.TryGetProperty("forks_count",out var f)?f.GetInt64():0,
            DefaultBranch=branch,LocalPath=target,Status="Downloading",LastUpdatedUtc=DateTime.UtcNow.ToString("O")
        };
        _db.UpsertRepository(record); _db.SaveDetails(id,"repository",json); _db.LogEvent(id,"Metadata","Repository metadata retrieved.");
        progress.Report("Downloading default-branch source archive...");
        var zipPath=Path.Combine(target,"source.zip");
        using(var z=await _http.GetAsync($"{api}/zipball/{Uri.EscapeDataString(branch)}",HttpCompletionOption.ResponseHeadersRead,ct))
        { z.EnsureSuccessStatusCode(); await using var fs=File.Create(zipPath); await z.Content.CopyToAsync(fs,ct); }
        var source=Path.Combine(target,"source"); if(Directory.Exists(source))Directory.Delete(source,true); Directory.CreateDirectory(source);
        ZipFile.ExtractToDirectory(zipPath,source,true);
        foreach(var section in new[]{"languages","branches","tags","releases","contributors","commits","issues"})
        {
            ct.ThrowIfCancellationRequested();
            var endpoint=section switch {
                "languages"=>$"{api}/languages","branches"=>$"{api}/branches?per_page=100","tags"=>$"{api}/tags?per_page=100",
                "releases"=>$"{api}/releases?per_page=100","contributors"=>$"{api}/contributors?per_page=100",
                "commits"=>$"{api}/commits?per_page=100",_=>$"{api}/issues?state=all&per_page=100"};
            try { progress.Report($"Archiving {section}..."); using var rr=await _http.GetAsync(endpoint,ct); rr.EnsureSuccessStatusCode(); _db.SaveDetails(id,section,await rr.Content.ReadAsStringAsync(ct)); }
            catch(HttpRequestException ex){_db.LogEvent(id,"Warning",$"{section}: {ex.Message}");}
        }
        try {
            using var rr=await _http.GetAsync($"{api}/readme",ct);
            if(rr.IsSuccessStatusCode){using var rd=JsonDocument.Parse(await rr.Content.ReadAsStringAsync(ct)); var dl=rd.RootElement.GetProperty("download_url").GetString();
                if(!string.IsNullOrWhiteSpace(dl)){var md=await _http.GetStringAsync(dl,ct); await File.WriteAllTextAsync(Path.Combine(target,"README.md"),md,ct); _db.SaveDetails(id,"readme",md);}}
        } catch(HttpRequestException ex){_db.LogEvent(id,"Warning","README: "+ex.Message);}
        record.Status="Archived";record.LastUpdatedUtc=DateTime.UtcNow.ToString("O");_db.UpsertRepository(record);
        WriteManifest(target, record);
        _db.LogEvent(id,"Archive","Default-branch source and accessible metadata archived.");progress.Report("Archive completed.");return record;
    }
    // Refresh repository metadata from public repository HTML, without using GitHub REST API quota.
    public async Task<int> RefreshPublicMetadataAsync(IEnumerable<RepositoryRecord> records, IProgress<string> progress, CancellationToken ct)
    {
        var updated = 0;
        var items = records.ToList();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var record = items[i];
            if (!Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report($"[{i + 1}/{items.Count}] Skipping invalid repository URL: {record.FullName}");
                continue;
            }

            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                progress.Report($"[{i + 1}/{items.Count}] Skipping non-repository URL: {record.Url}");
                continue;
            }

            var owner = parts[0];
            var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
            var repositoryUrl = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
            try
            {
                progress.Report($"[{i + 1}/{items.Count}] Refreshing metadata for {record.FullName}...");
                using var response = await _webHttp.GetAsync(repositoryUrl, ct);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(ct);

                var stars = ExtractJsonLong(html, "stargazerCount") ?? ExtractJsonLong(html, "stargazers_count");
                var forks = ExtractJsonLong(html, "forkCount") ?? ExtractJsonLong(html, "forks_count");
                var branch = ExtractJsonString(html, "defaultBranch") ?? ExtractJsonString(html, "default_branch");
                var description = ExtractMetaContent(html, "og:description") ?? ExtractJsonString(html, "description");
                var languageMatch = Regex.Match(html, @"""primaryLanguage""\s*:\s*\{\s*""name""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);

                if (stars.HasValue) record.Stars = stars.Value;
                if (forks.HasValue) record.Forks = forks.Value;
                if (!string.IsNullOrWhiteSpace(branch)) record.DefaultBranch = System.Net.WebUtility.HtmlDecode(branch);
                if (!string.IsNullOrWhiteSpace(description)) record.Description = description;
                if (languageMatch.Success) record.Language = System.Net.WebUtility.HtmlDecode(languageMatch.Groups[1].Value);
                record.Url = repositoryUrl;
                record.LastUpdatedUtc = DateTime.UtcNow.ToString("O");

                _db.UpsertRepository(record);
                if (!string.IsNullOrWhiteSpace(record.LocalPath) && Directory.Exists(record.LocalPath))
                    WriteManifest(record.LocalPath, record);
                updated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.Report($"[{i + 1}/{items.Count}] Could not refresh {record.FullName}: {ex.Message}");
            }

            // Avoid hammering GitHub's web frontend when refreshing a large catalog.
            if (i < items.Count - 1)
                await Task.Delay(300, ct);
        }

        progress.Report($"Metadata refresh finished: {updated}/{items.Count} repositories updated. No GitHub REST API calls were made.");
        return updated;
    }

    // Source-only downloads use public GitHub HTML and codeload.github.com rather than the REST API.
    public async Task DownloadSourceAsync(string inputUrl, string outputRoot, string? token, IProgress<string> progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(inputUrl.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a standard https://github.com/owner/repository URL.");

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException("Download requires a single repository URL, not a user profile URL.");
        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        var repositoryUrl = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        var fullName = $"{owner}/{repo}";

        progress.Report($"Reading public repository page for {fullName} (no REST API calls)...");
        using var pageResponse = await _webHttp.GetAsync(repositoryUrl, ct);
        pageResponse.EnsureSuccessStatusCode();
        var html = await pageResponse.Content.ReadAsStringAsync(ct);

        var branch = ExtractJsonString(html, "defaultBranch") ??
                     ExtractJsonString(html, "default_branch");
        if (string.IsNullOrWhiteSpace(branch))
        {
            var branchMatch = Regex.Match(html, @"[""'](?:defaultBranch|default_branch)[""']?\s*:\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!branchMatch.Success)
                branchMatch = Regex.Match(html, @"""defaultBranch""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (branchMatch.Success)
                branch = System.Net.WebUtility.HtmlDecode(branchMatch.Groups[1].Value);
        }
        if (string.IsNullOrWhiteSpace(branch))
            throw new InvalidOperationException($"Could not determine the default branch from {repositoryUrl}. GitHub may have changed its page format or is temporarily throttling requests.");

        var description = ExtractMetaContent(html, "og:description") ??
                          ExtractJsonString(html, "description") ?? "";
        var languageMatch = Regex.Match(html, @"""primaryLanguage""\s*:\s*\{\s*""name""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        var language = languageMatch.Success ? System.Net.WebUtility.HtmlDecode(languageMatch.Groups[1].Value) : "";
        var stars = ExtractJsonLong(html, "stargazerCount") ?? ExtractJsonLong(html, "stargazers_count") ?? 0;
        var forks = ExtractJsonLong(html, "forkCount") ?? ExtractJsonLong(html, "forks_count") ?? 0;

        var target = Path.Combine(outputRoot, Sanitize(fullName.Replace('/', '_')));
        Directory.CreateDirectory(target);
        if (!Directory.Exists(target))
            throw new IOException($"The repository destination folder could not be created: {target}");
        var zipPath = Path.Combine(target, "source.zip");

        var record = new RepositoryRecord
        {
            RepositoryId = "github:" + fullName.ToLowerInvariant(),
            FullName = fullName,
            Url = repositoryUrl,
            Description = description,
            Language = language,
            Stars = stars,
            Forks = forks,
            DefaultBranch = branch,
            LocalPath = target,
            Status = "Downloading",
            LastUpdatedUtc = DateTime.UtcNow.ToString("O")
        };
        _db.UpsertRepository(record);
        WriteManifest(target, record);

        var encodedBranch = string.Join("/", branch.Split('/').Select(Uri.EscapeDataString));
        var zipUrl = $"https://codeload.github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/zip/refs/heads/{encodedBranch}";
        progress.Report($"Downloading {fullName} ({branch}) directly from GitHub archive host...");
        using (var zipResponse = await _webHttp.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            zipResponse.EnsureSuccessStatusCode();
            if (Directory.Exists(zipPath))
                throw new IOException($"Cannot write ZIP because a directory already exists at: {zipPath}");
            await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await zipResponse.Content.CopyToAsync(fs, ct);
        }

        var source = Path.Combine(target, "source");
        if (Directory.Exists(source)) Directory.Delete(source, true);
        Directory.CreateDirectory(source);
        ZipFile.ExtractToDirectory(zipPath, source, true);
        record.Status = "Downloaded";
        record.LastUpdatedUtc = DateTime.UtcNow.ToString("O");
        _db.UpsertRepository(record);
        WriteManifest(target, record);
        progress.Report($"Download completed: {fullName} — stars: {stars:N0}, forks: {forks:N0}; source.zip and extracted files saved to {target}");
    }

    private static string? ExtractJsonString(string html, string propertyName)
    {
        var match = Regex.Match(html, "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        try { return JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\""); }
        catch (JsonException) { return match.Groups[1].Value; }
    }

    private static long? ExtractJsonLong(string html, string propertyName)
    {
        var match = Regex.Match(html, "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static string? ExtractMetaContent(string html, string property)
    {
        var escaped = Regex.Escape(property);
        var match = Regex.Match(html, @"<meta\s+[^>]*(?:property|name)=['""]" + escaped + @"['""][^>]*content=['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(html, @"<meta\s+[^>]*content=['""]([^'""]*)['""][^>]*(?:property|name)=['""]" + escaped + @"['""]", RegexOptions.IgnoreCase);
        }
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static void WriteManifest(string target, RepositoryRecord record)
    {
        var path = Path.Combine(target, "ArchiveManifest.json");
        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public async Task<List<string>> GetUserRepositoryUrlsAsync(string profileUrl, string? token, IProgress<string> progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(profileUrl.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a GitHub user profile URL, for example https://github.com/morluto.");

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1 || parts[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a GitHub user profile URL containing only https://github.com/username.");
        var username = parts[0];
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // GitHub's public repository tab is paginated HTML. We intentionally avoid
        // /users/{username}/repos so source-only bulk downloads do not consume REST API quota.
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Listing public repositories for {username} from GitHub website (page {page})...");
            var pageUrl = $"https://github.com/{Uri.EscapeDataString(username)}?tab=repositories&page={page}";
            using var response = await _webHttp.GetAsync(pageUrl, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);
            var before = seen.Count;
            var pattern = $@"href=[""']/{Regex.Escape(username)}/([^/""?#]+)[""']";
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
            {
                var repoName = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                if (repoName.Equals("repositories", StringComparison.OrdinalIgnoreCase) ||
                    repoName.Equals("followers", StringComparison.OrdinalIgnoreCase) ||
                    repoName.Equals("following", StringComparison.OrdinalIgnoreCase) ||
                    repoName.Equals("sponsors", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seen.Add($"{username}/{repoName}"))
                    urls.Add($"https://github.com/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(repoName)}");
            }

            // The final page may still repeat pinned repositories, so stop when it adds none.
            if (seen.Count == before) break;
            // Be polite to GitHub's website; this is not a way to bypass website throttling.
            await Task.Delay(250, ct);
        }

        return urls;
    }

    private void SetAuthorization(string? token)
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    private static string Sanitize(string value){foreach(var c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');return value.Replace("..","_");}
}