// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.PowerApps.Persistence.Models;
using Microsoft.PowerPlatform.PowerApps.Persistence.Yaml;
using YamlDotNet.Serialization;

namespace Microsoft.PowerPlatform.PowerApps.Persistence;

/// <summary>
/// Represents a .msapp file.
/// </summary>
public class MsappArchive : IMsappArchive, IDisposable
{
    #region Constants

    public static class Directories
    {
        public const string Src = "Src";
        public const string Controls = "Controls";
        public const string Components = "Components";
        public const string AppTests = "AppTests";
        public const string References = "References";
        public const string Resources = "Resources";
    }

    public const string YamlFileExtension = ".yaml";
    public const string YamlFxFileExtension = ".fx.yaml";
    public const string JsonFileExtension = ".json";
    public const string AppFileName = "1.fx.yaml";

    #endregion

    #region Fields

    private readonly Lazy<IDictionary<string, ZipArchiveEntry>> _canonicalEntries;
    private App? _app;
    private bool _isDisposed;
    private readonly ILogger<MsappArchive>? _logger;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    #endregion

    #region Internal classes

    /// <summary>
    /// Helper class for deserializing the top level control editor state.
    /// </summary>
    private class TopParentJson
    {
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
        public ControlEditorState? TopParent { get; set; }
#pragma warning restore CS0649
    }

    #endregion

    #region Constructors

    public MsappArchive(string path, IYamlSerializationFactory yamlSerializationFactory, ILogger<MsappArchive>? logger = null)
        : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read, leaveOpen: false, yamlSerializationFactory, logger)
    {
    }

    public MsappArchive(Stream stream, IYamlSerializationFactory yamlSerializationFactory, ILogger<MsappArchive>? logger = null)
        : this(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null, yamlSerializationFactory, logger)
    {
    }

    public MsappArchive(Stream stream, ZipArchiveMode mode, IYamlSerializationFactory yamlSerializationFactory, ILogger<MsappArchive>? logger = null)
        : this(stream, mode, leaveOpen: false, entryNameEncoding: null, yamlSerializationFactory, logger)
    {
    }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="mode"></param>
    /// <param name="leaveOpen">
    ///     true to leave the stream open after the System.IO.Compression.ZipArchive object is disposed; otherwise, false
    /// </param>
    /// <param name="yamlSerializationFactory"></param>
    /// <param name="logger"></param>
    public MsappArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen, IYamlSerializationFactory yamlSerializationFactory, ILogger<MsappArchive>? logger = null)
        : this(stream, mode, leaveOpen, null, yamlSerializationFactory, logger)
    {
    }

    public MsappArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen, Encoding? entryNameEncoding, IYamlSerializationFactory yamlSerializationFactory, ILogger<MsappArchive>? logger = null)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _serializer = yamlSerializationFactory.CreateSerializer();
        _deserializer = yamlSerializationFactory.CreateDeserializer();
        _logger = logger;
        ZipArchive = new ZipArchive(stream, mode, leaveOpen, entryNameEncoding);
        _canonicalEntries = new Lazy<IDictionary<string, ZipArchiveEntry>>
        (() =>
        {
            var canonicalEntries = new Dictionary<string, ZipArchiveEntry>();
            // If we're creating a new archive, there are no entries to canonicalize.
            if (mode == ZipArchiveMode.Create)
                return canonicalEntries;
            foreach (var entry in ZipArchive.Entries)
            {
                if (!canonicalEntries.TryAdd(NormalizePath(entry.FullName), entry))
                    _logger?.LogInformation($"Duplicate entry found in archive: {entry.FullName}");
            }
            return canonicalEntries;
        });
    }

    #endregion

    #region Factory Methods

    public static IMsappArchive Create(string path, IYamlSerializationFactory yamlSerializationFactory, ILogger<MsappArchive>? logger = null)
    {
        var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        return new MsappArchive(fileStream, ZipArchiveMode.Create, yamlSerializationFactory, logger);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Canonical entries in the archive.  Keys are normalized paths (lowercase, forward slashes, no trailing slash).
    /// </summary>
    public IReadOnlyDictionary<string, ZipArchiveEntry> CanonicalEntries => _canonicalEntries.Value.AsReadOnly();

    /// <inheritdoc/>
    public ZipArchive ZipArchive { get; private set; }

    /// <summary>
    /// Total sum of decompressed sizes of all entries in the archive.
    /// </summary>
    public long DecompressedSize => ZipArchive.Entries.Sum(zipArchiveEntry => zipArchiveEntry.Length);

    /// <summary>
    /// Total sum of compressed sizes of all entries in the archive.
    /// </summary>
    public long CompressedSize => ZipArchive.Entries.Sum(zipArchiveEntry => zipArchiveEntry.CompressedLength);

    public App? App
    {
        get
        {
            if (_app == null)
                _app = LoadApp();
            return _app;
        }
        set => _app = value;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Returns all entries in the archive that are in the given directory.
    /// </summary>
    /// <param name="directoryName"></param>
    /// <param name="extension"></param>
    /// <returns></returns>
    public IEnumerable<ZipArchiveEntry> GetDirectoryEntries(string directoryName, string? extension = null)
    {
        _ = directoryName ?? throw new ArgumentNullException(nameof(directoryName));

        directoryName = NormalizePath(directoryName);

        foreach (var entry in CanonicalEntries)
        {
            if (!entry.Key.StartsWith(directoryName + '/'))
                continue;

            if (extension != null && !entry.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return entry.Value;
        }
    }

    /// <inheritdoc/>
    public ZipArchiveEntry? GetEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            return null;

        entryName = NormalizePath(entryName);
        if (CanonicalEntries.TryGetValue(entryName, out var entry))
            return entry;

        return null;
    }

    /// <summary>
    /// Returns the entry in the archive with the given name or throws if it does not exist.
    /// </summary>
    /// <param name="entryName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    public ZipArchiveEntry GetRequiredEntry(string entryName)
    {
        var entry = GetEntry(entryName) ??
            throw new FileNotFoundException($"Entry '{entryName}' not found in msapp archive.");

        return entry;
    }

    /// <inheritdoc/>
    public ZipArchiveEntry CreateEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new ArgumentException("Entry name cannot be null or whitespace.", nameof(entryName));

        var canonicalEntryName = NormalizePath(entryName);
        if (_canonicalEntries.Value.ContainsKey(canonicalEntryName))
            throw new InvalidOperationException($"Entry {entryName} already exists in the archive.");

        var entry = ZipArchive.CreateEntry(entryName);
        _canonicalEntries.Value.Add(canonicalEntryName, entry);

        return entry;
    }

    public T Deserialize<T>(ZipArchiveEntry archiveEntry) where T : class
    {
        _ = archiveEntry ?? throw new ArgumentNullException(nameof(archiveEntry));
        using var textReader = new StreamReader(archiveEntry.Open());
        try
        {
            var result = _deserializer!.Deserialize(textReader) as T;
            return result ?? throw new PersistenceException($"Failed to deserialize archive entry.") { FileName = archiveEntry.FullName };
        }
        catch (Exception ex)
        {
            throw new PersistenceException("Failed to deserialize archive entry.", ex) { FileName = archiveEntry.FullName };
        }

    }

    public static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
    }

    public void Save()
    {
        if (_app == null)
            throw new InvalidOperationException("App is not set.");

        var appEntry = CreateEntry(Path.Combine(Directories.Src, Directories.Controls, AppFileName));
        using (var appWriter = new StreamWriter(appEntry.Open()))
        {
            _serializer.Serialize(appWriter, _app);
        }

        foreach (var screen in _app.Screens)
        {
            var screenEntry = CreateEntry(Path.Combine(Directories.Src, Directories.Controls, $"{screen.Name}.fx.yaml"));
            using var screenWriter = new StreamWriter(screenEntry.Open());
            _serializer.Serialize(screenWriter, screen);
        }
    }

    #endregion

    #region Private Methods

    private App? LoadApp()
    {
        // For app entry name is always "1.fx.yaml"now 
        var appEntry = GetEntry(Path.Combine(Directories.Src, Directories.Controls, AppFileName));
        if (appEntry == null)
            return null;
        var app = Deserialize<App>(appEntry);

        app.Screens = LoadScreens();

        return app;
    }

    private List<Screen> LoadScreens()
    {
        _logger?.LogInformation("Loading top level screens from Yaml.");

        var screens = new Dictionary<string, Screen>();
        foreach (var yamlEntry in GetDirectoryEntries(Path.Combine(Directories.Src, Directories.Controls), YamlFileExtension))
        {
            // Skip the app file
            if (yamlEntry.FullName.EndsWith(AppFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var screen = Deserialize<Screen>(yamlEntry);
            screens.Add(screen.Name, screen);
        }

        _logger?.LogInformation("Loading top level controls editor state.");
        var controlEditorStates = new Dictionary<string, ControlEditorState>();
        foreach (var editorStateEntry in GetDirectoryEntries(Path.Combine(Directories.Controls), JsonFileExtension))
        {
            try
            {
                var topParentJson = JsonSerializer.Deserialize<TopParentJson>(editorStateEntry.Open());
                controlEditorStates.Add(topParentJson!.TopParent!.Name, topParentJson.TopParent);
            }
            catch (Exception ex)
            {
                throw new PersistenceException("Failed to deserialize control editor state file.", ex) { FileName = editorStateEntry.FullName };
            }
        }

        // Merge the editor state into the controls
        foreach (var control in screens.Values)
        {
            if (controlEditorStates.TryGetValue(control.Name, out var editorState))
            {
                MergeControlEditorState(control, editorState);
                controlEditorStates.Remove(control.Name);
            }
        }

        return screens.Values.ToList();
    }

    private static void MergeControlEditorState(Control control, ControlEditorState controlEditorState)
    {
        control.EditorState = controlEditorState;
        if (control.Controls == null)
            return;

        foreach (var child in control.Controls)
        {
            if (controlEditorState.Controls == null)
                continue;

            // Find the editor state for the child by name
            var childEditorState = controlEditorState.Controls.FirstOrDefault(c => c.Name == child.Name);
            if (childEditorState == null)
                continue;

            MergeControlEditorState(child, childEditorState);
        }
        controlEditorState.Controls = null;
    }

    #endregion

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                if (!_leaveOpen)
                {
                    ZipArchive.Dispose();
                    _stream.Dispose();
                }
            }

            _isDisposed = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
