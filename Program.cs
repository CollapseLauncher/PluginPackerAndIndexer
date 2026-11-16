using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;

#if !BFLAT
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable IdentifierTypo
#endif
#pragma warning disable SYSLIB1054
// ReSharper disable AccessToDisposedClosure
// ReSharper disable StringLiteralTypo
// ReSharper disable InvocationIsCultureSensitive
// ReSharper disable InconsistentNaming

namespace Indexer;

public class SelfUpdateAssetInfo
{
    public required string FilePath { get; set; }

    public long Size { get; set; }

    public required byte[] FileHash { get; set; }
}

public class Program
{
    public static readonly Guid IID_IPlugin = new([108, 108, 111, 67, 112, 97, 101, 115, 114, 127, 0, 0, 0, 0, 0, 16]);

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

            string? filePath = FindPluginLibraryAndGetAssets(path, out List<SelfUpdateAssetInfo> assetInfo, out PluginManifest? reference);
            if (filePath == null || reference == null || string.IsNullOrEmpty(reference.MainLibraryName))
            {
                Console.Error.WriteLine("No valid plugin library was found.");
                return 1;
            }

            string foundFileDir      = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
            string referenceFilePath = Path.Combine(foundFileDir, "manifest.json");
            int    retCode           = WriteToJson(reference, referenceFilePath, filePath, assetInfo);
            return retCode != 0 ? retCode : PackFiles(foundFileDir, reference, assetInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An unknown error has occurred! {ex}");
            return int.MinValue;
        }
    }

    private static int WriteToJson(PluginManifest reference, string referenceFilePath, string dllPath, List<SelfUpdateAssetInfo> assetInfo)
    {
        DateTimeOffset creationDate = reference.PluginCreationDate.ToOffset(reference.PluginCreationDate.Offset);

        Console.WriteLine($"Plugin has been found! {dllPath}");
        Console.WriteLine($"  Main Library Path Name : {reference.MainLibraryName}");
        Console.WriteLine($"  Main Plugin Name       : {reference.MainPluginName}");
        Console.WriteLine($"  Creation Date          : {creationDate}");
        Console.WriteLine($"  Version                : {reference.PluginVersion}");
        Console.Write("Writing metadata info...");

        using FileStream   referenceFileStream = File.Create(referenceFilePath);
        using StreamWriter writer              = new(referenceFileStream);

        writer.WriteLine("{");
        writer.WriteLine($"""
                            "{nameof(PluginManifest.MainLibraryName)}": "{reference.MainLibraryName}",
                            "{nameof(PluginManifest.MainPluginName)}": "{reference.MainPluginName}",
                            "{nameof(PluginManifest.MainPluginAuthor)}": "{reference.MainPluginAuthor}",
                            "{nameof(PluginManifest.MainPluginDescription)}": "{reference.MainPluginDescription}",
                            "{nameof(PluginManifest.PluginStandardVersion)}": "{reference.PluginStandardVersion}",
                            "{nameof(PluginManifest.PluginVersion)}": "{reference.PluginVersion}",
                            "{nameof(PluginManifest.PluginCreationDate)}": "{creationDate:O}",
                            "{nameof(PluginManifest.ManifestDate)}": "{reference.ManifestDate:O}",
                          """);
        if (!string.IsNullOrEmpty(reference.PluginAlternativeIcon))
            writer.WriteLine($"  \"{nameof(PluginManifest.PluginAlternativeIcon)}\": \"{reference.PluginAlternativeIcon}\",");

        writer.WriteLine("  \"Assets\": [");
        for (int i = 0; i < assetInfo.Count; i++)
        {
            SelfUpdateAssetInfo asset  = assetInfo[i];
            bool                isLast = i + 1 == assetInfo.Count;

            writer.WriteLine("    {");
            writer.WriteLine($"""
                                    "{nameof(asset.FilePath)}": "{asset.FilePath}",
                                    "{nameof(asset.Size)}": {asset.Size},
                                    "{nameof(asset.FileHash)}": "{Convert.ToBase64String(asset.FileHash)}"
                              """);
            writer.Write    ("    }");
            if (!isLast)
            {
                writer.Write(',');
            }
            writer.WriteLine();
        }
        writer.WriteLine("  ]");
        writer.WriteLine("}");

        writer.Flush();

        Console.WriteLine(" Done!");
        return 0;
    }

    private static int PackFiles(string outputPath, PluginManifest referenceInfo, List<SelfUpdateAssetInfo> fileList)
    {
        string packageName     = $"{Path.GetFileNameWithoutExtension(referenceInfo.MainLibraryName)}_{referenceInfo.PluginVersion}_API-{referenceInfo.PluginStandardVersion}_{referenceInfo.ManifestDate:yyyyMMdd}";
        string packageFilePath = Path.Combine(outputPath, packageName + ExPackageExtension);

#if !BFLAT
        int threads = Environment.ProcessorCount;
        Console.WriteLine($"Writing output package in parallel using {threads} threads at: {packageFilePath}...");
#endif

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

#if !BFLAT
            Lock thisLock = new();
            Parallel.ForEach(fileList, CompressAndCreate);
#else
            foreach (SelfUpdateAssetInfo asset in fileList)
                CompressAndCreate(asset);
#endif

            return 0;

            void CompressAndCreate(SelfUpdateAssetInfo asset)
            {
#if !BFLAT
                Interlocked.Increment(ref count);
#else
                ++count;
#endif
                int currentCount = count;

                string filePath = Path.Combine(outputPath, asset.FilePath);
                DateTime lastWriteTime = File.GetLastWriteTimeUtc(filePath);

                if (lastWriteTime.Year is < 1980 or > 2107)
                    lastWriteTime = new DateTime(1980, 1, 1, 0, 0, 0);

                Console.WriteLine($"  [{currentCount}/{length}] Compressing asset to buffer: {asset.FilePath}");
#if !BFLAT && NET11_0_OR_GREATER
                const string CompressionExtension = ".zstd";
                using FileStream tempStream = File.Open(Path.Combine(Path.GetTempPath(), Path.GetTempFileName()),
                                                        new FileStreamOptions
                                                        {
                                                            Mode    = FileMode.Create,
                                                            Access  = FileAccess.ReadWrite,
                                                            Share   = FileShare.ReadWrite,
                                                            Options = FileOptions.DeleteOnClose
                                                        });
                using ZstandardStream compressStream = new(tempStream, CompressionLevel.SmallestSize);
                using FileStream fileStream = File.OpenRead(filePath);

                fileStream.CopyTo(compressStream);
                compressStream.Flush();
#else
                using FileStream tempStream = File.OpenRead(filePath);
#endif

                tempStream.Position = 0;

#if !BFLAT
                using (thisLock.EnterScope())
#endif
                {
                    Console.WriteLine($"  [{currentCount}/{length}] Compress done. Now locking and writing buffer to package for: {asset.FilePath}");

#if !BFLAT && NET11_0_OR_GREATER
                    string entryBrExt = asset.FilePath + CompressionExtension;
                    ZipArchiveEntry entry = packageWriter.CreateEntry(entryBrExt, CompressionLevel.NoCompression);
#else
                    string entryBrExt = asset.FilePath;
                    ZipArchiveEntry entry = packageWriter.CreateEntry(entryBrExt, CompressionLevel.SmallestSize);
#endif
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

    private static string? FindPluginLibraryAndGetAssets(string dirPath, out List<SelfUpdateAssetInfo> fileList, out PluginManifest? referenceInfo)
    {
        List<SelfUpdateAssetInfo> fileListRef         = [];
        string?                   mainLibraryFilePath = null;
        PluginManifest?           referenceInfoResult = null;

        fileList      = fileListRef;
        referenceInfo = null;

#if !BFLAT
        Parallel.ForEach(Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories), ImplSearch);
#else
        foreach (string filePath in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            ImplSearch(filePath);
#endif
        referenceInfo = referenceInfoResult;
        if (mainLibraryFilePath == null)
            return null;

        string actualDir = Path.GetDirectoryName(mainLibraryFilePath) ?? "";
#if !BFLAT
        Parallel.ForEach(Directory.EnumerateFiles(actualDir, "*", SearchOption.AllDirectories), ImplIndex);
#else
        foreach (string filePath in Directory.EnumerateFiles(actualDir, "*", SearchOption.AllDirectories))
            ImplIndex(filePath);
#endif

        return mainLibraryFilePath;

        void ImplIndex(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            if (filePath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                return;

            if (fileName.EndsWith(ExPackageExtension, StringComparison.OrdinalIgnoreCase))
                return;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 10);
            try
            {
                using MD5        hash       = MD5.Create();
                using FileStream fileStream = File.OpenRead(filePath);

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
                    Size     = PInvoke.GetFileSize(filePath)
                };

#if !BFLAT
                lock (fileListRef)
#endif
                {
                    fileListRef.Add(assetInfo);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        void ImplSearch(string filePath)
        {
            if (!string.IsNullOrEmpty(mainLibraryFilePath))
                return;

            string fileName = Path.GetFileName(filePath);
            Console.WriteLine($"Finding entry point library: {filePath}");
            if (mainLibraryFilePath == null &&
                IsPluginLibrary(filePath, fileName, out PluginManifest? referenceInfoInner))
            {
#if !BFLAT
                Interlocked.Exchange(ref mainLibraryFilePath, filePath);
                Interlocked.Exchange(ref referenceInfoResult, referenceInfoInner);
#else
                mainLibraryFilePath = filePath;
                referenceInfoResult = referenceInfoInner;
#endif
            }
        }
    }

    private static ReadOnlySpan<byte> ProcNameTryGetApiExport          => "TryGetApiExport\0"u8;
    private const  string             ProcNameGetPlugin                = "GetPlugin";
    private const  string             ProcNameGetPluginVersion         = "GetPluginVersion";
    private const  string             ProcNameGetPluginStandardVersion = "GetPluginStandardVersion";

    private static unsafe bool IsPluginLibrary(string filePath, string fileName, [NotNullWhen(true)] out PluginManifest? referenceInfo)
    {
        nint handle = nint.Zero;
        referenceInfo = null;

        ReadOnlySpan<char> extension = Path.GetExtension(fileName);
        if (ExAllowedPluginExt
            .AsSpan()
            .IndexOf(extension, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        nint pluginIUnknownAbi = nint.Zero;
        nint pluginAbi = nint.Zero;
        try
        {
            handle = PInvoke.LoadLibrary(filePath.GetPointer());
            nint exportAddress = handle == nint.Zero ? 0 : PInvoke.GetProcAddress(handle, ProcNameTryGetApiExport.GetPointer());
            if (handle == nint.Zero || exportAddress == nint.Zero)
            {
                return false;
            }

            var tryGetApiExportCallback = (delegate* unmanaged[Cdecl]<ushort*, out nint, int>)exportAddress;
            if (tryGetApiExportCallback(ProcNameGetPlugin.GetPointer(), out nint getPluginDelegateP) != 0)
            {
                return false;
            }

            pluginIUnknownAbi = ((delegate* unmanaged[Cdecl]<nint>)getPluginDelegateP)();
            if (pluginIUnknownAbi == nint.Zero ||
                Marshal.QueryInterface(pluginIUnknownAbi, in IID_IPlugin, out pluginAbi) != 0)
                return false;

            if (tryGetApiExportCallback(ProcNameGetPluginVersion.GetPointer(), out nint getPluginVersionDelegateP) != 0)
                return false;

            GameVersion pluginVersion = *((delegate* unmanaged[Cdecl]<GameVersion*>)getPluginVersionDelegateP)();
            if (tryGetApiExportCallback(ProcNameGetPluginStandardVersion.GetPointer(), out nint getPluginStandardVersionDelegateP) != 0)
                return false;

            GameVersion pluginStandardVersion = *((delegate* unmanaged[Cdecl]<GameVersion*>)getPluginStandardVersionDelegateP)();

            ((delegate* unmanaged[Cdecl]<nint, out nint, int>)(*(*(void***)pluginAbi + 4)))(pluginAbi, out nint pluginNameP);                   // GetPluginName(out string?);
            ((delegate* unmanaged[Cdecl]<nint, out nint, int>)(*(*(void***)pluginAbi + 5)))(pluginAbi, out nint pluginDescriptionP);            // GetPluginAuthor(out string?);
            ((delegate* unmanaged[Cdecl]<nint, out nint, int>)(*(*(void***)pluginAbi + 6)))(pluginAbi, out nint pluginAuthorP);                 // GetPluginDescription(out string?);
            ((delegate* unmanaged[Cdecl]<nint, out DateTime*, int>)(*(*(void***)pluginAbi + 7)))(pluginAbi, out DateTime* pluginCreationDateP); // GetPluginCreationDate(out DateTime*);

            string?        pluginName         = pluginNameP.GetStringAndReleaseW();
            string?        pluginAuthor       = pluginAuthorP.GetStringAndReleaseW();
            string?        pluginDescription  = pluginDescriptionP.GetStringAndReleaseW();
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
            if (pluginAbi != nint.Zero) Marshal.Release(pluginAbi);
            if (pluginIUnknownAbi != nint.Zero) Marshal.Release(pluginIUnknownAbi);
            if (handle != nint.Zero) _ = PInvoke.FreeLibrary(handle);
        }
    }

    private static void PrintHelp()
    {
        string? execPath = Path.GetFileName(Environment.ProcessPath);
        Console.WriteLine($"Usage: {execPath} [plugin_dll_directory_path]");
    }

    private static unsafe string? TryGetAlternateIconData(nint pluginAbi)
    {
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(*(void***)pluginAbi + 9)))(pluginAbi, out nint iconUrlOrDataP); // GetPluginAppIconUrl(out string?);
            string? iconUrlOrData = iconUrlOrDataP.GetStringAndReleaseW();
            return string.IsNullOrEmpty(iconUrlOrData) ? null : iconUrlOrData;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error while retrieving plugin alternative icon data: {ex}");
            return null;
        }
    }
}

file static class Extension
{
    internal static unsafe ushort* GetPointer(this string? str)
    {
        fixed (char* pinRef = &Utf16StringMarshaller.GetPinnableReference(str))
            return (ushort*)pinRef;
    }

    internal static unsafe T* GetPointer<T>(this ReadOnlySpan<T> span) where T : unmanaged
        => (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    internal static unsafe string? GetStringAndReleaseW(this nint ptr)
    {
        if (ptr == nint.Zero)
            return null;

        try
        {
            return new string((char*)ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}

file static class PInvoke
{
    private const string kernel32Name = "Kernel32.dll";

    [DllImport(kernel32Name, EntryPoint = "LoadLibraryW")]
    internal static unsafe extern nint LoadLibrary(ushort* lpLibFileName);

    [DllImport(kernel32Name, EntryPoint = "GetProcAddress")]
    internal static unsafe extern nint GetProcAddress(nint hModule, byte* lpProcName);

    [DllImport(kernel32Name, EntryPoint = "FreeLibrary")]
    internal static extern int FreeLibrary(nint hModule);

    [DllImport(kernel32Name, EntryPoint = "CreateFileW")]
    private static unsafe extern nint CreateFile(ushort* lpFileName,
                                                 uint    dwDesiredAccess,
                                                 uint    dwShareMode,
                                                 nint    lpSecurityAttributes,
                                                 uint    dwCreationDisposition,
                                                 uint    dwFlagsAndAttributes,
                                                 nint    hTemplateFile);

    [DllImport(kernel32Name, EntryPoint = "GetFileSizeEx")]
    private static extern int GetFileSizeEx(nint hFile, out long lpFileSize);

    [DllImport(kernel32Name, EntryPoint = "CloseHandle")]
    private static extern int CloseHandle(nint handle);

    internal static unsafe long GetFileSize(string filePath)
    {
        nint fileHandle = nint.Zero;
        try
        {
            fileHandle = CreateFile(filePath.GetPointer(),
                                    0x80000000u,
                                    0x00000001u | 0x00000002u,
                                    nint.Zero,
                                    0x00000003u,
                                    0x00000080u,
                                    nint.Zero);
            if (fileHandle == nint.Zero)
                return 0;

            if (GetFileSizeEx(fileHandle, out long size) != 1)
                return 0;

            return size;
        }
        finally
        {
            if (fileHandle != nint.Zero) _ = CloseHandle(fileHandle);
        }
    }
}