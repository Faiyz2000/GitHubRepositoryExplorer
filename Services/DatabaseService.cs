using Microsoft.Data.Sqlite;
using GitHubRepositoryExplorer.Models;

namespace GitHubRepositoryExplorer.Services;

public sealed class DatabaseService
{
    public string DataDirectory { get; }
    public string CatalogPath => Path.Combine(DataDirectory, "RepositoryCatalog.db");
    public string DetailsPath => Path.Combine(DataDirectory, "RepositoryDetails.db");

    public DatabaseService()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubRepositoryExplorer");
        Directory.CreateDirectory(DataDirectory);
        using (var c = Open(CatalogPath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Repositories(
                RepositoryId TEXT PRIMARY KEY, FullName TEXT NOT NULL UNIQUE, Url TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '', Language TEXT NOT NULL DEFAULT '',
                Stars INTEGER NOT NULL DEFAULT 0, Forks INTEGER NOT NULL DEFAULT 0,
                DefaultBranch TEXT NOT NULL DEFAULT 'main', LocalPath TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT 'Registered', LastUpdatedUtc TEXT NOT NULL DEFAULT '')";
            cmd.ExecuteNonQuery();
        }
        using (var c = Open(DetailsPath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS RepositoryMetadata(
                RepositoryId TEXT NOT NULL, Section TEXT NOT NULL, JsonPayload TEXT NOT NULL,
                CapturedUtc TEXT NOT NULL, PRIMARY KEY(RepositoryId, Section));
                CREATE TABLE IF NOT EXISTS ArchiveEvents(Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RepositoryId TEXT NOT NULL, EventType TEXT NOT NULL, Message TEXT NOT NULL, CreatedUtc TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public List<RepositoryRecord> GetRepositories(string? filter = null)
    {
        var list = new List<RepositoryRecord>();
        using var c = Open(CatalogPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT RepositoryId,FullName,Url,Description,Language,Stars,Forks,DefaultBranch,LocalPath,Status,LastUpdatedUtc
            FROM Repositories WHERE @filter='' OR FullName LIKE @like OR Description LIKE @like OR Language LIKE @like ORDER BY FullName COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@filter", filter ?? "");
        cmd.Parameters.AddWithValue("@like", $"%{filter ?? ""}%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new RepositoryRecord {
            RepositoryId=r.GetString(0), FullName=r.GetString(1), Url=r.GetString(2), Description=r.GetString(3),
            Language=r.GetString(4), Stars=r.GetInt64(5), Forks=r.GetInt64(6), DefaultBranch=r.GetString(7),
            LocalPath=r.GetString(8), Status=r.GetString(9), LastUpdatedUtc=r.GetString(10)
        });
        return list;
    }

    public void UpsertRepository(RepositoryRecord r)
    {
        using var c = Open(CatalogPath);
        using var tx = c.BeginTransaction();
        // Replace a URL-only placeholder row with the real GitHub numeric ID after metadata is fetched.
        using (var cleanup = c.CreateCommand())
        {
            cleanup.Transaction = tx;
            cleanup.CommandText = "DELETE FROM Repositories WHERE FullName=@name AND RepositoryId<>@id";
            cleanup.Parameters.AddWithValue("@name", r.FullName);
            cleanup.Parameters.AddWithValue("@id", r.RepositoryId);
            cleanup.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO Repositories VALUES(@id,@name,@url,@desc,@lang,@stars,@forks,@branch,@path,@status,@updated)
        ON CONFLICT(RepositoryId) DO UPDATE SET FullName=excluded.FullName,Url=excluded.Url,Description=excluded.Description,
        Language=excluded.Language,Stars=excluded.Stars,Forks=excluded.Forks,DefaultBranch=excluded.DefaultBranch,
        LocalPath=excluded.LocalPath,Status=excluded.Status,LastUpdatedUtc=excluded.LastUpdatedUtc";
        cmd.Parameters.AddWithValue("@id",r.RepositoryId); cmd.Parameters.AddWithValue("@name",r.FullName);
        cmd.Parameters.AddWithValue("@url",r.Url); cmd.Parameters.AddWithValue("@desc",r.Description);
        cmd.Parameters.AddWithValue("@lang",r.Language); cmd.Parameters.AddWithValue("@stars",r.Stars);
        cmd.Parameters.AddWithValue("@forks",r.Forks); cmd.Parameters.AddWithValue("@branch",r.DefaultBranch);
        cmd.Parameters.AddWithValue("@path",r.LocalPath); cmd.Parameters.AddWithValue("@status",r.Status);
        cmd.Parameters.AddWithValue("@updated",r.LastUpdatedUtc); cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void SaveDetails(string id, string section, string json)
    {
        using var c = Open(DetailsPath); using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO RepositoryMetadata VALUES(@id,@section,@json,@utc)
            ON CONFLICT(RepositoryId,Section) DO UPDATE SET JsonPayload=excluded.JsonPayload,CapturedUtc=excluded.CapturedUtc";
        cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@section",section);
        cmd.Parameters.AddWithValue("@json",json); cmd.Parameters.AddWithValue("@utc",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public void LogEvent(string id, string type, string message)
    {
        using var c = Open(DetailsPath); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ArchiveEvents(RepositoryId,EventType,Message,CreatedUtc) VALUES(@id,@type,@msg,@utc)";
        cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@type",type); cmd.Parameters.AddWithValue("@msg",message);
        cmd.Parameters.AddWithValue("@utc",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public string GetDetailsSummary(string id)
    {
        using var c = Open(DetailsPath); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Section,CapturedUtc,length(JsonPayload) FROM RepositoryMetadata WHERE RepositoryId=@id ORDER BY Section";
        cmd.Parameters.AddWithValue("@id",id); using var r=cmd.ExecuteReader(); var rows=new List<string>();
        while(r.Read()) rows.Add($"{r.GetString(0)} — captured {r.GetString(1)} — {r.GetInt64(2):N0} JSON characters");
        return rows.Count==0 ? "No detailed metadata has been archived yet." : string.Join(Environment.NewLine,rows);
    }
}