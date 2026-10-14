# GitHub Repository Explorer
**Version 0.2.0** · C# Windows Forms · .NET 8 · SQLite

## v0.2.0
- Native Windows desktop application; local HTML export is optional.
- Two databases: RepositoryCatalog.db for the repository library and RepositoryDetails.db for per-repository metadata and archive events.
- Download default-branch source ZIP and capture accessible GitHub REST API metadata.
- Searchable repository grid, details panel, progress/activity log, cancel control.
- Responsive docking/split layout, Per-Monitor-V2 DPI awareness, Font − / Font + controls.
- Open archive, basic verification, optional HTML export.
- Optional GitHub personal access token.

## Build
Install Visual Studio 2022 with the **.NET desktop development** workload and .NET 8 SDK. Open `GitHubRepositoryExplorer.csproj`, restore NuGet packages, build, and run.

CLI:
```powershell
dotnet restore
dotnet build -c Release
dotnet run
dotnet publish -c Release -r win-x64 --self-contained true
```

Databases are created in `%LOCALAPPDATA%\GitHubRepositoryExplorer\`. Source archives default to `%USERPROFILE%\Documents\GitHubArchives`.

## Scope
This is an initial functional foundation, not a full GitHub website mirror. It downloads the default branch and captures first-page API results for selected sections. Full Git history, all branches' source, pagination beyond the first 100 records, full issue/PR conversations, Wiki, Discussions, Actions logs, and every linked/binary asset are not yet mirrored. Build verification has not been performed in the generation environment.