using System.Net.Http.Headers;
using System.Text.Json;

namespace WordFinder.Desktop;

internal sealed record ReleaseInfo(string Version, string Notes, Uri Page);

internal static class UpdateClient
{
    public static async Task<ReleaseInfo?> CheckAsync(string repository, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WordFinder", "0.2.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("仓库不存在、尚无正式版本，或仓库不是公开发行仓库。");
        if ((int)response.StatusCode is 403 or 429)
            throw new InvalidOperationException("GitHub 暂时限制了检查请求，请稍后重试。本机搜索仍可使用。");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (root.GetProperty("prerelease").GetBoolean() || root.GetProperty("draft").GetBoolean()) return null;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote))
            throw new InvalidOperationException("发布版本号无法识别，请使用 v1.2.3 格式。");
        var current = typeof(UpdateClient).Assembly.GetName().Version!;
        remote = new(remote.Major, remote.Minor, Math.Max(0, remote.Build), Math.Max(0, remote.Revision));
        if (remote <= current) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(tag, notes, new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag)}"));
    }
}
