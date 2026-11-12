using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitHubRepositoryExplorer.Services;

public sealed class RepositoryReadmeService
{
    private readonly HttpClient _http = new();
    private readonly HttpClient _webHttp = new();

    public RepositoryReadmeService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubRepositoryExplorer/0.8");
        _webHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubRepositoryExplorer/0.8 (README downloader)");
    }

    public async Task<string> DownloadRepositoryInfoAsync(string inputUrl, string outputRoot, string? token, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(inputUrl.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a standard https://github.com/owner/repository URL.");

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException("Repository information requires a single repository URL.");

        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];

        Directory.CreateDirectory(outputRoot);
        var target = Path.Combine(outputRoot, Sanitize($"{owner}_{repo}"));
        Directory.CreateDirectory(target);
        SetAuthorization(token);

        progress?.Report($"Downloading rendered README for {owner}/{repo}...");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/readme");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.html+json"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var html = root.TryGetProperty("html", out var htmlElement) ? htmlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("GitHub did not return rendered README content.");

        var readmeName = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "README.md" : "README.md";
        var readmePath = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? readmeName : readmeName;
        var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlElement)
            ? htmlUrlElement.GetString() ?? $"https://github.com/{owner}/{repo}"
            : $"https://github.com/{owner}/{repo}";

        var rawMarkdown = DecodeRepositoryContent(root);
        await File.WriteAllTextAsync(Path.Combine(target, readmeName), rawMarkdown, new UTF8Encoding(false), ct);

        progress?.Report($"README retrieved: {readmePath}. Downloading embedded images and assets...");
        var assetDirectory = Path.Combine(target, "READMEAssets");
        Directory.CreateDirectory(assetDirectory);

        var rewrittenHtml = await DownloadAndRewriteImagesAsync(
            html, htmlUrl, owner, repo, rawMarkdown, assetDirectory, progress, ct);

        var htmlPath = Path.Combine(target, "README.html");
        await File.WriteAllTextAsync(
            htmlPath,
            BuildStandaloneHtml($"{owner}/{repo}", rewrittenHtml, htmlUrl),
            new UTF8Encoding(false),
            ct);

        var manifest = new
        {
            repository = $"{owner}/{repo}",
            repositoryUrl = $"https://github.com/{owner}/{repo}",
            readmeName,
            readmePath,
            downloadedUtc = DateTime.UtcNow.ToString("O"),
            renderedBy = "GitHub application/vnd.github.html+json",
            readmeHtml = Path.GetFileName(htmlPath),
            assetsFolder = Path.GetFileName(assetDirectory)
        };

        await File.WriteAllTextAsync(
            Path.Combine(target, "RepositoryInfoManifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            ct);

        progress?.Report($"Repository information saved to {target}");
        return htmlPath;
    }

    private async Task<string> DownloadAndRewriteImagesAsync(
        string html,
        string htmlUrl,
        string owner,
        string repo,
        string rawMarkdown,
        string assetDirectory,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
                     html, @"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']",
                     RegexOptions.IgnoreCase))
            sources.Add(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(
                     html, @"<source\b[^>]*\bsrcset\s*=\s*[""']([^""']+)[""']",
                     RegexOptions.IgnoreCase))
        {
            foreach (var candidate in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var source = candidate.Trim().Split(' ', 2)[0];
                if (!string.IsNullOrWhiteSpace(source))
                    sources.Add(source);
            }
        }

        foreach (Match match in Regex.Matches(
                     rawMarkdown, @"!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))",
                     RegexOptions.IgnoreCase))
        {
            var source = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(source))
                sources.Add(source);
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var counter = 0;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("#", StringComparison.Ordinal))
                continue;

            var resolved = ResolveAssetUri(source, htmlUrl, owner, repo);
            if (resolved == null ||
                !(resolved.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                  resolved.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var fileName = BuildAssetFileName(resolved, ++counter);
                var localPath = Path.Combine(assetDirectory, fileName);

                using var response = await _webHttp.GetAsync(
                    resolved, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    progress?.Report($"README asset unavailable: {resolved}");
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(
                    localPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await stream.CopyToAsync(file, ct);

                replacements[source] = "READMEAssets/" + fileName;
                progress?.Report($"Downloaded README asset: {fileName}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress?.Report($"README asset skipped: {resolved} ({ex.Message})");
            }
        }

        foreach (var replacement in replacements)
        {
            var oldValue = replacement.Key;
            var newValue = replacement.Value;

            html = html.Replace($"src=\"{oldValue}\"", $"src=\"{newValue}\"", StringComparison.Ordinal);
            html = html.Replace($"src='{oldValue}'", $"src='{newValue}'", StringComparison.Ordinal);
            html = html.Replace($"srcset=\"{oldValue}\"", $"srcset=\"{newValue}\"", StringComparison.Ordinal);
            html = html.Replace($"srcset='{oldValue}'", $"srcset='{newValue}'", StringComparison.Ordinal);
        }

        return html;
    }

    private static Uri? ResolveAssetUri(string source, string htmlUrl, string owner, string repo)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var absolute))
            return ConvertGithubBlobImageToRaw(absolute);

        if (source.StartsWith("//", StringComparison.Ordinal))
            return new Uri("https:" + source);

        if (source.StartsWith("/", StringComparison.Ordinal))
            return new Uri($"https://github.com/{owner}/{repo}/blob/main{source}");

        return Uri.TryCreate(new Uri(htmlUrl), source, out var relative)
            ? ConvertGithubBlobImageToRaw(relative)
            : null;
    }

    private static Uri ConvertGithubBlobImageToRaw(Uri uri)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var blobIndex = Array.FindIndex(parts, p => p.Equals("blob", StringComparison.OrdinalIgnoreCase));
        if (blobIndex >= 2 && blobIndex + 1 < parts.Length)
        {
            var owner = parts[0];
            var repo = parts[1];
            var branchAndPath = string.Join("/", parts.Skip(blobIndex + 1));
            return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branchAndPath}{uri.Query}");
        }

        var rawIndex = Array.FindIndex(parts, p => p.Equals("raw", StringComparison.OrdinalIgnoreCase));
        if (rawIndex >= 2 && rawIndex + 1 < parts.Length)
        {
            var owner = parts[0];
            var repo = parts[1];
            var branchAndPath = string.Join("/", parts.Skip(rawIndex + 1));
            return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branchAndPath}{uri.Query}");
        }

        return uri;
    }

    private static string DecodeRepositoryContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var contentElement))
            return string.Empty;

        var content = contentElement.GetString() ?? string.Empty;
        var encoding = root.TryGetProperty("encoding", out var encodingElement)
            ? encodingElement.GetString()
            : null;

        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            return content;

        content = content.Replace("\n", string.Empty, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(content));
    }

    private static string BuildStandaloneHtml(string repository, string body, string repositoryUrl)
    {
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{System.Net.WebUtility.HtmlEncode(repository)}} - README</title>
<style>
body { margin:0; padding:28px; background:#fff; color:#24292f; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; line-height:1.55; }
.markdown-body { max-width:1100px; margin:0 auto; overflow-wrap:anywhere; }
.markdown-body img { max-width:100%; height:auto; }
.markdown-body table { border-collapse:collapse; width:100%; display:block; overflow-x:auto; }
.markdown-body th,.markdown-body td { border:1px solid #d0d7de; padding:6px 13px; }
.markdown-body th { font-weight:600; background:#f6f8fa; }
.markdown-body pre { background:#f6f8fa; padding:16px; overflow:auto; border-radius:6px; }
.markdown-body code { font-family:Consolas,"Courier New",monospace; }
.markdown-body blockquote { margin:0; padding:0 1em; color:#57606a; border-left:.25em solid #d0d7de; }
.markdown-body details { margin:10px 0; }
a { color:#0969da; }
.repository-link { max-width:1100px; margin:0 auto 22px auto; padding-bottom:12px; border-bottom:1px solid #d8dee4; font-size:13px; }
</style>
</head>
<body>
<div class="repository-link">Repository: <strong>{{System.Net.WebUtility.HtmlEncode(repository)}}</strong> &nbsp;·&nbsp; <a href="{{System.Net.WebUtility.HtmlEncode(repositoryUrl)}}">Open original GitHub repository</a></div>
<article class="markdown-body">{{body}}</article>
</body>
</html>
""";
    }

    private static string BuildAssetFileName(Uri uri, int counter)
    {
        var rawName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(rawName))
            rawName = "asset";

        foreach (var c in Path.GetInvalidFileNameChars())
            rawName = rawName.Replace(c, '_');

        if (rawName.Length > 100)
            rawName = rawName[..100];

        var extension = Path.GetExtension(rawName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";

        return $"{counter:000}_{Path.GetFileNameWithoutExtension(rawName)}{extension}";
    }

    private void SetAuthorization(string? token)
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token.Trim());

        _webHttp.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return value.Replace("..", "_");
    }
}
