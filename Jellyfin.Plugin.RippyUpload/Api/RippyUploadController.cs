using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RippyUpload.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RippyUpload.Api;

/// <summary>
/// Receives video uploads from Rippy.
/// </summary>
[ApiController]
[Route("Rippy")]
[Authorize]
[Produces("application/json")]
public class RippyUploadController : ControllerBase
{
    private const int BufferSize = 81920;
    private const int HeaderLength = 12;
    private const int MaxFolderDepth = 4;
    private const int MaxListEntries = 10000;
    private const long MaxChunkBytes = 64L * 1024 * 1024;
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IncomingDirectoryName = ".rippy-incoming";

    private static readonly char[] ForbiddenNameChars = { ':', '<', '>', '"', '|', '?', '*', '\0' };
    private static readonly string[] AllowedExtensions = { ".mp4", ".mkv" };
    private static readonly string[] WindowsReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly SemaphoreSlim ScanLock = new SemaphoreSlim(1, 1);
    private static int _scanPending;
    private static int _activeUploads;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChunkSession> ChunkSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, ChunkSession>();

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RippyUploadController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RippyUploadController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RippyUploadController}"/> interface.</param>
    public RippyUploadController(ILibraryManager libraryManager, ILogger<RippyUploadController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Reports whether the authenticated user may upload and the active limits.
    /// </summary>
    /// <returns>Status information.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        var userId = GetUserId();
        var destination = config is null ? null : ResolveDestination(config.DestinationPath);

        return new StatusResponse
        {
            CanUpload = config is not null && userId != Guid.Empty && config.AllowedUserIds.Contains(userId),
            Configured = destination is not null,
            MaxFileSizeBytes = config is null || config.MaxFileSizeMegabytes <= 0 ? 0 : (long)config.MaxFileSizeMegabytes * 1024 * 1024,
            AllowedExtensions = AllowedExtensions,
            UsedBytes = destination is null || userId == Guid.Empty || Plugin.Instance is null ? 0 : Plugin.Instance.Usage.GetUsage(userId, destination),
            QuotaBytes = config is null || config.UserQuotaMegabytes <= 0 ? 0 : (long)config.UserQuotaMegabytes * 1024 * 1024,
            Chunked = true,
            MaxChunkBytes = MaxChunkBytes
        };
    }

    /// <summary>
    /// Uploads a video file as the raw request body.
    /// </summary>
    /// <param name="filename">The target file name (must end in .mp4 or .mkv).</param>
    /// <param name="folder">Optional relative sub-folder inside the upload directory, using "/" between levels.</param>
    /// <param name="cancellationToken">Cancellation token tied to the request.</param>
    /// <returns>The result of the upload.</returns>
    [HttpPost("Upload")]
    [Consumes("video/mp4", "video/x-matroska", "application/octet-stream")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload([FromQuery] string? filename, [FromQuery] string? folder, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured", "Plugin is not initialized.");
        }

        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "A Jellyfin user session token is required. API keys are not accepted.");
        }

        if (!config.AllowedUserIds.Contains(userId))
        {
            _logger.LogWarning("Rippy upload rejected: user {UserId} is not permitted to upload", userId);
            return Error(StatusCodes.Status403Forbidden, "forbidden", "This user is not allowed to upload videos.");
        }

        var destination = ResolveDestination(config.DestinationPath);
        if (destination is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured", "The upload destination has not been configured by an administrator.");
        }

        var requestedName = string.IsNullOrEmpty(filename) ? Request.Headers["X-Filename"].ToString() : filename;
        if (!TryValidateName(requestedName, true, out var fileName, out var extension, out var nameError))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_filename", nameError);
        }

        if (!TryValidateFolder(folder, out var folderSegments, out var folderError))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_folder", folderError);
        }

        var shaHeader = Request.Headers["X-Content-SHA256"].ToString().Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(shaHeader))
        {
            return Error(StatusCodes.Status400BadRequest, "missing_checksum", "The X-Content-SHA256 header is required.");
        }

        if (shaHeader.Length != 64 || !shaHeader.All(Uri.IsHexDigit))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_checksum", "X-Content-SHA256 must be 64 hexadecimal characters.");
        }

        var expectedSha = shaHeader;

        var maxBytes = config.MaxFileSizeMegabytes > 0 ? (long)config.MaxFileSizeMegabytes * 1024 * 1024 : long.MaxValue;
        if (Request.ContentLength.HasValue && Request.ContentLength.Value > maxBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "too_large", "The file exceeds the configured maximum upload size.");
        }

        if (Request.ContentLength == 0)
        {
            return Error(StatusCodes.Status400BadRequest, "empty_body", "The request body is empty.");
        }

        string finalPath;
        string incomingDirectory;
        try
        {
            Directory.CreateDirectory(destination);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!TryResolveDirectory(destination, folderSegments, out var targetDirectory))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_folder", "The folder resolves outside the upload directory.");
            }

            finalPath = Path.Combine(targetDirectory, fileName);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? string.Empty);
            if (!string.Equals(parent, targetDirectory, comparison))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_filename", "The file name resolves outside the upload directory.");
            }

            incomingDirectory = Path.Combine(destination, IncomingDirectoryName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rippy upload: destination directory is not usable");
            return Error(StatusCodes.Status500InternalServerError, "storage_error", "The upload directory is not accessible to Jellyfin.");
        }

        if (System.IO.File.Exists(finalPath))
        {
            return Error(StatusCodes.Status409Conflict, "exists", "A file with this name already exists.");
        }

        var limit = Math.Max(1, config.MaxConcurrentUploads);
        if (Interlocked.Increment(ref _activeUploads) > limit)
        {
            Interlocked.Decrement(ref _activeUploads);
            Response.Headers["Retry-After"] = "30";
            return Error(StatusCodes.Status429TooManyRequests, "busy", "Too many uploads are in progress. Retry later.");
        }

        var usage = Plugin.Instance!.Usage;
        long reservedBytes = 0;
        string? tempPath = null;
        try
        {
            var quotaBytes = config.UserQuotaMegabytes > 0 ? (long)config.UserQuotaMegabytes * 1024 * 1024 : 0;
            if (!usage.TryReserve(userId, destination, quotaBytes, Request.ContentLength, out var allowance, out reservedBytes, out var usedBytes))
            {
                return Error(
                    StatusCodes.Status507InsufficientStorage,
                    "quota_exceeded",
                    "Upload quota exceeded: " + (usedBytes / 1048576) + " MB of " + config.UserQuotaMegabytes + " MB is in use. Ask an administrator to move or remove your files.");
            }

            var effectiveMax = Math.Min(maxBytes, allowance);

            Directory.CreateDirectory(incomingDirectory);
            CleanStaleParts(incomingDirectory);
            tempPath = Path.Combine(incomingDirectory, Guid.NewGuid().ToString("N") + ".part");

            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                // Least privilege: owner read/write, group read. Not world-readable.
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            }

            IActionResult? failure = null;
            long size = 0;
            var sha = string.Empty;

            using (var fs = new FileStream(tempPath, streamOptions))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                var body = Request.Body;

                var headerRead = 0;
                while (headerRead < HeaderLength)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(headerRead, HeaderLength - headerRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    headerRead += read;
                }

                if (headerRead < HeaderLength || !HeaderMatches(extension, buffer))
                {
                    failure = Error(StatusCodes.Status400BadRequest, "invalid_content", "The file content does not look like a valid " + extension + " file.");
                }
                else
                {
                    size = headerRead;
                    await fs.WriteAsync(buffer.AsMemory(0, headerRead), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, headerRead);

                    while (true)
                    {
                        var read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        size += read;
                        if (size > effectiveMax)
                        {
                            failure = size > maxBytes
                                ? Error(StatusCodes.Status413PayloadTooLarge, "too_large", "The file exceeds the configured maximum upload size.")
                                : Error(StatusCodes.Status507InsufficientStorage, "quota_exceeded", "This upload would exceed your upload quota.");
                            break;
                        }

                        await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                    }

                    if (failure is null)
                    {
                        await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                        fs.Flush(true);
                        sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    }
                }
            }

            if (failure is not null)
            {
                return failure;
            }

            if (Request.ContentLength.HasValue && Request.ContentLength.Value != size)
            {
                return Error(StatusCodes.Status400BadRequest, "incomplete", "The received size does not match Content-Length.");
            }

            if (!string.Equals(expectedSha, sha, StringComparison.Ordinal))
            {
                return Error(StatusCodes.Status422UnprocessableEntity, "checksum_mismatch", "The SHA-256 of the received data does not match X-Content-SHA256.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            try
            {
                System.IO.File.Move(tempPath, finalPath, false);
                tempPath = null;
            }
            catch (IOException) when (System.IO.File.Exists(finalPath))
            {
                return Error(StatusCodes.Status409Conflict, "exists", "A file with this name already exists.");
            }

            if (!usage.Record(userId, string.Join('/', folderSegments.Append(fileName)), size))
            {
                _logger.LogWarning("Rippy upload usage ledger could not be saved to disk; quota is tracked in memory until restart");
            }

            _logger.LogInformation("Rippy upload complete: {FileName} ({Size} bytes) from user {UserId}", fileName, size, userId);
            QueueLibraryScan();

            return StatusCode(StatusCodes.Status201Created, new UploadResponse
            {
                Status = "ok",
                Filename = fileName,
                Folder = string.Join('/', folderSegments),
                Size = size,
                Sha256 = sha
            });
        }
        catch (OperationCanceledException)
        {
            return Error(StatusCodes.Status400BadRequest, "aborted", "The upload was cancelled before it completed.");
        }
        catch (BadHttpRequestException)
        {
            return Error(StatusCodes.Status400BadRequest, "aborted", "The request body could not be read completely.");
        }
        catch (IOException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return Error(StatusCodes.Status400BadRequest, "aborted", "The connection was closed before the upload completed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rippy upload failed while writing {FileName}", fileName);
            return Error(StatusCodes.Status500InternalServerError, "storage_error", "The file could not be written to the upload directory.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeUploads);
            if (reservedBytes > 0)
            {
                usage.Release(userId, reservedBytes);
            }

            if (tempPath is not null)
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete incomplete upload {TempPath}", tempPath);
                }
            }
        }
    }

    /// <summary>
    /// Reports how many bytes of a chunked upload the server already holds, so an interrupted upload can resume.
    /// </summary>
    /// <param name="uploadId">The client-chosen upload id (32 hex characters).</param>
    /// <returns>The received byte count (0 if nothing is stored yet).</returns>
    [HttpGet("Upload/Progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetChunkProgress([FromQuery] string? uploadId)
    {
        var denied = CheckChunkCaller(uploadId, out var userId, out var destination, out _);
        if (denied is not null)
        {
            return denied;
        }

        var partPath = ChunkPartPath(destination, userId, uploadId!);
        long received = System.IO.File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        return Ok(new ChunkResponse { Status = "ok", Received = received });
    }

    /// <summary>
    /// Appends one chunk of a large upload. Chunks must arrive in order; each is a separate small request so
    /// proxies with per-request body limits (for example Cloudflare's 100 MB) are not a problem.
    /// </summary>
    /// <param name="uploadId">The client-chosen upload id (32 hex characters).</param>
    /// <param name="offset">Where this chunk starts in the file.</param>
    /// <param name="total">The size of the whole file.</param>
    /// <param name="cancellationToken">Cancellation token tied to the request.</param>
    /// <returns>The number of bytes now stored, or 409 with the stored count if the offset does not match.</returns>
    [HttpPost("Upload/Chunk")]
    [Consumes("application/octet-stream")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadChunk([FromQuery] string? uploadId, [FromQuery] long offset, [FromQuery] long total, CancellationToken cancellationToken)
    {
        var denied = CheckChunkCaller(uploadId, out var userId, out var destination, out var config);
        if (denied is not null)
        {
            return denied;
        }

        var maxBytes = config.MaxFileSizeMegabytes > 0 ? (long)config.MaxFileSizeMegabytes * 1024 * 1024 : long.MaxValue;
        if (total <= 0 || offset < 0 || offset >= total)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_range", "offset and total must describe a position inside the file.");
        }

        if (total > maxBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "too_large", "The file exceeds the configured maximum upload size.");
        }

        var length = Request.ContentLength;
        if (!length.HasValue || length.Value <= 0 || length.Value > MaxChunkBytes)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_chunk", "A chunk needs a Content-Length between 1 byte and " + (MaxChunkBytes / 1048576) + " MB.");
        }

        if (offset + length.Value > total)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_range", "The chunk runs past the end of the file.");
        }

        var quotaBytes = config.UserQuotaMegabytes > 0 ? (long)config.UserQuotaMegabytes * 1024 * 1024 : 0;
        var usageLedger = Plugin.Instance!.Usage;
        if (offset == 0 && quotaBytes > 0)
        {
            // Non-binding early check so a doomed upload is refused before any data is sent.
            if (!usageLedger.TryReserve(userId, destination, quotaBytes, total, out _, out var probeReserved, out var probeUsed))
            {
                return Error(
                    StatusCodes.Status507InsufficientStorage,
                    "quota_exceeded",
                    "Upload quota exceeded: " + (probeUsed / 1048576) + " MB of " + config.UserQuotaMegabytes + " MB is in use. Ask an administrator to move or remove your files.");
            }

            usageLedger.Release(userId, probeReserved);
        }

        var buffer = new byte[(int)length.Value];
        var filled = 0;
        try
        {
            while (filled < buffer.Length)
            {
                var read = await Request.Body.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or BadHttpRequestException or IOException)
        {
            return Error(StatusCodes.Status400BadRequest, "aborted", "The chunk could not be read completely.");
        }

        if (filled != buffer.Length)
        {
            return Error(StatusCodes.Status400BadRequest, "incomplete", "The received chunk size does not match Content-Length.");
        }

        var key = ChunkKey(userId, uploadId!);
        PruneChunkSessions();
        var session = ChunkSessions.GetOrAdd(key, _ => new ChunkSession());
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var incomingDirectory = Path.Combine(destination, IncomingDirectoryName);
            Directory.CreateDirectory(incomingDirectory);
            if (offset == 0)
            {
                CleanStaleParts(incomingDirectory);
            }

            var partPath = ChunkPartPath(destination, userId, uploadId!);
            var stored = System.IO.File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            if (offset == 0 && stored > 0)
            {
                // A fresh start for this id: drop whatever an earlier attempt left behind.
                System.IO.File.Delete(partPath);
                stored = 0;
                session.ResetHash();
            }

            if (stored != offset)
            {
                return StatusCode(StatusCodes.Status409Conflict, new ChunkResponse { Status = "offset_mismatch", Received = stored });
            }

            if (stored == 0)
            {
                if (!HeaderMatchesAny(buffer))
                {
                    return Error(StatusCodes.Status400BadRequest, "invalid_content", "The file content does not look like a valid .mp4 or .mkv file.");
                }
            }

            if (session.Hash is null || session.Length != stored)
            {
                await session.RebuildHashAsync(partPath, stored, cancellationToken).ConfigureAwait(false);
            }

            var streamOptions = new FileStreamOptions
            {
                Mode = stored == 0 ? FileMode.CreateNew : FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous
            };
            if (stored == 0 && !OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            }

            session.Length = -1;
            using (var fs = new FileStream(partPath, streamOptions))
            {
                await fs.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            session.Hash!.AppendData(buffer, 0, buffer.Length);
            session.Length = stored + buffer.Length;
            session.LastUsedUtc = DateTime.UtcNow;
            return Ok(new ChunkResponse { Status = "ok", Received = session.Length });
        }
        catch (OperationCanceledException)
        {
            session.Length = -1;
            return Error(StatusCodes.Status400BadRequest, "aborted", "The upload was cancelled before the chunk was stored.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Length = -1;
            _logger.LogError(ex, "Rippy chunk upload failed while writing {UploadId}", uploadId);
            return Error(StatusCodes.Status500InternalServerError, "storage_error", "The chunk could not be written to the upload directory.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>
    /// Finishes a chunked upload: checks size, content and SHA-256, then moves the file into place.
    /// </summary>
    /// <param name="uploadId">The client-chosen upload id (32 hex characters).</param>
    /// <param name="filename">The target file name (must end in .mp4 or .mkv).</param>
    /// <param name="folder">Optional relative sub-folder inside the upload directory.</param>
    /// <param name="total">The size of the whole file.</param>
    /// <param name="cancellationToken">Cancellation token tied to the request.</param>
    /// <returns>The result of the upload.</returns>
    [HttpPost("Upload/Complete")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> CompleteChunkedUpload([FromQuery] string? uploadId, [FromQuery] string? filename, [FromQuery] string? folder, [FromQuery] long total, CancellationToken cancellationToken)
    {
        var denied = CheckChunkCaller(uploadId, out var userId, out var destination, out var config);
        if (denied is not null)
        {
            return denied;
        }

        if (!TryValidateName(filename, true, out var fileName, out var extension, out var nameError))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_filename", nameError);
        }

        if (!TryValidateFolder(folder, out var folderSegments, out var folderError))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_folder", folderError);
        }

        var expectedSha = Request.Headers["X-Content-SHA256"].ToString().Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(expectedSha))
        {
            return Error(StatusCodes.Status400BadRequest, "missing_checksum", "The X-Content-SHA256 header is required.");
        }

        if (expectedSha.Length != 64 || !expectedSha.All(Uri.IsHexDigit))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_checksum", "X-Content-SHA256 must be 64 hexadecimal characters.");
        }

        string finalPath;
        try
        {
            Directory.CreateDirectory(destination);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!TryResolveDirectory(destination, folderSegments, out var targetDirectory))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_folder", "The folder resolves outside the upload directory.");
            }

            finalPath = Path.Combine(targetDirectory, fileName);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? string.Empty);
            if (!string.Equals(parent, targetDirectory, comparison))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_filename", "The file name resolves outside the upload directory.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rippy upload: destination directory is not usable");
            return Error(StatusCodes.Status500InternalServerError, "storage_error", "The upload directory is not accessible to Jellyfin.");
        }

        if (System.IO.File.Exists(finalPath))
        {
            return Error(StatusCodes.Status409Conflict, "exists", "A file with this name already exists.");
        }

        var key = ChunkKey(userId, uploadId!);
        var partPath = ChunkPartPath(destination, userId, uploadId!);
        if (!System.IO.File.Exists(partPath))
        {
            return Error(StatusCodes.Status404NotFound, "no_upload", "No chunked upload with this id is in progress.");
        }

        var session = ChunkSessions.GetOrAdd(key, _ => new ChunkSession());
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var usage = Plugin.Instance!.Usage;
        long reservedBytes = 0;
        var moved = false;
        try
        {
            var stored = new FileInfo(partPath).Length;
            if (stored != total)
            {
                return StatusCode(StatusCodes.Status409Conflict, new ChunkResponse { Status = "offset_mismatch", Received = stored });
            }

            var header = new byte[HeaderLength];
            using (var probe = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var got = 0;
                while (got < HeaderLength)
                {
                    var read = await probe.ReadAsync(header.AsMemory(got, HeaderLength - got), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    got += read;
                }

                if (got < HeaderLength || !HeaderMatches(extension, header))
                {
                    return Error(StatusCodes.Status400BadRequest, "invalid_content", "The file content does not look like a valid " + extension + " file.");
                }
            }

            if (session.Hash is null || session.Length != stored)
            {
                await session.RebuildHashAsync(partPath, stored, cancellationToken).ConfigureAwait(false);
            }

            var sha = Convert.ToHexString(session.Hash!.GetHashAndReset()).ToLowerInvariant();
            session.Length = -1;
            if (!string.Equals(expectedSha, sha, StringComparison.Ordinal))
            {
                DeletePart(partPath);
                CloseChunkSession(key);
                return Error(StatusCodes.Status422UnprocessableEntity, "checksum_mismatch", "The SHA-256 of the received data does not match X-Content-SHA256.");
            }

            var quotaBytes = config.UserQuotaMegabytes > 0 ? (long)config.UserQuotaMegabytes * 1024 * 1024 : 0;
            if (!usage.TryReserve(userId, destination, quotaBytes, stored, out _, out reservedBytes, out var usedBytes))
            {
                return Error(
                    StatusCodes.Status507InsufficientStorage,
                    "quota_exceeded",
                    "Upload quota exceeded: " + (usedBytes / 1048576) + " MB of " + config.UserQuotaMegabytes + " MB is in use. Ask an administrator to move or remove your files.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                System.IO.File.Move(partPath, finalPath, false);
                moved = true;
            }
            catch (IOException) when (System.IO.File.Exists(finalPath))
            {
                return Error(StatusCodes.Status409Conflict, "exists", "A file with this name already exists.");
            }

            if (!usage.Record(userId, string.Join('/', folderSegments.Append(fileName)), stored))
            {
                _logger.LogWarning("Rippy upload usage ledger could not be saved to disk; quota is tracked in memory until restart");
            }

            _logger.LogInformation("Rippy chunked upload complete: {FileName} ({Size} bytes) from user {UserId}", fileName, stored, userId);
            QueueLibraryScan();
            CloseChunkSession(key);

            return StatusCode(StatusCodes.Status201Created, new UploadResponse
            {
                Status = "ok",
                Filename = fileName,
                Folder = string.Join('/', folderSegments),
                Size = stored,
                Sha256 = sha
            });
        }
        catch (OperationCanceledException)
        {
            session.Length = -1;
            return Error(StatusCodes.Status400BadRequest, "aborted", "The request was cancelled before the upload was finished.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Length = -1;
            _logger.LogError(ex, "Rippy chunked upload failed while finishing {FileName}", fileName);
            return Error(StatusCodes.Status500InternalServerError, "storage_error", "The file could not be written to the upload directory.");
        }
        finally
        {
            if (reservedBytes > 0)
            {
                usage.Release(userId, reservedBytes);
            }

            session.Gate.Release();
            if (moved)
            {
                ChunkSessions.TryRemove(key, out _);
            }
        }
    }

    private IActionResult? CheckChunkCaller(string? uploadId, out Guid userId, out string destination, out PluginConfiguration config)
    {
        userId = Guid.Empty;
        destination = string.Empty;
        config = null!;

        var current = Plugin.Instance?.Configuration;
        if (current is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured", "Plugin is not initialized.");
        }

        config = current;
        userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "A Jellyfin user session token is required. API keys are not accepted.");
        }

        if (!config.AllowedUserIds.Contains(userId))
        {
            _logger.LogWarning("Rippy upload rejected: user {UserId} is not permitted to upload", userId);
            return Error(StatusCodes.Status403Forbidden, "forbidden", "This user is not allowed to upload videos.");
        }

        var resolved = ResolveDestination(config.DestinationPath);
        if (resolved is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured", "The upload destination has not been configured by an administrator.");
        }

        destination = resolved;
        if (string.IsNullOrEmpty(uploadId) || uploadId.Length != 32 || !uploadId.All(Uri.IsHexDigit))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_upload_id", "uploadId must be 32 hexadecimal characters.");
        }

        return null;
    }

    private static string ChunkKey(Guid userId, string uploadId)
    {
        return userId.ToString("N") + "-" + uploadId.ToLowerInvariant();
    }

    private static string ChunkPartPath(string destination, Guid userId, string uploadId)
    {
        return Path.Combine(destination, IncomingDirectoryName, ChunkKey(userId, uploadId) + ".part");
    }

    private static bool HeaderMatchesAny(byte[] firstChunk)
    {
        if (firstChunk.Length < HeaderLength)
        {
            return false;
        }

        return HeaderMatches(".mkv", firstChunk) || HeaderMatches(".mp4", firstChunk);
    }

    private static void CloseChunkSession(string key)
    {
        if (ChunkSessions.TryRemove(key, out var session))
        {
            session.Dispose();
        }
    }

    private static void PruneChunkSessions()
    {
        foreach (var pair in ChunkSessions)
        {
            if (DateTime.UtcNow - pair.Value.LastUsedUtc > TimeSpan.FromDays(1) && pair.Value.Gate.CurrentCount > 0)
            {
                CloseChunkSession(pair.Key);
            }
        }
    }

    private void DeletePart(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete incomplete upload {TempPath}", path);
        }
    }

    private sealed class ChunkSession : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public IncrementalHash? Hash { get; set; }

        public long Length { get; set; } = -1;

        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

        public void ResetHash()
        {
            Hash?.Dispose();
            Hash = null;
            Length = -1;
        }

        public async Task RebuildHashAsync(string path, long count, CancellationToken cancellationToken)
        {
            ResetHash();
            var fresh = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (count > 0)
            {
                var buffer = new byte[BufferSize];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long remaining = count;
                while (remaining > 0)
                {
                    var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    fresh.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }

            Hash = fresh;
            Length = count;
        }

        public void Dispose()
        {
            Hash?.Dispose();
            Hash = null;
        }
    }

    /// <summary>Chunk response body.</summary>
    public sealed class ChunkResponse
    {
        /// <summary>Gets or sets the status ("ok" or "offset_mismatch").</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>Gets or sets how many bytes of the upload the server holds.</summary>
        [JsonPropertyName("received")]
        public long Received { get; set; }
    }

    private static bool TryResolveDirectory(string destination, string[] segments, out string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var expected = Path.Combine(new[] { destination }.Concat(segments).ToArray());
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected));
        var rootWithSeparator = destination.EndsWith(Path.DirectorySeparatorChar) ? destination : destination + Path.DirectorySeparatorChar;
        return string.Equals(directory, expected, comparison)
            && (segments.Length == 0 || directory.StartsWith(rootWithSeparator, comparison));
    }

    private static bool IsHiddenPath(string relativePath)
    {
        return relativePath.Split('/').Any(part => part.StartsWith('.'));
    }

    private void CleanStaleParts(string directory)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, "*.part"))
            {
                if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(stale) > TimeSpan.FromDays(1))
                {
                    System.IO.File.Delete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clean stale incomplete uploads in {Directory}", directory);
        }
    }

    /// <summary>
    /// Reports how much each user currently has in the upload directory (administrators only).
    /// </summary>
    /// <returns>Usage per user.</returns>
    [HttpGet("Usage")]
    [Authorize(Roles = "Administrator")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<UserUsage>> GetUsage()
    {
        var config = Plugin.Instance?.Configuration;
        var destination = config is null ? null : ResolveDestination(config.DestinationPath);
        if (destination is null || Plugin.Instance is null)
        {
            return new List<UserUsage>();
        }

        return Plugin.Instance.Usage.GetAllUsage(destination)
            .Where(pair => pair.Value > 0)
            .Select(pair => new UserUsage { UserId = pair.Key.ToString("N"), UsedBytes = pair.Value })
            .ToList();
    }

    /// <summary>
    /// Lists previously uploaded videos so Rippy can skip what already exists.
    /// </summary>
    /// <param name="folder">Optional relative folder to list (same rules as upload).</param>
    /// <param name="recursive">Whether to include sub-folders' contents.</param>
    /// <returns>The files and folders found.</returns>
    [HttpGet("List")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string? folder, [FromQuery] bool recursive = false)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured", "Plugin is not initialized.");
        }

        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "A Jellyfin user session token is required. API keys are not accepted.");
        }

        if (!config.AllowedUserIds.Contains(userId))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "This user is not allowed to use Rippy uploads.");
        }

        var destination = ResolveDestination(config.DestinationPath);
        if (destination is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured", "The upload destination has not been configured by an administrator.");
        }

        if (!TryValidateFolder(folder, out var segments, out var folderError))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_folder", folderError);
        }

        if (!TryResolveDirectory(destination, segments, out var directory))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_folder", "The folder resolves outside the upload directory.");
        }

        var response = new ListResponse { Folder = string.Join('/', segments) };
        if (!Directory.Exists(directory))
        {
            return Ok(response);
        }

        response.Exists = true;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            MaxRecursionDepth = MaxFolderDepth,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                if (IsHiddenPath(relative) || !AllowedExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant()))
                {
                    continue;
                }

                if (response.Files.Count >= MaxListEntries)
                {
                    response.Truncated = true;
                    break;
                }

                response.Files.Add(new ListedFile { RelativePath = relative, Size = new FileInfo(file).Length });
            }

            foreach (var sub in Directory.EnumerateDirectories(directory, "*", options))
            {
                var relative = Path.GetRelativePath(directory, sub).Replace(Path.DirectorySeparatorChar, '/');
                if (IsHiddenPath(relative))
                {
                    continue;
                }

                if (response.Folders.Count >= MaxListEntries)
                {
                    response.Truncated = true;
                    break;
                }

                response.Folders.Add(relative);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rippy list failed for {Directory}", directory);
            return Error(StatusCodes.Status500InternalServerError, "storage_error", "The upload directory could not be read.");
        }

        return Ok(response);
    }

    private static string? ResolveDestination(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathRooted(configured))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    }

    private static bool TryValidateName(string? input, bool isFile, out string fileName, out string extension, out string error)
    {
        fileName = string.Empty;
        extension = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = isFile
                ? "A filename is required (query parameter 'filename' or header 'X-Filename')."
                : "Folder names must not be empty.";
            return false;
        }

        // Canonical Unicode form so visually identical names cannot differ byte-wise.
        string candidate;
        try
        {
            candidate = input.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            error = "The filename is not valid Unicode.";
            return false;
        }

        if (candidate.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            error = "The filename must not contain path separators.";
            return false;
        }

        if (candidate != candidate.Trim()
            || candidate.StartsWith('.') || candidate.EndsWith('.')
            || candidate.StartsWith('-')
            || candidate.Contains("..", StringComparison.Ordinal)
            || candidate.IndexOfAny(ForbiddenNameChars) >= 0
            || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || candidate.Any(IsUnsafeChar)
            || Encoding.UTF8.GetByteCount(candidate) > 200)
        {
            error = "The filename contains invalid characters, sequences, or is too long.";
            return false;
        }

        if (isFile)
        {
            extension = Path.GetExtension(candidate).ToLowerInvariant();
            if (!AllowedExtensions.Contains(extension))
            {
                error = "Only .mp4 and .mkv files are accepted.";
                return false;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var stem = isFile ? Path.GetFileNameWithoutExtension(candidate) : candidate;
            if (WindowsReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase) || candidate.Contains('~'))
            {
                error = "The filename is reserved by, or ambiguous on, the operating system.";
                return false;
            }
        }

        fileName = candidate;
        return true;
    }

    private static bool TryValidateFolder(string? folder, out string[] segments, out string error)
    {
        segments = Array.Empty<string>();
        error = string.Empty;

        if (string.IsNullOrEmpty(folder))
        {
            return true;
        }

        if (folder.Contains('\\'))
        {
            error = "Use '/' between folder levels; backslashes are not allowed.";
            return false;
        }

        var parts = folder.Split('/');
        if (parts.Length > MaxFolderDepth)
        {
            error = "The folder path is nested too deeply (maximum " + MaxFolderDepth + " levels).";
            return false;
        }

        var clean = new List<string>();
        foreach (var part in parts)
        {
            if (!TryValidateName(part, false, out var name, out _, out var partError))
            {
                error = "Invalid folder: " + partError;
                return false;
            }

            clean.Add(name);
        }

        segments = clean.ToArray();
        return true;
    }

    private static bool IsUnsafeChar(char c)
    {
        // Rejects control, format (e.g. bidi overrides), separator, private-use and unassigned characters.
        // Surrogates are allowed: Normalize() has already rejected malformed pairs.
        var category = char.GetUnicodeCategory(c);
        return category is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    private static bool HeaderMatches(string extension, byte[] header)
    {
        if (extension == ".mkv")
        {
            return header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3;
        }

        return header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
    }

    private Guid GetUserId()
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    private ObjectResult Error(int status, string code, string message)
    {
        return StatusCode(status, new ErrorResponse { Error = code, Message = message });
    }

    private void QueueLibraryScan()
    {
        // Coalesce bursts: at most one scan running and one waiting.
        if (Interlocked.Exchange(ref _scanPending, 1) == 1)
        {
            return;
        }

        var libraryManager = _libraryManager;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            await ScanLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _scanPending, 0);
                await libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Library refresh after Rippy upload failed");
            }
            finally
            {
                ScanLock.Release();
            }
        });
    }

    /// <summary>Error response body.</summary>
    public sealed class ErrorResponse
    {
        /// <summary>Gets or sets the machine-readable error code.</summary>
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        /// <summary>Gets or sets the human-readable message.</summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Success response body.</summary>
    public sealed class UploadResponse
    {
        /// <summary>Gets or sets the status ("ok").</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>Gets or sets the stored file name.</summary>
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        /// <summary>Gets or sets the relative folder the file was stored in (empty for the root).</summary>
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = string.Empty;

        /// <summary>Gets or sets the stored size in bytes.</summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>Gets or sets the lowercase hex SHA-256 of the stored data.</summary>
        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>One user's current usage.</summary>
    public sealed class UserUsage
    {
        /// <summary>Gets or sets the Jellyfin user ID (32 hex characters).</summary>
        [JsonPropertyName("userId")]
        public string UserId { get; set; } = string.Empty;

        /// <summary>Gets or sets the bytes currently in the upload directory.</summary>
        [JsonPropertyName("usedBytes")]
        public long UsedBytes { get; set; }
    }

    /// <summary>One listed video file.</summary>
    public sealed class ListedFile
    {
        /// <summary>Gets or sets the path relative to the listed folder, using "/".</summary>
        [JsonPropertyName("path")]
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>Gets or sets the size in bytes.</summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    /// <summary>List response body.</summary>
    public sealed class ListResponse
    {
        /// <summary>Gets or sets a value indicating whether the folder exists.</summary>
        [JsonPropertyName("exists")]
        public bool Exists { get; set; }

        /// <summary>Gets or sets the listed folder (empty for the root).</summary>
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = string.Empty;

        /// <summary>Gets or sets the video files found.</summary>
        [JsonPropertyName("files")]
        public List<ListedFile> Files { get; set; } = new List<ListedFile>();

        /// <summary>Gets or sets the sub-folders found.</summary>
        [JsonPropertyName("folders")]
        public List<string> Folders { get; set; } = new List<string>();

        /// <summary>Gets or sets a value indicating whether the listing was cut off at the entry limit.</summary>
        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }
    }

    /// <summary>Status response body.</summary>
    public sealed class StatusResponse
    {
        /// <summary>Gets or sets a value indicating whether the caller may upload.</summary>
        [JsonPropertyName("canUpload")]
        public bool CanUpload { get; set; }

        /// <summary>Gets or sets a value indicating whether an upload destination is configured.</summary>
        [JsonPropertyName("configured")]
        public bool Configured { get; set; }

        /// <summary>Gets or sets the maximum upload size in bytes (0 = unlimited).</summary>
        [JsonPropertyName("maxFileSizeBytes")]
        public long MaxFileSizeBytes { get; set; }

        /// <summary>Gets or sets the bytes the caller currently has in the upload directory.</summary>
        [JsonPropertyName("usedBytes")]
        public long UsedBytes { get; set; }

        /// <summary>Gets or sets the per-user quota in bytes (0 = unlimited).</summary>
        [JsonPropertyName("quotaBytes")]
        public long QuotaBytes { get; set; }

        /// <summary>Gets or sets a value indicating whether chunked uploads (Upload/Chunk, Upload/Complete) are supported.</summary>
        [JsonPropertyName("chunked")]
        public bool Chunked { get; set; }

        /// <summary>Gets or sets the largest single chunk in bytes.</summary>
        [JsonPropertyName("maxChunkBytes")]
        public long MaxChunkBytes { get; set; }

        /// <summary>Gets or sets the accepted file extensions.</summary>
        [JsonPropertyName("allowedExtensions")]
        public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
    }
}
