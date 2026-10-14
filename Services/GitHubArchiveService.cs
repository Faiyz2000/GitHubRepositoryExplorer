using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubRepositoryExplorer.Models;

namespace GitHubRepositoryExplorer.Services;

public sealed class GitHubArchiveService
{
    private readonly HttpClient _http = new();
    private readonly DatabaseService _db;
    public GitHubArchiveService(DatabaseService db)
    {
        _db=db; _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubRepositoryExplorer/0.2");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<RepositoryRecord> ArchiveAsync(string inputUrl,string outputRoot,string? token,IProgress<string> progress,CancellationToken ct)
    {
        if(!Uri.TryCreate(inputUrl.Trim(),UriKind.Absolute,out var uri) || !uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a standard https://github.com/owner/repository URL.");
        var parts=uri.AbsolutePath.Trim('/').Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length<2) throw new InvalidOperationException("URL must include an owner and repository name.");
        var owner=parts[0]; var repo=parts[1].EndsWith(".git",StringComparison.OrdinalIgnoreCase)?parts[1][..^4]:parts[1];
        _http.DefaultRequestHeaders.Authorization=string.IsNullOrWhiteSpace(token)?null:new AuthenticationHeaderValue("Bearer",token.Trim());
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
        _db.LogEvent(id,"Archive","Default-branch source and accessible metadata archived.");progress.Report("Archive completed.");return record;
    }
    private static string Sanitize(string value){foreach(var c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');return value.Replace("..","_");}
}