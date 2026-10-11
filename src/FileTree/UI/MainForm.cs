using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileTree.Model;
using FileTree.Scanning;

namespace FileTree.UI;

internal sealed class MainForm : Form
{
    private const int TopFileLimit = 1000;

    private sealed record DriveItem(DriveInfo Drive, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Container _components = new();
    private readonly string? _autoScan;
    private readonly ToolStripComboBox _driveBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 320 };
    private readonly ToolStripButton _scanButton = new("掃描") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton _folderButton = new("掃描資料夾…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton _cancelButton = new("取消") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
    private readonly TreeListView _tree = new() { Dock = DockStyle.Fill };
    private readonly FileListView _files = new() { Dock = DockStyle.Fill };
    private readonly Label _filesLabel = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 24, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 0, 0), AutoEllipsis = true, Text = "最大檔案",
    };
    private readonly TreemapControl _treemap = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _mainSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
    private readonly SplitContainer _topSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false, Maximum = 1000, MarqueeAnimationSpeed = 30 };
    private readonly System.Windows.Forms.Timer _progressTimer;
    private readonly Stopwatch _clock = new();
    private FileTreeModel? _model;
    private ScanState? _state;
    private CancellationTokenSource? _cts;

    public MainForm(string? autoScan)
    {
        _autoScan = autoScan;
        _progressTimer = new System.Windows.Forms.Timer(_components) { Interval = 100 };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft JhengHei UI", 9F);
        Size = new Size(1280, 860);
        StartPosition = FormStartPosition.CenterScreen;
        Text = $"FileTree 磁碟空間分析（{(ScanService.IsAdministrator ? "系統管理員：MFT 直讀" : "一般權限：慢速遍歷")}）";

        var toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
        toolStrip.Items.AddRange([new ToolStripLabel("磁碟："), _driveBox, new ToolStripSeparator(), _scanButton, _folderButton,
            new ToolStripSeparator(), _cancelButton]);
        _scanButton.Click += (_, _) => ScanSelectedDrive();
        _folderButton.Click += (_, _) => ScanChosenFolder();
        _cancelButton.Click += (_, _) =>
        {
            _cancelButton.Enabled = false;
            _cts?.Cancel();
        };

        var filesPanel = new Panel { Dock = DockStyle.Fill };
        filesPanel.Controls.Add(_files); // Fill first so the Top label docks above it
        filesPanel.Controls.Add(_filesLabel);
        _topSplit.Panel1.Controls.Add(_tree);
        _topSplit.Panel2.Controls.Add(filesPanel);
        _mainSplit.Panel1.Controls.Add(_topSplit);
        _mainSplit.Panel2.Controls.Add(_treemap);

        var author = new ToolStripStatusLabel("製作者：黃政文") { ForeColor = Color.Gray, Font = new Font("Microsoft JhengHei UI", 8F) };
        var statusStrip = new StatusStrip { ShowItemToolTips = true };
        statusStrip.Items.AddRange([_status, _progress, author]);

        Controls.Add(_mainSplit); // Fill first so the strips dock around it
        Controls.Add(toolStrip);
        Controls.Add(statusStrip);
        ResumeLayout(true);

        _tree.NodeSelected += node => SelectNode(node, true);
        _files.NodeActivated += node =>
        {
            _tree.Reveal(node);
            SelectNode(node, true);
        };
        _treemap.NodeClicked += node =>
        {
            _tree.Reveal(node);
            SelectNode(node, false);
        };
        _tree.ContextMenuStrip = CreateMenu(() => _tree.SelectedNode);
        _files.ContextMenuStrip = CreateMenu(() => _files.SelectedNode);
        _treemap.ContextMenuStrip = CreateMenu(() => _treemap.NodeAt(_treemap.PointToClient(Cursor.Position)));

        LoadDrives();
        _status.Text = ScanService.IsAdministrator ? "就緒" : "一般權限：使用慢速遍歷，無權限的資料夾會略過";
    }

    private void LoadDrives()
    {
        string? wanted = _autoScan is { Length: > 0 } ? _autoScan[..1] : Path.GetPathRoot(Environment.SystemDirectory)?[..1];
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            string label = $"{drive.Name} {drive.DriveFormat} {SizeFormat.Bytes(drive.TotalSize)}（可用 {SizeFormat.Bytes(drive.TotalFreeSpace)}）";
            int index = _driveBox.Items.Add(new DriveItem(drive, label));
            if (wanted is not null && drive.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) _driveBox.SelectedIndex = index;
        }
        if (_driveBox.SelectedIndex < 0 && _driveBox.Items.Count > 0) _driveBox.SelectedIndex = 0;
        _scanButton.Enabled = _driveBox.Items.Count > 0;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _mainSplit.SplitterDistance = _mainSplit.Height / 2;
        _topSplit.SplitterDistance = _topSplit.Width * 55 / 100;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_autoScan is null) return;
        // "C", "C:" or "C:\" picks a drive in the list; anything longer is a folder path.
        if (_autoScan.TrimEnd(':', '\\').Length <= 1) ScanSelectedDrive();
        else StartScan(_autoScan);
    }

    private void ScanSelectedDrive()
    {
        if (_driveBox.SelectedItem is DriveItem item) StartScan(item.Drive.RootDirectory.FullName);
    }

    private void ScanChosenFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "選擇要掃描的資料夾", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) StartScan(dialog.SelectedPath);
    }

    private async void StartScan(string path)
    {
        if (_state is not null) return;
        var state = new ScanState();
        using var cts = new CancellationTokenSource();
        _state = state;
        _cts = cts;
        _scanButton.Enabled = false;
        _folderButton.Enabled = false;
        _driveBox.Enabled = false;
        _cancelButton.Enabled = true;
        _model = null;
        _tree.SetModel(null);
        _files.SetModel(null);
        _filesLabel.Text = "最大檔案";
        _treemap.SetModel(null, "掃描中…");
        _status.Text = "掃描中…";
        _status.ToolTipText = null;
        _progress.Visible = true;
        _clock.Restart();
        _progressTimer.Start();
        try
        {
            var outcome = await Task.Run(() => ScanService.Scan(path, state, false, cts.Token));
            ShowOutcome(outcome, path);
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            _treemap.SetModel(null, "已取消掃描");
            _status.Text = $"已取消掃描（{_clock.Elapsed.TotalSeconds:F1} 秒）";
        }
        catch (Exception ex)
        {
            _treemap.SetModel(null, "掃描失敗");
            _status.Text = $"掃描失敗：{ex.Message}";
            MessageBox.Show(this, ex.Message, "掃描失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progressTimer.Stop();
            _progress.Visible = false;
            _state = null;
            _cts = null;
            _scanButton.Enabled = true;
            _folderButton.Enabled = true;
            _driveBox.Enabled = true;
            _cancelButton.Enabled = false;
        }
    }

    private void UpdateProgress()
    {
        if (_state is not { } state) return;
        long done = Interlocked.Read(ref state.Done), total = Interlocked.Read(ref state.Total);
        double seconds = _clock.Elapsed.TotalSeconds;
        if (total > 0)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)Math.Clamp(done * 1000 / total, 0, 1000);
            _status.Text = $"讀取 MFT 中… {done:N0} / {total:N0} 筆紀錄（{seconds:F1} 秒）";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _status.Text = done > 0 ? $"遍歷資料夾中… 已找到 {done:N0} 個項目（{seconds:F1} 秒）" : $"掃描中…（{seconds:F1} 秒）";
        }
    }

    private void ShowOutcome(ScanOutcome outcome, string path)
    {
        var model = outcome.Model;
        _model = model;
        _tree.SetModel(model);
        _files.SetModel(model);
        _treemap.SetModel(model);
        SelectNode(model.Root, true);
        _tree.Focus();

        string scanned = SizeFormat.Bytes(model.Alloc(model.Root));
        var parts = new List<string>
        {
            outcome.UsedMft ? "MFT 直讀" : "一般遍歷",
            $"{model.FileCount:N0} 個檔案、{model.DirectoryCount:N0} 個資料夾",
            outcome.IsFolder ? $"資料夾 {scanned}"
                : outcome.VolumeUsedBytes > 0 ? $"掃描 {scanned}／NTFS 已用 {SizeFormat.Bytes(outcome.VolumeUsedBytes)}"
                : $"掃描 {scanned}／磁碟已用 {SizeFormat.Bytes(DiskUsed())}",
            $"讀取 {outcome.ScanTime.TotalSeconds:F2} 秒＋建樹 {outcome.BuildTime.TotalSeconds:F2} 秒",
        };
        if (outcome.Skipped > 0) parts.Add($"無權限資料夾 {outcome.Skipped:N0} 個");
        if (outcome.Note is not null) parts.Add(outcome.Note);
        _status.Text = string.Join("　｜　", parts);
        _status.ToolTipText = _status.Text;

        long DiskUsed()
        {
            var drive = new DriveInfo(path);
            return drive.TotalSize - drive.TotalFreeSpace;
        }
    }

    /// <summary>
    /// Syncs the treemap and the largest-files list with a selected node. With <paramref name="retarget"/>,
    /// a selected folder also becomes the treemap root.
    /// </summary>
    private void SelectNode(int node, bool retarget)
    {
        var model = _model;
        if (model is null || node < 0) return;
        if (model.IsDirectory(node))
        {
            if (retarget) _treemap.ShowRoot(node);
            _files.SetFiles(model.TopFiles(node, TopFileLimit));
            _filesLabel.Text = $"最大檔案：{model.FullPath(node)}（前 {TopFileLimit:N0} 名）";
        }
        else if (_treemap.Root < 0 || !model.IsAncestorOrSelf(_treemap.Root, node))
        {
            _treemap.ShowRoot(model.Parent(node));
        }
        _treemap.Highlight(node);
    }

    private ContextMenuStrip CreateMenu(Func<int> target)
    {
        var menu = new ContextMenuStrip(_components);
        int node = -1;
        menu.Items.Add("在檔案總管中顯示", null, (_, _) => ShowInExplorer(node));
        menu.Items.Add("複製完整路徑", null, (_, _) => CopyPath(node));
        menu.Opening += (_, e) =>
        {
            node = _model is null ? -1 : target();
            e.Cancel = node < 0;
        };
        return menu;
    }

    private void ShowInExplorer(int node)
    {
        if (_model is null || node < 0) return;
        string path = _model.FullPath(node);
        try
        {
            // A drive root ends with '\', which would escape a closing quote, and never contains spaces.
            Process.Start("explorer.exe", path.EndsWith('\\') ? path : $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"無法開啟檔案總管：{ex.Message}", "FileTree", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyPath(int node)
    {
        if (_model is null || node < 0) return;
        try
        {
            Clipboard.SetText(_model.FullPath(node));
        }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, $"無法存取剪貼簿：{ex.Message}", "FileTree", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _components.Dispose();
        base.Dispose(disposing);
    }
}
