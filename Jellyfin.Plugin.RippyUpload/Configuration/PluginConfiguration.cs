using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RippyUpload.Configuration;

/// <summary>
/// Persisted plugin settings and per-user upload permissions.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the absolute directory where completed uploads are stored.
    /// </summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum size of a single upload in megabytes. 0 disables the limit.
    /// </summary>
    public int MaxFileSizeMegabytes { get; set; } = 10240;

    /// <summary>
    /// Gets or sets how many megabytes each user may have in the upload directory at once. 0 disables the limit.
    /// </summary>
    public int UserQuotaMegabytes { get; set; } = 10240;

    /// <summary>
    /// Gets or sets the maximum number of uploads processed at the same time.
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 2;

    /// <summary>
    /// Gets or sets the Jellyfin user IDs that are allowed to upload. Empty by default.
    /// </summary>
    public Guid[] AllowedUserIds { get; set; } = Array.Empty<Guid>();
}
