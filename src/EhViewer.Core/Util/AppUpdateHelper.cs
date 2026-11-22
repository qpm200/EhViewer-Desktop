using System.Text.Json;

namespace EhViewer.Core.Util;

public sealed record AppUpdateInfo(
    string Version,
    int VersionCode,
    string? DownloadUrl,
    bool MustUpdate,
    string? Title,
    string? ReleaseNotes);

public sealed record AppUpdateCheckResult(bool HasUpdate, AppUpdateInfo? Latest);

/// <summary>
/// Parses upstream <c>AppUpdater</c> or GitHub Release JSON feeds and compares semantic versions.
/// </summary>
public static class AppUpdateHelper
{
    public static AppUpdateCheckResult ParseAndCheck(string json, string currentVersion, int currentVersionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AppUpdateCheckResult(false, null);
        }

        AppUpdateInfo? info = root.TryGetProperty("version", out _)
            ? ParseUpstreamJson(root)
            : root.TryGetProperty("tag_name", out _)
                ? ParseGitHubReleaseJson(root)
                : null;

        if (info is null || string.IsNullOrWhiteSpace(info.Version))
        {
            return new AppUpdateCheckResult(false, null);
        }

        int cmp = CompareVersion(currentVersion, info.Version);
        bool hasUpdate = cmp < 0 || (cmp == 0 && info.VersionCode > currentVersionCode);
        return new AppUpdateCheckResult(hasUpdate, info);
    }

    public static int CompareVersion(string? left, string? right)
    {
        int[] leftParts = ParseNumericParts(left);
        int[] rightParts = ParseNumericParts(right);
        int maxLen = Math.Max(leftParts.Length, rightParts.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int l = i < leftParts.Length ? leftParts[i] : 0;
            int r = i < rightParts.Length ? rightParts[i] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }

        return 0;
    }

    private static AppUpdateInfo ParseUpstreamJson(JsonElement root)
    {
        string version = GetString(root, "version") ?? "0.0.0";
        int versionCode = root.TryGetProperty("versionCode", out JsonElement vc) && vc.TryGetInt32(out int code)
            ? code
            : 0;
        string? downloadUrl = GetString(root, "fileDownloadUrl");
        bool mustUpdate = root.TryGetProperty("mustUpdate", out JsonElement mu) &&
            mu.ValueKind == JsonValueKind.True;
        string? title = null;
        string? notes = null;

        if (root.TryGetProperty("updateContent", out JsonElement content) && content.ValueKind == JsonValueKind.Object)
        {
            title = GetString(content, "title");
            notes = GetString(content, "content");
        }

        return new AppUpdateInfo(version, versionCode, downloadUrl, mustUpdate, title, notes);
    }

    private static AppUpdateInfo ParseGitHubReleaseJson(JsonElement root)
    {
        string rawTag = GetString(root, "tag_name") ?? "0.0.0";
        string version = rawTag.Trim().TrimStart('v', 'V');
        string? title = GetString(root, "name") ?? rawTag;
        string? notes = GetString(root, "body");
        string? downloadUrl = GetString(root, "html_url");

        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? assetUrl = GetString(asset, "browser_download_url");
                if (!string.IsNullOrWhiteSpace(assetUrl))
                {
                    downloadUrl = assetUrl;
                    break;
                }
            }
        }

        return new AppUpdateInfo(version, 0, downloadUrl, MustUpdate: false, title, notes);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int[] ParseNumericParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return [0];
        }

        string cleaned = version.Trim().TrimStart('v', 'V');
        int suffixIdx = cleaned.IndexOfAny(['-', '+']);
        if (suffixIdx >= 0)
        {
            cleaned = cleaned[..suffixIdx];
        }

        string[] segments = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return [0];
        }

        int[] result = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            int.TryParse(segments[i], out result[i]);
        }

        return result;
    }
}
