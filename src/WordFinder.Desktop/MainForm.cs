using System.Diagnostics;
using System.Text;
using WordFinder.Core;

namespace WordFinder.Desktop;

internal sealed class MainForm : Form
{
    private readonly IndexDatabase database;
    private readonly AppSettings settings;
    private readonly SearchService search;
    private readonly ComboBox category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly FlowLayoutPanel targetList = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true };
    private readonly List<TextBox> targets = [];
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoGenerateColumns = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Label empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Text = "先添加文件夹，再点击“更新索引”。\n索引完成后，输入目标即可搜索。" };
    private readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = SystemColors.Window };
    private readonly Label previewTitle = new() { AutoSize = true, Text = "选中结果查看命中内容", Margin = new Padding(0, 5, 0, 8) };
    private readonly Button settingsButton = MakeButton("目录和设置");
    private readonly Button indexButton = MakeButton("更新索引");
    private readonly Button pauseButton = MakeButton("暂停");
    private readonly Button stopButton = MakeButton("停止");
    private readonly Button searchButton = MakeButton("搜索", true);
    private readonly Button previousButton = MakeButton("上一页");
    private readonly Button nextButton = MakeButton("下一页");
    private readonly Label pageLabel = new() { AutoSize = true, Margin = new Padding(12, 9, 12, 0) };
    private readonly Label fileLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 8 };
    private readonly ToolStripStatusLabel status = new() { TextAlign = ContentAlignment.MiddleLeft, Text = "就绪" };
    private readonly Label statistics = new() { AutoSize = true, Text = "本地索引", Margin = new Padding(12, 9, 0, 0), ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer monitorTimer = new() { Interval = 1000 };
    private readonly DirectoryChangeMonitor monitor;
    private readonly Stopwatch reconciliation = Stopwatch.StartNew();
    private long lastChange;
    private int monitorError;
    private bool settingsOpen;
    private bool automaticSuspended;
    private readonly SemaphoreSlim searchGate = new(1, 1);
    private readonly Stack<long> history = new();
    private SearchPage page = new([], false);
    private long cursor;
    private CancellationTokenSource? queryCancellation;
    private IndexerController? controller;
    private Task<IndexSummary>? runningTask;
    private bool updatingCategories;
    private bool closing;
    private bool exitReady;
    private int previewRequest;

    private sealed record CategoryChoice(long? Id, string Label);
    private static string VersionText => typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "未知";
    private sealed class HitView(SearchHit hit)
    {
        public SearchHit Hit { get; } = hit;
        public string FileName => Path.GetFileName(Hit.Path);
        public string FilePath => Hit.Path;
        public string Sheet => Hit.Sheet;
        public int Row => Hit.Row;
        public string Categories => Hit.Categories;
        public string Preview => Hit.Preview;
        public string State => Hit.IsStale ? "旧索引" : "正常";
    }

    public MainForm(IndexDatabase database, AppSettings settings)
    {
        this.database = database;
        this.settings = settings;
        search = new(database);
        monitor = new(error =>
        {
            if (error) Interlocked.Exchange(ref monitorError, 1);
            Interlocked.Exchange(ref lastChange, Stopwatch.GetTimestamp());
        });
        Text = "WordFinder · Excel 本地检索";
        Size = new(1220, 850);
        MinimumSize = new(960, 690);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(246, 248, 250);
        BuildLayout();
        AddTarget();
        AddTarget();
        settingsButton.Click += async (_, _) => await GuardAsync(OpenSettingsAsync);
        indexButton.Click += async (_, _) =>
        {
            automaticSuspended = false;
            Interlocked.Exchange(ref lastChange, 0);
            await GuardAsync(() => StartIndexAsync(false));
        };
        pauseButton.Click += (_, _) =>
        {
            if (controller is null) return;
            controller.SetPaused(!controller.IsPaused);
            pauseButton.Text = controller.IsPaused ? "继续" : "暂停";
            status.Text = controller.IsPaused ? "已请求暂停，等待当前读取步骤结束…" : "正在继续索引…";
        };
        stopButton.Click += async (_, _) =>
        {
            automaticSuspended = true;
            Interlocked.Exchange(ref lastChange, 0);
            if (controller is not null) await GuardAsync(controller.StopAsync);
        };
        searchButton.Click += async (_, _) => await GuardAsync(() => SearchAsync(true));
        category.SelectedIndexChanged += (_, _) => { if (!updatingCategories) ScheduleSearch(); };
        debounce.Tick += async (_, _) => { debounce.Stop(); await GuardAsync(() => SearchAsync(true)); };
        refreshTimer.Tick += (_, _) => UpdateProgress();
        monitorTimer.Tick += async (_, _) => await GuardAsync(CheckForChangesAsync);
        previousButton.Click += async (_, _) =>
        {
            if (!history.TryPop(out cursor)) return;
            await GuardAsync(() => SearchAsync(false));
        };
        nextButton.Click += async (_, _) =>
        {
            if (page.Hits.Count == 0 || !page.HasMore) return;
            history.Push(cursor);
            cursor = page.Hits[^1].RowId;
            await GuardAsync(() => SearchAsync(false));
        };
        grid.SelectionChanged += async (_, _) => await GuardAsync(LoadPreviewAsync);
        grid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 || eventArgs.ColumnIndex < 0) return;
            var property = grid.Columns[eventArgs.ColumnIndex].DataPropertyName;
            if (property == nameof(HitView.FileName)) OpenSelected(false);
            else if (property == nameof(HitView.FilePath)) OpenSelected(true);
            else if (property == nameof(HitView.Row)) OpenSelectedRow();
        };
        grid.CellToolTipTextNeeded += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0 && grid.Rows[eventArgs.RowIndex].DataBoundItem is HitView view)
                eventArgs.ToolTipText = view.Hit.Path;
        };
        FormClosing += OnClosing;
        Shown += async (_, _) => await GuardAsync(async () =>
        {
            await ReloadCategoriesAsync();
            await RefreshStatisticsAsync();
            targets[0].Focus();
            ConfigureMonitor();
            monitorTimer.Start();
            if (database.GetRoots().Count > 0) Interlocked.Exchange(ref lastChange, Stopwatch.GetTimestamp());
        });
        UpdateEnabled();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 0), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new[] { 52F, 48F, 40F, 110F }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 28));
        layout.RowStyles.Add(new(SizeType.Absolute, 12));
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = "WordFinder", AutoSize = true, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), Location = new Point(0, 0) });
        heading.Controls.Add(new Label { Text = "Excel 本地全文检索 · 原文件只读", AutoSize = true, Location = new Point(210, 17), ForeColor = SystemColors.GrayText });
        layout.Controls.Add(heading, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        toolbar.Controls.AddRange([settingsButton, indexButton, pauseButton, stopButton]);
        var failures = MakeButton("失败文件 / 日志");
        failures.Click += async (_, _) => await GuardAsync(ShowFailuresAsync);
        toolbar.Controls.Add(failures);
        var updates = MakeButton("检查软件更新");
        updates.Click += async (_, _) => await GuardAsync(CheckUpdatesAsync);
        toolbar.Controls.Add(updates);
        var help = MakeButton("使用说明");
        help.Click += (_, _) => ShowHelp();
        toolbar.Controls.Add(help);
        layout.Controls.Add(toolbar, 0, 1);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        filters.Controls.Add(new Label { Text = "分类", AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
        filters.Controls.Add(category);
        filters.Controls.Add(new Label { Text = "多个目标必须在同一行；可以位于不同列。", AutoSize = true, Margin = new Padding(18, 6, 0, 0), ForeColor = SystemColors.GrayText });
        layout.Controls.Add(filters, 0, 2);
        var searchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        searchLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        searchLayout.ColumnStyles.Add(new(SizeType.Absolute, 130));
        searchLayout.Controls.Add(targetList, 0, 0);
        var searchActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 0, 0, 0) };
        searchButton.Width = 104;
        searchButton.Height = 36;
        searchActions.Controls.Add(searchButton);
        var addTarget = MakeButton("＋ 添加目标");
        addTarget.Click += (_, _) => AddTarget();
        searchActions.Controls.Add(addTarget);
        searchLayout.Controls.Add(searchActions, 1, 0);
        layout.Controls.Add(searchLayout, 0, 3);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            Size = new Size(1000, 450), SplitterDistance = 205, Panel1MinSize = 90, Panel2MinSize = 200, SplitterWidth = 6 };
        var results = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        AddColumn("文件名", nameof(HitView.FileName), 19);
        AddColumn("工作表", nameof(HitView.Sheet), 11);
        AddColumn("行号", nameof(HitView.Row), 6);
        AddColumn("分类", nameof(HitView.Categories), 11);
        AddColumn("文件路径", nameof(HitView.FilePath), 28);
        AddColumn("命中预览", nameof(HitView.Preview), 32);
        AddColumn("索引状态", nameof(HitView.State), 9);
        grid.RowTemplate.Height = 29;
        grid.ColumnHeadersHeight = 32;
        results.Controls.Add(grid);
        results.Controls.Add(empty);
        empty.BringToFront();
        split.Panel1.Controls.Add(results);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = SystemColors.Window, Padding = new Padding(8) };
        detail.RowStyles.Add(new(SizeType.AutoSize));
        detail.RowStyles.Add(new(SizeType.AutoSize));
        detail.RowStyles.Add(new(SizeType.Percent, 100));
        detail.Controls.Add(previewTitle, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var copyMatches = MakeButton("复制命中内容");
        copyMatches.Click += async (_, _) => await GuardAsync(() => CopyAsync("matches"));
        var copyRow = MakeButton("复制整行");
        copyRow.Click += async (_, _) => await GuardAsync(() => CopyAsync("row"));
        var copyPath = MakeButton("复制路径");
        copyPath.Click += async (_, _) => await GuardAsync(() => CopyAsync("path"));
        var openFolder = MakeButton("打开文件夹");
        openFolder.Click += (_, _) => OpenSelected(true);
        var openFile = MakeButton("打开文件");
        openFile.Click += (_, _) => OpenSelected(false);
        actions.Controls.AddRange([copyMatches, copyRow, copyPath, openFolder, openFile]);
        detail.Controls.Add(actions, 0, 1);
        preview.Margin = new Padding(0, 8, 0, 0);
        detail.Controls.Add(preview, 0, 2);
        split.Panel2.Controls.Add(detail);
        layout.Controls.Add(split, 0, 4);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        navigation.Controls.AddRange([previousButton, nextButton, pageLabel, statistics]);
        layout.Controls.Add(navigation, 0, 5);
        layout.Controls.Add(fileLabel, 0, 6);
        layout.Controls.Add(progress, 0, 7);
        var bar = new StatusStrip();
        bar.Items.Add(status);
        Controls.Add(layout);
        Controls.Add(bar);
    }

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, Height = 32, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 4),
        BackColor = primary ? Color.FromArgb(30, 94, 173) : SystemColors.Control,
        ForeColor = primary ? Color.White : SystemColors.ControlText,
        FlatStyle = primary ? FlatStyle.Flat : FlatStyle.Standard
    };

    private void AddColumn(string caption, string property, float weight) => grid.Columns.Add(new DataGridViewTextBoxColumn
    { HeaderText = caption, DataPropertyName = property, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });

    private void AddTarget()
    {
        if (targets.Count >= 16) { status.Text = "最多支持 16 个搜索目标。"; return; }
        var row = new FlowLayoutPanel { Height = 36, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 6, 2) };
        var input = new TextBox { Width = 105, PlaceholderText = "输入关键词", MaxLength = 20, AccessibleName = "搜索目标" };
        input.TextChanged += (_, _) =>
        {
            input.Width = Math.Clamp(TextRenderer.MeasureText(input.Text.Length == 0 ? input.PlaceholderText : input.Text, input.Font).Width + 24, 105, 280);
            ScheduleSearch();
        };
        input.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            debounce.Stop();
            await GuardAsync(() => SearchAsync(true));
        };
        row.Controls.Add(input);
        var remove = new Button { Text = "×", Width = 30, Height = 27, Margin = new Padding(4, 2, 0, 0), AccessibleName = "移除搜索目标" };
        remove.Click += (_, _) =>
        {
            if (targets.Count == 1) { input.Clear(); return; }
            targets.Remove(input);
            targetList.Controls.Remove(row);
            row.Dispose();
            ScheduleSearch();
        };
        row.Controls.Add(remove);
        targets.Add(input);
        targetList.Controls.Add(row);
        if (Visible) input.Focus();
    }

    private string[] CurrentTargets() => targets.Select(input => input.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
    private SearchHit? SelectedHit => grid.CurrentRow?.DataBoundItem is HitView view ? view.Hit : null;

    private void ScheduleSearch()
    {
        if (closing) return;
        queryCancellation?.Cancel();
        debounce.Stop();
        debounce.Start();
    }

    private async Task SearchAsync(bool reset)
    {
        if (closing) return;
        debounce.Stop();
        queryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        var text = CurrentTargets();
        if (reset) { history.Clear(); cursor = 0; }
        if (text.Length == 0)
        {
            page = new([], false);
            grid.DataSource = null;
            empty.Text = "在目标框中输入要查找的内容。\n多个目标在同一行分别命中即可。";
            empty.Visible = true;
            pageLabel.Text = "";
            UpdateEnabled();
            queryCancellation = null;
            return;
        }
        var root = (category.SelectedItem as CategoryChoice)?.Id;
        var watch = Stopwatch.StartNew();
        status.Text = "正在搜索本机索引…";
        try
        {
            await searchGate.WaitAsync(cancellation.Token);
            SearchPage result;
            try { result = await Task.Run(() => search.Search(text, root, cursor, cancellationToken: cancellation.Token), cancellation.Token); }
            finally { searchGate.Release(); }
            if (cancellation.IsCancellationRequested || closing) return;
            page = result;
            grid.DataSource = result.Hits.Select(hit => new HitView(hit)).ToList();
            empty.Text = "没有符合全部目标的行。\n可减少目标或切换分类后重试。";
            empty.Visible = result.Hits.Count == 0;
            pageLabel.Text = $"第 {history.Count + 1} 页 · 本页 {result.Hits.Count} 行" + (result.HasMore ? " · 还有更多" : "");
            status.Text = $"搜索完成 · {watch.ElapsedMilliseconds:N0} ms · 只查询本地索引";
            UpdateEnabled();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(queryCancellation, cancellation)) queryCancellation = null; }
    }

    private async Task LoadPreviewAsync()
    {
        var request = ++previewRequest;
        var hit = SelectedHit;
        if (hit is null) { preview.Clear(); previewTitle.Text = "选中结果查看命中内容"; return; }
        var cells = await Task.Run(() => search.GetCells(hit.RowId));
        if (request != previewRequest || closing) return;
        previewTitle.Text = $"{Path.GetFileName(hit.Path)}  /  {hit.Sheet}  /  第 {hit.Row:N0} 行" + (hit.IsStale ? "  · 当前显示旧索引，请更新或查看失败详情" : "");
        var queries = CurrentTargets().Select(TextStorage.Normalize).ToArray();
        var content = new StringBuilder();
        content.AppendLine(hit.Path).AppendLine();
        foreach (var cell in cells.Where(cell => queries.Any(target => TextStorage.Contains(cell.Text, target))))
        {
            var matches = queries.Where(target => TextStorage.Contains(cell.Text, target));
            var caption = string.Join("、", matches);
            content.Append('[').Append(cell.Address(hit.Row)).Append(']');
            if (caption.Length > 0) content.Append("  命中 ").Append(caption);
            content.Append("  ").AppendLine(cell.Text.Length > 2000 ? cell.Text[..2000] + "…（预览已缩短，复制整行可获取完整内容）" : cell.Text);
        }
        preview.Text = content.ToString();
    }

    private async Task ReloadCategoriesAsync()
    {
        var selected = (category.SelectedItem as CategoryChoice)?.Id;
        var roots = await Task.Run(database.GetRoots);
        updatingCategories = true;
        try
        {
            var choices = new[] { new CategoryChoice(null, "全部分类") }.Concat(roots.Select(root => new CategoryChoice(root.Id, root.Label))).ToList();
            category.DisplayMember = nameof(CategoryChoice.Label);
            category.DataSource = choices;
            category.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected) ?? choices[0];
        }
        finally { updatingCategories = false; }
    }

    private async Task RefreshStatisticsAsync()
    {
        var data = await Task.Run(database.GetStatistics);
        if (closing) return;
        statistics.Text = $"{data.Files:N0} 个文件 · {data.Rows:N0} 行 · 索引 {FormatBytes(data.StorageBytes)}";
        if (data.Files > 0 && CurrentTargets().Length == 0)
            empty.Text = "在目标框中输入要查找的内容。\n多个目标在同一行分别命中即可。";
    }

    private async Task OpenSettingsAsync()
    {
        if (runningTask is not null) return;
        settingsOpen = true;
        var indexClearRequested = false;
        try
        {
            using var dialog = new SettingsForm(database, settings);
            dialog.ShowDialog(this);
            indexClearRequested = dialog.IndexClearRequested;
        }
        finally { settingsOpen = false; }
        if (indexClearRequested)
        {
            automaticSuspended = true;
            Interlocked.Exchange(ref lastChange, 0);
        }
        ConfigureMonitor();
        await ReloadCategoriesAsync();
        await RefreshStatisticsAsync();
        await SearchAsync(true);
        if (indexClearRequested) status.Text = "自动更新已暂停，点击更新索引可恢复。";
        if (database.GetRoots().Count > 0 && !automaticSuspended)
            Interlocked.Exchange(ref lastChange, Stopwatch.GetTimestamp());
    }

    private void ConfigureMonitor()
    {
        var errors = monitor.Configure(database.GetRoots());
        if (errors.Count > 0) status.Text = "部分目录无法监听，将定期重新检查：" + string.Join("；", errors);
    }

    private async Task CheckForChangesAsync()
    {
        if (closing || settingsOpen || automaticSuspended) return;
        var error = Interlocked.Exchange(ref monitorError, 0) != 0;
        if (error || reconciliation.Elapsed >= TimeSpan.FromMinutes(15))
        {
            reconciliation.Restart();
            ConfigureMonitor();
            Interlocked.Exchange(ref lastChange, Stopwatch.GetTimestamp());
        }
        var change = Interlocked.Read(ref lastChange);
        if (change == 0 || Stopwatch.GetElapsedTime(change) < TimeSpan.FromSeconds(3) || runningTask is not null) return;
        if (Interlocked.CompareExchange(ref lastChange, 0, change) != change) return;
        if (database.GetRoots().Count == 0) return;
        status.Text = "检测到文件变化，正在自动更新索引…";
        await StartIndexAsync(false);
    }

    private async Task StartIndexAsync(bool retryOnly)
    {
        if (runningTask is not null) return;
        if (database.GetRoots().Count == 0)
        {
            await OpenSettingsAsync();
            status.Text = "添加文件夹后，点击“更新索引”开始。";
            return;
        }
        controller = new(database, settings.MemoryLimitMiB);
        runningTask = controller.RunAsync(retryOnly);
        refreshTimer.Start();
        UpdateEnabled();
        try
        {
            var result = await runningTask;
            status.Text = result.Cancelled ? "索引已停止；自动更新已暂停，点击更新索引可恢复" : $"索引完成：更新 {result.Parsed:N0}，跳过 {result.Skipped:N0}，失败 {result.Failed:N0}";
            if (controller.Messages.Count > 0) status.Text += " · 详情见失败文件 / 日志";
        }
        finally
        {
            runningTask = null;
            refreshTimer.Stop();
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = 0;
            fileLabel.Text = "";
            pauseButton.Text = "暂停";
            UpdateEnabled();
        }
        if (!closing)
        {
            await RefreshStatisticsAsync();
            if (CurrentTargets().Length > 0) await SearchAsync(true);
        }
    }

    private void UpdateProgress()
    {
        if (controller is null) return;
        var state = controller.Latest;
        progress.Style = state.Total == 0 && state.Phase is not "done" ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        if (state.Total > 0) progress.Value = Math.Clamp((int)(1000L * state.Completed / state.Total), 0, 1000);
        fileLabel.Text = $"{state.Message}  {state.File}";
        status.Text = state.Total > 0
            ? $"已完成 {state.Completed:N0} / {state.Total:N0} · 更新 {state.Parsed:N0} · 跳过 {state.Skipped:N0} · 失败 {state.Failed:N0}" + (state.Cells > 0 ? $" · 当前 {state.Cells:N0} 单元格" : "")
            : state.Message;
    }

    private void UpdateEnabled()
    {
        var active = runningTask is not null;
        settingsButton.Enabled = !active;
        indexButton.Enabled = !active;
        pauseButton.Enabled = active;
        stopButton.Enabled = active;
        previousButton.Enabled = history.Count > 0;
        nextButton.Enabled = page.HasMore;
    }

    private async Task CopyAsync(string kind)
    {
        var hit = SelectedHit;
        if (hit is null) { status.Text = "请先选择一行结果。"; return; }
        string text;
        if (kind == "path") text = hit.Path;
        else if (kind == "row") text = await Task.Run(() => search.CopyRow(hit.RowId));
        else
        {
            var queries = CurrentTargets().Select(TextStorage.Normalize).ToArray();
            var cells = await Task.Run(() => search.GetCells(hit.RowId));
            text = string.Join(Environment.NewLine, cells.Where(cell => queries.Any(target => TextStorage.Contains(cell.Text, target))).Select(cell => cell.Text));
        }
        if (text.Length == 0) return;
        Clipboard.SetText(text);
        status.Text = "已复制到剪贴板。";
    }

    private void OpenSelected(bool folder)
    {
        var hit = SelectedHit;
        if (hit is null) { status.Text = "请先选择一行结果。"; return; }
        try
        {
            if (folder)
            {
                var directory = Path.GetDirectoryName(hit.Path)!;
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("原文件夹已不存在。");
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{hit.Path}\"") { UseShellExecute = true });
            }
            else
            {
                if (!File.Exists(hit.Path)) throw new FileNotFoundException("源文件已不存在，当前结果来自本机索引。");
                Process.Start(new ProcessStartInfo(hit.Path) { UseShellExecute = true });
            }
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void OpenSelectedRow()
    {
        var hit = SelectedHit;
        if (hit is null) return;
        if (!File.Exists(hit.Path)) { ShowError(new FileNotFoundException("源文件已不存在。")); return; }
        var excelType = Type.GetTypeFromProgID("Excel.Application");
        if (excelType is null)
        {
            MessageBox.Show(this, "跳转到指定行需要安装 Microsoft Excel。可以双击文件名使用默认程序打开。", "无法定位行");
            return;
        }
        dynamic? excel = null;
        try
        {
            excel = Activator.CreateInstance(excelType)!;
            excel.AutomationSecurity = 3; // Disable workbook macros during the explicit open.
            dynamic book = excel.Workbooks.Open(hit.Path, UpdateLinks: 0, ReadOnly: true);
            dynamic sheet = book.Worksheets[hit.Sheet];
            excel.Visible = true;
            sheet.Activate();
            excel.Goto(sheet.Cells[hit.Row, 1], true);
            status.Text = "已在 Excel 中定位到所选行（只读）。";
        }
        catch (Exception exception)
        {
            if (excel is not null) { try { excel.Quit(); } catch (Exception) { } }
            ShowError(new InvalidOperationException("无法定位到该行，请确认工作表仍存在且 Excel 可打开文件。\n" + exception.Message));
        }
    }

    private async Task ShowFailuresAsync()
    {
        var failures = await Task.Run(database.GetFailures);
        var text = new StringBuilder();
        text.AppendLine($"未完成文件：{failures.Count:N0} 个").AppendLine();
        foreach (var file in failures.Take(500)) text.AppendLine(file.Path).AppendLine(file.Message + (file.HasPreviousIndex ? "（旧索引仍可查询）" : "")).AppendLine();
        if (failures.Count > 500) text.AppendLine("界面仅显示前 500 个文件，完整详情请查看数据目录 index.log。");
        if (controller is not null && controller.Messages.Count > 0)
            text.AppendLine("本次任务提示：").AppendLine(string.Join(Environment.NewLine, controller.Messages));
        using var dialog = new TextDialog("失败文件与任务提示", text.ToString());
        if (failures.Count > 0 && runningTask is null)
            dialog.Controls.Add(new Button { Text = "重试全部未完成文件", Dock = DockStyle.Bottom, Height = 38, DialogResult = DialogResult.Retry });
        if (dialog.ShowDialog(this) == DialogResult.Retry) await StartIndexAsync(true);
    }

    private async Task CheckUpdatesAsync()
    {
        if (settings.GitHubRepository.Length == 0)
        {
            MessageBox.Show(this, "尚未配置 GitHub 发布仓库。请在“目录和设置 → 资源与数据”填写 owner/repo。", "检查软件更新");
            return;
        }
        status.Text = "正在检查软件版本，本机搜索仍可使用…";
        var release = await UpdateClient.CheckAsync(settings.GitHubRepository, CancellationToken.None);
        if (release is null) { status.Text = "当前已是最新正式版本。"; return; }
        using var dialog = new TextDialog($"发现新版本 {release.Version}", $"当前版本：{VersionText}\r\n新版本：{release.Version}\r\n\r\n{release.Notes}\r\n\r\n下载后关闭软件，再替换 WordFinder.exe。配置和兼容索引将保留。");
        dialog.Controls.Add(new Button { Text = "打开 GitHub 下载页面", Dock = DockStyle.Bottom, Height = 38, DialogResult = DialogResult.OK });
        if (dialog.ShowDialog(this) == DialogResult.OK) Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowHelp()
    {
        using var dialog = new TextDialog($"WordFinder 使用说明 · {VersionText}", """
            1. 点击“目录和设置”，添加一个或多个本机文件夹，可重命名分类标记。
            2. 点击“更新索引”，等待文件完成；最小化后可以继续后台运行。
            3. 每个目标框输入一段内容，例如目标1“张三”、目标2“上海”。
               同一行中分别有单元格包含全部目标即可，不会跨行或跨表组合。
               空格按原样匹配，英文忽略大小写，留空目标不参与搜索。
            4. 可按分类筛选，选中结果查看内容，复制整行可直接粘贴到 Excel。

            索引与搜索不启动 Excel，原文件始终只读。
            日期、编号和公式按文件中保存的值及格式索引；不会重新计算公式。
            隐藏工作表和隐藏行列也会被索引。
            软件打开期间自动监听文件变化，启动时也会增量核对；可手动点击“更新索引”。
            点击停止会暂停自动更新，手动点击更新索引或重新打开软件后恢复。
            旧文件更新失败时会保留旧索引并标记；错误详情可在“失败文件 / 日志”查看。
            内存不足可到设置中提高索引额度后重试。首版暂不读取加密文件。
            关闭软件会停止任务并保存完成文件，下次从未完成文件重新开始。

            开发技术：.NET / WinForms、ExcelDataReader、ExcelNumberFormat、SQLite FTS5。
            数据保存在当前 Windows 用户的本机目录，GitHub 仅用于程序版本检查和下载。
            """);
        var licenses = new Button { Text = "开源组件与许可", Dock = DockStyle.Bottom, Height = 36 };
        licenses.Click += (_, _) =>
        {
            var assembly = typeof(MainForm).Assembly;
            var notices = new StringBuilder();
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name == "WordFinder.ThirdPartyNotices" || name.StartsWith("WordFinder.Licenses.", StringComparison.Ordinal)).Order())
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                notices.AppendLine(resource).AppendLine(reader.ReadToEnd()).AppendLine();
            }
            using var licenseDialog = new TextDialog("开源组件许可", notices.ToString());
            licenseDialog.ShowDialog(dialog);
        };
        dialog.Controls.Add(licenses);
        dialog.ShowDialog(this);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs args)
    {
        if (exitReady) return;
        args.Cancel = true;
        if (closing) return;
        closing = true;
        debounce.Stop();
        refreshTimer.Stop();
        monitorTimer.Stop();
        monitor.Dispose();
        queryCancellation?.Cancel();
        Enabled = false;
        try
        {
            var task = runningTask;
            if (controller is not null && task is not null)
            {
                await controller.StopAsync();
                try { await task; } catch (Exception) { /* Progress is recovered next time. */ }
            }
        }
        finally
        {
            exitReady = true;
            Close();
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!closing) ShowError(exception); }
    }

    private void ShowError(Exception exception)
    {
        status.Text = "操作未完成，请查看提示。";
        MessageBox.Show(this, exception.Message, "WordFinder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):N2} GB" : $"{bytes / (1024d * 1024):N1} MB";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { debounce.Dispose(); refreshTimer.Dispose(); monitorTimer.Dispose(); monitor.Dispose(); }
        base.Dispose(disposing);
    }
}
