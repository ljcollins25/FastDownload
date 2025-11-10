# FastDownload
Tool for downloading very large files from a URL as fast as possible using range queries to parallel download of chunks. By default, it downloads without disk buffering to ensure VHDs can be mounted without needed to flush to disk first. Also, supports uploading and downloading files in a custom chunk compressed format.

## Features

- High-speed parallel downloads using HTTP range requests with configurable concurrency and chunk size.
- Direct streaming without disk buffering by default so VHDs can be mounted immediately.
- Upload support with block-based chunking, selectable chunk size, compression algorithm and compression level.
- Pluggable hash/checksum types for block integrity and deduplication.
- Manifest export/import to persist or resume transfer metadata.
- Bandwidth control (max/min MB/s).
- Sparse-file aware transfers and optional skipping of zero-only regions.
- Optional cross-machine coordination using an Azure blob based semaphore for shared concurrency control.
- Built-in proxy mode to allow download chaining to reduce load on the http endpoint.

## Usage

Basic download of a file
```
fastdownload download --uri {url of remote file} --output {local file path}
```

### Download:
```
Description:
  Download a file

Usage:
  fastdownload download [options]

Options:
  --correlation-id <correlation-id>                          Correlation ID [default:
                                                             fe450103-1684-46c8-af0e-2e2fa3f8adf9]
  --uri <uri> (REQUIRED)                                     Uri to download from
  --output <output> (REQUIRED)                               The file to write it down to
  --verbose                                                  Enable verbose logging [default: False]
  --manifest-path <manifest-path>                            The file to write extracted manifest
  --maximum-download-mbps <maximum-download-mbps>            Maximum download speed in MB/s. 0 disables the limit.
                                                             [default: 2000]
  --minimum-download-mbps <minimum-download-mbps>            Minimum download speed in MB/s. Takes effect only when
                                                             --maximum-download-mbps is set. [default: 500]
  --blob-semaphore-uri <blob-semaphore-uri>                  Semaphore of azure blob folder for shared represent
                                                             semaphore for managing download concurrency
  --blob-semaphore-count <blob-semaphore-count>              Azure blob semaphore concurrency limit
  --blob-semaphore-keepalive-secs                            Azure blob semaphore keep alive interval (in seconds)
  <blob-semaphore-keepalive-secs>                            [default: 30]
  --blob-semaphore-recheck-secs                              Azure blob semaphore recheck interval (in seconds)
  <blob-semaphore-recheck-secs>                              [default: 5]
  --blob-semaphore-cleanup-ratio                             The percentage between 0 and 1.0 of times when expired
  <blob-semaphore-cleanup-ratio>                             blobs should be deleted [default: 0.01]
  --concurrency <concurrency>                                Maximum concurrency [default: 16]
  --decompression-concurrency <decompression-concurrency>    Maximum decompression concurrency []
  --write-concurrency <write-concurrency>                    Maximum number of writer threads [default: 8]
  --chunk-size <chunk-size>                                  Chunk size [default: 134217728]
  --write-through                                            Write Through [default: True]
  --progressSecs <progressSecs>                              Interval (in seconds) to report progress [default: 1]
  --async                                                    Async IO [default: False]
  --sparse                                                   Sparse IO [default: True]
  --skip-zero-regions                                        Skip chunks only containing zeros and trim leading and
                                                             trailing zeros before writing (result file will remain
                                                             sparse) [default: False]
  --dedude-download-chunks                                   Deduplicate downloading of chunks to only unique chunks
                                                             [default: False]
  --timeout <timeout>                                        Timeout in minutes. The entire program will be cancelled
                                                             if it takes this long [default: 60]
  --chunk-download-timeout <chunk-download-timeout>          Chunk download timeout in minutes. The entire download
                                                             will be permanently cancelled if any single chunk takes
                                                             this long [default: 10]
  --chunk-batch-max <chunk-batch-max>                        Maximum number of buffers to write in a batch [default: 4]
  --max-outstanding-writes <max-outstanding-writes>          Maximum number of write batches that may be pending for
                                                             each writing worker [default: 0]
  --proxy-buffer-mb <proxy-buffer-mb>                        The amount of megabytes of buffers to retain for proxy
                                                             server [default: 16371]
  --proxy-scope <proxy-scope>                                Specifies the scope identifier for a scope in which
                                                             machines can share content. [default: default]
  --proxy-server-port <proxy-server-port>                    The port used by launched proxy server to expose
                                                             downloaded file. If unspecified, proxy communication is
                                                             disabled.
  --proxy-port-with-defaults <proxy-port-with-defaults>      The port used by launched proxy server to expose
                                                             downloaded file. Also, sets other defaults for proxy
                                                             communication.
  --server-protocol <http|https>                             http or https [default: https]
  --server-shutdown-delay-secs <server-shutdown-delay-secs>  Delay before shutting down proxy server after download
                                                             completes (in seconds)
  -?, -h, --help                                             Show help and usage information
```

### Upload:
```
Description:
  Upload a file

Usage:
  fastdownload upload [options]

Options:
  --correlation-id <correlation-id>                           Correlation ID [default:
                                                              437d4cff-6adc-49ec-b561-93391f9845ac]
  --uri <uri> (REQUIRED)                                      Uri to upload to in Azure Storage
  --input <input> (REQUIRED)                                  The file to upload
  --block-size <block-size>                                   Block size [default: 134217728]
  --compression <Brotli|Deflate|GZip|Lz4|None|Zstd>           Compression algorithm [default: Brotli]
  --compression-level                                         Compression level [default: Fastest]
  <Fastest|NoCompression|Optimal|SmallestSize>
  --hash-type                                                 Hash type of uncompressed blocks [default: SHA256]
  <Dedup1024K|Dedup64K|DedupNode|DedupSingleChunk|Deprecated
  Vso0|MD5|Murmur|SHA1|SHA256|Unknown|Vso0>
  --checksum-hash-type                                        Hash type of compressed blocks (used as a checksum)
  <Dedup1024K|Dedup64K|DedupNode|DedupSingleChunk|Deprecated  [default: Murmur]
  Vso0|MD5|Murmur|SHA1|SHA256|Unknown|Vso0>
  --overwrite                                                 Overwrite the target file [default: False]
  --verbose                                                   Enable verbose logging [default: False]
  --raw                                                       Specifies that file should be uploaded as-is with no
                                                              compression or added metadata [default: False]
  --manifest-path <manifest-path>                             The file path to write computed manifest
  --concurrency <concurrency>                                 Maximum concurrency [default: 16]
  --timeout <timeout>                                         Timeout in minutes. The entire program will be cancelled
                                                              if it takes this long [default: 60]
  --sparse-handling <Compact|None|SkipHoles>                  When uploading, how to handle upload of non-hole parts of
                                                              sparse files [default: None]
  -?, -h, --help        
```