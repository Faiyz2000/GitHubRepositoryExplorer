namespace GitHubRepositoryExplorer.Models;
public sealed class RepositoryRecord
{
    public string RepositoryId { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string Language { get; set; } = "";
    public long Stars { get; set; }
    public long Forks { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string LocalPath { get; set; } = "";
    public string Status { get; set; } = "Registered";
    public string LastUpdatedUtc { get; set; } = "";
}