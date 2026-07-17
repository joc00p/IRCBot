using System.Diagnostics;
using IRCBot.Shared;

namespace IRCBot.ControlApp;

public sealed class ControlForm : Form
{
    private readonly BotControlClient _client = new();

    // Bot host process controls
    private readonly TextBox _hostPort = new() { Text = "6690", Width = 55 };
    private readonly Button _launchBtn = new() { Text = "Launch Bot Host", Width = 120 };
    private readonly Label _hostStatus = new() { Text = "Host not running", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 8, 0, 0) };
    private Process? _hostProc;

    // Connection bar
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 90 };
    private readonly TextBox _port = new() { Text = "6690", Width = 55 };
    private readonly TextBox _pass = new() { Width = 90, UseSystemPasswordChar = true, PlaceholderText = "password" };
    private readonly Button _connectBtn = new() { Text = "Connect", Width = 90 };
    private readonly CheckBox _autoRefresh = new() { Text = "Auto-refresh (2s)", Checked = true, AutoSize = true };
    private readonly Label _status = new() { Text = "Disconnected", AutoSize = true, ForeColor = Color.Firebrick };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    // Bots grid
    private readonly ListView _botsView = new()
    {
        View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9)
    };

    public ControlForm()
    {
        Text = "IRC Bot Remote Control";
        Width = 940;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 500);

        _botsView.Columns.Add("Nick", 120);
        _botsView.Columns.Add("Server Host", 110);
        _botsView.Columns.Add("Port", 55);
        _botsView.Columns.Add("Status", 90);
        _botsView.Columns.Add("Channels", 220);
        _botsView.Columns.Add("Last Event", 160);
        _botsView.Columns.Add("Id", 70);

        BuildLayout();

        _launchBtn.Click += async (_, _) => await ToggleHostAsync();
        _connectBtn.Click += async (_, _) => await ToggleConnectAsync();
        _autoRefresh.CheckedChanged += (_, _) => { if (_client.IsConnected && _autoRefresh.Checked) _timer.Start(); else _timer.Stop(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => { _timer.Stop(); _client.Dispose(); StopHost(); };
    }

    private void BuildLayout()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(6, 6, 6, 0), WrapContents = true };
        Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(3, 8, 0, 0) };

        // Row 1 — launch/stop the bot host process
        bar.Controls.Add(Lbl("Control Port:"));
        bar.Controls.Add(_hostPort);
        bar.Controls.Add(_launchBtn);
        bar.Controls.Add(_hostStatus);
        bar.SetFlowBreak(_hostStatus, true);

        // Row 2 — connect the control client
        bar.Controls.Add(Lbl("Host:"));
        bar.Controls.Add(_host);
        bar.Controls.Add(Lbl("Port:"));
        bar.Controls.Add(_port);
        bar.Controls.Add(_pass);
        bar.Controls.Add(_connectBtn);
        bar.Controls.Add(_autoRefresh);
        bar.Controls.Add(_status);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 380 };

        var botsPanel = new Panel { Dock = DockStyle.Fill };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
        AddButton(actions, "Add Bot…", async () => await AddBotAsync());
        AddButton(actions, "Start", async () => await BotAction(BotCommands.Start));
        AddButton(actions, "Stop", async () => await BotAction(BotCommands.Stop));
        AddButton(actions, "Join…", async () => await JoinPartAsync(BotCommands.Join));
        AddButton(actions, "Part…", async () => await JoinPartAsync(BotCommands.Part));
        AddButton(actions, "Say…", async () => await SayAsync());
        AddButton(actions, "Remove", async () => await RemoveBotAsync());
        AddButton(actions, "Refresh", async () => await RefreshAsync());
        botsPanel.Controls.Add(_botsView);
        botsPanel.Controls.Add(actions);

        split.Panel1.Controls.Add(botsPanel);
        split.Panel2.Controls.Add(_log);

        Controls.Add(split);
        Controls.Add(bar);
    }

    // ── Bot host process ────────────────────────────────────────────────
    private async Task ToggleHostAsync()
    {
        if (_hostProc is { HasExited: false }) { StopHost(); return; }

        var exe = FindHostExe();
        if (exe == null)
        {
            using var ofd = new OpenFileDialog { Title = "Locate IRCBotHost.exe", Filter = "IRCBotHost.exe|IRCBotHost.exe|Executables|*.exe" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            exe = ofd.FileName;
        }
        if (!int.TryParse(_hostPort.Text, out var controlPort)) { Warn("Invalid control port"); return; }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            psi.ArgumentList.Add(controlPort.ToString());
            if (!string.IsNullOrEmpty(_pass.Text)) psi.ArgumentList.Add(_pass.Text);

            _hostProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _hostProc.OutputDataReceived += (_, e) => { if (e.Data != null) UI(() => Log("[host] " + e.Data)); };
            _hostProc.ErrorDataReceived  += (_, e) => { if (e.Data != null) UI(() => Log("[host!] " + e.Data)); };
            _hostProc.Exited += (_, _) => UI(() =>
            {
                Log("Bot host exited");
                _launchBtn.Text = "Launch Bot Host";
                SetHostStatus("Host not running", false);
            });
            _hostProc.Start();
            _hostProc.BeginOutputReadLine();
            _hostProc.BeginErrorReadLine();

            _launchBtn.Text = "Stop Bot Host";
            SetHostStatus($"Host running (control {controlPort})", true);
            Log($"Launched {Path.GetFileName(exe)} {controlPort}");

            _host.Text = "127.0.0.1";
            _port.Text = controlPort.ToString();
            for (int i = 0; i < 12 && !_client.IsConnected; i++)
            {
                await Task.Delay(300);
                if (await ConnectAsync()) break;
            }
        }
        catch (Exception ex) { Warn($"Failed to launch bot host: {ex.Message}"); }
    }

    private void StopHost()
    {
        try
        {
            if (_client.IsConnected)
            {
                _timer.Stop(); _client.Dispose();
                SetStatus("Disconnected", false); _connectBtn.Text = "Connect";
            }
            if (_hostProc is { HasExited: false })
            {
                _hostProc.Kill(entireProcessTree: true);
                _hostProc.WaitForExit(2000);
            }
        }
        catch { }
        finally
        {
            _hostProc?.Dispose();
            _hostProc = null;
            if (!IsDisposed) { _launchBtn.Text = "Launch Bot Host"; SetHostStatus("Host not running", false); }
        }
    }

    private static string? FindHostExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var hostBin = Path.Combine(dir.FullName, "Host", "bin");
            if (Directory.Exists(hostBin))
            {
                var exe = Directory.GetFiles(hostBin, "IRCBotHost.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (exe != null) return exe;
            }
        }
        return null;
    }

    // ── Connection ──────────────────────────────────────────────────────
    private async Task ToggleConnectAsync()
    {
        if (_client.IsConnected)
        {
            _timer.Stop(); _client.Dispose();
            SetStatus("Disconnected", false); _connectBtn.Text = "Connect";
            return;
        }
        await ConnectAsync();
    }

    private async Task<bool> ConnectAsync()
    {
        try
        {
            if (!int.TryParse(_port.Text, out var port)) { Warn("Invalid port"); return false; }
            await _client.ConnectAsync(_host.Text.Trim(), port, _pass.Text);
            SetStatus($"Connected to {_host.Text}:{port}", true);
            _connectBtn.Text = "Disconnect";
            Log($"Connected to control port {_host.Text}:{port}");
            if (_autoRefresh.Checked) _timer.Start();
            await RefreshAsync();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Disconnected", false);
            Log($"Connection failed: {ex.Message}");
            return false;
        }
    }

    // ── Refresh ─────────────────────────────────────────────────────────
    private async Task RefreshAsync()
    {
        if (!_client.IsConnected) return;
        try
        {
            var r = await _client.SimpleAsync(BotCommands.List);
            if (r.Bots is not { } bots) return;
            var selected = SelectedId();
            _botsView.BeginUpdate();
            _botsView.Items.Clear();
            foreach (var b in bots.OrderBy(b => b.Nick))
            {
                var item = new ListViewItem(new[]
                {
                    b.Nick, b.Host, b.Port.ToString(), b.Status.ToString(),
                    string.Join(", ", b.Channels), b.LastEvent, b.Id
                });
                item.ForeColor = b.Status switch
                {
                    BotStatus.Connected => Color.ForestGreen,
                    BotStatus.Connecting => Color.DarkOrange,
                    BotStatus.Error => Color.Firebrick,
                    _ => Color.DimGray
                };
                _botsView.Items.Add(item);
                if (b.Id == selected) item.Selected = true;
            }
            _botsView.EndUpdate();
        }
        catch (Exception ex) { Log($"Refresh error: {ex.Message}"); }
    }

    // ── Bot actions ─────────────────────────────────────────────────────
    private async Task RunAction(Task<BotResponse> call)
    {
        try
        {
            var r = await call;
            Log(r.Ok ? "✓ " + (r.Message ?? "OK") : "✗ " + (r.Error ?? "Error"));
            await RefreshAsync();
        }
        catch (Exception ex) { Log("✗ " + ex.Message); }
    }

    private async Task BotAction(string cmd)
    {
        var id = SelectedId();
        if (id == null) { Warn("Select a bot"); return; }
        await RunAction(_client.ActionAsync(cmd, ("id", id)));
    }

    private async Task AddBotAsync()
    {
        if (!_client.IsConnected) { Warn("Connect to a bot host first"); return; }
        using var dlg = new AddBotDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await RunAction(_client.ActionAsync(BotCommands.Add,
            ("nick", dlg.Nick), ("host", dlg.HostName), ("port", dlg.Port), ("channels", dlg.Channels)));
    }

    private async Task JoinPartAsync(string cmd)
    {
        var id = SelectedId();
        if (id == null) { Warn("Select a bot"); return; }
        var channel = Prompt($"{cmd} channel (e.g. #test):", "#test");
        if (string.IsNullOrWhiteSpace(channel)) return;
        await RunAction(_client.ActionAsync(cmd, ("id", id), ("channel", channel)));
    }

    private async Task SayAsync()
    {
        var id = SelectedId();
        if (id == null) { Warn("Select a bot"); return; }
        var target = Prompt("Target (channel or nick):", "#test");
        if (string.IsNullOrWhiteSpace(target)) return;
        var text = Prompt("Message:", "");
        if (string.IsNullOrEmpty(text)) return;
        await RunAction(_client.ActionAsync(BotCommands.Say, ("id", id), ("target", target), ("text", text)));
    }

    private async Task RemoveBotAsync()
    {
        var id = SelectedId();
        if (id == null) { Warn("Select a bot"); return; }
        await RunAction(_client.ActionAsync(BotCommands.Remove, ("id", id)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private string? SelectedId() =>
        _botsView.SelectedItems.Count == 0 ? null : _botsView.SelectedItems[0].SubItems[6].Text;

    private static void AddButton(Control parent, string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
        b.Click += async (_, _) => await onClick();
        parent.Controls.Add(b);
    }

    private void UI(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
    }

    private void SetStatus(string text, bool connected)
    {
        _status.Text = text;
        _status.ForeColor = connected ? Color.ForestGreen : Color.Firebrick;
    }

    private void SetHostStatus(string text, bool running)
    {
        _hostStatus.Text = text;
        _hostStatus.ForeColor = running ? Color.ForestGreen : Color.Gray;
    }

    private void Log(string msg) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    private void Warn(string msg) => MessageBox.Show(this, msg, "IRC Bot Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private string? Prompt(string label, string def)
    {
        using var dlg = new Form { Text = "IRC Bot Control", Width = 440, Height = 180, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 400 };
        var box = new TextBox { Left = 12, Top = 44, Width = 400, Text = def };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 256, Top = 90, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 337, Top = 90, Width = 75 };
        dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }
}

// Dialog for creating a bot: nick, server host, port, initial channels.
public sealed class AddBotDialog : Form
{
    private readonly TextBox _nick = new() { Left = 130, Top = 12, Width = 260, Text = "MyBot" };
    private readonly TextBox _host = new() { Left = 130, Top = 44, Width = 260, Text = "localhost" };
    private readonly TextBox _port = new() { Left = 130, Top = 76, Width = 260, Text = "6667" };
    private readonly TextBox _channels = new() { Left = 130, Top = 108, Width = 260, Text = "#test" };

    public string Nick => _nick.Text.Trim();
    public string HostName => _host.Text.Trim();
    public string Port => _port.Text.Trim();
    public string Channels => _channels.Text.Trim();

    public AddBotDialog()
    {
        Text = "Add Bot";
        Width = 420; Height = 210;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;

        Label L(string t, int top) => new() { Text = t, Left = 12, Top = top + 3, Width = 115 };
        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Left = 234, Top = 140, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 315, Top = 140, Width = 75 };

        Controls.AddRange(new Control[]
        {
            L("Nick:", 12), _nick,
            L("Server host:", 44), _host,
            L("Server port:", 76), _port,
            L("Channels (csv):", 108), _channels,
            ok, cancel
        });
        AcceptButton = ok; CancelButton = cancel;
    }
}
