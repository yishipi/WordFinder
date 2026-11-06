using System.Text.Json;

namespace WordFinder.Core;

public sealed class AppSettings
{
    public int MemoryLimitMiB { get; set; } = 512;
    public string GitHubRepository { get; set; } = "yishipi/WordFinder";

    public static AppSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("设置文件为空，请恢复 settings.json 后重试。");
        settings.Validate();
        return settings;
    }

    public void Save(string directory)
    {
        Validate();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public void Validate()
    {
        if (MemoryLimitMiB is < 256 or > 32768)
            throw new ArgumentException("索引内存额度应为 256–32768 MB。");
        GitHubRepository = GitHubRepository.Trim();
        if (GitHubRepository.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(
                GitHubRepository, @"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("GitHub 仓库请填写为 owner/repo，例如 example/WordFinder。");
    }
}
