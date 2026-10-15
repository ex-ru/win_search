using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class NativeDependencyAudit
{
    private const string ManifestName = "native-provenance.json";
    private static readonly string[] RequiredVcRuntime =
    [
        "MSVCP140.dll",
        "VCRUNTIME140.dll",
        "VCRUNTIME140_1.dll"
    ];

    // These are the Windows system DLLs observed in the pinned native payload. API-set
    // contracts are implemented by Windows and are intentionally matched by prefix.
    private static readonly HashSet<string> WindowsSystemImports = new(StringComparer.OrdinalIgnoreCase)
    {
        "bcrypt.dll",
        "kernel32.dll",
        "msvcrt.dll",
        "shell32.dll",
        "advapi32.dll",
        "psapi.dll",
        "gdi32.dll",
        "user32.dll"
    };

    private sealed record NativeFile(string RelativePath, string FullPath, string Name, long Size, string Sha256);

    public static string Run(string packageDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native dependency audit requires Windows.");

        string packageRoot = Path.GetFullPath(packageDirectory);
        string manifestPath = Path.Combine(packageRoot, ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Native provenance manifest is missing: {manifestPath}", manifestPath);

        Dictionary<string, NativeFile> files = ReadManifest(packageRoot, manifestPath);
        foreach (string required in RequiredVcRuntime)
        {
            if (!files.ContainsKey(required))
                throw new InvalidDataException($"The native manifest must include app-local {required}.");
        }

        var importsByFile = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (NativeFile file in files.Values.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            VerifyManifestFile(file);
            importsByFile[file.Name] = ReadImports(file.FullPath);
        }

        foreach ((string source, IReadOnlyList<string> imports) in importsByFile)
        {
            foreach (string import in imports)
            {
                if (IsWindowsSystemImport(import) ||
                    string.Equals(import, "vulkan-1.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!files.ContainsKey(import))
                    throw new InvalidDataException($"{source} imports {import}, which is not present in native-provenance.json.");
            }
        }

        string llamaPath = RequireManifestFile(files, "llama.dll").FullPath;
        string mtmdPath = RequireManifestFile(files, "mtmd.dll").FullPath;
        IntPtr llamaHandle = IntPtr.Zero;
        IntPtr mtmdHandle = IntPtr.Zero;
        try
        {
            // Use absolute paths so this tests the files shipped with the package.
            llamaHandle = NativeLibrary.Load(llamaPath);
            mtmdHandle = NativeLibrary.Load(mtmdPath);
            VerifyLoadedVcRuntime(packageRoot, files);
        }
        finally
        {
            if (mtmdHandle != IntPtr.Zero) NativeLibrary.Free(mtmdHandle);
            if (llamaHandle != IntPtr.Zero) NativeLibrary.Free(llamaHandle);
        }

        int scanned = importsByFile.Count;
        int importCount = importsByFile.Values.Sum(list => list.Count);
        return $"Audited {scanned} native PE files and {importCount} imports; llama.dll and mtmd.dll loaded, and the required VC runtime modules matched the package bytes.";
    }

    private static Dictionary<string, NativeFile> ReadManifest(string packageRoot, string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (!document.RootElement.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("native-provenance.json must contain a files array.");

        var files = new Dictionary<string, NativeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in filesElement.EnumerateArray())
        {
            string relativePath = RequiredString(entry, "name");
            string sha256 = RequiredString(entry, "sha256");
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Invalid SHA-256 for native file {relativePath}.");
            if (!entry.TryGetProperty("size", out JsonElement sizeElement) || !sizeElement.TryGetInt64(out long size) || size < 0)
                throw new InvalidDataException($"Invalid size for native file {relativePath}.");

            string safeRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(safeRelativePath) || safeRelativePath.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
                throw new InvalidDataException($"Unsafe native manifest path: {relativePath}.");

            string fullPath = Path.GetFullPath(Path.Combine(packageRoot, safeRelativePath));
            if (!IsWithin(fullPath, packageRoot))
                throw new InvalidDataException($"Native manifest path escapes the package: {relativePath}.");

            string name = Path.GetFileName(safeRelativePath);
            var file = new NativeFile(relativePath, fullPath, name, size, sha256.ToLowerInvariant());
            if (!files.TryAdd(name, file))
                throw new InvalidDataException($"Native package contains duplicate DLL names: {name}.");
        }

        if (files.Count == 0)
            throw new InvalidDataException("native-provenance.json contains no native files.");
        return files;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Native manifest entry is missing {property}.");
        return value.GetString()!;
    }

    private static NativeFile RequireManifestFile(IReadOnlyDictionary<string, NativeFile> files, string name) =>
        files.TryGetValue(name, out NativeFile? file)
            ? file
            : throw new FileNotFoundException($"Native manifest is missing {name}.");

    private static void VerifyManifestFile(NativeFile file)
    {
        if (!File.Exists(file.FullPath))
            throw new FileNotFoundException($"Native package file is missing: {file.RelativePath}", file.FullPath);

        var info = new FileInfo(file.FullPath);
        if (info.Length != file.Size)
            throw new InvalidDataException($"Native package size differs from manifest for {file.RelativePath}: {info.Length} != {file.Size}.");

        string actualHash;
        using (FileStream stream = File.OpenRead(file.FullPath))
            actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Native package SHA-256 differs from manifest for {file.RelativePath}.");
    }

    private static IReadOnlyList<string> ReadImports(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        PEHeader header = pe.PEHeaders.PEHeader ?? throw new BadImageFormatException("PE optional header is missing.", path);
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadNormalImports(pe, header.ImportTableDirectory, imports, path);
        ReadDelayImports(pe, header.DelayImportTableDirectory, header.ImageBase, imports, path);
        return imports.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ReadNormalImports(PEReader pe, DirectoryEntry directory, HashSet<string> imports, string path)
    {
        if (directory.RelativeVirtualAddress == 0 && directory.Size == 0) return;
        if (directory.RelativeVirtualAddress <= 0 || directory.Size <= 0)
            throw new BadImageFormatException("Invalid import table directory.", path);

        PEMemoryBlock block = pe.GetSectionData(directory.RelativeVirtualAddress);
        int available = Math.Min(block.Length, directory.Size);
        int descriptors = available / 20;
        var reader = block.GetReader();
        bool terminated = false;
        for (int i = 0; i < descriptors; i++)
        {
            uint originalThunk = reader.ReadUInt32();
            uint timestamp = reader.ReadUInt32();
            uint forwarderChain = reader.ReadUInt32();
            uint nameRva = reader.ReadUInt32();
            uint firstThunk = reader.ReadUInt32();
            if ((originalThunk | timestamp | forwarderChain | nameRva | firstThunk) == 0)
            {
                terminated = true;
                break;
            }
            if (nameRva == 0 || nameRva > int.MaxValue)
                throw new BadImageFormatException("Invalid import name RVA.", path);
            imports.Add(ReadAsciiString(pe, (int)nameRva, path));
        }
        if (!terminated)
            throw new BadImageFormatException("Import descriptor table has no terminator within its declared size.", path);
    }

    private static void ReadDelayImports(PEReader pe, DirectoryEntry directory, ulong imageBase, HashSet<string> imports, string path)
    {
        if (directory.RelativeVirtualAddress == 0 && directory.Size == 0) return;
        if (directory.RelativeVirtualAddress <= 0 || directory.Size <= 0)
            throw new BadImageFormatException("Invalid delay-import table directory.", path);

        PEMemoryBlock block = pe.GetSectionData(directory.RelativeVirtualAddress);
        int available = Math.Min(block.Length, directory.Size);
        int descriptors = available / 32;
        var reader = block.GetReader();
        bool terminated = false;
        for (int i = 0; i < descriptors; i++)
        {
            uint attributes = reader.ReadUInt32();
            uint nameValue = reader.ReadUInt32();
            uint moduleHandle = reader.ReadUInt32();
            uint iat = reader.ReadUInt32();
            uint importNameTable = reader.ReadUInt32();
            uint boundIat = reader.ReadUInt32();
            uint unloadIat = reader.ReadUInt32();
            uint timestamp = reader.ReadUInt32();
            if ((attributes | nameValue | moduleHandle | iat | importNameTable | boundIat | unloadIat | timestamp) == 0)
            {
                terminated = true;
                break;
            }
            if (nameValue == 0)
                throw new BadImageFormatException("Delay-import descriptor has no DLL name.", path);

            ulong nameRva = (attributes & 1) != 0 ? nameValue : nameValue >= imageBase ? nameValue - imageBase : ulong.MaxValue;
            if (nameRva == 0 || nameRva > int.MaxValue)
                throw new BadImageFormatException("Invalid delay-import name RVA.", path);
            imports.Add(ReadAsciiString(pe, (int)nameRva, path));
        }
        if (!terminated)
            throw new BadImageFormatException("Delay-import descriptor table has no terminator within its declared size.", path);
    }

    private static string ReadAsciiString(PEReader pe, int rva, string path)
    {
        BlobReader reader = pe.GetSectionData(rva).GetReader();
        var bytes = new List<byte>(32);
        bool terminated = false;
        while (reader.RemainingBytes > 0 && bytes.Count < 512)
        {
            byte value = reader.ReadByte();
            if (value == 0)
            {
                terminated = true;
                break;
            }
            if (value is < 0x21 or > 0x7e)
                throw new BadImageFormatException("Import DLL name is not printable ASCII.", path);
            bytes.Add(value);
        }
        if (!terminated || bytes.Count == 0)
            throw new BadImageFormatException("Import DLL name is empty or unterminated.", path);
        string name = Encoding.ASCII.GetString(bytes.ToArray());
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.OrdinalIgnoreCase))
            throw new BadImageFormatException($"Import DLL name contains a path: {name}.", path);
        return name;
    }

    private static bool IsWindowsSystemImport(string import) =>
        import.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) ||
        import.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase) ||
        WindowsSystemImports.Contains(import);

    private static void VerifyLoadedVcRuntime(string packageRoot, IReadOnlyDictionary<string, NativeFile> files)
    {
        using Process process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>()
            .Where(module => RequiredVcRuntime.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (string runtimeName in RequiredVcRuntime)
        {
            NativeFile packageFile = RequireManifestFile(files, runtimeName);
            List<ProcessModule> loaded = modules
                .Where(module => string.Equals(module.ModuleName, runtimeName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (loaded.Count == 0)
                throw new InvalidOperationException($"{runtimeName} was not loaded after loading llama.dll and mtmd.dll.");

            foreach (ProcessModule module in loaded)
            {
                string modulePath = Path.GetFullPath(module.FileName);
                bool fromPackage = IsWithin(modulePath, packageRoot);
                bool fromRunnerOverlay = IsWithin(modulePath, Path.GetFullPath(AppContext.BaseDirectory));
                if (!fromPackage && !fromRunnerOverlay)
                    throw new InvalidOperationException($"{runtimeName} loaded from outside the package and smoke runner: {modulePath}.");

                using FileStream stream = File.OpenRead(modulePath);
                string actualHash = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actualHash, packageFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Loaded {runtimeName} bytes do not match the packaged file: {modulePath}.");
            }
        }
    }

    private static bool IsWithin(string filePath, string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string relative = Path.GetRelativePath(fullDirectory, Path.GetFullPath(filePath));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
