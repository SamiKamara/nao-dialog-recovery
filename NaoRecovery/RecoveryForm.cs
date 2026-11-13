using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NaoRecovery;

public sealed class RecoveryForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 42, 57);
    private static readonly Color Teal = Color.FromArgb(0, 112, 121);
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NAODialogRecovery");
    private readonly TextBox address = new() { PlaceholderText = "Enter NAO's IPv4 address", AccessibleName = "NAO IP address", Font = new Font("Segoe UI", 13), Width = 240 };
    private readonly Button recover = new() { Text = "Check & recover", AutoSize = false, Size = new Size(190, 44) };
    private readonly Button check = new() { Text = "Check only", AutoSize = false, Size = new Size(125, 44) };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = false, Size = new Size(90, 44), Enabled = false };
    private readonly Button elias = new() { Text = "Open ELIAS", AutoSize = true };
    private readonly Button saveLog = new() { Text = "Save log...", AutoSize = true };
    private readonly Label status = new() { Text = "Ready to check NAO", AutoSize = true, MaximumSize = new Size(600, 0), Font = new Font("Segoe UI Semibold", 15), ForeColor = Ink };
    private readonly Label result = new() { Text = "If a lesson says it is listening but gives no feedback, leave it open while checking.", AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Ink };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10) };
    private readonly StringBuilder logText = new();
    private CancellationTokenSource? operation;
    private string? logPath;

    public RecoveryForm()
    {
        Text = "NAO Dialog Recovery";
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Color.FromArgb(242, 247, 248);
        ClientSize = new Size(680, 650);
        MinimumSize = new Size(650, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 1, RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new float[] { 43, 49, 66, 36, 60, 43, 66 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        layout.Controls.Add(new Label { Text = "NAO Dialog Recovery", AutoSize = true, Font = new Font("Segoe UI Semibold", 23), ForeColor = Ink }, 0, 0);
        layout.Controls.Add(new Label { Text = "Version 1.1.1 · Recover ELIAS startup and recognition-context faults.\nAutomatically preserves Finnish, English, or the current installed language.", AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Color.FromArgb(74, 93, 105) }, 0, 1);
        var target = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        target.Controls.Add(new Label { Text = "NAO IP address", AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
        target.Controls.Add(address);
        layout.Controls.Add(target, 0, 2);
        layout.Controls.Add(new Label { Text = "Press NAO's chest button once to hear its current IP address.", AutoSize = true, ForeColor = Color.FromArgb(74, 93, 105) }, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        recover.FlatStyle = FlatStyle.Flat;
        recover.UseMnemonic = false;
        recover.FlatAppearance.BorderSize = 0;
        recover.BackColor = Teal;
        recover.ForeColor = Color.White;
        foreach (var button in new[] { recover, check, cancel }) { button.Margin = new Padding(0, 0, 10, 0); actions.Controls.Add(button); }
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(status, 0, 5);
        layout.Controls.Add(result, 0, 6);
        log.AccessibleName = "Recovery log";
        layout.Controls.Add(log, 0, 7);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        footer.Controls.Add(elias);
        footer.Controls.Add(saveLog);
        layout.Controls.Add(footer, 0, 8);
        Controls.Add(layout);
        AcceptButton = recover;
        LoadSettings();
        recover.Click += async (_, _) => await RunAsync(true);
        check.Click += async (_, _) => await RunAsync(false);
        cancel.Click += (_, _) => operation?.Cancel();
        saveLog.Click += (_, _) => SaveLog();
        elias.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("http://robotapp.eliasai.com/lessons") { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Could not open ELIAS"); }
        };
        FormClosing += (_, _) => operation?.Cancel();
    }

    private async Task RunAsync(bool repair)
    {
        if (operation is not null) return;
        operation = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        recover.Enabled = check.Enabled = address.Enabled = elias.Enabled = false;
        cancel.Enabled = true;
        log.Clear();
        logText.Clear();
        logPath = null;
        status.Text = "Checking NAO...";
        status.ForeColor = Ink;
        result.Text = repair ? "Checking first. A healthy dialog service is left running." : "Checking the current state.";
        var host = address.Text.Trim();
        try
        {
            Directory.CreateDirectory(Path.Combine(DataFolder, "Logs"));
            logPath = Path.Combine(DataFolder, "Logs", $"Recovery-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            AppendLog($"NAO Dialog Recovery 1.1.1 | {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            AppendLog(repair ? "Mode: check and recover" : "Mode: check only");
            var engine = new RecoveryEngine(AppendLog);
            var answer = await engine.RunAsync(host, repair, operation.Token);
            status.Text = answer.Status switch
            {
                RecoveryStatus.Healthy => "NAO is listening",
                RecoveryStatus.Idle => "Dialog ready · no active lesson",
                RecoveryStatus.Busy => "Check again while listening",
                RecoveryStatus.ReloadRequired => "Refresh ELIAS to finish",
                _ => "Action needed"
            };
            status.ForeColor = answer.Status == RecoveryStatus.NeedsAttention ? Color.FromArgb(145, 82, 13) : Teal;
            result.Text = answer.Message;
            AppendLog(answer.Message);
            SaveSettings(host);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Check stopped";
            result.Text = "The check was cancelled or timed out. If recovery had already started, the robot may still be completing it. Run Check only to verify.";
            AppendLog(result.Text);
        }
        catch (Exception error)
        {
            status.Text = "Could not finish the check";
            status.ForeColor = Color.FromArgb(157, 42, 42);
            result.Text = error is HttpRequestException or System.Net.WebSockets.WebSocketException or TimeoutException
                ? "Could not complete the robot connection. Confirm NAO's IP and that this PC can reach it, then try again. See the log for details."
                : error.Message;
            AppendLog($"ERROR: {error.Message}");
        }
        finally
        {
            if (logPath is not null) { try { File.WriteAllText(logPath, logText.ToString(), Encoding.UTF8); } catch { } }
            operation.Dispose();
            operation = null;
            if (!IsDisposed)
            {
                recover.Enabled = check.Enabled = address.Enabled = elias.Enabled = true;
                cancel.Enabled = false;
            }
        }
    }

    private void AppendLog(string line)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        logText.Append(entry);
        if (!IsDisposed) log.AppendText(entry);
    }

    private void LoadSettings()
    {
        try
        {
            var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(DataFolder, "settings.json")));
            if (config?.TryGetValue("address", out var host) == true) address.Text = host;
        }
        catch { }
    }

    private static void SaveSettings(string host)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, "settings.json"), JsonSerializer.Serialize(new { address = host }));
        }
        catch { }
    }

    private void SaveLog()
    {
        if (logText.Length == 0) { MessageBox.Show(this, "Run a check first to create a log.", "NAO Dialog Recovery"); return; }
        using var dialog = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt", FileName = "NAO-Recovery-Log.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, logText.ToString(), Encoding.UTF8); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Could not save log"); }
    }
}
