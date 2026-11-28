using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Manages optional local <c>llama-server.exe</c> (<c>llama.cpp</c>) runtime and
/// <c>Sakura-7B-Qwen2.5-v1.0-GGUF</c> model download, startup, health checks, and clean shutdown.
/// </summary>
public sealed class LlamaCppServerManager : IDisposable
{
    public const string SakuraModelFileName = "sakura-7b-qwen2.5-v1.0-iq4xs.gguf";
    public const string SakuraModelUrl =
        "https://huggingface.co/SakuraLLM/Sakura-7B-Qwen2.5-v1.0-GGUF/resolve/main/sakura-7b-qwen2.5-v1.0-iq4xs.gguf";
    public const string SakuraModelMirrorUrl =
        "https://hf-mirror.com/SakuraLLM/Sakura-7B-Qwen2.5-v1.0-GGUF/resolve/main/sakura-7b-qwen2.5-v1.0-iq4xs.gguf";
    public const string LlamaCppReleasesApi =
        "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10";

    private static readonly TimeSpan HealthProbeCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NegativeResolveCacheTtl = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<LlamaCppServerManager>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _procGate = new();
    private readonly object _resolveCacheGate = new();

    private Process? _process;
    private ChildProcessJobObject? _jobObject;
    private string? _runningServerPath;
    private string? _runningModelPath;
    private int _configuredPort;
    private int _activePort;
    private int _runningGpuLayers;
    private long _lastHealthSuccessTimestamp;
    private string? _cachedConfiguredServerPath;
    private string? _cachedResolvedServerPath;
    private long _cachedResolvedServerTimestamp;
    private bool _hasServerPathCache;
    private readonly Queue<string> _stderrTail = new();
    private bool _disposed;

    internal int ServerPathScanCount { get; private set; }

    public LlamaCppServerManager(
        AppPaths paths,
        HttpClient? httpClient = null,
        ILogger<LlamaCppServerManager>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _logger = logger;
    }

    public bool IsServerRunning
    {
        get
        {
            lock (_procGate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int ActivePort
    {
        get
        {
            lock (_procGate)
            {
                return _activePort;
            }
        }
    }

    internal static int FindFreePort(int preferredPort)
    {
        int clamped = Math.Clamp(preferredPort, 1025, 65534);
        if (IsPortAvailable(clamped))
        {
            return clamped;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int ephemeralPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return ephemeralPort;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public string ResolveModelPath(DesktopSettings? settings)
    {
        string? configured = settings?.LocalSakuraModelPath;
        return string.IsNullOrWhiteSpace(configured)
            ? _paths.DefaultSakuraModelPath
            : configured.Trim();
    }

    public string? ResolveServerExecutablePath(DesktopSettings? settings)
    {
        string? configured = string.IsNullOrWhiteSpace(settings?.LocalLlamaServerPath)
            ? null
            : settings.LocalLlamaServerPath.Trim();

        lock (_resolveCacheGate)
        {
            if (_hasServerPathCache &&
                string.Equals(_cachedConfiguredServerPath, configured, StringComparison.OrdinalIgnoreCase))
            {
                if (_cachedResolvedServerPath is not null)
                {
                    if ((configured is null ||
                         string.Equals(_cachedResolvedServerPath, configured, StringComparison.OrdinalIgnoreCase) ||
                         !File.Exists(configured)) &&
                        File.Exists(_cachedResolvedServerPath))
                    {
                        return _cachedResolvedServerPath;
                    }
                }
                else if (Stopwatch.GetElapsedTime(_cachedResolvedServerTimestamp, Stopwatch.GetTimestamp()) < NegativeResolveCacheTtl)
                {
                    return null;
                }
            }
        }

        string? resolved = ResolveServerExecutablePathCore(configured);
        lock (_resolveCacheGate)
        {
            ServerPathScanCount++;
            _cachedConfiguredServerPath = configured;
            _cachedResolvedServerPath = resolved;
            _cachedResolvedServerTimestamp = Stopwatch.GetTimestamp();
            _hasServerPathCache = true;
        }

        return resolved;
    }

    internal void InvalidateServerPathCache()
    {
        lock (_resolveCacheGate)
        {
            _hasServerPathCache = false;
            _cachedConfiguredServerPath = null;
            _cachedResolvedServerPath = null;
            _cachedResolvedServerTimestamp = 0;
        }
    }

    internal void ExpireNegativeServerPathCacheForTests()
    {
        lock (_resolveCacheGate)
        {
            if (_hasServerPathCache && _cachedResolvedServerPath is null)
            {
                _cachedResolvedServerTimestamp = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 10.0);
            }
        }
    }

    private string? ResolveServerExecutablePathCore(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string direct = Path.Combine(_paths.DefaultLlamaCppDirectory, "llama-server.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        if (Directory.Exists(_paths.DefaultLlamaCppDirectory))
        {
            return Directory.EnumerateFiles(
                _paths.DefaultLlamaCppDirectory,
                "llama-server.exe",
                SearchOption.AllDirectories).FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// Builds the official SakuraLLM v1.0 chat completion payload with low-temperature deterministic sampling.
    /// </summary>
    public static JsonObject BuildSakuraChatPayload(string sourceText) =>
        BuildSakuraChatPayload(sourceText, contextLines: null, glossaryEntries: null);

    /// <summary>
    /// Builds a context-aware SakuraLLM v1.0 chat completion payload using surrounding dialogue from the same page.
    /// </summary>
    public static JsonObject BuildSakuraChatPayload(string sourceText, IReadOnlyList<string>? contextLines) =>
        BuildSakuraChatPayload(sourceText, contextLines, glossaryEntries: null);

    /// <summary>
    /// Builds a context- and glossary-aware SakuraLLM v1.0 chat completion payload.
    /// </summary>
    public static JsonObject BuildSakuraChatPayload(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        IReadOnlyList<KeyValuePair<string, string>>? glossaryEntries)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var userBuilder = new StringBuilder();
        if (contextLines is { Count: > 0 })
        {
            List<string> usableContext = contextLines
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.Trim())
                .Where(l => !string.Equals(l, sourceText.Trim(), StringComparison.Ordinal))
                .Take(8)
                .ToList();

            if (usableContext.Count > 0)
            {
                userBuilder.AppendLine("（相邻对白仅供参考上下文人称与语气，请勿重复输出以下参考行：）");
                foreach (string ctx in usableContext)
                {
                    userBuilder.Append("· ").AppendLine(ctx);
                }

                userBuilder.AppendLine();
            }
        }

        AppendGlossaryHint(userBuilder, glossaryEntries);
        userBuilder.Append("将下面的日文文本翻译成中文：\n").Append(sourceText.Trim());

        return CreateSakuraPayloadObject(userBuilder.ToString(), maxTokens: 512, stream: false);
    }

    /// <summary>
    /// Builds a numbered batch translation payload so SakuraLLM translates all bubbles on a page with unified context.
    /// </summary>
    public static JsonObject BuildSakuraBatchPayload(IReadOnlyList<string> sourceLines) =>
        BuildSakuraBatchPayload(sourceLines, previousPageContext: null, glossaryEntries: null, stream: false);

    /// <summary>
    /// Builds a numbered batch translation payload with optional cross-page context, glossary, and SSE streaming.
    /// </summary>
    public static JsonObject BuildSakuraBatchPayload(
        IReadOnlyList<string> sourceLines,
        IReadOnlyList<string>? previousPageContext,
        IReadOnlyList<KeyValuePair<string, string>>? glossaryEntries,
        bool stream = false)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);

        var sb = new StringBuilder();
        if (previousPageContext is { Count: > 0 })
        {
            List<string> prev = previousPageContext
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.ReplaceLineEndings(" ").Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (prev.Count > 0)
            {
                sb.AppendLine("（上一页末尾对白供人称与语气参考，请勿输出以下参考行：）");
                foreach (string line in prev)
                {
                    sb.Append("· ").AppendLine(line);
                }

                sb.AppendLine();
            }
        }

        AppendGlossaryHint(sb, glossaryEntries);
        sb.AppendLine("请按编号逐行将以下漫画同一页的日文气泡翻译成简体中文，保持原有编号 [1] [2]... 一一对应且不要合并、遗漏或添加解释：");
        for (int i = 0; i < sourceLines.Count; i++)
        {
            string line = (sourceLines[i] ?? string.Empty).ReplaceLineEndings(" ").Trim();
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(line);
        }

        return CreateSakuraPayloadObject(sb.ToString().TrimEnd(), maxTokens: Math.Clamp(sourceLines.Count * 160, 512, 1800), stream);
    }

    private static void AppendGlossaryHint(StringBuilder sb, IReadOnlyList<KeyValuePair<string, string>>? glossaryEntries)
    {
        if (glossaryEntries is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine("（专有名词/术语锁定，出现以下词条请严格采用指定中文译名：）");
        foreach (KeyValuePair<string, string> kv in glossaryEntries.Take(12))
        {
            sb.Append("· ").Append(kv.Key).Append(" => ").AppendLine(kv.Value);
        }

        sb.AppendLine();
    }

    private static JsonObject CreateSakuraPayloadObject(string userContent, int maxTokens, bool stream = false) => new()
    {
        ["model"] = "sakura",
        ["temperature"] = 0.1,
        ["top_p"] = 0.3,
        ["frequency_penalty"] = 0.0,
        ["max_tokens"] = maxTokens,
        ["stream"] = stream,
        ["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "你是一个轻小说和漫画翻译模型，可以流畅通顺地以日本轻小说和日漫的风格将日文翻译成简体中文，并联系上下文正确使用人称代词，不擅自添加原文中没有的代词。"
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContent
            }
        }
    };

    public async Task<string> DownloadSakuraModelAsync(
        bool useMirror,
        string? destinationPath = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string targetPath = string.IsNullOrWhiteSpace(destinationPath)
            ? _paths.DefaultSakuraModelPath
            : destinationPath.Trim();
        string url = useMirror ? SakuraModelMirrorUrl : SakuraModelUrl;

        await DownloadFileWithResumeAsync(url, targetPath, "下载 Sakura-7B 模型", progress, cancellationToken)
            .ConfigureAwait(false);
        return targetPath;
    }

    public async Task<string> DownloadLlamaCppRuntimeAsync(
        string? preferredBackend,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string backend = NormalizeBackend(preferredBackend);
        progress?.Report(new ModelDownloadProgress(0, null, "正在查询 llama.cpp 最新 Windows 发行版..."));

        using var req = new HttpRequestMessage(HttpMethod.Get, LlamaCppReleasesApi);
        req.Headers.UserAgent.ParseAdd("EhViewer.Desktop/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("GitHub release metadata did not include a release list.");
        }

        List<(string Name, string Url)> windowsZips = [];
        foreach (JsonElement release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<(string Name, string Url)> candidateZips = [];
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) &&
                    !string.IsNullOrWhiteSpace(url) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                {
                    candidateZips.Add((name, url));
                }
            }

            if (candidateZips.Any(a => a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)))
            {
                windowsZips = candidateZips;
                break;
            }
        }

        (string Name, string Url)? mainZip = SelectLlamaCppAsset(windowsZips, backend);
        if (mainZip is null)
        {
            throw new InvalidOperationException($"未在最新 llama.cpp 发布中找到适用于 Windows x64 ({backend}) 的安装包。");
        }

        Directory.CreateDirectory(_paths.DefaultLlamaCppDirectory);
        string tempZipPath = Path.Combine(_paths.DefaultLlamaCppDirectory, mainZip.Value.Name);

        await DownloadFileWithResumeAsync(
            mainZip.Value.Url,
            tempZipPath,
            $"下载 {mainZip.Value.Name}",
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new ModelDownloadProgress(1, 1, "正在解压 llama-server.exe..."));
        ZipFile.ExtractToDirectory(tempZipPath, _paths.DefaultLlamaCppDirectory, overwriteFiles: true);
        try { File.Delete(tempZipPath); } catch { }

        if (backend == "cuda")
        {
            string cudaTag = mainZip.Value.Name.Contains("cuda-12", StringComparison.OrdinalIgnoreCase)
                ? "cuda-12"
                : "cuda";
            (string Name, string Url)? cudartZip = windowsZips.FirstOrDefault(a =>
                a.Name.StartsWith("cudart-llama-bin-win-" + cudaTag, StringComparison.OrdinalIgnoreCase));
            if (cudartZip is null or { Url: null })
            {
                cudartZip = windowsZips.FirstOrDefault(a =>
                    a.Name.StartsWith("cudart-llama-bin-win-cuda", StringComparison.OrdinalIgnoreCase));
            }
            if (cudartZip is { Url.Length: > 0 })
            {
                string cudartPath = Path.Combine(_paths.DefaultLlamaCppDirectory, cudartZip.Value.Name);
                await DownloadFileWithResumeAsync(
                    cudartZip.Value.Url,
                    cudartPath,
                    $"下载 {cudartZip.Value.Name}",
                    progress,
                    cancellationToken).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(cudartPath, _paths.DefaultLlamaCppDirectory, overwriteFiles: true);
                try { File.Delete(cudartPath); } catch { }
            }
        }

        string? exePath = Directory.EnumerateFiles(
            _paths.DefaultLlamaCppDirectory,
            "llama-server.exe",
            SearchOption.AllDirectories).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new FileNotFoundException("解压完成，但未在包中找到 llama-server.exe。");
        }

        InvalidateServerPathCache();
        return exePath;
    }

    public async Task<Uri> EnsureRunningAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        string modelPath = ResolveModelPath(settings);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"未找到本地 Sakura-7B 模型文件：{modelPath}。请先在设置中下载或指定本地 .gguf 文件。",
                modelPath);
        }

        string? serverPath = ResolveServerExecutablePath(settings);
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            throw new FileNotFoundException(
                "未找到 llama-server.exe。请先在设置中自动下载 llama.cpp 或浏览选择本地 llama-server.exe。");
        }

        int configuredPort = Math.Clamp(settings.LocalLlamaPort is > 1024 and < 65535 ? settings.LocalLlamaPort : 8766, 1025, 65534);
        int gpuLayers = settings.LocalLlamaGpuLayers;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReusableRunningInstance(serverPath, modelPath, configuredPort, gpuLayers))
            {
                int reusablePort = ActivePort;
                long lastHealthTs = Volatile.Read(ref _lastHealthSuccessTimestamp);
                if (IsWithinHealthCacheTtl(lastHealthTs, Stopwatch.GetTimestamp(), HealthProbeCacheTtl) ||
                    await ProbeHealthAsync(reusablePort, cancellationToken).ConfigureAwait(false))
                {
                    return new Uri($"http://127.0.0.1:{reusablePort}");
                }
            }

            StopServerCore();

            int activePort = FindFreePort(configuredPort);

            lock (_procGate)
            {
                _stderrTail.Clear();
            }

            var psi = new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = $"--model \"{modelPath}\" --host 127.0.0.1 --port {activePort} --ctx-size 2048 --n-gpu-layers {gpuLayers}",
                WorkingDirectory = Path.GetDirectoryName(serverPath) ?? _paths.DefaultLlamaCppDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => CaptureOutputLine(e.Data);
            process.ErrorDataReceived += (_, e) => CaptureOutputLine(e.Data);
            ChildProcessJobObject? jobObject = null;

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法启动 llama-server.exe 进程。");
                }

                jobObject = ChildProcessJobObject.TryCreate();
                jobObject?.TryAssignProcess(process);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                process.Dispose();
                jobObject?.Dispose();
                throw;
            }

            lock (_procGate)
            {
                _process = process;
                _jobObject = jobObject;
                _runningServerPath = serverPath;
                _runningModelPath = modelPath;
                _configuredPort = configuredPort;
                _activePort = activePort;
                _runningGpuLayers = gpuLayers;
                Volatile.Write(ref _lastHealthSuccessTimestamp, 0);
            }

            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool exited;
                    int exitCode = -1;
                    bool hadProcess;
                    lock (_procGate)
                    {
                        hadProcess = _process is not null;
                        exited = _process is null || _process.HasExited;
                        if (exited && _process is not null)
                        {
                            exitCode = _process.ExitCode;
                        }
                    }

                    if (exited)
                    {
                        string tail = GetStderrTail();
                        throw new InvalidOperationException(hadProcess
                            ? $"llama-server 提前退出（退出码 {exitCode}）。{tail}"
                            : $"llama-server 进程已停止。{tail}");
                    }

                    if (await ProbeHealthAsync(activePort, cancellationToken).ConfigureAwait(false))
                    {
                        return new Uri($"http://127.0.0.1:{activePort}");
                    }

                    await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                }

                string timeoutTail = GetStderrTail();
                throw new TimeoutException($"等待 llama-server 就绪超时。{timeoutTail}");
            }
            catch
            {
                StopServerCore();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void StopServer()
    {
        StopServerCore();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopServer();
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
        _gate.Dispose();
    }

    internal static (string Name, string Url)? SelectLlamaCppAsset(
        IReadOnlyList<(string Name, string Url)> windowsZips,
        string backend)
    {
        if (windowsZips.Count == 0)
        {
            return null;
        }

        string norm = NormalizeBackend(backend);
        (string Name, string Url)? match = norm switch
        {
            "vulkan" => windowsZips.FirstOrDefault(a =>
                a.Name.Contains("vulkan", StringComparison.OrdinalIgnoreCase) &&
                a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)),
            "cuda" => windowsZips.FirstOrDefault(a =>
                a.Name.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)),
            _ => windowsZips.FirstOrDefault(a =>
                (a.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
                 a.Name.Contains("avx2", StringComparison.OrdinalIgnoreCase)) &&
                a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase))
        };

        if (match is { Url.Length: > 0 })
        {
            return match;
        }

        (string Name, string Url) fallback = windowsZips.FirstOrDefault(a =>
            a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
            !a.Name.Contains("cudart", StringComparison.OrdinalIgnoreCase));

        return fallback.Url is { Length: > 0 } ? fallback : windowsZips[0];
    }

    private static string NormalizeBackend(string? backend)
    {
        string value = backend?.Trim().ToLowerInvariant() ?? "vulkan";
        return value is "cuda" or "cpu" or "vulkan" ? value : "vulkan";
    }

    private async Task DownloadFileWithResumeAsync(
        string url,
        string destinationPath,
        string label,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partPath = destinationPath + ".part";
        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("EhViewer.Desktop/1.0");
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(new ModelDownloadProgress(existingBytes, existingBytes, $"{label} 完成"));
            return;
        }

        response.EnsureSuccessStatusCode();

        bool append = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
        long baseOffset = append ? existingBytes : 0;
        long? contentLength = response.Content.Headers.ContentLength;
        long? totalBytes = contentLength.HasValue ? baseOffset + contentLength.Value : null;

        await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var local = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long written = baseOffset;
            long lastReportTick = Environment.TickCount64;

            progress?.Report(new ModelDownloadProgress(written, totalBytes, $"{label}..."));

            int read;
            while ((read = await remote.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                long now = Environment.TickCount64;
                if (now - lastReportTick >= 250)
                {
                    lastReportTick = now;
                    progress?.Report(new ModelDownloadProgress(written, totalBytes, $"{label}..."));
                }
            }

            await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelDownloadProgress(written, totalBytes ?? written, $"{label} 完成"));
        }

        File.Move(partPath, destinationPath, overwrite: true);
    }

    internal static bool IsWithinHealthCacheTtl(long lastSuccessTimestamp, long currentTimestamp, TimeSpan ttl)
        => lastSuccessTimestamp > 0 && Stopwatch.GetElapsedTime(lastSuccessTimestamp, currentTimestamp) < ttl;

    private async Task<bool> ProbeHealthAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(900));
            using HttpResponseMessage resp = await _http
                .GetAsync($"http://127.0.0.1:{port}/health", cts.Token)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Volatile.Write(ref _lastHealthSuccessTimestamp, Stopwatch.GetTimestamp());
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private bool IsReusableRunningInstance(string serverPath, string modelPath, int configuredPort, int gpuLayers)
    {
        lock (_procGate)
        {
            return _process is { HasExited: false } &&
                   _activePort > 0 &&
                   string.Equals(_runningServerPath, serverPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(_runningModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
                   _configuredPort == configuredPort &&
                   _runningGpuLayers == gpuLayers;
        }
    }

    private void StopServerCore()
    {
        Process? proc;
        ChildProcessJobObject? jobObject;
        lock (_procGate)
        {
            proc = _process;
            jobObject = _jobObject;
            _process = null;
            _jobObject = null;
            _runningServerPath = null;
            _runningModelPath = null;
            _configuredPort = 0;
            _activePort = 0;
            _runningGpuLayers = 0;
            Volatile.Write(ref _lastHealthSuccessTimestamp, 0);
        }

        try
        {
            if (proc is null)
            {
                return;
            }

            try
            {
                proc.CancelOutputRead();
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                proc.CancelErrorRead();
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error stopping llama-server process");
            }
            finally
            {
                proc.Dispose();
            }
        }
        finally
        {
            try
            {
                jobObject?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error disposing llama-server job object");
            }
        }
    }

    private void CaptureOutputLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_procGate)
        {
            if (_stderrTail.Count >= 25)
            {
                _stderrTail.Dequeue();
            }
            _stderrTail.Enqueue(line.Trim());
        }
    }

    private string GetStderrTail()
    {
        lock (_procGate)
        {
            return _stderrTail.Count == 0
                ? string.Empty
                : string.Join(" | ", _stderrTail);
        }
    }

    internal sealed class ChildProcessJobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        private IntPtr _handle;
        private int _disposed;

        internal static int ExtendedLimitInformationSize => Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

        private ChildProcessJobObject(IntPtr handle)
        {
            _handle = handle;
        }

        public static ChildProcessJobObject? TryCreate()
        {
            IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(handle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return new ChildProcessJobObject(handle);
        }

        public bool TryAssignProcess(Process process)
        {
            if (process is null || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            IntPtr handle = Volatile.Read(ref _handle);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (process.HasExited)
                {
                    return false;
                }

                return AssignProcessToJobObject(handle, process.Handle);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
