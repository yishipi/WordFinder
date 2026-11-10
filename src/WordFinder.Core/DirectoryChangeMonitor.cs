namespace WordFinder.Core;

/// <summary>Watches configured local roots; callers serialize and debounce indexing.</summary>
public sealed class DirectoryChangeMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Action<bool> changed;

    public DirectoryChangeMonitor(Action<bool> changed) => this.changed = changed;

    public IReadOnlyList<string> Configure(IEnumerable<DirectoryRoot> roots)
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        var errors = new List<string>();
        foreach (var root in roots)
        {
            FileSystemWatcher? files = null;
            FileSystemWatcher? directories = null;
            try
            {
                files = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024
                };
                directories = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName,
                    InternalBufferSize = 16 * 1024
                };
                files.Changed += (_, args) => { if (IsExcel(args.FullPath)) changed(false); };
                files.Created += (_, args) => { if (IsExcel(args.FullPath)) changed(false); };
                files.Deleted += (_, args) => { if (IsExcel(args.FullPath)) changed(false); };
                files.Renamed += (_, args) => { if (IsExcel(args.FullPath) || IsExcel(args.OldFullPath)) changed(false); };
                directories.Created += (_, _) => changed(false);
                directories.Deleted += (_, _) => changed(false);
                directories.Renamed += (_, _) => changed(false);
                files.Error += (_, _) => changed(true);
                directories.Error += (_, _) => changed(true);
                files.EnableRaisingEvents = true;
                directories.EnableRaisingEvents = true;
                watchers.Add(files);
                watchers.Add(directories);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                files?.Dispose();
                directories?.Dispose();
                errors.Add($"{root.Path}：{exception.Message}");
            }
        }
        return errors;
    }

    private static bool IsExcel(string path) => RelevantExcel(path) &&
        !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    private static bool RelevantExcel(string path) => Path.GetExtension(path).ToUpperInvariant() is ".XLS" or ".XLSX" or ".XLSM" or ".XLSB";

    public void Dispose()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }
}
