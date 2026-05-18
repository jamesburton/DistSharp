using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace DistSharp.Providers.Onnx;

/// <summary>Resolves and downloads ONNX model variants from the Hugging Face Hub cache.</summary>
/// <remarks>
/// Hub layout: <c>&lt;cacheRoot&gt;/hub/models--&lt;owner&gt;--&lt;name&gt;/snapshots/&lt;sha&gt;/&lt;variant&gt;/</c>.
/// Phase 1 variant priority: <c>cpu-int4-rtn-block-32-acc-level-4</c> → first <c>cpu-int4-*</c> → first <c>cpu-fp16-*</c> → <c>cpu-fp32-*</c>.
/// TODO (Phase 2): honour <c>HUGGINGFACE_HUB_CACHE</c> env var in addition to <c>HF_HOME</c>.
/// </remarks>
public sealed class HuggingFaceModelCache
{
    private const string HfBaseUrl = "https://huggingface.co";

    private static readonly string[] VariantPriority =
    [
        "cpu-int4-rtn-block-32-acc-level-4",
    ];

    private readonly string cacheRoot;
    private readonly HttpClient http;

    /// <summary>Initializes a new instance of the <see cref="HuggingFaceModelCache"/> class.</summary>
    /// <param name="cacheRoot">Override for the HF cache root. When <see langword="null"/>, resolved from the environment.</param>
    /// <param name="http">The HTTP client used for HF Hub API calls.</param>
    public HuggingFaceModelCache(string? cacheRoot, HttpClient http)
    {
        this.cacheRoot = cacheRoot ?? ResolveDefaultCacheRoot();
        this.http = http;
    }

    /// <summary>Resolves the local path of a model variant directory, if cached.</summary>
    /// <param name="repoId">The Hugging Face repo id, e.g. <c>microsoft/Phi-4-mini-instruct-onnx</c>.</param>
    /// <param name="variantOverride">An exact variant subdirectory name to require. When <see langword="null"/>, variant priority order is used.</param>
    /// <returns>The absolute path to the variant directory, or <see langword="null"/> if the repo is not cached.</returns>
    public string? ResolveLocalVariant(string repoId, string? variantOverride)
    {
        var snapshotDir = this.FindLatestSnapshot(repoId);
        if (snapshotDir is null)
        {
            return null;
        }

        return SelectVariant(snapshotDir, variantOverride);
    }

    /// <summary>
    /// Resolves the local path of a model variant directory, downloading the model from HF Hub if not cached.
    /// </summary>
    /// <param name="repoId">The Hugging Face repo id.</param>
    /// <param name="variantOverride">An exact variant subdirectory name to require, or <see langword="null"/> to use priority order.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The absolute path to the variant directory.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no suitable variant is found after downloading.</exception>
    public async Task<string> ResolveVariantAsync(string repoId, string? variantOverride, CancellationToken cancellationToken)
    {
        var local = this.ResolveLocalVariant(repoId, variantOverride);
        if (local is not null)
        {
            return local;
        }

        await this.DownloadRepoAsync(repoId, variantOverride, cancellationToken).ConfigureAwait(false);

        return this.ResolveLocalVariant(repoId, variantOverride)
            ?? throw new InvalidOperationException($"ONNX: Model '{repoId}' was downloaded but no suitable variant directory was found.");
    }

    /// <summary>Lists the Hugging Face repo IDs that are present in the local hub cache.</summary>
    /// <returns>An enumerable of repo IDs, e.g. <c>microsoft/Phi-4-mini-instruct-onnx</c>.</returns>
    public IEnumerable<string> ListCachedRepos()
    {
        var hubDir = Path.Combine(this.cacheRoot, "hub");
        if (!Directory.Exists(hubDir))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(hubDir))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith("models--", StringComparison.Ordinal))
            {
                continue;
            }

            // models--owner--name → owner/name
            var withoutPrefix = name["models--".Length..];
            var separatorIndex = withoutPrefix.IndexOf("--", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                continue;
            }

            var owner = withoutPrefix[..separatorIndex];
            var repoName = withoutPrefix[(separatorIndex + 2)..];
            yield return $"{owner}/{repoName}";
        }
    }

    private static string ResolveDefaultCacheRoot()
    {
        var hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrEmpty(hfHome))
        {
            return hfHome;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "huggingface");
    }

    private static string? SelectVariant(string snapshotDir, string? variantOverride)
    {
        if (variantOverride is not null)
        {
            var overridePath = Path.Combine(snapshotDir, variantOverride);
            return Directory.Exists(overridePath) ? overridePath : null;
        }

        // Exact priority match first
        foreach (var priority in VariantPriority)
        {
            var exactPath = Path.Combine(snapshotDir, priority);
            if (Directory.Exists(exactPath))
            {
                return exactPath;
            }
        }

        // Wildcard fallbacks: cpu-int4*, cpu-fp16*, cpu-fp32* (matches exact and with suffix)
        var subdirs = Directory.GetDirectories(snapshotDir);

        var int4 = subdirs.FirstOrDefault(d => Path.GetFileName(d).StartsWith("cpu-int4", StringComparison.OrdinalIgnoreCase));
        if (int4 is not null)
        {
            return int4;
        }

        var fp16 = subdirs.FirstOrDefault(d => Path.GetFileName(d).StartsWith("cpu-fp16", StringComparison.OrdinalIgnoreCase));
        if (fp16 is not null)
        {
            return fp16;
        }

        return subdirs.FirstOrDefault(d => Path.GetFileName(d).StartsWith("cpu-fp32", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInTargetVariant(string filePath, string? variantOverride)
    {
        var parts = filePath.Split('/');
        if (parts.Length < 2)
        {
            return false;
        }

        var subdir = parts[0];

        if (variantOverride is not null)
        {
            return subdir.Equals(variantOverride, StringComparison.OrdinalIgnoreCase);
        }

        // Accept any cpu-* subdir; we'll select the best one after download
        return subdir.StartsWith("cpu-", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyHfToken(HttpRequestMessage request)
    {
        var token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static (string Owner, string Name) ParseRepoId(string repoId)
    {
        var slash = repoId.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            throw new ArgumentException($"Invalid HF repo id '{repoId}': expected 'owner/name' format.", nameof(repoId));
        }

        return (repoId[..slash], repoId[(slash + 1)..]);
    }

    private string? FindLatestSnapshot(string repoId)
    {
        var (owner, name) = ParseRepoId(repoId);
        var repoCacheDir = Path.Combine(this.cacheRoot, "hub", $"models--{owner}--{name}");
        if (!Directory.Exists(repoCacheDir))
        {
            return null;
        }

        var snapshotsDir = Path.Combine(repoCacheDir, "snapshots");
        if (!Directory.Exists(snapshotsDir))
        {
            return null;
        }

        // Use the most recently modified snapshot
        var snapshots = Directory.GetDirectories(snapshotsDir)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToList();

        return snapshots.Count > 0 ? snapshots[0] : null;
    }

    private async Task DownloadRepoAsync(string repoId, string? variantOverride, CancellationToken cancellationToken)
    {
        var sha = await this.FetchLatestRevisionAsync(repoId, cancellationToken).ConfigureAwait(false);
        var files = await this.FetchFileListAsync(repoId, sha, variantOverride, cancellationToken).ConfigureAwait(false);
        await this.DownloadFilesAsync(repoId, sha, files, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> FetchLatestRevisionAsync(string repoId, CancellationToken cancellationToken)
    {
        var url = $"{HfBaseUrl}/api/models/{repoId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHfToken(request);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(json);
        return node?["sha"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"ONNX: Could not determine latest revision for '{repoId}'.");
    }

    private async Task<IReadOnlyList<string>> FetchFileListAsync(string repoId, string sha, string? variantOverride, CancellationToken cancellationToken)
    {
        var url = $"{HfBaseUrl}/api/models/{repoId}/tree/{sha}?recursive=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHfToken(request);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var array = JsonNode.Parse(json) as JsonArray ?? throw new InvalidOperationException($"ONNX: Unexpected tree response for '{repoId}'.");

        var files = new List<string>();
        foreach (var item in array)
        {
            if (item?["type"]?.GetValue<string>() != "file")
            {
                continue;
            }

            var path = item["path"]?.GetValue<string>();
            if (path is null)
            {
                continue;
            }

            // Filter to the requested variant subdir, or all cpu-* variant subdirs when no override
            if (IsInTargetVariant(path, variantOverride))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private async Task DownloadFilesAsync(string repoId, string sha, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var (owner, name) = ParseRepoId(repoId);
        var snapshotDir = Path.Combine(this.cacheRoot, "hub", $"models--{owner}--{name}", "snapshots", sha);

        foreach (var filePath in files)
        {
            var localPath = Path.Combine(snapshotDir, filePath.Replace('/', Path.DirectorySeparatorChar));
            var localDir = Path.GetDirectoryName(localPath)!;
            Directory.CreateDirectory(localDir);

            var url = $"{HfBaseUrl}/{repoId}/resolve/{sha}/{filePath}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHfToken(request);

            using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);
        }
    }
}
