using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable AccessToDisposedClosure
// ReSharper disable StringLiteralTypo
// ReSharper disable InvocationIsCultureSensitive

namespace Indexer;

public class SelfUpdateAssetInfo
{
    public required string FilePath { get; set; }

    public long Size { get; set; }

    public required byte[] FileHash { get; set; }
}

public class Program
{
    private const string ExAllowedPluginExt = ".dll;.exe;.so;.dylib";
    private const string ExPackageExtension = ".zip";

    public static int Main(params string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return int.MaxValue;
        }

        try
        {
            string path = args[0];
            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine("Path is not a directory or it doesn't exist!");
                return 2;
            }

            FileInfo? fileInfo = FindPluginLibraryAndGetAssets(path, out List<SelfUpdateAssetInfo> assetInfo, out PluginManifest? reference);
            if (fileInfo == null || reference == null || string.IsNullOrEmpty(reference.MainLibraryName))
            {
                Console.Error.WriteLine("No valid plugin library was found.");
                return 1;
            }

            string referenceFilePath = Path.Combine(path, "manifest.json");
            int    retCode           = WriteToJson(reference, referenceFilePath, assetInfo);
            return retCode != 0 ? retCode : PackFiles(path, reference, assetInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An unknown error has occurred! {ex}");
            return int.MinValue;
        }
    }

    private static int WriteToJson(PluginManifest reference, string referenceFilePath, List<SelfUpdateAssetInfo> assetInfo)
    {
        DateTimeOffset creationDate = reference.PluginCreationDate.ToOffset(reference.PluginCreationDate.Offset);

        Console.WriteLine("Plugin has been found!");
        Console.WriteLine($"  Main Library Path Name : {reference.MainLibraryName}");
        Console.WriteLine($"  Main Plugin Name       : {reference.MainPluginName}");
        Console.WriteLine($"  Creation Date          : {creationDate}");
        Console.WriteLine($"  Version                : {reference.PluginVersion}");
        Console.Write("Writing metadata info...");

        using FileStream referenceFileStream = File.Create(referenceFilePath);
        using Utf8JsonWriter writer = new(referenceFileStream, new JsonWriterOptions
        {
            Indented        = true,
            IndentCharacter = ' ',
            IndentSize      = 2,
            NewLine         = "\n"
        });

        writer.WriteStartObject();

        writer.WriteString(nameof(PluginManifest.MainLibraryName),       reference.MainLibraryName);
        writer.WriteString(nameof(PluginManifest.MainPluginName),        reference.MainPluginName);
        writer.WriteString(nameof(PluginManifest.MainPluginAuthor),      reference.MainPluginAuthor);
        writer.WriteString(nameof(PluginManifest.MainPluginDescription), reference.MainPluginDescription);
        writer.WriteString(nameof(PluginManifest.PluginStandardVersion), reference.PluginStandardVersion.ToString());
        writer.WriteString(nameof(PluginManifest.PluginVersion),         reference.PluginVersion.ToString());
        writer.WriteString(nameof(PluginManifest.PluginCreationDate),    creationDate);
        writer.WriteString(nameof(PluginManifest.ManifestDate),          reference.ManifestDate);
        if (reference.PluginAlternativeIcon?.Length != 0)
        {
            writer.WriteString(nameof(PluginManifest.PluginAlternativeIcon), reference.PluginAlternativeIcon);
        }

        writer.WriteStartArray("Assets");
        foreach (SelfUpdateAssetInfo asset in assetInfo)
        {
            writer.WriteStartObject();

            writer.WriteString(nameof(asset.FilePath), asset.FilePath);
            writer.WriteNumber(nameof(asset.Size), asset.Size);
            writer.WriteBase64String(nameof(asset.FileHash), asset.FileHash);

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();

        Console.WriteLine(" Done!");
        return 0;
    }

    private static int PackFiles(string outputPath, PluginManifest referenceInfo, List<SelfUpdateAssetInfo> fileList)
    {
        string packageName     = $"{Path.GetFileNameWithoutExtension(referenceInfo.MainLibraryName)}_{referenceInfo.PluginVersion}_API-{referenceInfo.PluginStandardVersion}_{referenceInfo.ManifestDate:yyyyMMdd}";
        string packageFilePath = Path.Combine(outputPath, packageName + ExPackageExtension);

        int threads = Environment.ProcessorCount;

        Console.WriteLine($"Writing output package in parallel using {threads} threads at: {packageFilePath}...");

        try
        {
            using FileStream packageFileStream = File.Create(packageFilePath);
            using ZipArchive packageWriter     = new(packageFileStream, ZipArchiveMode.Create, false, Encoding.UTF8);

            fileList.Add(new SelfUpdateAssetInfo
            {
                FileHash = [],
                FilePath = "manifest.json"
            });

            int length = fileList.Count;
            int count  = 0;

            Lock thisLock = new();
            Parallel.ForEach(fileList, CompressAndCreate);

            return 0;

            void CompressAndCreate(SelfUpdateAssetInfo asset)
            {
                Interlocked.Increment(ref count);
                int currentCount = count;

                string filePath = Path.Combine(outputPath, asset.FilePath);
                DateTime lastWriteTime = File.GetLastWriteTimeUtc(filePath);

                if (lastWriteTime.Year is < 1980 or > 2107)
                    lastWriteTime = new DateTime(1980, 1, 1, 0, 0, 0);

                Console.WriteLine($"  [{currentCount}/{length}] Compressing asset to buffer: {asset.FilePath}");
                using FileStream tempStream = File.Open(Path.Combine(Path.GetTempPath(), Path.GetTempFileName()),
                                                        new FileStreamOptions
                                                        {
                                                            Mode    = FileMode.Create,
                                                            Access  = FileAccess.ReadWrite,
                                                            Share   = FileShare.ReadWrite,
                                                            Options = FileOptions.DeleteOnClose
                                                        });
                using ZstandardStream compressStream = new(tempStream, CompressionLevel.SmallestSize);
                using FileStream      fileStream     = File.OpenRead(filePath);

                fileStream.CopyTo(compressStream);
                compressStream.Flush();

                tempStream.Position = 0;

                using (thisLock.EnterScope())
                {
                    Console.WriteLine($"  [{currentCount}/{length}] Compress done. Now locking and writing buffer to package for: {asset.FilePath}");

                    string          entryBrExt = asset.FilePath + ".zstd";
                    ZipArchiveEntry entry      = packageWriter.CreateEntry(entryBrExt, CompressionLevel.NoCompression);
                    entry.LastWriteTime = lastWriteTime;

                    using Stream entryStream = entry.Open();
                    tempStream.CopyTo(entryStream);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return Marshal.GetHRForException(e);
        }
    }

    private static FileInfo? FindPluginLibraryAndGetAssets(string dirPath, out List<SelfUpdateAssetInfo> fileList, out PluginManifest? referenceInfo)
    {
        DirectoryInfo             directoryInfo       = new(dirPath);
        List<SelfUpdateAssetInfo> fileListRef         = [];
        FileInfo?                 mainLibraryFileInfo = null;
        PluginManifest?           referenceInfoResult = null;

        fileList      = fileListRef;
        referenceInfo = null;

        string manifestPath = Path.Combine(dirPath, "manifest.json");
        if (!File.Exists(manifestPath))
            return null;

        Parallel.ForEach(directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories), Impl);
        referenceInfo = referenceInfoResult;

        return mainLibraryFileInfo;

        void Impl(FileInfo fileInfo)
        {
            if (fileInfo.Name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                return;

            string fileName = fileInfo.FullName.AsSpan(directoryInfo.FullName.Length).TrimStart("\\/").ToString();
            string filePath = Path.Combine(dirPath, fileName);

            if (fileName.EndsWith(ExPackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (mainLibraryFileInfo == null &&
                IsPluginLibrary(filePath, fileName, out PluginManifest? referenceInfoInner))
            {
                Interlocked.Exchange(ref mainLibraryFileInfo, fileInfo);
                Interlocked.Exchange(ref referenceInfoResult, referenceInfoInner);
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 10);
            try
            {
                using MD5 hash = MD5.Create();
                using FileStream fileStream = fileInfo.OpenRead();

                int read;
                while ((read = fileStream.Read(buffer)) > 0)
                {
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                }

                hash.TransformFinalBlock(buffer, 0, read);

                byte[] hashBytes = hash.Hash ?? [];
                SelfUpdateAssetInfo assetInfo = new()
                {
                    FileHash = hashBytes,
                    FilePath = fileName,
                    Size     = fileInfo.Length
                };

                lock (fileListRef)
                {
                    fileListRef.Add(assetInfo);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static unsafe bool IsPluginLibrary(string filePath, string fileName, [NotNullWhen(true)] out PluginManifest? referenceInfo)
    {
        nint handle = nint.Zero;
        referenceInfo = null;

        ReadOnlySpan<char> extension = Path.GetExtension(fileName);
        if (ExAllowedPluginExt
            .AsSpan()
            .IndexOf(extension, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        char* getPluginNameP = (char*)Utf16StringMarshaller.ConvertToUnmanaged("GetPlugin");
        char* getPluginVersionNameP = (char*)Utf16StringMarshaller.ConvertToUnmanaged("GetPluginVersion");
        char* getGetPluginStandardVersionNameP = (char*)Utf16StringMarshaller.ConvertToUnmanaged("GetPluginStandardVersion");

        void* pluginAbi = null;
        try
        {
            if (!NativeLibrary.TryLoad(filePath, out handle) ||
                !NativeLibrary.TryGetExport(handle, "TryGetApiExport", out nint exportAddress) ||
                exportAddress == nint.Zero)
            {
                return false;
            }

            var tryGetApiExportCallback = (delegate* unmanaged[Cdecl]<char*, out nint, int>)exportAddress;
            int tryResult               = tryGetApiExportCallback(getPluginNameP, out nint getPluginDelegateP);

            if (tryResult != 0 ||
                getPluginDelegateP == nint.Zero)
            {
                return false;
            }

            pluginAbi = ((delegate* unmanaged[Cdecl]<void*>)getPluginDelegateP)();
            if (pluginAbi == null)
                return false;

            tryResult = tryGetApiExportCallback(getPluginVersionNameP, out nint getPluginVersionDelegateP);
            if (tryResult != 0 ||
                getPluginVersionDelegateP == nint.Zero)
                return false;

            GameVersion pluginVersion = *((delegate* unmanaged[Cdecl]<GameVersion*>)getPluginVersionDelegateP)();
            tryResult = tryGetApiExportCallback(getGetPluginStandardVersionNameP, out nint getPluginStandardVersionDelegateP);

            if (tryResult != 0 ||
                getPluginStandardVersionDelegateP == nint.Zero)
                return false;

            GameVersion pluginStandardVersion = *((delegate* unmanaged[Cdecl]<GameVersion*>)getPluginStandardVersionDelegateP)();

            ((delegate* unmanaged[Stdcall]<void*, out char*, int>)(*(*(void***)pluginAbi + 4)))
                (pluginAbi, out char* pluginNameP);
            ((delegate* unmanaged[Stdcall]<void*, out char*, int>)(*(*(void***)pluginAbi + 5)))
                (pluginAbi, out char* pluginDescriptionP);
            ((delegate* unmanaged[Stdcall]<void*, out char*, int>)(*(*(void***)pluginAbi + 6)))
                (pluginAbi, out char* pluginAuthorP);
            ((delegate* unmanaged[Stdcall]<void*, out DateTime*, int>)(*(*(void***)pluginAbi + 7)))
                (pluginAbi, out DateTime* pluginCreationDateP);

            string?        pluginName         = GetStringAndReleaseW(pluginNameP);
            string?        pluginAuthor       = GetStringAndReleaseW(pluginAuthorP);
            string?        pluginDescription  = GetStringAndReleaseW(pluginDescriptionP);
            DateTimeOffset pluginCreationDate = *pluginCreationDateP;

            referenceInfo = new PluginManifest
            {
                MainPluginName        = pluginName,
                MainPluginAuthor      = pluginAuthor,
                MainPluginDescription = pluginDescription,
                PluginCreationDate    = pluginCreationDate,
                PluginVersion         = pluginVersion,
                PluginStandardVersion = pluginStandardVersion,
                PluginAlternativeIcon = TryGetAlternateIconData(pluginAbi),
                MainLibraryName       = fileName,
                ManifestDate          = DateTimeOffset.UtcNow
            };
            return true;
        }
        finally
        {
            if (pluginAbi != null) Marshal.Release((nint)pluginAbi);
            if (handle != nint.Zero) NativeLibrary.Free(handle);
        }
    }

    private static void PrintHelp()
    {
        string? execPath = Path.GetFileName(Environment.ProcessPath);
        Console.WriteLine($"Usage: {execPath} [plugin_dll_directory_path]");
    }

    private static unsafe string? TryGetAlternateIconData(void* pluginAbi)
    {
        try
        {
            ((delegate* unmanaged[Stdcall]<void*, out char*, int>)(*(*(void***)pluginAbi + 9)))
                (pluginAbi, out char* iconUrlOrDataP);

            string? iconUrlOrData = GetStringAndReleaseW(iconUrlOrDataP);
            if (string.IsNullOrEmpty(iconUrlOrData))
                return null;

            if (Base64.IsValid(iconUrlOrData))
                return iconUrlOrData;

            return !Uri.TryCreate(iconUrlOrData, UriKind.Absolute, out Uri? iconUrl) ? null : iconUrl.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error while retrieving plugin alternative icon data: {ex}");
            return null;
        }
    }

    private static unsafe string? GetStringAndReleaseW(void* ptr)
    {
        if (ptr == null)
            return null;

        try
        {
            return new string((char*)ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)ptr);
        }
    }
}