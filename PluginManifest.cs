using System;
using System.Collections.Generic;

namespace Indexer;

/// <summary>
/// Contains information about the manifest for the plugin.
/// </summary>
public class PluginManifest
{
    /// <summary>
    /// Gets the version of plugin.
    /// </summary>
    public GameVersion PluginVersion { get; set; }

    /// <summary>
    /// Gets the standard/core library version of the API of the plugin.
    /// </summary>
    public GameVersion PluginStandardVersion { get; set; }

    /// <summary>
    /// Creation date of the plugin.
    /// </summary>
    public DateTimeOffset PluginCreationDate { get; set; }

    /// <summary>
    /// The alternative icon to be displayed on the main (plugin loader) application (if available).<br/>
    /// Value can be a URL, local file path or a Base64 string.<br/>
    /// </summary>
    public string? PluginAlternativeIcon { get; set; }

    /// <summary>
    /// Gets the compilation date of the plugin.
    /// </summary>
    public DateTimeOffset ManifestDate { get; set; }

    /// <summary>
    /// Gets the path of the main library file.
    /// </summary>
    public required string MainLibraryName { get; set; }

    /// <summary>
    /// Gets the plugin name info.
    /// </summary>
    public string? MainPluginName { get; set; }

    /// <summary>
    /// Gets the plugin's author info.
    /// </summary>
    public string? MainPluginAuthor { get; set; }

    /// <summary>
    /// Gets the plugin's description info.
    /// </summary>
    public string? MainPluginDescription { get; set; }
}