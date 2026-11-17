using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using WordFinder.Core;
using WordFinder.Desktop;
using WordFinder.Tests;

namespace WordFinder.Benchmarks;

internal static class DesktopChecks
{
    public static void Run(string directory)
    {
        Exception? failure = null;
        // Console benchmark entry points may be MTA. Keep the form and its
        // continuations on one STA thread with a real, persistent message loop.
        var thread = new Thread(() =>
        {
            try { RunOnUiThread(directory); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Desktop regression checks timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunOnUiThread(string directory)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Control.CheckForIllegalCrossThreadCalls = true;
        Application.EnableVisualStyles();
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
        var root = Path.Combine(directory, "ui-regression", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        WorkbookFixture.Write(Path.Combine(source, "pages.xlsx"), new TestSheet("Sheet",
            Enumerable.Range(1, 150).Select(row => new TestCell(row, "A", "needle")).ToArray()));
        var database = new IndexDatabase(Path.Combine(root, "data"));
        database.Initialize();
        database.AddRoot(source);
        new IndexRunner(database).Run(Guid.NewGuid().ToString("N"), false);
        using var form = new MainForm(database, new AppSettings()) { ShowInTaskbar = false, Opacity = 0 };
        using var loop = new ApplicationContext();
        typeof(MainForm).GetField("automaticSuspended", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
        Exception? failure = null;
        var completed = false;
        form.Shown += async (_, _) =>
        {
            try
            {
                // Post into Application.Run instead of pumping DoEvents: a
                // temporary outermost loop uninstalls the UI sync context.
                await Task.Yield();
                await CheckAsync(form);
                completed = true;
            }
            catch (Exception exception) { failure = exception; }
            finally { form.Close(); loop.ExitThread(); }
        };
        form.Show();
        // The check closes the form while a query is pending. Keep pumping
        // until its cancellation continuation has also been verified.
        Application.Run(loop);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Require(completed, "The UI loop exited before the checks completed.");
    }

    private static async Task CheckAsync(MainForm form)
    {
        Require(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "UI synchronization context is missing.");
        var uiThread = Environment.CurrentManagedThreadId;
        await WaitUntilAsync(() => Field<System.Windows.Forms.Timer>(form, "monitorTimer").Enabled, "Window initialization");
        Field<System.Windows.Forms.Timer>(form, "monitorTimer").Stop();
        var input = Field<List<TextBox>>(form, "targets")[0];
        var grid = Field<DataGridView>(form, "grid");
        var next = Field<Button>(form, "nextButton");
        var previous = Field<Button>(form, "previousButton");
        var gate = Field<SemaphoreSlim>(form, "searchGate");
        var paints = 0;
        var bindings = 0;
        grid.CellPainting += (_, _) => { Require(Environment.CurrentManagedThreadId == uiThread, "Painting left the UI thread."); paints++; };
        grid.DataBindingComplete += (_, _) => { Require(Environment.CurrentManagedThreadId == uiThread, "Binding left the UI thread."); bindings++; };
        SetTarget("needle");
        await SearchAsync();
        CheckPage(1, 50, 0);
        Paint();
        for (var round = 0; round < 10; round++)
        {
            gate.Wait();
            try
            {
                next.PerformClick();
                next.PerformClick();
                Require(!next.Enabled, "Navigation remained enabled during search.");
            }
            finally { gate.Release(); }
            await WaitForSearchAsync();
            CheckPage(51, 100, 1);
            Paint();
            gate.Wait();
            try
            {
                next.PerformClick();
                Field<CancellationTokenSource>(form, "queryCancellation").Cancel();
                await WaitForSearchAsync();
            }
            finally { gate.Release(); }
            CheckPage(51, 100, 1);
            previous.PerformClick();
            await WaitForSearchAsync();
            CheckPage(1, 50, 0);
            Paint();
        }
        SetTarget("no-matching-row");
        await SearchAsync();
        Require(grid.Rows.Count == 0, "Empty results retained old rows.");
        SetTarget("");
        await SearchAsync();
        Require(grid.DataSource is null, "Cleared search retained its data source.");
        SetTarget("needle");
        await SearchAsync();
        Paint();
        Require(paints > 0 && bindings > 0, "The real grid was not painted and bound.");
        Require(Environment.CurrentManagedThreadId == uiThread && SynchronizationContext.Current is WindowsFormsSynchronizationContext,
            "Async checks left the UI synchronization context.");
        // Closing must cancel a pending query without accessing disposed controls.
        gate.Wait();
        try
        {
            var pending = SearchAsync();
            form.Close();
            await pending;
            Require(form.IsDisposed, "The test window did not close.");
        }
        finally { gate.Release(); }
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Rounds = 10, Paints = paints, Bindings = bindings,
            UiThread = uiThread, CrossThreadChecks = Control.CheckForIllegalCrossThreadCalls, EmptyResults = true, CancelledClose = true }));

        void SetTarget(string value)
        {
            input.Text = value;
            Field<System.Windows.Forms.Timer>(form, "debounce").Stop();
        }
        Task SearchAsync() => (Task)typeof(MainForm).GetMethod("SearchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, [true, null])!;
        Task WaitForSearchAsync() => WaitUntilAsync(() => Field<CancellationTokenSource?>(form, "queryCancellation") is null, "Search completion");
        void CheckPage(int first, int last, int depth)
        {
            var page = Field<SearchPage>(form, "page");
            Require(page.Hits[0].Row == first && page.Hits[^1].Row == last && Field<Stack<long>>(form, "history").Count == depth,
                "Pagination results or history changed unexpectedly.");
            Require(Field<Label>(form, "pageLabel").Text.StartsWith($"第 {depth + 1} 页"), "Pagination label is incorrect.");
        }
        void Paint()
        {
            using var image = new Bitmap(grid.Width, grid.Height);
            grid.DrawToBitmap(image, grid.ClientRectangle);
        }
    }

    private static T Field<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;

    private static async Task WaitUntilAsync(Func<bool> condition, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(operation);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
