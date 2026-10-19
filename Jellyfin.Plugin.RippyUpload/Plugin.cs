using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.RippyUpload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RippyUpload;

/// <summary>
/// Rippy Upload plugin: receives video uploads from Rippy for authorized Jellyfin users.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Usage = new UsageLedger(Path.Combine(applicationPaths.PluginConfigurationsPath, "RippyUpload.usage.json"));
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the per-user upload ledger used for quotas.
    /// </summary>
    public UsageLedger Usage { get; }

    /// <inheritdoc />
    public override string Name => "Rippy Upload";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7b2f3c1e-5a4d-4e8b-9c6a-2d1f8e0b3a97");

    /// <inheritdoc />
    public override string Description => "Receives MP4/MKV video uploads from Rippy for users who have been granted upload permission.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "RippyUpload",
            DisplayName = "Rippy Upload",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "cloud_upload"
        };
    }
}
