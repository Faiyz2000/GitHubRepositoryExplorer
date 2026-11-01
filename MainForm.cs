using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using GitHubRepositoryExplorer.Models;
using GitHubRepositoryExplorer.Services;

namespace GitHubRepositoryExplorer;

public sealed class MainForm : Form
{
    private readonly DatabaseService _db = new();
    private readonly GitHubArchiveService _archive;
    private readonly BindingList<RepositoryRecord> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _url = new(), _output = new(), _token = new(), _search = new(), _details = new(), _log = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Button _downloadButton = new(), _archiveButton = new(), _userDownloadButton = new(), _cancelButton = new(), _refreshButton = new();
    private CancellationTokenSource? _cts;
    private float _fontSize = 9.5f;
    private int _sortColumnIndex = 0;
    private bool _sortAscending = true;
    private readonly string _defaultOutput = @"D:\GitHubArchives";

    public MainForm()
    {
        _archive = new GitHubArchiveService(_db);
        Text = "GitHub Repository Explorer — v0.7.1";
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", _fontSize);
        BuildUi();
        _output.Text = _defaultOutput;
        RefreshRows();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        Controls.Add(root);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 5, AutoSize = true };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddLabeled(top, "GitHub URL", _url, 0, 1);
        AddLabeled(top, "Output folder", _output, 2, 3);
        _url.PlaceholderText = "Repository URL or user profile URL (https://github.com/username)";
        _url.TextChanged += (_, _) => UpdateUrlAvailabilityStatus();
        top.Controls.Add(MakeButton("Browse…", (_, _) => BrowseOutput()), 4, 0);
        root.Controls.Add(top, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 8, 0, 8) };
        actions.Controls.Add(MakeButton("Add Repository", (_, _) => AddRepository()));
        _downloadButton.Text = "Download Source"; _downloadButton.AutoSize = true;
        _downloadButton.Click += async (_, _) => await DownloadSelectedOrUrlAsync(); actions.Controls.Add(_downloadButton);
        _archiveButton.Text = "Archive Repository"; _archiveButton.AutoSize = true;
        _archiveButton.Click += async (_, _) => await ArchiveSelectedOrUrlAsync(); actions.Controls.Add(_archiveButton);
        _userDownloadButton.Text = "Download User Repositories"; _userDownloadButton.AutoSize = true;
        _userDownloadButton.Click += async (_, _) => await DownloadUserRepositoriesAsync(); actions.Controls.Add(_userDownloadButton);
        actions.Controls.Add(MakeButton("Update Archive", async (_, _) => await ArchiveSelectedOrUrlAsync()));
        actions.Controls.Add(MakeButton("Open Archive", (_, _) => OpenSelected()));
        actions.Controls.Add(MakeButton("Verify Archive", (_, _) => VerifySelected()));
        actions.Controls.Add(MakeButton("Export Website", (_, _) => ExportWebsite()));
        actions.Controls.Add(MakeButton("Remove Repository", (_, _) => RemoveSelected()));
        _cancelButton.Text = "Cancel"; _cancelButton.AutoSize = true; _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => _cts?.Cancel(); actions.Controls.Add(_cancelButton);
        actions.Controls.Add(MakeButton("Font −", (_, _) => ChangeFont(-1)));
        actions.Controls.Add(MakeButton("Font +", (_, _) => ChangeFont(1)));
        _refreshButton.Text = "Refresh List"; _refreshButton.AutoSize = true; _refreshButton.Click += async (_, _) => await RefreshListAsync(); actions.Controls.Add(_refreshButton);
        root.Controls.Add(actions, 0, 1);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
// The SplitContainer can initially be narrower than the requested panel widths.
// Set the divider only after layout gives it a usable width.
split.SizeChanged += (_, _) =>
{
    if (split.Width > 520)
    {
        var maxDistance = split.Width - 220;
        if (maxDistance >= 300)
            split.SplitterDistance = Math.Clamp(640, 300, maxDistance);
    }
};
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _search.PlaceholderText = "Search repositories by name, description, or language…"; _search.Dock = DockStyle.Top;
        _search.TextChanged += (_, _) => RefreshRows(scanArchiveFolders: false); left.Controls.Add(_search, 0, 0);
        ConfigureGrid(); left.Controls.Add(_grid, 0, 1); split.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        right.Controls.Add(new Label { Text = "Selected repository details", AutoSize = true, Padding = new Padding(0, 4, 0, 4) }, 0, 0);
        _details.Multiline = true; _details.ReadOnly = true; _details.ScrollBars = ScrollBars.Both; _details.WordWrap = false; _details.Dock = DockStyle.Fill;
        right.Controls.Add(_details, 0, 1);
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false; _log.Dock = DockStyle.Fill;
        right.Controls.Add(_log, 0, 2); split.Panel2.Controls.Add(right); root.Controls.Add(split, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, AutoSize = true };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progress.Dock = DockStyle.Fill; footer.Controls.Add(_progress, 0, 0);
        _status.Text = "Ready"; _status.AutoSize = true; footer.Controls.Add(_status, 0, 1);
        var tokenPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        tokenPanel.Controls.Add(new Label { Text = "Optional GitHub token:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _token.Width = 220; _token.UseSystemPasswordChar = true; _token.PlaceholderText = "Personal access token (optional)";
        tokenPanel.Controls.Add(_token); footer.Controls.Add(tokenPanel, 1, 0); root.Controls.Add(footer, 0, 3);
        _grid.SelectionChanged += (_, _) => ShowSelectedDetails();
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is RepositoryRecord selected)
                _url.Text = selected.Url;
        };
    }

    private static void AddLabeled(TableLayoutPanel panel, string label, Control control, int labelCol, int controlCol)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, labelCol, 0);
        control.Dock = DockStyle.Fill; panel.Controls.Add(control, controlCol, 0);
    }

    private Button MakeButton(string text, EventHandler handler)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 32), Margin = new Padding(3) };
        b.Click += handler; return b;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false;
        _grid.ReadOnly = true; _grid.MultiSelect = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.RowHeadersVisible = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="Repository", DataPropertyName=nameof(RepositoryRecord.FullName), FillWeight=150, MinimumWidth=130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="Language", DataPropertyName=nameof(RepositoryRecord.Language), FillWeight=65, MinimumWidth=70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="Stars", DataPropertyName=nameof(RepositoryRecord.Stars), FillWeight=45, MinimumWidth=55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="Status", DataPropertyName=nameof(RepositoryRecord.Status), FillWeight=70, MinimumWidth=75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="Last Updated (UTC)", DataPropertyName=nameof(RepositoryRecord.LastUpdatedUtc), FillWeight=110, MinimumWidth=130 });
        foreach (DataGridViewColumn column in _grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (_sortColumnIndex == e.ColumnIndex)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumnIndex = e.ColumnIndex;
                _sortAscending = true;
            }
            RefreshRows();
        };
        _grid.DataSource = _rows;
    }

    private RepositoryRecord? Selected() => _grid.CurrentRow?.DataBoundItem as RepositoryRecord;

    private void RefreshRows(bool scanArchiveFolders = true)
    {
        var selectedId = Selected()?.RepositoryId;
        if (scanArchiveFolders)
        {
            try
            {
                var root = _output.Text.Trim();
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    _db.DiscoverArchiveFolders(root);
            }
            catch (Exception ex)
            {
                AppendLog("Archive folder scan warning: " + ex.Message);
            }
        }

        var records = _db.GetRepositories(_search.Text.Trim());
        IOrderedEnumerable<RepositoryRecord> sorted = _sortColumnIndex switch
        {
            0 => _sortAscending ? records.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase) : records.OrderByDescending(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            1 => _sortAscending ? records.OrderBy(r => r.Language, StringComparer.OrdinalIgnoreCase) : records.OrderByDescending(r => r.Language, StringComparer.OrdinalIgnoreCase),
            2 => _sortAscending ? records.OrderBy(r => r.Stars) : records.OrderByDescending(r => r.Stars),
            3 => _sortAscending ? records.OrderBy(r => r.Status, StringComparer.OrdinalIgnoreCase) : records.OrderByDescending(r => r.Status, StringComparer.OrdinalIgnoreCase),
            4 => _sortAscending ? records.OrderBy(r => r.LastUpdatedUtc, StringComparer.OrdinalIgnoreCase) : records.OrderByDescending(r => r.LastUpdatedUtc, StringComparer.OrdinalIgnoreCase),
            _ => records.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
        };

        _rows.RaiseListChangedEvents = false;
        _rows.Clear();
        foreach (var item in sorted) _rows.Add(item);
        _rows.RaiseListChangedEvents = true;
        _rows.ResetBindings();
        for (int i = 0; i < _grid.Columns.Count; i++)
            _grid.Columns[i].HeaderCell.SortGlyphDirection = i == _sortColumnIndex
                ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;

        if (selectedId != null)
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].RepositoryId == selectedId) { _grid.Rows[i].Selected = true; _grid.CurrentCell = _grid.Rows[i].Cells[0]; break; }
        ShowSelectedDetails();
    }

    private void ShowSelectedDetails()
    {
        var r=Selected(); if(r==null){_details.Clear();return;}
        _details.Text=$"Name: {r.FullName}{Environment.NewLine}URL: {r.Url}{Environment.NewLine}Description: {r.Description}{Environment.NewLine}Language: {r.Language}{Environment.NewLine}Stars: {r.Stars:N0}{Environment.NewLine}Forks: {r.Forks:N0}{Environment.NewLine}Default branch: {r.DefaultBranch}{Environment.NewLine}Status: {r.Status}{Environment.NewLine}Local path: {r.LocalPath}{Environment.NewLine}{Environment.NewLine}{_db.GetDetailsSummary(r.RepositoryId)}";
    }

    private void AddRepository()
    {
        if(!Uri.TryCreate(_url.Text.Trim(),UriKind.Absolute,out var uri)||uri.Host!="github.com"){MessageBox.Show(this,"Enter a valid https://github.com/owner/repository URL.","Invalid URL");return;}
        var p=uri.AbsolutePath.Trim('/').Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(p.Length<2){MessageBox.Show(this,"The URL must include an owner and repository name.");return;}
        var name=$"{p[0]}/{p[1].Replace(".git","")}";
        var r=new RepositoryRecord{RepositoryId="pending:"+name.ToLowerInvariant(),FullName=name,Url=$"https://github.com/{name}",Status="Registered"};
        try{_db.UpsertRepository(r);_db.LogEvent(r.RepositoryId,"Registered","Repository registered from URL.");RefreshRows();AppendLog($"Registered {name}");}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Could not add repository");}
    }

    private void UpdateUrlAvailabilityStatus()
    {
        var url = _url.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) ||
            !TryGetRepositoryFolder(url, _output.Text.Trim(), out var target))
            return;

        if (Directory.Exists(target) &&
            (File.Exists(Path.Combine(target, "source.zip")) || Directory.Exists(Path.Combine(target, "source"))))
            _status.Text = $"Local copy found: {Path.GetFileName(target)}. Download Source will ask before replacing it.";
        else
            _status.Text = "No local source copy found for this repository. Download Source can download it.";
    }

    private static bool TryGetRepositoryFolder(string url, string outputRoot, out string target)
    {
        target = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        var folderName = $"{owner}_{repo}";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            folderName = folderName.Replace(invalid, '_');
        target = Path.Combine(outputRoot, folderName);
        return true;
    }

    private async Task DownloadSelectedOrUrlAsync()
    {
        var url = _url.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) && Selected() is { } selected) url = selected.Url;
        if (!TryGetRepositoryFolder(url, _output.Text.Trim(), out var existingTarget))
        {
            MessageBox.Show(this, "Enter a valid repository URL in the form https://github.com/owner/repository.", "Invalid URL");
            return;
        }

        if (Directory.Exists(existingTarget) &&
            (File.Exists(Path.Combine(existingTarget, "source.zip")) || Directory.Exists(Path.Combine(existingTarget, "source"))))
        {
            var answer = MessageBox.Show(this,
                $"This repository already appears to exist locally:{Environment.NewLine}{existingTarget}{Environment.NewLine}{Environment.NewLine}Choose Yes to download again and replace the source files, or No to keep the existing files.",
                "Repository already exists", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                AppendLog($"Skipped download because the repository already exists locally: {existingTarget}");
                RefreshRows();
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(_output.Text.Trim());
            _cts = new CancellationTokenSource();
            SetOperationButtons(false);
            _cancelButton.Enabled = true;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Minimum = 0;
            _progress.Maximum = GitHubArchiveService.SourceDownloadSteps;
            _progress.Value = 0;
            IProgress<string> progress = new Progress<string>(m => { _status.Text = $"{_progress.Value}/{_progress.Maximum} download stages — {m}"; AppendLog(m); });
            Action<int> stepProgress = step =>
            {
                _progress.Value = Math.Clamp(step, _progress.Minimum, _progress.Maximum);
                _status.Text = $"{_progress.Value}/{_progress.Maximum} download stages completed";
            };
            await _archive.DownloadSourceAsync(url, _output.Text.Trim(), _token.Text, progress, _cts.Token, stepProgress);
            _progress.Value = _progress.Maximum;
            _status.Text = $"{_progress.Maximum}/{_progress.Maximum} download stages completed";
            MessageBox.Show(this, "Source download completed. The ZIP and extracted source files were saved under the output folder.", "Download Source");
        }
        catch (OperationCanceledException) { AppendLog("Download cancelled."); _status.Text = "Cancelled"; }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks;
            SetOperationButtons(true); _cancelButton.Enabled = false; _cts?.Dispose(); _cts = null; RefreshRows();
        }
    }

    private async Task DownloadUserRepositoriesAsync()
    {
        var profileUrl = _url.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileUrl))
        {
            MessageBox.Show(this, "Enter a GitHub user profile URL, for example https://github.com/morluto.", "User profile required");
            return;
        }
        try
        {
            Directory.CreateDirectory(_output.Text.Trim());
            _cts = new CancellationTokenSource();
            SetOperationButtons(false);
            _cancelButton.Enabled = true;
            _progress.Style = ProgressBarStyle.Marquee;
            IProgress<string> listingProgress = new Progress<string>(m => { _status.Text = m; AppendLog(m); });
            var urls = await _archive.GetUserRepositoryUrlsAsync(profileUrl, _token.Text, listingProgress, _cts.Token);
            if (urls.Count == 0)
            {
                MessageBox.Show(this, "No public repositories were found for this user.", "Nothing to archive");
                return;
            }
            var answer = MessageBox.Show(this, $"Found {urls.Count} public repositories. Archive them sequentially using the same workflow as a single repository?{Environment.NewLine}{Environment.NewLine}This downloads public source archives directly from GitHub and does not use the GitHub REST API quota.", "Download user repositories", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { AppendLog("User repository download cancelled before starting."); return; }
            var completed = 0;
            var skipped = 0;
            var processed = 0;
            var failed = new List<string>();
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Minimum = 0;
            var stepsPerRepository = GitHubArchiveService.SourceDownloadSteps;
            _progress.Maximum = Math.Max(1, urls.Count * stepsPerRepository);
            _progress.Value = 0;
            _status.Text = $"0/{urls.Count} repositories processed — 0/{_progress.Maximum} download stages";
            foreach (var repositoryUrl in urls)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var name = new Uri(repositoryUrl).AbsolutePath.Trim('/');
                var repositoryBaseStep = processed * stepsPerRepository;
                IProgress<string> progress = new Progress<string>(m =>
                {
                    _status.Text = $"{processed}/{urls.Count} repositories processed — {_progress.Value}/{_progress.Maximum} stages — {m}";
                    AppendLog(m);
                });
                Action<int> stepProgress = step =>
                {
                    _progress.Value = Math.Clamp(repositoryBaseStep + step, _progress.Minimum, _progress.Maximum);
                    _status.Text = $"{processed}/{urls.Count} repositories processed — {_progress.Value}/{_progress.Maximum} stages completed";
                };
                try
                {
                    if (TryGetRepositoryFolder(repositoryUrl, _output.Text.Trim(), out var existingTarget) &&
                        Directory.Exists(existingTarget) &&
                        (File.Exists(Path.Combine(existingTarget, "source.zip")) || Directory.Exists(Path.Combine(existingTarget, "source"))))
                    {
                        skipped++;
                        AppendLog($"SKIPPED {name}: a local source archive already exists at {existingTarget}");
                    }
                    else
                    {
                        progress.Report($"Downloading {name} ({processed + 1}/{urls.Count})...");
                        await _archive.DownloadSourceAsync(repositoryUrl, _output.Text.Trim(), _token.Text, progress, _cts.Token, stepProgress);
                        completed++;
                        await Task.Delay(300, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add(name); AppendLog($"FAILED {name}: {ex.Message}"); }

                processed++;
                _progress.Value = Math.Min(_progress.Maximum, processed * stepsPerRepository);
                _status.Text = $"{processed}/{urls.Count} repositories processed — {_progress.Value}/{_progress.Maximum} stages completed";
            }
            var summary = $"User repository download finished.{Environment.NewLine}Downloaded: {completed}{Environment.NewLine}Skipped (already present): {skipped}{Environment.NewLine}Failed: {failed.Count}";
            if (failed.Count > 0) summary += Environment.NewLine + Environment.NewLine + "Failed repositories:" + Environment.NewLine + string.Join(Environment.NewLine, failed);
            _status.Text = $"User download complete: {completed} successful, {failed.Count} failed";
            AppendLog(summary.Replace(Environment.NewLine, " | "));
            MessageBox.Show(this, summary, "Download user repositories", MessageBoxButtons.OK, failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { AppendLog("Bulk user download cancelled."); _status.Text = "Cancelled"; }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "User download failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks;
            SetOperationButtons(true); _cancelButton.Enabled = false; _cts?.Dispose(); _cts = null; RefreshRows();
        }
    }

    private void SetOperationButtons(bool enabled)
    {
        _downloadButton.Enabled = enabled;
        _archiveButton.Enabled = enabled;
        _userDownloadButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
    }

    private async Task RefreshListAsync()
    {
        try
        {
            Directory.CreateDirectory(_output.Text.Trim());
            RefreshRows();
            var records = _db.GetRepositories();
            if (records.Count == 0)
            {
                AppendLog("Refresh List: no repositories found in the selected archive folder or catalog.");
                return;
            }

            _cts = new CancellationTokenSource();
            SetOperationButtons(false);
            _cancelButton.Enabled = true;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Minimum = 0;
            _progress.Maximum = Math.Max(1, records.Count);
            _progress.Value = 0;
            var completedIndexes = new HashSet<int>();
            IProgress<string> progress = new Progress<string>(message =>
            {
                AppendLog(message);
                _status.Text = message;
                var closeBracket = message.IndexOf(']');
                var slash = message.IndexOf('/');
                if (message.StartsWith("[", StringComparison.Ordinal) && slash > 1 && closeBracket > slash &&
                    int.TryParse(message.AsSpan(1, slash - 1), out var index) && completedIndexes.Add(index))
                    _progress.Value = Math.Min(_progress.Maximum, completedIndexes.Count);
            });

            var updated = await _archive.RefreshPublicMetadataAsync(records, progress, _cts.Token);
            _status.Text = $"Refresh complete: {updated}/{records.Count} repositories updated";
            AppendLog($"Refresh List completed. Existing folder discovery was preserved; public metadata refreshed for {updated}/{records.Count} repositories.");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Metadata refresh cancelled";
            AppendLog("Metadata refresh cancelled. The repository list and discovered folders remain available.");
        }
        catch (Exception ex)
        {
            AppendLog("Refresh List error: " + ex);
            MessageBox.Show(this, ex.Message, "Refresh List failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = 0;
            SetOperationButtons(true);
            _cancelButton.Enabled = false;
            _cts?.Dispose();
            _cts = null;
            RefreshRows();
        }
    }

    private async Task ArchiveSelectedOrUrlAsync()
    {
        var url=_url.Text.Trim(); if(string.IsNullOrWhiteSpace(url)&&Selected() is{} selected)url=selected.Url;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Host!="github.com"){MessageBox.Show(this,"Enter a GitHub repository URL or select a repository.");return;}
        try{
            Directory.CreateDirectory(_output.Text.Trim());_cts=new CancellationTokenSource();SetOperationButtons(false);_cancelButton.Enabled=true;
            _progress.Style=ProgressBarStyle.Blocks;_progress.Minimum=0;_progress.Maximum=1;_progress.Value=0;
            IProgress<string> progress=new Progress<string>(m=>{_status.Text=$"0/1 repositories processed — {m}";AppendLog(m);});
            var r=await _archive.ArchiveAsync(url,_output.Text.Trim(),_token.Text,progress,_cts.Token);
            _progress.Value=1;_status.Text="1/1 repositories processed";
            AppendLog($"Archived {r.FullName} to {r.LocalPath}");MessageBox.Show(this,$"Archive completed.{Environment.NewLine}{r.LocalPath}","GitHub Repository Explorer"); 
        }catch(OperationCanceledException){AppendLog("Operation cancelled.");_status.Text="Cancelled";}
        catch(Exception ex){AppendLog("ERROR: "+ex.Message);MessageBox.Show(this,ex.Message,"Archive failed",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        finally{_progress.Style=ProgressBarStyle.Blocks;_progress.Value=0;SetOperationButtons(true);_cancelButton.Enabled=false;_cts?.Dispose();_cts=null;RefreshRows();}
    }

    private void BrowseOutput()
    {
        using var d=new FolderBrowserDialog{SelectedPath=Directory.Exists(_output.Text)?_output.Text:_defaultOutput,Description="Choose where downloaded source files and repository archives are stored"};
        if(d.ShowDialog(this)==DialogResult.OK)_output.Text=d.SelectedPath;
    }

    private void OpenSelected()
    {
        var r=Selected();if(r==null||string.IsNullOrWhiteSpace(r.LocalPath)||!Directory.Exists(r.LocalPath)){MessageBox.Show(this,"Select an archived repository first.");return;}
        Process.Start(new ProcessStartInfo("explorer.exe",r.LocalPath){UseShellExecute=true});
    }

    private void VerifySelected()
    {
        var r=Selected();if(r==null){MessageBox.Show(this,"Select a repository first.");return;}
        var exists=Directory.Exists(r.LocalPath);var zip=exists&&File.Exists(Path.Combine(r.LocalPath,"source.zip"));
        var report=$"Repository: {r.FullName}{Environment.NewLine}Archive folder exists: {exists}{Environment.NewLine}Source ZIP exists: {zip}";
        AppendLog(report);MessageBox.Show(this,report,"Archive verification",MessageBoxButtons.OK,exists&&zip?MessageBoxIcon.Information:MessageBoxIcon.Warning);
    }

    private void ExportWebsite()
    {
        var r=Selected();if(r==null||string.IsNullOrWhiteSpace(r.LocalPath)||!Directory.Exists(r.LocalPath)){MessageBox.Show(this,"Select an archived repository first.");return;}
        var md=Path.Combine(r.LocalPath,"README.md");var readme=File.Exists(md)?File.ReadAllText(md):"README was not available in the archive.";
        var html=$"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{System.Net.WebUtility.HtmlEncode(r.FullName)}</title><style>body{{font:16px/1.6 system-ui;max-width:1000px;margin:2rem auto;padding:0 1rem}}pre{{white-space:pre-wrap;overflow-wrap:anywhere;background:#f4f4f4;padding:1rem}}</style></head><body><h1>{System.Net.WebUtility.HtmlEncode(r.FullName)}</h1><p>{System.Net.WebUtility.HtmlEncode(r.Description)}</p><p>Stars: {r.Stars:N0} · Forks: {r.Forks:N0} · Language: {System.Net.WebUtility.HtmlEncode(r.Language)}</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(r.Url)}\">Original GitHub repository</a></p><h2>README (Markdown source)</h2><pre>{System.Net.WebUtility.HtmlEncode(readme)}</pre><p>Source files are in the adjacent source directory.</p></body></html>";
        var path=Path.Combine(r.LocalPath,"index.html");File.WriteAllText(path,html,Encoding.UTF8);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});AppendLog("Exported local website: "+path);
    }

    private void RemoveSelected()
    {
        var r=Selected();if(r==null)return;
        if(MessageBox.Show(this,$"Remove {r.FullName} from the catalog? Local files will not be deleted.","Confirm removal",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        using var c=new SqliteConnection($"Data Source={_db.CatalogPath}");c.Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM Repositories WHERE RepositoryId=@id";cmd.Parameters.AddWithValue("@id",r.RepositoryId);cmd.ExecuteNonQuery();
        AppendLog($"Removed catalog entry for {r.FullName}; local archive retained.");RefreshRows();
    }

    private void ChangeFont(int delta)
    {
        _fontSize=Math.Clamp(_fontSize+delta,7f,24f);ApplyFont(this,new Font("Segoe UI",_fontSize));
        _grid.RowTemplate.Height=Math.Max(22,(int)Math.Ceiling(_fontSize*2.4));_grid.ColumnHeadersHeight=Math.Max(26,(int)Math.Ceiling(_fontSize*2.8));
        _status.Text=$"Font: {_fontSize:0.#} pt | Catalog: {_db.CatalogPath} | Details: {_db.DetailsPath}";
    }

    private static void ApplyFont(Control parent,Font font){parent.Font=font;foreach(Control child in parent.Controls)ApplyFont(child,font);}
    private void AppendLog(string message){if(InvokeRequired){BeginInvoke(new Action(()=>AppendLog(message)));return;}_log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");}
}