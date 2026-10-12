using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.Plugin.XRay.Runtime;

/// <summary>
/// Downloads (once) and locates the native ONNX runtime and the face models.
/// Everything is pulled from NuGet so the plugin itself stays small and platform independent.
/// </summary>
public sealed class ModelManager
{
    private const string OnnxRuntimeVersion = "1.26.0";

    private static readonly (string Package, string Version, string Entry, string File)[] Models =
    [
        ("faceaisharp.models.scrfd.2dot5g_kps", "0.20230205.2", "contentFiles/any/any/onnx/scrfd_2.5g_kps.onnx", "scrfd_2.5g_kps.onnx"),
        ("faceaisharp.models.arcface.lresnet100e-ir-int8", "0.20230419.1", "contentFiles/any/any/onnx/arcfaceresnet100-11-int8.onnx", "arcfaceresnet100-11-int8.onnx"),
    ];

    private static readonly object ResolverLock = new();
    private static string? _nativeLibraryPath;
    private static bool _resolverInstalled;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _ready;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelManager"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ModelManager(IHttpClientFactory httpClientFactory, ILogger<ModelManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the SCRFD face detector model path.
    /// </summary>
    public string DetectorModelPath => Path.Combine(ModelDirectory, Models[0].File);

    /// <summary>
    /// Gets the ArcFace embedding model path.
    /// </summary>
    public string EmbedderModelPath => Path.Combine(ModelDirectory, Models[1].File);

    private static string BaseDirectory => Path.Combine(Plugin.Instance!.DataFolderPath, "runtime");

    private static string ModelDirectory => Path.Combine(BaseDirectory, "models");

    private static string NativeDirectory => Path.Combine(BaseDirectory, "onnxruntime-" + OnnxRuntimeVersion, GetRuntimeIdentifier());

    /// <summary>
    /// Ensures the native runtime and models are present and loadable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
            {
                return;
            }

            var feed = Plugin.Instance!.Configuration.NuGetFeed.TrimEnd('/');
            var rid = GetRuntimeIdentifier();
            var nativeName = GetNativeFileName();
            var nativePath = Path.Combine(NativeDirectory, nativeName);
            if (!File.Exists(nativePath))
            {
                var url = $"{feed}/microsoft.ml.onnxruntime/{OnnxRuntimeVersion}/microsoft.ml.onnxruntime.{OnnxRuntimeVersion}.nupkg";
                await ExtractAsync(url, $"runtimes/{rid}/native/{nativeName}", nativePath, cancellationToken).ConfigureAwait(false);
            }

            foreach (var (package, version, entry, file) in Models)
            {
                var target = Path.Combine(ModelDirectory, file);
                if (!File.Exists(target))
                {
                    await ExtractAsync($"{feed}/{package}/{version}/{package}.{version}.nupkg", entry, target, cancellationToken).ConfigureAwait(false);
                }
            }

            InstallResolver(nativePath);
            _ready = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void InstallResolver(string nativePath)
    {
        lock (ResolverLock)
        {
            _nativeLibraryPath = nativePath;
            if (_resolverInstalled)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
            _resolverInstalled = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_nativeLibraryPath is not null && libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
            && !libraryName.Contains("providers", StringComparison.OrdinalIgnoreCase))
        {
            return NativeLibrary.Load(_nativeLibraryPath);
        }

        return IntPtr.Zero;
    }

    private static string GetRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var a => throw new PlatformNotSupportedException("X-Ray face analysis does not support CPU architecture " + a)
        };

        if (OperatingSystem.IsWindows())
        {
            return "win-" + arch;
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux-" + arch;
        }

        if (OperatingSystem.IsMacOS() && arch == "arm64")
        {
            return "osx-arm64";
        }

        throw new PlatformNotSupportedException("X-Ray face analysis does not support this OS: " + RuntimeInformation.OSDescription);
    }

    private static string GetNativeFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "onnxruntime.dll";
        }

        return OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";
    }

    private async Task ExtractAsync(string packageUrl, string entryName, string targetPath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("X-Ray: downloading {Entry} from {Url}", entryName, packageUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        var uri = new Uri(packageUrl);

        string? tempPackage = null;
        Stream? source = await HttpRangeStream.TryOpenAsync(client, uri, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            // No range support: fall back to downloading the whole package.
            tempPackage = Path.Combine(BaseDirectory, Guid.NewGuid().ToString("N") + ".nupkg");
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var fs = File.Create(tempPackage);
                await using (fs.ConfigureAwait(false))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }
            }

            source = File.OpenRead(tempPackage);
        }

        try
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read);
            var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException($"{entryName} not found in {packageUrl}");
            var partial = targetPath + ".partial";
            await using (var input = entry.Open())
            {
                var output = File.Create(partial);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(partial, targetPath, overwrite: true);
            _logger.LogInformation("X-Ray: saved {Path} ({Bytes} bytes)", targetPath, new FileInfo(targetPath).Length);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            if (tempPackage is not null)
            {
                File.Delete(tempPackage);
            }
        }
    }
}
