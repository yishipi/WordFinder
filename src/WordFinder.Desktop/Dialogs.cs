using System.Diagnostics;
using WordFinder.Core;

namespace WordFinder.Desktop;

internal sealed class SettingsForm : Form
{
    private readonly IndexDatabase database;
    private readonly AppSettings settings;
    private readonly TextBox folderPath = new() { Width = 410, PlaceholderText = "也可粘贴本机文件夹路径，再点击添加", AccessibleName = "文件夹路径" };
    private readonly ListView roots = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly NumericUpDown memory = new() { Minimum = 256, Maximum = 32768, Increment = 128, Width = 150 };
    private readonly TextBox repository = new() { Width = 340, PlaceholderText = "owner/repo（发布前配置）" };
    private readonly Label storage = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    private bool busy;
    public bool IndexClearRequested { get; private set; }

    public SettingsForm(IndexDatabase database, AppSettings settings)
    {
        this.database = database;
        this.settings = settings;
        Text = "目录和设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new(820, 570);
        MinimumSize = new(700, 500);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var directories = new TabPage("文件夹分类") { Padding = new Padding(10) };
        var rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        rootLayout.RowStyles.Add(new(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new(SizeType.AutoSize));
        rootLayout.Controls.Add(new Label { AutoSize = true, Text = "可添加多个本机文件夹，自动包含子目录。目录改动立即保存。", Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        roots.Columns.Add("分类标记", 150);
        roots.Columns.Add("文件夹路径", 520);
        rootLayout.Controls.Add(roots, 0, 1);
        var rootActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        rootActions.Controls.Add(Button("添加文件夹", (_, _) => AddRoot()));
        rootActions.Controls.Add(Button("重命名标记", (_, _) => RenameRoot()));
        rootActions.Controls.Add(Button("移除分类", async (_, _) => await RemoveRootAsync()));
        rootActions.Controls.Add(folderPath);
        rootLayout.Controls.Add(rootActions, 0, 2);
        directories.Controls.Add(rootLayout);
        var options = new TabPage("资源与数据") { Padding = new Padding(14) };
        var optionsLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        optionsLayout.Controls.Add(new Label { AutoSize = true, Text = "后台索引内存上限（MB）" });
        memory.Value = settings.MemoryLimitMiB;
        optionsLayout.Controls.Add(memory);
        optionsLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(650, 0), Text = "额度只作用于索引进程。较低额度更省资源；大文件内存不足时可调高后重试。", Margin = new Padding(0, 6, 0, 20) });
        optionsLayout.Controls.Add(new Label { AutoSize = true, Text = "GitHub 发布仓库（可选，用于检查软件更新）" });
        repository.Text = settings.GitHubRepository;
        optionsLayout.Controls.Add(repository);
        storage.Margin = new Padding(0, 20, 0, 8);
        optionsLayout.Controls.Add(storage);
        var dataActions = new FlowLayoutPanel { AutoSize = true, Width = 660, Height = 40 };
        dataActions.Controls.Add(Button("打开数据目录", (_, _) => Process.Start(new ProcessStartInfo(database.DirectoryPath) { UseShellExecute = true })));
        dataActions.Controls.Add(Button("整理压缩", async (_, _) => await MaintainAsync(false)));
        dataActions.Controls.Add(Button("清空索引", async (_, _) => await MaintainAsync(true)));
        optionsLayout.Controls.Add(dataActions);
        options.Controls.Add(optionsLayout);
        tabs.TabPages.Add(directories);
        tabs.TabPages.Add(options);
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(new Label { Text = "原 Excel 始终只读。移除分类或清空索引均不删除源文件。", AutoSize = true, Margin = new Padding(0, 10, 0, 10) }, 0, 1);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = Button("保存并关闭", (_, _) => Save());
        var close = Button("关闭", (_, _) => Close());
        footer.Controls.Add(save);
        footer.Controls.Add(close);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = close;
        FormClosing += (_, eventArgs) => { if (busy) eventArgs.Cancel = true; };
        Shown += async (_, _) =>
        {
            try { ReloadRoots(); await RefreshStorageAsync(); }
            catch (Exception exception) { ShowError(exception); }
        };
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 0) };
        button.Click += handler;
        return button;
    }

    private void ReloadRoots()
    {
        roots.Items.Clear();
        foreach (var root in database.GetRoots()) roots.Items.Add(new ListViewItem([root.Label, root.Path]) { Tag = root });
    }

    private void AddRoot()
    {
        var path = folderPath.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            using var picker = new FolderBrowserDialog { Description = "选择要索引的本机文件夹", UseDescriptionForTitle = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            path = picker.SelectedPath;
        }
        try { database.AddRoot(path); folderPath.Clear(); ReloadRoots(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void RenameRoot()
    {
        if (roots.SelectedItems.Count == 0) return;
        var root = (DirectoryRoot)roots.SelectedItems[0].Tag!;
        using var input = new Form { Text = "重命名分类标记", Size = new(430, 165), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var text = new TextBox { Text = root.Label, Dock = DockStyle.Top, MaxLength = 80 };
        var confirm = new Button { Text = "保存", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK, Height = 32 };
        input.Padding = new Padding(16);
        input.Controls.Add(text);
        input.Controls.Add(confirm);
        input.AcceptButton = confirm;
        if (input.ShowDialog(this) != DialogResult.OK) return;
        try { database.RenameRoot(root.Id, text.Text); ReloadRoots(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task RemoveRootAsync()
    {
        if (roots.SelectedItems.Count == 0) return;
        var root = (DirectoryRoot)roots.SelectedItems[0].Tag!;
        if (MessageBox.Show(this, $"移除分类“{root.Label}”及仅属于它的索引？\n原 Excel 文件会保留。", "移除分类", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await BusyAsync(() => database.RemoveRoot(root.Id));
        ReloadRoots();
    }

    private async Task MaintainAsync(bool clear)
    {
        var prompt = clear ? "清空已有索引？目录分类和源文件会保留。自动更新将暂停，点击“更新索引”或重新打开软件后恢复。" : "整理索引可能需要一些时间和额外磁盘空间，期间请等待完成。";
        if (MessageBox.Show(this, prompt, clear ? "清空索引" : "整理压缩", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        if (clear) IndexClearRequested = true;
        await BusyAsync(() =>
        {
            if (clear) database.ClearIndex();
            else
            {
                var drive = new DriveInfo(Path.GetPathRoot(database.DirectoryPath)!);
                if (drive.AvailableFreeSpace < database.StorageBytes() * 2 + 64L * 1024 * 1024)
                    throw new IOException("整理所需的临时空间不足，请先释放磁盘空间。");
                database.Compact();
            }
        });
    }

    private async Task BusyAsync(Action action)
    {
        busy = true;
        UseWaitCursor = true;
        Enabled = false;
        try { await Task.Run(action); }
        catch (Exception exception) { ShowError(exception); }
        finally { busy = false; Enabled = true; UseWaitCursor = false; }
        await RefreshStorageAsync();
    }

    private async Task RefreshStorageAsync()
    {
        var statistics = await Task.Run(database.GetStatistics);
        if (IsDisposed) return;
        storage.Text = $"本机索引：{statistics.Files:N0} 个文件，{statistics.Rows:N0} 行\n占用 {MainForm.FormatBytes(statistics.StorageBytes)}（包含当前 WAL）\n位置：{database.DirectoryPath}";
    }

    private void Save()
    {
        var next = new AppSettings { MemoryLimitMiB = (int)memory.Value, GitHubRepository = repository.Text };
        try
        {
            next.Save(database.DirectoryPath);
            settings.MemoryLimitMiB = next.MemoryLimitMiB;
            settings.GitHubRepository = next.GitHubRepository;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception) => MessageBox.Show(this, exception.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

internal sealed class TextDialog : Form
{
    public TextDialog(string title, string text)
    {
        Text = title;
        Size = new(850, 600);
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text });
    }
}
