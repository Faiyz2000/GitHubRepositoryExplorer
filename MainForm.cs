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
    private readonly Button _downloadButton = new(), _archiveButton = new(), _userDownloadButton = new(), _cancelButton = new();
    private CancellationTokenSource? _cts;
    private float _fontSize = 9.5f;
    private readonly string _defaultOutput = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitHubArchives");

    public MainForm()
    {
        _archive = new GitHubArchiveService(_db);
        Text = "GitHub Repository Explorer — v0.4.0";
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
        actions.Controls.Add(MakeButton("Refresh List", (_, _) => RefreshRows()));
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
        _search.TextChanged += (_, _) => RefreshRows(); left.Controls.Add(_search, 0, 0);
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
        _grid.DataSource = _rows;
    }

    private RepositoryRecord? Selected() => _grid.CurrentRow?.DataBoundItem as RepositoryRecord;

    private void RefreshRows()
    {
        var selectedId = Selected()?.RepositoryId;
        _rows.Clear();
        foreach (var item in _db.GetRepositories(_search.Text.Trim())) _rows.Add(item);
        if (selectedId != null)
            for (int i=0; i<_rows.Count; i++) if (_rows[i].RepositoryId == selectedId) { _grid.Rows[i].Selected = true; break; }
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

    private async Task DownloadSelectedOrUrlAsync()
    {
        var url = _url.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) && Selected() is { } selected) url = selected.Url;
        try
        {
            Directory.CreateDirectory(_output.Text.Trim());
            _cts = new CancellationTokenSource();
            SetOperationButtons(false);
            _cancelButton.Enabled = true;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Minimum = 0;
            _progress.Maximum = 1;
            _progress.Value = 0;
            IProgress<string> progress = new Progress<string>(m => { _status.Text = $"0/1 repositories completed — {m}"; AppendLog(m); });
            await _archive.DownloadSourceAsync(url, _output.Text.Trim(), _token.Text, progress, _cts.Token);
            _progress.Value = 1;
            _status.Text = "1/1 repositories completed";
            MessageBox.Show(this, "Source download completed. The ZIP and extracted source files were saved under the output folder.", "Download Source");
        }
        catch (OperationCanceledException) { AppendLog("Download cancelled."); _status.Text = "Cancelled"; }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks; _progress.Value = 0;
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
            var answer = MessageBox.Show(this, $"Found {urls.Count} public repositories. Archive them sequentially using the same workflow as a single repository?{Environment.NewLine}{Environment.NewLine}This may take a while and will use GitHub API rate limits.", "Download user repositories", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { AppendLog("User repository download cancelled before starting."); return; }
            var completed = 0;
            var processed = 0;
            var failed = new List<string>();
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Minimum = 0;
            _progress.Maximum = urls.Count;
            _progress.Value = 0;
            IProgress<string> progress = new Progress<string>(m =>
            {
                _status.Text = $"{processed}/{urls.Count} repositories completed — {m}";
                AppendLog(m);
            });
            _status.Text = $"0/{urls.Count} repositories completed";
            foreach (var repositoryUrl in urls)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var name = new Uri(repositoryUrl).AbsolutePath.Trim('/');
                try
                {
                    progress.Report($"Downloading {name} ({processed + 1}/{urls.Count})...");
                    await _archive.DownloadSourceAsync(repositoryUrl, _output.Text.Trim(), _token.Text, progress, _cts.Token);
                    completed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add(name); AppendLog($"FAILED {name}: {ex.Message}"); }
                finally
                {
                    processed++;
                    _progress.Value = processed;
                    _status.Text = $"{processed}/{urls.Count} repositories completed";
                }
            }
            var summary = $"User repository download finished.{Environment.NewLine}Successful: {completed}{Environment.NewLine}Failed: {failed.Count}";
            if (failed.Count > 0) summary += Environment.NewLine + Environment.NewLine + "Failed repositories:" + Environment.NewLine + string.Join(Environment.NewLine, failed);
            _status.Text = $"User download complete: {completed} successful, {failed.Count} failed";
            AppendLog(summary.Replace(Environment.NewLine, " | "));
            MessageBox.Show(this, summary, "Download user repositories", MessageBoxButtons.OK, failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { AppendLog("Bulk user download cancelled."); _status.Text = "Cancelled"; }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "User download failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks; _progress.Value = 0;
            SetOperationButtons(true); _cancelButton.Enabled = false; _cts?.Dispose(); _cts = null; RefreshRows();
        }
    }

    private void SetOperationButtons(bool enabled)
    {
        _downloadButton.Enabled = enabled;
        _archiveButton.Enabled = enabled;
        _userDownloadButton.Enabled = enabled;
    }

    private async Task ArchiveSelectedOrUrlAsync()
    {
        var url=_url.Text.Trim(); if(string.IsNullOrWhiteSpace(url)&&Selected() is{} selected)url=selected.Url;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Host!="github.com"){MessageBox.Show(this,"Enter a GitHub repository URL or select a repository.");return;}
        try{
            Directory.CreateDirectory(_output.Text.Trim());_cts=new CancellationTokenSource();SetOperationButtons(false);_cancelButton.Enabled=true;
            _progress.Style=ProgressBarStyle.Blocks;_progress.Minimum=0;_progress.Maximum=1;_progress.Value=0;
            IProgress<string> progress=new Progress<string>(m=>{_status.Text=$"0/1 repositories completed — {m}";AppendLog(m);});
            var r=await _archive.ArchiveAsync(url,_output.Text.Trim(),_token.Text,progress,_cts.Token);
            _progress.Value=1;_status.Text="1/1 repositories completed";
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