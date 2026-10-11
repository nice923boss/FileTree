using FileTree.Model;

namespace FileTree.UI;

/// <summary>Virtual list of files (e.g. the largest files under a folder).</summary>
internal sealed class FileListView : ListView
{
    private FileTreeModel? _model;
    private int[] _files = [];

    /// <summary>Raised when the user activates (double-clicks or presses Enter on) a file.</summary>
    public event Action<int>? NodeActivated;

    public FileListView()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        DoubleBuffered = true;
        Columns.Add("名稱", 220);
        Columns.Add("佔用空間", 85, HorizontalAlignment.Right);
        Columns.Add("大小", 85, HorizontalAlignment.Right);
        Columns.Add("所在資料夾", 400);
    }

    public int SelectedNode => SelectedIndices.Count > 0 && SelectedIndices[0] < _files.Length ? _files[SelectedIndices[0]] : -1;

    public void SetModel(FileTreeModel? model)
    {
        _model = model;
        SetFiles([]);
    }

    public void SetFiles(int[] files)
    {
        SelectedIndices.Clear();
        _files = files;
        VirtualListSize = files.Length;
        if (files.Length > 0) EnsureVisible(0);
        Invalidate();
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        if (_model is null || e.ItemIndex >= _files.Length)
        {
            e.Item = new ListViewItem(["", "", "", ""]);
            return;
        }
        int node = _files[e.ItemIndex];
        int parent = _model.Parent(node);
        e.Item = new ListViewItem([_model.Name(node), SizeFormat.Bytes(_model.Alloc(node)), SizeFormat.Bytes(_model.Size(node)),
            parent >= 0 ? _model.FullPath(parent) : ""]);
    }

    protected override void OnItemActivate(EventArgs e)
    {
        base.OnItemActivate(e);
        if (SelectedNode >= 0) NodeActivated?.Invoke(SelectedNode);
    }
}
