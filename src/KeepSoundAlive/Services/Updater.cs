using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace KeepSoundAlive.Services;

public sealed record UpdateInfo(Version Version, string Tag, string AssetName, Uri AssetUrl, long Size, string Sha256);

/// <summary>
/// Self-update from GitHub releases. It only runs when the user asks for it (the update button or --update);
/// otherwise the app makes no network requests at all.
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/labpak/KeepSoundAlive/releases/latest";
    private const string ChecksumsAsset = "SHA256SUMS.txt";

    public const string ReleasesPage = AppInfo.RepositoryUrl + "/releases/latest";

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KeepSoundAlive", AppInfo.Version));
        return client;
    });

    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Only the published single-file executable can replace itself. A development build is several files,
    /// and under "dotnet KeepSoundAlive.dll" the process path is dotnet itself.
    /// </summary>
    public static bool CanUpdateInPlace =>
        string.IsNullOrEmpty(typeof(Updater).Assembly.Location) && Environment.ProcessPath is not null;

    /// <summary>The release file for this OS and architecture, e.g. KeepSoundAlive-win-x64.exe.</summary>
    public static string? AssetName { get; } = GetAssetName();

    /// <summary>Returns the newer release, or null when <paramref name="current"/> is already the latest.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var response = await Http.Value.GetAsync(LatestReleaseApi, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var release = json.RootElement;

        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= current)
        {
            return null;
        }

        var assetName = AssetName ?? throw new PlatformNotSupportedException("There is no release build for this platform.");
        Uri? assetUrl = null, checksumsUrl = null;
        long size = 0;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == assetName && url is not null)
            {
                assetUrl = new Uri(url);
                size = asset.GetProperty("size").GetInt64();
            }
            else if (name == ChecksumsAsset && url is not null)
            {
                checksumsUrl = new Uri(url);
            }
        }

        if (assetUrl is null || checksumsUrl is null)
        {
            throw new InvalidOperationException($"Release {tag} has no {(assetUrl is null ? assetName : ChecksumsAsset)}.");
        }

        var checksums = await Http.Value.GetStringAsync(checksumsUrl, ct);
        var sha256 = FindChecksum(checksums, assetName)
                     ?? throw new InvalidOperationException($"{ChecksumsAsset} of {tag} has no entry for {assetName}.");

        return new UpdateInfo(Normalize(latest), tag, assetName, assetUrl, size, sha256);
    }


    /// <summary>
    /// Downloads the update next to <paramref name="exePath"/> as "*.new" and verifies its checksum.
    /// Nothing is replaced yet: call <see cref="ApplyAfterExit"/> and exit.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, string exePath, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var newPath = exePath + ".new";
        try
        {
            using (var response = await Http.Value.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? update.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(newPath);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total);
                    }
                }

                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The downloaded file doesn't match its checksum.");
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(newPath, File.GetUnixFileMode(exePath) | UnixFileMode.UserExecute);
            }

            return newPath;
        }
        catch
        {
            TryDelete(newPath);
            throw;
        }
    }

    /// <summary>
    /// Hands the swap over to a small system shell script that waits for this process to exit, moves
    /// <paramref name="newPath"/> over <paramref name="exePath"/> and optionally starts it.
    /// The running executable must not be touched while it runs: a single-file app loads its assemblies lazily
    /// from its own file, so replacing it underneath would break every later assembly load.
    /// </summary>
    public static void ApplyAfterExit(string newPath, string exePath, bool restart)
    {
        var pid = Environment.ProcessId.ToString();
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            var script = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                Wait-Process -Id {{pid}} -Timeout 60
                for ($i = 0; $i -lt 20; $i++) {
                    try { Move-Item -LiteralPath {{Quote(newPath)}} -Destination {{Quote(exePath)}} -Force -ErrorAction Stop; break }
                    catch { Start-Sleep -Milliseconds 500 }
                }
                if (${{(restart ? "true" : "false")}} -and -not (Test-Path -LiteralPath {{Quote(newPath)}})) { Start-Process -FilePath {{Quote(exePath)}} }
                """;
            start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                    "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)),
                },
                CreateNoWindow = true,
            };
        }
        else
        {
            const string script =
                "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; " +
                "mv -f \"$2\" \"$3\" && [ \"$4\" = 1 ] && nohup \"$3\" >/dev/null 2>&1 &";
            start = new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", script, "sh", pid, newPath, exePath, restart ? "1" : "0" },
            };
        }

        start.UseShellExecute = false;
        using var _ = Process.Start(start);
    }

    /// <summary>Removes a stale "*.new" download left by an update that couldn't be applied.</summary>
    public static void CleanUpLeftovers()
    {
        if (Environment.ProcessPath is { } exe)
        {
            TryDelete(exe + ".new");
        }
    }

    internal static string? FindChecksum(string checksums, string fileName)
    {
        // sha256sum format: "<hex>  <name>" (or "<hex> *<name>" for binary mode).
        foreach (var raw in checksums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space > 0 && line[space..].Trim().TrimStart('*') == fileName)
            {
                return line[..space];
            }
        }

        return null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string? GetAssetName()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        return arch is null ? null
            : OperatingSystem.IsWindows() ? $"KeepSoundAlive-win-{arch}.exe"
            : OperatingSystem.IsLinux() ? $"KeepSoundAlive-linux-{arch}"
            : null;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path); // no-op when the file doesn't exist
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
