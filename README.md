# GitHub Repository Explorer
**Version 0.2.0** · C# Windows Forms · .NET 8 · SQLite

## Open in Visual Studio 2022

**Open `GitHubRepositoryExplorer.sln` (not the individual source file).** The solution includes the `GitHubRepositoryExplorer` Windows Forms project and is configured for Debug/Release builds. Because it contains one executable project, Visual Studio can use it as the startup project.

1. Install Visual Studio 2022 with the **.NET desktop development** workload and the **.NET 8 SDK**.
2. Clone or extract the repository.
3. Double-click `GitHubRepositoryExplorer.sln`, or in Visual Studio choose **File → Open → Project/Solution** and select it.
4. Allow NuGet package restore to complete.
5. Select **GitHubRepositoryExplorer** as the startup project if Visual Studio does not select it automatically (right-click the project in Solution Explorer → **Set as Startup Project**).
6. Choose **Debug → Start Debugging** (F5) or **Start Without Debugging** (Ctrl+F5).

The `.vsconfig` file at the repository root declares the recommended Visual Studio workload and can be used by Visual Studio Installer to install missing components.

## Project features

- Native Windows desktop application; local HTML export is optional.
- Two databases: RepositoryCatalog.db for the repository library and RepositoryDetails.db for per-repository metadata and archive events.
- Download default-branch source ZIP and capture accessible GitHub REST API metadata.
- Searchable repository grid, details panel, progress/activity log, cancel control.
- Responsive docking/split layout, Per-Monitor-V2 DPI awareness, Font − / Font + controls.
- Open archive, basic verification, optional HTML export.
- Optional GitHub personal access token.

## Build from the command line

Run these commands from the directory containing `GitHubRepositoryExplorer.sln`:

```powershell
dotnet restore .\GitHubRepositoryExplorer.sln
dotnet build .\GitHubRepositoryExplorer.sln -c Release
dotnet run --project .\GitHubRepositoryExplorer.csproj
dotnet publish .\GitHubRepositoryExplorer.csproj -c Release -r win-x64 --self-contained true
```

Databases are created in `%LOCALAPPDATA%\GitHubRepositoryExplorer\`. Source archives default to `%USERPROFILE%\Documents\GitHubArchives`.

## Scope

This is an initial functional foundation, not a full GitHub website mirror. It downloads the default branch and captures first-page API results for selected sections. Full Git history, all branches' source, pagination beyond the first 100 records, full issue/PR conversations, Wiki, Discussions, Actions logs, and every linked/binary asset are not yet mirrored. Build verification has not been performed in the generation environment.
