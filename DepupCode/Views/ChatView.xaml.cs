using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DepupCode.Models;
using DepupCode.Services;

namespace DepupCode.Views;

public partial class ChatView : UserControl
{
    private readonly SettingsService _settings;
    private readonly AiChatStore _store = new();
    private readonly ObservableCollection<AiChatSession> _sessions;
    private readonly Dictionary<string, string> _customAgentInstructions = new(StringComparer.OrdinalIgnoreCase);
    private AiChatSession? _activeSession;
    private string? _workspaceRoot;
    private CancellationTokenSource? _sending;
    private ComboBoxItem? _lastModeItem;
    private bool _selectingMode;

    public event EventHandler? CloseRequested;
    public event Action<string, string>? FileWritten;
    public event Action<string>? FileDeleted;
    public Func<string, bool>? CanWriteFile { get; set; }
    public IReadOnlyList<AiExternalTool> ExternalTools { get; set; } = [];

    public ChatView() : this(new SettingsService()) { }

    public ChatView(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadCustomAgents();
        _lastModeItem = ModeCombo.SelectedItem as ComboBoxItem;
        _sessions = new ObservableCollection<AiChatSession>(_store.Load());
        _activeSession = _sessions.FirstOrDefault();
        if (_activeSession is null) CreateSession();
        else ActivateSession(_activeSession, persist: false);
        UpdateConfigurationStatus();
        UpdateSkillsStatus();
    }

    public void SetWorkspace(string? folder)
    {
        _workspaceRoot = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? Path.GetFullPath(folder) : null;
        WorkspaceText.Text = _workspaceRoot is null ? "Workspace belum dibuka" : "⌂ " + Path.GetFileName(_workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        WorkspaceText.ToolTip = _workspaceRoot ?? "Buka folder workspace untuk memakai mode Agent.";
        WorkspaceText.Foreground = _workspaceRoot is null ? GetBrush("ThemeDimBrush") : GetBrush("ThemeAccentSoftBrush");
        UpdateSkillsStatus();
    }

    private void UpdateSkillsStatus()
    {
        var skills = new OpenCodeSkillCatalog(_workspaceRoot);
        SkillsButton.Content = $"Skills {skills.Count}";
        SkillsButton.ToolTip = skills.Count == 0
            ? "Buat SKILL.md dalam .opencode/skills, .claude/skills, atau .agents/skills"
            : skills.DescribeAvailableSkills();
    }

    private void Skills_Click(object sender, RoutedEventArgs e)
    {
        var skills = new OpenCodeSkillCatalog(_workspaceRoot);
        var details = skills.Count == 0
            ? "Belum ada skill. Tambahkan file SKILL.md di:\n.opencode/skills/<nama>/SKILL.md\n.claude/skills/<nama>/SKILL.md\n.agents/skills/<nama>/SKILL.md"
            : skills.DescribeAvailableSkills();
        MessageBox.Show(Window.GetWindow(this), details, $"Skills terdeteksi ({skills.Count})",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CreateSession()
    {
        // Tombol + selalu berarti satu sesi baru. Jika sesi aktif masih kosong, pakai ulang
        // sesi itu agar daftar riwayat tidak dipenuhi banyak entri "Chat baru".
        if (_activeSession is not null && IsEmptySession(_activeSession))
        {
            CleanupEmptySessions(_activeSession);
            PromptBox.Clear();
            PersistSessions();
            RenderMessages();
            UpdateSessionTitle();
            PromptBox.Focus();
            return;
        }

        var session = new AiChatSession();
        _sessions.Insert(0, session);
        _activeSession = session;
        CleanupEmptySessions(session);
        PromptBox.Clear();
        PersistSessions();
        RenderMessages();
        UpdateSessionTitle();
        PromptBox.Focus();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e) => CreateSession();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void CancelActiveRequest() => _sending?.Cancel();

    private void ActivateSession(AiChatSession session, bool persist = true)
    {
        _activeSession = session;
        CleanupEmptySessions(session);
        if (persist) PersistSessions();
        else UpdateSessionTitle();
        RenderMessages();
    }

    private void SessionHistory_Click(object sender, RoutedEventArgs e)
    {
        ModeCombo.IsDropDownOpen = false;
        var menu = new ContextMenu
        {
            PlacementTarget = SessionHistoryButton,
            Placement = PlacementMode.Bottom,
            Background = GetBrush("ThemePopupBrush"),
            BorderBrush = GetBrush("ThemeBorderLightBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            HasDropShadow = true
        };
        var menuItemStyle = TryFindResource("SubMenuItemStyle") as Style;
        foreach (var session in _sessions.OrderByDescending(item => item.UpdatedUtc))
        {
            var active = ReferenceEquals(session, _activeSession);
            var item = new MenuItem
            {
                Header = (active ? "✓  " : "     ") + session.Title,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = session.Turns.Any(turn => turn.Role is "user" or "assistant")
                    ? $"{session.Turns.Count(turn => turn.Role is "user" or "assistant")} pesan"
                    : "Sesi kosong"
            };
            if (menuItemStyle is not null) item.Style = menuItemStyle;
            var target = session;
            item.Click += (_, _) => ActivateSession(target);
            menu.Items.Add(item);
        }
        var separatorTemplate = new ControlTemplate(typeof(Separator));
        var separatorLine = new FrameworkElementFactory(typeof(Border));
        separatorLine.SetValue(Border.HeightProperty, 1.0);
        separatorLine.SetValue(Border.BackgroundProperty, GetBrush("ThemeSeparatorBrush"));
        separatorLine.SetValue(Border.MarginProperty, new Thickness(0, 3, 0, 3));
        separatorTemplate.VisualTree = separatorLine;
        menu.Items.Add(new Separator { Template = separatorTemplate });
        var newChat = new MenuItem { Header = "＋  Sesi chat baru" };
        if (menuItemStyle is not null) newChat.Style = menuItemStyle;
        newChat.Click += (_, _) => CreateSession();
        menu.Items.Add(newChat);
        menu.IsOpen = true;
    }

    private static bool IsEmptySession(AiChatSession session) =>
        !session.Turns.Any(turn => turn.Role is "user" or "assistant") &&
        (session.Artifacts is null || session.Artifacts.Count == 0);

    private void CleanupEmptySessions(AiChatSession keep)
    {
        foreach (var empty in _sessions.Where(item => !ReferenceEquals(item, keep) && IsEmptySession(item)).ToList())
            _sessions.Remove(empty);
    }

    private void UpdateSessionTitle()
    {
        var title = string.IsNullOrWhiteSpace(_activeSession?.Title) ? "Chat baru" : _activeSession!.Title;
        SessionTitleText.Text = title;
        SessionTitleText.ToolTip = title;
        SessionHistoryButton.ToolTip = _sessions.Count > 1 ? $"Riwayat sesi chat ({_sessions.Count})" : "Riwayat sesi chat";
    }

    private void RenderMessages()
    {
        MessagesPanel.Children.Clear();
        if (_activeSession is null || !_activeSession.Turns.Any(turn => turn.Role is "user" or "assistant"))
        {
            var welcome = new StackPanel { Margin = new Thickness(3, 24, 3, 12) };
            welcome.Children.Add(new TextBlock
            {
                Text = "Apa yang ingin kamu kerjakan?", FontSize = 17, FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("ThemePrimaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });
            welcome.Children.Add(new TextBlock
            {
                Text = "AI Agent dapat memahami workspace, menjelaskan kode, menyusun rencana, dan membantu mengubah file.",
                FontSize = 11, Foreground = GetBrush("ThemeMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
            });
            foreach (var suggestion in new[] { "Jelaskan struktur project ini", "Cari bug di workspace", "Audit keamanan proyek", "Buat rencana untuk fitur baru" })
            {
                var chip = new Button
                {
                    Content = suggestion, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 3, 0, 3),
                    Background = GetBrush("ThemeSurfaceBrush"), BorderBrush = GetBrush("ThemeBorderBrush"),
                    Foreground = GetBrush("ThemeSecondaryBrush"), FontSize = 10.5, Cursor = Cursors.Hand
                };
                chip.Click += (_, _) =>
                {
                    PromptBox.Text = suggestion;
                    if (suggestion.StartsWith("Audit keamanan", StringComparison.OrdinalIgnoreCase))
                        ModeCombo.SelectedItem = ModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "SecurityAudit");
                    PromptBox.Focus();
                    PromptBox.CaretIndex = PromptBox.Text.Length;
                };
                welcome.Children.Add(chip);
            }
            MessagesPanel.Children.Add(welcome);
            return;
        }

        foreach (var turn in _activeSession.Turns.Where(turn => turn.Role is "user" or "assistant"))
        {
            var isUser = turn.Role == "user";
            var bubble = new Border
            {
                MaxWidth = 520,
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = GetBrush(isUser ? "ThemeActiveSurfaceBrush" : "ThemePanelBrush"),
                BorderBrush = GetBrush(isUser ? "ThemeBorderLightBrush" : "ThemeSeparatorBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 9), Margin = new Thickness(0, 4, 0, 7)
            };
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = isUser ? "KAMU" : "DEPUP AI", FontSize = 8.5, FontWeight = FontWeights.Bold,
                Foreground = GetBrush(isUser ? "ThemeAccentSoftBrush" : "ThemeMutedBrush"), Margin = new Thickness(0, 0, 0, 5)
            });
            body.Children.Add(new TextBlock
            {
                Text = turn.Content, TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
                Foreground = GetBrush("ThemePrimaryBrush")
            });
            bubble.Child = body;
            MessagesPanel.Children.Add(bubble);
        }
        RenderArtifacts(_activeSession.Artifacts ?? []);
        MessagesScroll.ScrollToEnd();
    }

    private void RenderArtifacts(IEnumerable<AiChatArtifact> artifacts)
    {
        var recent = artifacts.OrderBy(artifact => artifact.CreatedUtc).TakeLast(8).ToList();
        if (recent.Count == 0) return;
        var section = new Border
        {
            Background = GetBrush("ThemeSurfaceBrush"), BorderBrush = GetBrush("ThemeBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9), Margin = new Thickness(0, 8, 0, 4)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"HASIL KERJA · {artifacts.Count()}", FontSize = 8.5, FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ThemeAccentSoftBrush"), Margin = new Thickness(0, 0, 0, 5)
        });
        foreach (var artifact in recent)
        {
            var item = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            item.Children.Add(new TextBlock
            {
                Text = $"{artifact.Type} · {artifact.Title}", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("ThemePrimaryBrush"), TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(artifact.Detail))
                item.Children.Add(new TextBlock
                {
                    Text = artifact.Detail, FontSize = 9, Foreground = GetBrush("ThemeMutedBrush"),
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 70
                });
            if (artifact.FilePath is { Length: > 0 } path && File.Exists(path) &&
                Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 520;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    item.Children.Add(new Image
                    {
                        Source = bitmap, MaxWidth = 480, MaxHeight = 150, Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0)
                    });
                }
                catch { }
            }
            var hasRestore = artifact.RollbackBackupPath is { Length: > 0 } backup && File.Exists(backup);
            var hasCreatedFileUndo = artifact.CreatedByAgent && !string.IsNullOrWhiteSpace(artifact.ExpectedFileHash);
            if (_sending is null && (hasRestore || hasCreatedFileUndo) && !string.IsNullOrWhiteSpace(artifact.WorkspaceRelativePath))
            {
                var restore = new Button
                {
                    Content = artifact.CreatedByAgent ? "Batalkan pembuatan file" : "Pulihkan versi sebelumnya",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 5, 0, 0), FontSize = 9,
                    Background = GetBrush("ThemeInputBrush"), BorderBrush = GetBrush("ThemeBorderBrush"),
                    Foreground = GetBrush("ThemeSecondaryBrush"), Cursor = Cursors.Hand
                };
                restore.Click += async (_, _) => await RestoreArtifactAsync(artifact);
                item.Children.Add(restore);
            }
            stack.Children.Add(item);
        }
        section.Child = stack;
        MessagesPanel.Children.Add(section);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sending is not null)
        {
            _sending.Cancel();
            return;
        }

        await SubmitPromptAsync(PromptBox.Text.Trim());
    }

    public async Task SubmitPromptAsync(string prompt, string? preferredMode = null, string? imageDataUrl = null)
    {
        if (_sending is not null)
        {
            ShowStatusMessage("AI sedang memproses chat lain. Tunggu hingga selesai, lalu coba analisis lagi.", isError: true);
            return;
        }
        prompt = prompt.Trim();
        if (!string.IsNullOrWhiteSpace(preferredMode))
        {
            var preferredItem = ModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), preferredMode, StringComparison.OrdinalIgnoreCase));
            if (preferredItem is not null) ModeCombo.SelectedItem = preferredItem;
        }
        if (prompt.Length == 0 || _activeSession is null) return;
        var mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Agent";
        var configuration = _settings.Load();
        var usesCopilot = string.Equals(configuration.AiProvider, "Copilot", StringComparison.OrdinalIgnoreCase);
        var credentialName = string.Equals(configuration.AiProvider, "Gemini", StringComparison.OrdinalIgnoreCase) ? "gemini" : "ai";
        if (mode is not ("Ask" or "Plan") && string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            ShowStatusMessage("Buka folder workspace sebelum memulai Agent.", isError: true);
            return;
        }
        if (!usesCopilot && string.IsNullOrWhiteSpace(_settings.GetToken(credentialName)) && !IsLocalEndpoint(configuration.AiApiBaseUrl))
        {
            Configure_Click(this, new RoutedEventArgs());
            return;
        }

        PromptBox.Clear();
        var userTurn = new AiChatTurn { Role = "user", Content = prompt, EphemeralImageDataUrl = imageDataUrl };
        _activeSession.Turns.Add(userTurn);
        if (_activeSession.Turns.Count == 1)
            _activeSession.Title = prompt.Length > 38 ? prompt[..38].TrimEnd() + "…" : prompt;
        _activeSession.UpdatedUtc = DateTime.UtcNow;
        var index = _sessions.IndexOf(_activeSession);
        if (index > 0) _sessions.Move(index, 0);
        PersistSessions();
        RenderMessages();

        _sending = new CancellationTokenSource();
        SendButton.Content = "■";
        SendButton.ToolTip = "Hentikan respons";
        ModelText.Text = "Menghubungi AI…";
        PromptBox.IsEnabled = false;
        ModeCombo.IsEnabled = false;
        try
        {
            var agent = new AiAgentService(_settings, _workspaceRoot, ExternalTools);
            agent.FileWritten += (path, content) => FileWritten?.Invoke(path, content);
            await agent.SendAsync(_activeSession, mode, ConfirmActionAsync, description =>
            {
                Dispatcher.Invoke(() => ModelText.Text = description);
            }, GetCurrentCustomInstructions(), _sending.Token);
        }
        catch (OperationCanceledException)
        {
            ShowStatusMessage("Respons dihentikan.");
        }
        catch (Exception ex)
        {
            ShowStatusMessage(ex.Message, isError: true);
        }
        finally
        {
            userTurn.EphemeralImageDataUrl = null;
            if (_activeSession is not null) _activeSession.UpdatedUtc = DateTime.UtcNow;
            PersistSessions();
            RenderMessages();
            _sending.Dispose();
            _sending = null;
            SendButton.Content = "↑";
            SendButton.ToolTip = "Kirim (Enter)";
            PromptBox.IsEnabled = true;
            ModeCombo.IsEnabled = true;
            UpdateConfigurationStatus();
            PromptBox.Focus();
        }
    }

    private void ShowStatusMessage(string text, bool isError = false)
    {
        var box = new Border
        {
            Background = GetBrush(isError ? "ThemeSurfaceBrush" : "ThemeInputBrush"),
            BorderBrush = GetBrush("ThemeBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 0, 8)
        };
        box.Child = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 10.5,
            Foreground = GetBrush(isError ? "#FBBF24" : "ThemeMutedBrush")
        };
        MessagesPanel.Children.Add(box);
        MessagesScroll.ScrollToEnd();
    }

    private async Task<bool> ConfirmActionAsync(AiToolRequest request)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ConfirmActionAsync(request)).Task.Unwrap();
        if ((request.Action is "Buat file" or "Ubah file" or "Pulihkan versi sebelumnya" or "Hapus file yang dibuat AI") &&
            CanWriteFile is not null && !CanWriteFile(request.Target))
            return false;
        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = request.Action,
            Owner = owner,
            Width = 600,
            Height = 510,
            MinWidth = 420,
            MinHeight = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.CanResize,
            Background = GetBrush("ThemeWindowBrush"),
            Foreground = GetBrush("ThemePrimaryBrush")
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = request.Action, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var target = new TextBlock
        {
            Text = request.Target, Foreground = GetBrush("ThemeAccentSoftBrush"), FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(target, 1);
        grid.Children.Add(target);
        var details = new TextBox
        {
            Text = request.Details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 10.5, Padding = new Thickness(9),
            Background = GetBrush("ThemeInputBrush"), Foreground = GetBrush("ThemeSecondaryBrush"), BorderBrush = GetBrush("ThemeBorderBrush")
        };
        Grid.SetRow(details, 2);
        grid.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var deny = new Button { Content = "Tolak", MinWidth = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var approve = new Button { Content = "Setujui", MinWidth = 100, Height = 32, IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        deny.Click += (_, _) => dialog.DialogResult = false;
        approve.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(deny);
        actions.Children.Add(approve);
        Grid.SetRow(actions, 3);
        grid.Children.Add(actions);
        dialog.Content = grid;
        return await Task.FromResult(dialog.ShowDialog() == true);
    }

    private async Task RestoreArtifactAsync(AiChatArtifact artifact)
    {
        if (_sending is not null || string.IsNullOrWhiteSpace(_workspaceRoot)) return;
        var agent = new AiAgentService(_settings, _workspaceRoot);
        agent.FileWritten += (path, content) => FileWritten?.Invoke(path, content);
        agent.FileDeleted += path => FileDeleted?.Invoke(path);
        try
        {
            if (await agent.RestorePreviousVersionAsync(artifact, ConfirmActionAsync))
            {
                PersistSessions();
                RenderMessages();
                ShowStatusMessage($"Versi sebelumnya dipulihkan: {artifact.WorkspaceRelativePath}");
            }
        }
        catch (Exception ex) { ShowStatusMessage("Pemulihan gagal: " + ex.Message, isError: true); }
    }

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendButton_Click(this, new RoutedEventArgs());
        }
    }

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settings.Load();
        var clearApiKey = false;
        var dialog = new Window
        {
            Title = "Pengaturan AI Agent", Owner = Window.GetWindow(this), Width = 510, Height = 630,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow, Background = GetBrush("ThemeWindowBrush"), Foreground = GetBrush("ThemePrimaryBrush")
        };
        var content = new Grid { Margin = new Thickness(22) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(new TextBlock { Text = "Sambungkan model AI", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        var description = new TextBlock
        {
            Text = "Pilih OpenAI/compatible, Google Gemini, atau GitHub Copilot. Copilot memakai autentikasi Copilot CLI atau gh.",
            Foreground = GetBrush("ThemeMutedBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(description, 1); content.Children.Add(description);
        var fields = new StackPanel();
        var providerBox = new ComboBox { Height = 32, Padding = new Thickness(7, 3, 7, 3) };
        providerBox.Items.Add(new ComboBoxItem { Content = "OpenAI / Compatible", Tag = "OpenAI" });
        providerBox.Items.Add(new ComboBoxItem { Content = "Google Gemini", Tag = "Gemini" });
        providerBox.Items.Add(new ComboBoxItem { Content = "GitHub Copilot", Tag = "Copilot" });
        providerBox.SelectedItem = providerBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), settings.AiProvider, StringComparison.OrdinalIgnoreCase)) ?? providerBox.Items[0];
        AddComboField(fields, "PROVIDER", providerBox);
        var languageBox = new ComboBox { Height = 32, Padding = new Thickness(7, 3, 7, 3) };
        languageBox.Items.Add(new ComboBoxItem { Content = "Bahasa Indonesia", Tag = "Indonesian" });
        languageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = "English" });
        languageBox.SelectedItem = languageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), settings.AiLanguage, StringComparison.OrdinalIgnoreCase)) ?? languageBox.Items[0];
        AddComboField(fields, "BAHASA UTAMA AI", languageBox);
        AddField(fields, "URL API", new TextBox { Text = settings.AiApiBaseUrl, Height = 32, Padding = new Thickness(8, 4, 8, 4) });
        var endpointBox = (TextBox)((Border)fields.Children[^1]).Child;
        var endpointLabel = (TextBlock)fields.Children[^2];
        var endpointBorder = (Border)fields.Children[^1];
        AddField(fields, "Model", new TextBox { Text = settings.AiModel, Height = 32, Padding = new Thickness(8, 4, 8, 4) });
        var modelBox = (TextBox)((Border)fields.Children[^1]).Child;
        var keyLabel = new TextBlock { Text = "API KEY", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = GetBrush("ThemeMutedBrush"), Margin = new Thickness(0, 0, 0, 5) };
        fields.Children.Add(keyLabel);
        var keyBox = new PasswordBox { Height = 32, Padding = new Thickness(8, 4, 8, 4), ToolTip = "Kosongkan untuk mempertahankan API key tersimpan" };
        fields.Children.Add(keyBox);
        var keyState = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_settings.GetToken(settings.AiProvider == "Gemini" ? "gemini" : "ai"))
                ? (IsLocalEndpoint(settings.AiApiBaseUrl) ? "Endpoint lokal: API key biasanya tidak diperlukan." : "API key belum diatur.")
                : "API key tersimpan dan dilindungi Windows DPAPI.",
            Foreground = GetBrush("ThemeMutedBrush"), FontSize = 10, Margin = new Thickness(0, 5, 0, 0)
        };
        fields.Children.Add(keyState);
        void UpdateProviderFields(bool applyPreset)
        {
            var provider = (providerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OpenAI";
            var copilot = provider == "Copilot";
            endpointLabel.Visibility = endpointBorder.Visibility = copilot ? Visibility.Collapsed : Visibility.Visible;
            keyLabel.Visibility = keyBox.Visibility = keyState.Visibility = copilot ? Visibility.Collapsed : Visibility.Visible;
            if (applyPreset)
            {
                switch (provider)
                {
                    case "Gemini":
                        endpointBox.Text = "https://generativelanguage.googleapis.com/v1beta/openai";
                        modelBox.Text = "gemini-2.5-flash";
                        keyLabel.Text = "GEMINI API KEY";
                        break;
                    case "Copilot":
                        modelBox.Text = "gpt-5";
                        break;
                    default:
                        endpointBox.Text = "https://api.openai.com/v1";
                        modelBox.Text = "gpt-4.1-mini";
                        keyLabel.Text = "API KEY";
                        break;
                }
            }
            if (provider == "OpenAI") keyLabel.Text = "API KEY";
            var credentialName = provider == "Gemini" ? "gemini" : "ai";
            keyState.Text = string.IsNullOrWhiteSpace(_settings.GetToken(credentialName))
                ? (IsLocalEndpoint(endpointBox.Text) ? "Endpoint lokal: API key biasanya tidak diperlukan." : "API key belum diatur.")
                : "API key tersimpan dan dilindungi Windows DPAPI.";
        }
        providerBox.SelectionChanged += (_, _) => UpdateProviderFields(applyPreset: true);
        UpdateProviderFields(applyPreset: false);
        Grid.SetRow(fields, 2); content.Children.Add(fields);
        var note = new TextBlock
        {
            Text = "Percakapan disimpan lokal. Prompt dan konteks file dapat dikirim ke provider. Masuk dengan Copilot CLI atau `gh auth login` untuk Copilot.",
            Foreground = GetBrush("ThemeMutedBrush"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(note, 5); content.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 0, 0) };
        var cancel = new Button { Content = "Batal", MinWidth = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var clear = new Button { Content = "Hapus key", MinWidth = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Simpan", MinWidth = 94, Height = 32, IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        clear.Click += (_, _) => { clearApiKey = true; keyBox.Clear(); keyState.Text = "API key akan dihapus saat pengaturan disimpan."; };
        save.Click += (_, _) =>
        {
            var provider = (providerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OpenAI";
            if (provider != "Copilot" && (!Uri.TryCreate(endpointBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
            {
                MessageBox.Show(dialog, "Masukkan URL API yang valid (http atau https).", "URL tidak valid", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(modelBox.Text))
            {
                MessageBox.Show(dialog, "Nama model wajib diisi.", "Model belum diisi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            settings.AiProvider = provider;
            settings.AiLanguage = (languageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Indonesian";
            if (provider != "Copilot") settings.AiApiBaseUrl = endpointBox.Text.Trim().TrimEnd('/');
            settings.AiModel = modelBox.Text.Trim();
            _settings.Save(settings);
            if (provider != "Copilot")
            {
                var credentialName = provider == "Gemini" ? "gemini" : "ai";
                if (clearApiKey) _settings.SaveToken(credentialName, "");
                else if (!string.IsNullOrWhiteSpace(keyBox.Password)) _settings.SaveToken(credentialName, keyBox.Password);
            }
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(clear); buttons.Children.Add(save);
        Grid.SetRow(buttons, 6); content.Children.Add(buttons);
        dialog.Content = content;
        if (dialog.ShowDialog() == true) UpdateConfigurationStatus();
    }

    private void AddField(Panel panel, string label, TextBox textBox)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ThemeMutedBrush"), Margin = new Thickness(0, 0, 0, 5)
        });
        var border = new Border
        {
            Background = GetBrush("ThemeInputBrush"), BorderBrush = GetBrush("ThemeBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12)
        };
        textBox.Background = Brushes.Transparent;
        textBox.Foreground = GetBrush("ThemePrimaryBrush");
        textBox.BorderThickness = new Thickness(0);
        border.Child = textBox;
        panel.Children.Add(border);
    }

    private void AddComboField(Panel panel, string label, ComboBox comboBox)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ThemeMutedBrush"), Margin = new Thickness(0, 0, 0, 5)
        });
        var border = new Border
        {
            Background = GetBrush("ThemeInputBrush"), BorderBrush = GetBrush("ThemeBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12),
            Child = comboBox
        };
        comboBox.Background = Brushes.Transparent;
        comboBox.Foreground = GetBrush("ThemePrimaryBrush");
        comboBox.BorderThickness = new Thickness(0);
        panel.Children.Add(border);
    }

    private void UpdateConfigurationStatus()
    {
        var configuration = _settings.Load();
        var usesCopilot = string.Equals(configuration.AiProvider, "Copilot", StringComparison.OrdinalIgnoreCase);
        var key = _settings.GetToken(configuration.AiProvider == "Gemini" ? "gemini" : "ai");
        var baseLabel = !usesCopilot && string.IsNullOrWhiteSpace(key) && !IsLocalEndpoint(configuration.AiApiBaseUrl)
            ? "Atur provider AI lewat ⚙" : (usesCopilot ? "Copilot · " : configuration.AiProvider + " · ") + configuration.AiModel;
        var inputTokens = _activeSession?.InputTokens ?? 0;
        var outputTokens = _activeSession?.OutputTokens ?? 0;
        var totalTokens = inputTokens > long.MaxValue - outputTokens ? long.MaxValue : inputTokens + outputTokens;
        ModelText.Text = totalTokens > 0 ? $"{baseLabel} · {totalTokens:N0} tok" : baseLabel;
        ModelText.ToolTip = totalTokens > 0
            ? $"Token yang dilaporkan provider — input {inputTokens:N0}, output {outputTokens:N0}. Biaya bergantung pada provider/model."
            : usesCopilot ? "Autentikasi akun GitHub Copilot; jumlah token tidak disediakan SDK." : configuration.AiApiBaseUrl;
    }

    private static bool IsLocalEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private void LoadCustomAgents()
    {
        foreach (var agent in _settings.Load().AiCustomAgents ?? [])
        {
            if (string.IsNullOrWhiteSpace(agent.Name) || string.IsNullOrWhiteSpace(agent.Instructions)) continue;
            _customAgentInstructions[agent.Name] = agent.Instructions;
            ModeCombo.Items.Add(new ComboBoxItem { Content = agent.Name, Tag = "Custom", ToolTip = "Custom agent" });
        }
    }

    private string? GetCurrentCustomInstructions()
    {
        var item = ModeCombo.SelectedItem as ComboBoxItem;
        var name = item?.Tag?.ToString() == "Custom" ? item.Content?.ToString() : null;
        return name is not null && _customAgentInstructions.TryGetValue(name, out var instructions) ? instructions : null;
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingMode) return;
        if (ModeCombo.SelectedItem is ComboBoxItem { Tag: "CreateCustomAgent" })
        {
            var previous = _lastModeItem ?? ModeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == "Agent");
            _selectingMode = true;
            ModeCombo.SelectedItem = previous;
            ModeCombo.IsDropDownOpen = false;
            _selectingMode = false;
            Dispatcher.BeginInvoke(new Action(() => AddCustomAgent_Click(this, new RoutedEventArgs())),
                DispatcherPriority.Background);
            return;
        }

        _lastModeItem = ModeCombo.SelectedItem as ComboBoxItem;
        if (ModeCombo.SelectedItem is ComboBoxItem { Tag: "Custom" } item)
            ModeCombo.ToolTip = _customAgentInstructions.GetValueOrDefault(item.Content?.ToString() ?? "");
        else if ((ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "SecurityAudit")
            ModeCombo.ToolTip = "Audit keamanan proyek secara read-only; tidak menjalankan MCP, terminal, atau perubahan file.";
        else
            ModeCombo.ToolTip = "Pilih agent atau mode chat";
    }

    private void AddCustomAgent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Buat custom agent", Owner = Window.GetWindow(this), Width = 490, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize, Background = GetBrush("ThemeWindowBrush"), Foreground = GetBrush("ThemePrimaryBrush")
        };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = "Buat agent sesuai cara kerja kamu", FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var nameBox = new TextBox { Height = 32, Padding = new Thickness(8, 5, 8, 5), ToolTip = "Contoh: Reviewer API" };
        var fields = new StackPanel();
        fields.Children.Add(new TextBlock { Text = "NAMA AGENT", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = GetBrush("ThemeMutedBrush"), Margin = new Thickness(0, 0, 0, 5) });
        AddField(fields, "INSTRUKSI", new TextBox());
        var instructionsBox = (TextBox)((Border)fields.Children[^1]).Child;
        instructionsBox.AcceptsReturn = true;
        instructionsBox.TextWrapping = TextWrapping.Wrap;
        instructionsBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        instructionsBox.MinHeight = 110;
        var nameBorder = new Border
        {
            Background = GetBrush("ThemeInputBrush"), BorderBrush = GetBrush("ThemeBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 13), Child = nameBox
        };
        nameBox.Background = Brushes.Transparent; nameBox.Foreground = GetBrush("ThemePrimaryBrush"); nameBox.BorderThickness = new Thickness(0);
        fields.Children.Insert(1, nameBorder);
        var validation = new TextBlock
        {
            Foreground = GetBrush("#FBBF24"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };
        fields.Children.Add(validation);
        Grid.SetRow(fields, 1); Grid.SetRowSpan(fields, 2); layout.Children.Add(fields);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Batal", MinWidth = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Tambah agent", MinWidth = 112, Height = 32, IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        save.Click += (_, _) =>
        {
            // Validasi inline di dalam dialog, tanpa MessageBox bertumpuk.
            validation.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(instructionsBox.Text))
            {
                validation.Text = "Isi nama dan instruksi agent terlebih dahulu.";
                validation.Visibility = Visibility.Visible;
                (string.IsNullOrWhiteSpace(nameBox.Text) ? nameBox : instructionsBox).Focus();
                return;
            }
            if (ModeCombo.Items.OfType<ComboBoxItem>().Any(item => item.Tag?.ToString() == "Custom" &&
                string.Equals(item.Content?.ToString(), nameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                validation.Text = "Nama agent tersebut sudah digunakan. Pilih nama lain.";
                validation.Visibility = Visibility.Visible;
                nameBox.Focus();
                nameBox.SelectAll();
                return;
            }
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons);
        dialog.Content = layout;
        if (dialog.ShowDialog() != true) return;

        var name = nameBox.Text.Trim();
        var instruction = instructionsBox.Text.Trim();
        _customAgentInstructions[name] = instruction;
        var config = _settings.Load();
        config.AiCustomAgents ??= [];
        config.AiCustomAgents.Add(new AiCustomAgent { Name = name, Instructions = instruction });
        _settings.Save(config);
        var customItem = new ComboBoxItem { Content = name, Tag = "Custom", ToolTip = instruction };
        ModeCombo.Items.Add(customItem);
        _lastModeItem = customItem;
        ModeCombo.SelectedItem = customItem;
    }

    private void PersistSessions()
    {
        if (_activeSession is not null) _activeSession.UpdatedUtc = DateTime.UtcNow;
        _store.Save(_sessions);
        UpdateSessionTitle();
    }

    private Brush GetBrush(string resourceKey)
    {
        if (resourceKey.StartsWith('#'))
        {
            try { return (Brush)new BrushConverter().ConvertFromString(resourceKey)!; }
            catch { return Brushes.Gray; }
        }
        return TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
    }
}
