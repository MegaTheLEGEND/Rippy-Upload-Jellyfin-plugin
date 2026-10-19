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
[Authorize(Policy = "DefaultAuthorization")]
[Produces("application/json")]
public class RippyUploadController : ControllerBase
{
    private const int BufferSize = 81920;
    private const int HeaderLength = 12;
    private const int MaxFolderDepth = 4;
    private const int MaxListEntries = 10000;
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
            QuotaBytes = config is null || config.UserQuotaMegabytes <= 0 ? 0 : (long)config.UserQuotaMegabytes * 1024 * 1024
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
    [Authorize(Policy = "RequiresElevation")]
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

        /// <summary>Gets or sets the accepted file extensions.</summary>
        [JsonPropertyName("allowedExtensions")]
        public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
    }
}
