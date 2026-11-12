using System.Diagnostics;

namespace GitHubRepositoryExplorer;

public sealed class RepositoryInfoForm : Form
{
    private readonly WebBrowser _browser = new();

    public RepositoryInfoForm(string htmlPath, string repositoryName)
    {
        Text = $"Repository Information — {repositoryName}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(800, 600);
        Size = new Size(1200, 800);
        Font = new Font("Segoe UI", 9.5f);

        _browser.Dock = DockStyle.Fill;
        _browser.ScriptErrorsSuppressed = true;
        _browser.AllowNavigation = true;
        _browser.Navigating += BrowserNavigating;
        Controls.Add(_browser);

        Shown += (_, _) => _browser.Navigate(htmlPath);
    }

    private void BrowserNavigating(object? sender, WebBrowserNavigatingEventArgs e)
    {
        if (e.Url.Scheme is "http" or "https")
        {
            e.Cancel = true;
            try { Process.Start(new ProcessStartInfo(e.Url.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        }
    }
}
