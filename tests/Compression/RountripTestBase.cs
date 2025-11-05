// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Native.IO;
using BuildXL.Utilities;
using FastDownload.Cli;
using FastDownload.Download;
using FastDownload.Shared.Manifest.V0;
using FastDownload.Tests.Utilities;
using FastDownload.Tests.Utilities.HttpServer;
using FastDownload.Upload;
using FastDownload.Utilities;
using NLog;
using NativeFileUtils = BuildXL.Native.IO.FileUtilities;

namespace FastDownload.Tests.Roundtrip
{
    internal sealed record UploadOutput(UploadArguments Arguments, ReturnCode ReturnCode, long InputLength, ContentHash InputHash)
    {
        public Statistics Statistics => Arguments.Statistics;
    }

    internal sealed record DownloadOutput(DownloadArguments Arguments, ReturnCode ReturnCode, ContentHash? OutputHash)
    {
        public Statistics Statistics => Arguments.Statistics;
    }

    internal sealed record RoundtripOutput(UploadOutput Upload, DownloadOutput Download);

    internal interface ISparseContentGenerator
    {
        IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> GenerateContentRegions(UploadArguments upload, DownloadArguments download);
    }

    internal interface IContentGenerator : ISparseContentGenerator
    {
        byte[] GenerateContent(UploadArguments upload, DownloadArguments download);

        IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> ISparseContentGenerator.GenerateContentRegions(UploadArguments upload, DownloadArguments download)
        {
            yield return (0, GenerateContent(upload, download));
        }
    }

    internal interface IBehavior<TArgument, TOutput>
    {
        Task Configure(TArgument arg) => Task.CompletedTask;

        Task BeforeAsync(TArgument arg) => Task.CompletedTask;

        Task AfterAsync(TOutput result) => Task.CompletedTask;
    }

    internal interface IUploadBehavior : IBehavior<UploadArguments, UploadOutput>
    {
    }

    internal interface IDownloadBehavior : IBehavior<DownloadArguments, DownloadOutput>
    {
    }

    internal interface IRoundtripBehavior : IUploadBehavior, IDownloadBehavior
    {
        Task AfterRoundtripAsync(RoundtripOutput result)
        {
            return Task.CompletedTask;
        }
    }

    internal sealed class Compound : IRoundtripBehavior
    {
        private readonly object[] _behaviors;
        private readonly bool _parallel;

        private Compound(object[] behaviors, bool parallel)
        {
            _behaviors = behaviors;
            _parallel = parallel;
        }

        public static Compound Parallel(params object[] behaviors)
        {
            return new Compound(behaviors, parallel: true);
        }

        public static Compound Sequential(params object[] behaviors)
        {
            return new Compound(behaviors, parallel: false);
        }

        public Task Configure(DownloadArguments arg) => InvokeAsync<IDownloadBehavior>(b => b.Configure(arg));
        public Task BeforeAsync(DownloadArguments arg) => InvokeAsync<IDownloadBehavior>(b => b.BeforeAsync(arg));
        public Task AfterAsync(DownloadOutput arg) => InvokeAsync<IDownloadBehavior>(b => b.AfterAsync(arg));

        public Task Configure(UploadArguments arg) => InvokeAsync<IUploadBehavior>(b => b.Configure(arg));
        public Task BeforeAsync(UploadArguments arg) => InvokeAsync<IUploadBehavior>(b => b.BeforeAsync(arg));
        public Task AfterAsync(UploadOutput arg) => InvokeAsync<IUploadBehavior>(b => b.AfterAsync(arg));

        public Task AfterRoundtripAsync(RoundtripOutput arg) => InvokeAsync<IRoundtripBehavior>(b => b.AfterRoundtripAsync(arg));

        private async Task InvokeAsync<T>(Func<T, Task?> factory)
        {
            var tasks = new List<Task>(capacity: _behaviors.Length);
            foreach (var behavior in _behaviors.OfType<T>())
            {
                var task = factory.Invoke(behavior) ?? Task.CompletedTask;

                if (_parallel)
                {
                    tasks.Add(task);
                }
                else
                {
                    await task;
                }
            }

            if (_parallel)
            {
                await Task.WhenAll(tasks);
            }
        }
    }

    internal class RoundtripTest
    {
        protected Logger Logger { get; } = LogManager.GetCurrentClassLogger();

        private readonly ISparseContentGenerator _contentGenerator;
        private readonly IRoundtripBehavior _behavior;

        internal event OnBeforeWriteContent OnBeforeWriteContent;
        internal Action<string> VerifyManifest;
        public string? CachedBasePath { get; set; }
        public string? FolderName { get; set; }
        public double MinExecutionTimeoutMinutes = 0;

        public CompressionAlgorithm CompressionAlgorithm { get; set; } = CompressionAlgorithm.Brotli;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;
        public HashType CompressedHashType { get; set; } = HashType.Murmur;

        public RoundtripTest(ISparseContentGenerator contentGenerator, IRoundtripBehavior behavior)
        {
            _contentGenerator = contentGenerator;
            _behavior = behavior;
        }

        public virtual async Task ExecuteRoundtripTestAsync(HashType hashType = HashType.SHA256, uint blockSize = 1024 * 1024)
        {
            var disposableFolder = new DisposableTempDirectorySlim(CachedBasePath, FolderName);

            try
            {
                // Ensure folder is empty
                NativeFileUtils.DeleteDirectoryContents(disposableFolder.Path, deleteRootDirectory: false);

                var temporaryInputPath = Path.Combine(disposableFolder.Path, "input.bin");
                var hashFile = $"{temporaryInputPath}.{hashType}.{blockSize}";

                var upload = new UploadArguments
                {
                    CorrelationId = Guid.NewGuid().ToString(),
                    Path = temporaryInputPath,
                    Uri = new Uri($"file://{Path.Combine(disposableFolder.Path, "intermediate.bin")}"),
                    BlockSize = blockSize,
                    Compression = CompressionAlgorithm,
                    CompressionLevel = CompressionLevel,
                    UncompressedHashType = hashType,
                    CompressedHashType = CompressedHashType,
                    Overwrite = true,
                    Verbose = true,
                    MaximumUploadConcurrency = Environment.ProcessorCount,
                    ExecutionTimeoutMinutes = Math.Max(MinExecutionTimeoutMinutes, Debugger.IsAttached ? 10 : 0.5),
                };
                await _behavior.Configure(upload);

                var download = new DownloadArguments
                {
                    VerificationExpectedContentPath = temporaryInputPath,
                    CorrelationId = Guid.NewGuid().ToString(),
                    Uri = new Uri("http://ThisIsAPlaceholderHost"),
                    Path = Path.Combine(disposableFolder.Path, "output.bin"),
                    ManifestPath = VerifyManifest == null ? null : Path.Combine(disposableFolder.Path, "output.manifest"),
                    ExecutionTimeoutMinutes = Math.Max(MinExecutionTimeoutMinutes, Debugger.IsAttached ? 10 : 2),
                    ChunkDownloadTimeoutMinutes = 10,
                    ChunkSize = (uint)upload.BlockSize,
                    MaximumDownloadConcurrency = 64,
                    MaximumDecompressionConcurrency = (uint)Environment.ProcessorCount,
                    MaxWriteConcurrency = 8,
                    // This is capped on purpose to ensure we execute that code path, and to slow down the tests to a level
                    // that's easy to see. Individual tests may override this as needed.
                    MinimumDownloadMbps = 5,
                    MaximumDownloadMbps = 20,
                    MaximumOutstandingWritesPerWriter = 4,
                    MaxContiguousBuffers = 4,
                    Verbose = true,
                    Async = false,
                    ProgressIntervalSeconds = 1,
                    SimulationMode = DownloadArguments.SimulationModes.None,
                    WriteThrough = true,
                    Sparse = true,
                };
                await _behavior.Configure(download);

                var totalContentLength = await CacheAsync("generate", async () =>
                {
                    var content = _contentGenerator.GenerateContentRegions(upload, download);
                    await TestExtensions.WriteAsync(temporaryInputPath, content, sparse: upload.SparseAware, AsyncOut.Var<long>(out var totalContentLength));
                    return totalContentLength.Value;
                }, associatedFile: temporaryInputPath);

                var inputHashTask = CacheAsync("hash", () => TestExtensions.TryHashFileAsync(temporaryInputPath, hashType));

                await _behavior.BeforeAsync(upload);
                var uploadResult = await CacheAsync("upload", async () =>
                {
                    var returnCode = await ExecuteUploadAsync(upload);

                    return new
                    {
                        ReturnCode = returnCode,
                        Statistics = upload.Statistics.GetStatisticsMap(),
                    };
                },
                onCacheRestore: result =>
                {
                    upload.Statistics.AddFromMap(result.Statistics);
                },
                associatedFile: upload.Uri.AbsolutePath);

                var inputHash = await inputHashTask;

                var uploadOutput = new UploadOutput(upload, uploadResult.ReturnCode, totalContentLength, inputHash.Value);

                await _behavior.AfterAsync(uploadOutput);

                await _behavior.BeforeAsync(download);
                DownloadOutput downloadOutput = await ExecuteDownloadWithServiceAsync(upload, download);

                VerifyManifest?.Invoke(File.ReadAllText(download.ManifestPath!));
                await AfterDownloadAsync(downloadOutput);

                var roundtripOutput = new RoundtripOutput(uploadOutput, downloadOutput);
                await _behavior.AfterRoundtripAsync(roundtripOutput);
            }
            catch (Exception ex)
            {
                // We ignore the failure to dispose here as it's not relevant to the test output. This means we
                // might leave some files behind in the temp directory, but that's fine.
                Logger.ForErrorEvent()
                    .Message("Error encountered during test")
                    .Exception(ex)
                    .Log();

                throw;
            }
            finally
            {
                try
                {
                    disposableFolder.Dispose();
                }
                catch (Exception ex)
                {
                    // We ignore the failure to dispose here as it's not relevant to the test output. This means we
                    // might leave some files behind in the temp directory, but that's fine.
                    Logger.ForErrorEvent()
                        .Message("Failed to dispose temporary folder")
                        .Exception(ex)
                        .Log();
                }
            }
        }

        private sealed record CachedValue<T>(T Value);

        private static readonly JsonSerializerOptions CachedOutputOptions = new JsonSerializerOptions()
        {
            WriteIndented = true,
            Converters =
            {
                new ContentHashJsonConverter(),
                new JsonStringEnumConverter()
            }
        };

        private async Task<T> CacheAsync<T>(string operation, Func<Task<T>> executeAsync, string? associatedFile = null, Action<T>? onCacheRestore = null)
        {
            var cachedPath = CachedBasePath != null ? Path.Combine(CachedBasePath, $"{operation}.json") : null;
            Logger.Debug($"Starting {operation}. Cache target = '{cachedPath}'");

            string? cachedAssociatedFilePath = null;
            if (cachedPath != null)
            {
                cachedAssociatedFilePath = cachedPath + ".associated";

                if (File.Exists(cachedPath)
                    && (associatedFile == null || File.Exists(cachedAssociatedFilePath)))
                {
                    if (associatedFile == null || NativeFileUtils.TryCreateHardLink(associatedFile, cachedAssociatedFilePath) == CreateHardLinkStatus.Success)
                    {
                        string json = File.ReadAllText(cachedPath);
                        var cachedResult = JsonSerializer.Deserialize<CachedValue<T>>(json, CachedOutputOptions)!.Value;
                        Logger.Debug($"Deserialized result {operation}. Cache target = '{cachedPath}'. Result = {json}");
                        onCacheRestore?.Invoke(cachedResult);
                        return cachedResult;
                    }
                }
            }

            Logger.Debug($"Executing {operation}. Cache target = '{cachedPath}'. ");
            var result = await executeAsync();
            Logger.Debug($"Executed {operation}. Cache target = '{cachedPath}'. ");

            if (cachedPath != null)
            {
                var serialized = JsonSerializer.Serialize(new CachedValue<T>(result), CachedOutputOptions);

                File.WriteAllText(cachedPath, serialized);
                Logger.Debug($"Serializing result {operation}. Cache target = '{cachedPath}'. Result = {serialized}");

                if (associatedFile != null)
                {
                    NativeFileUtils.DeleteFile(cachedAssociatedFilePath!);
                    var hardlinkResult = NativeFileUtils.TryCreateHardLink(cachedAssociatedFilePath, associatedFile);
                    Logger.Debug($"Hard link associated file for {operation}. [{hardlinkResult}] '{cachedAssociatedFilePath}' => '{associatedFile}'. ");
                }
            }

            return result;
        }

        protected async Task AfterDownloadAsync(DownloadOutput downloadOutput) => await _behavior.AfterAsync(downloadOutput);

        protected virtual Task<ReturnCode> ExecuteUploadAsync(UploadArguments upload)
        {
            return upload.RunAsync(CancellationToken.None);
        }

        protected virtual async Task<DownloadOutput> ExecuteDownloadAsync(UploadArguments upload, DownloadArguments download)
        {
            var downloadReturnCode = await download.RunAsync(CancellationToken.None);

            ContentHash? outputHash = null;

            // If the return code is not Success, there's no guarantees about what happened to the file, so we don't
            // even try to do anything.
            if (downloadReturnCode == ReturnCode.Success)
            {
                outputHash = await TestExtensions.TryHashFileAsync(download.Path, upload.UncompressedHashType);
            }

            return new DownloadOutput(download, downloadReturnCode, outputHash);
        }

        protected virtual async Task<DownloadOutput> ExecuteDownloadWithServiceAsync(UploadArguments upload, DownloadArguments download)
        {
            DownloadOutput? downloadOutput = null;
            await SetupHttpServerHandlers(upload)
                .WithTemporaryHttpServerAsync(
                    async baseUri =>
                    {
                        download.Uri = new Uri(baseUri.AbsoluteUri + SelectHttpHandler());
                        downloadOutput = await ExecuteDownloadAsync(upload, download);
                    });

            return downloadOutput;
        }

        protected virtual string SelectHttpHandler()
        {
            return "blob";
        }

        protected virtual Dictionary<string, IHttpHandler> SetupHttpServerHandlers(UploadArguments upload)
        {
            return new Dictionary<string, IHttpHandler>
            {
                [SelectHttpHandler()] = new HttpDiskFileHandler(upload.Uri.LocalPath)
                {
                    BlockSize = (int)upload.BlockSize,
                    OnBeforeWriteContent = (chunk, content) => OnBeforeWriteContent?.Invoke(chunk, content)
                },
            };
        }
    }
}
