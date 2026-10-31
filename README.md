# GitHub Repository Explorer
**Version 0.3.0** · C# Windows Forms · .NET 8 · SQLite

## Open in Visual Studio 2022

**Open `GitHubRepositoryExplorer.sln` (not the individual source file).** The solution includes the `GitHubRepositoryExplorer` Windows Forms project and is configured for Debug/Release builds. Because it contains one executable project, Visual Studio can use it as the startup project.

1. Install Visual Studio 2022 with the **.NET desktop development** workload and the **.NET 8 SDK**.
2. Clone or extract the repository.
3. Double-click `GitHubRepositoryExplorer.sln`, or in Visual Studio choose **File → Open → Project/Solution** and select it.
4. Allow NuGet package restore to complete.
5. Select **GitHubRepositoryExplorer** as the startup project if Visual Studio does not select it automatically (right-click the project in Solution Explorer → **Set as Startup Project**).
6. Choose **Debug → Start Debugging** (F5) or **Start Without Debugging** (Ctrl+F5).

The `.vsconfig` file at the repository root declares the recommended Visual Studio workload and can be used by Visual Studio Installer to install missing components.

## v0.3.0

- Split **Download Source** and **Archive Repository** into separate operations.
- Added **Download User Repositories** for a GitHub user profile URL, such as `https://github.com/morluto`.
- User repository discovery uses the GitHub REST API with 100 repositories per page and continues through all pages. Each repository's default-branch ZIP is downloaded and extracted sequentially, with per-repository success/failure logging and cancellation support.
- The bulk operation asks for confirmation after listing repositories and before downloading them.
- The application title and assembly/file versions are updated to 0.3.0.

## Project features

- Native Windows desktop application; local HTML export is optional.
- Two databases: `RepositoryCatalog.db` for the repository library and `RepositoryDetails.db` for per-repository metadata and archive events.
- **Download Source**: downloads the selected repository's default-branch ZIP and extracts the source into the output folder. It does not register the repository or capture metadata.
- **Archive Repository**: downloads and extracts the default-branch source, registers repository metadata in the catalog database, and captures supported GitHub API metadata.
- **Download User Repositories**: takes a profile URL such as `https://github.com/morluto`, lists that user's public owned repositories across all API pages, then runs the same source-download workflow for each repository. Each gets a `source.zip` plus extracted files under its own folder. It does not register repositories or capture metadata. Forks owned by the user are included when GitHub lists them. This workflow targets public repositories.
- Searchable repository grid, details panel, progress/activity log, cancel control.
- Responsive docking/split layout, Per-Monitor-V2 DPI awareness, Font − / Font + controls.
- Open archive, basic verification, optional HTML export.
- Optional GitHub personal access token to improve API rate limits and access permitted private data.

## Using user-profile downloads

1. Enter a profile URL in the **GitHub URL** field, for example `https://github.com/morluto`.
2. Choose the destination in **Archive folder**.
3. Optionally enter a GitHub personal access token.
4. Click **Download User Repositories** and review the repository count.
5. Confirm to begin. The application downloads repositories sequentially. If one repository fails, it logs the failure and continues with the remaining repositories. Click **Cancel** to stop the operation.

Use **Download Source** for a single repository URL (for example `https://github.com/owner/repository`), **Download User Repositories** for all public repositories owned by a user, and **Archive Repository** when you want the full supported metadata-preservation workflow for one repository.

## Build from the command line

Run these commands from the directory containing `GitHubRepositoryExplorer.sln`:

```powershell
dotnet restore .\GitHubRepositoryExplorer.sln
dotnet build .\GitHubRepositoryExplorer.sln -c Release
dotnet run --project .\GitHubRepositoryExplorer.csproj
dotnet publish .\GitHubRepositoryExplorer.csproj -c Release -r win-x64 --self-contained true
```

Databases are created in `%LOCALAPPDATA%\GitHubRepositoryExplorer\`. Source archives default to `%USERPROFILE%\Documents\GitHubArchives`.

## Scope and limitations

This is a local source-and-metadata archiver, not a complete GitHub website mirror. Source downloads include the default branch only. Metadata collection is limited to the API sections currently implemented and usually to the first 100 records per section. Full Git history, source for every branch/tag, all paginated issues and pull requests, Wiki, Discussions, Actions logs, and every linked/binary asset are not mirrored. User-profile archival processes repositories sequentially and may take considerable time or encounter GitHub API rate limits. Build verification has not been performed in the generation environment.
