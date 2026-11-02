using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DepupCode.Services;

namespace DepupCode.Views;

/// <summary>Source Control panel that lives in the sidebar.</summary>
public partial class GitView : UserControl
{
    private string? _folder;
    private readonly Action<GitChange> _openDiff;
    private readonly Func<string> _getToken;
    private readonly Action _changed;
    private bool _suppress;

    public GitView(Action<GitChange> openDiff, Func<string> getToken, Action changed)
    {
        InitializeComponent();
        _openDiff = openDiff;
        _getToken = getToken;
        _changed = changed;
        MessageBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Commit_Click(this, new RoutedEventArgs()); }
        };
    }

    public void SetRoot(string? folder)
    {
        _folder = folder;
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        var info = GitService.ReadPanel(_folder);
        _suppress = true;
        ChangesList.ItemsSource = info.Changes;
        _suppress = false;
        IgnoredList.ItemsSource = info.Ignored.Take(8).ToList();
        IgnoredHeader.Visibility = info.Ignored.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IgnoredCount.Text = info.Ignored.Count > 8 ? $"@gitignore · +{info.Ignored.Count - 8}" : "@gitignore";
        ChangesCount.Text = info.Changes.Count.ToString();
        BranchText.Text = "⑂ Branch: " + info.Branch;
        SyncText.Text = info.IsRepository ? $"↑{info.Ahead} ↓{info.Behind}" : "";
        EmptyText.Visibility = info.IsRepository && info.Changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommitButton.IsEnabled = info.IsRepository;
        CloudControlGate.Refresh(this);
        StatusText.Text = "";
        var hasFolder = !string.IsNullOrWhiteSpace(_folder);
        NoRepoPanel.Visibility = info.IsRepository ? Visibility.Collapsed : Visibility.Visible;
        InitButton.Visibility = PublishButton.Visibility = PrivateCheck.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
        if (!hasFolder) NoRepoText.Text = "Belum ada folder yang dibuka. Buka folder project dari menu File atau panel Explorer untuk memakai source control.";
    }

    private void Init_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GitService.Initialize(_folder);
            NoRepoStatus.Text = "";
            Refresh();
            _changed();
        }
        catch (Exception ex) { NoRepoStatus.Text = ex.Message; }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        var folder = _folder;
        var token = _getToken();
        var isPrivate = PrivateCheck.IsChecked == true;
        InitButton.IsEnabled = PublishButton.IsEnabled = false;
        NoRepoStatus.Text = "Membuat repository GitHub dan mengirim kode…";
        try
        {
            var url = await GitService.PublishToGitHubAsync(folder, token, isPrivate);
            Refresh();
            _changed();
            StatusText.Text = "Dipublikasikan: " + url;
        }
        catch (Exception ex) { NoRepoStatus.Text = ex.Message; }
        finally
        {
            InitButton.IsEnabled = PublishButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
        }
    }

    private void Message_Changed(object sender, TextChangedEventArgs e) =>
        MessageHint.Visibility = MessageBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Change_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (ChangesList.SelectedItem is GitChange change) _openDiff(change);
    }

    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        var message = MessageBox.Text;
        var folder = _folder;
        var token = _getToken();
        CommitButton.IsEnabled = false;
        StatusText.Text = "Commit & push berjalan…";
        try
        {
            var result = await Task.Run(() => GitService.CommitAndPush(folder, message, token));
            StatusText.Text = result;
            MessageBox.Clear();
            Refresh();
            _changed();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { CommitButton.IsEnabled = true; }
    }
}
