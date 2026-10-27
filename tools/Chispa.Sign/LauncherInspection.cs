using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

// Read the finished artifact without launching an administrator process. The URL comes
// from the actual bundled assembly, not the build arguments or a neighbouring DLL.
static class LauncherInspection
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("inspect-launcher <exe> <expected-server-url>");
        var path = Path.GetFullPath(args[1]);
        var level = ExecutionLevel(path);
        if (level != "requireAdministrator") throw new InvalidDataException("Launcher does not require administrator privileges.");
        var url = ServerUrl(path);
        if (!string.Equals(url, args[2], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bundled launcher server URL does not match the expected address.");
        Console.WriteLine(JsonSerializer.Serialize(new { serverUrl = url, executionLevel = level }));
        return 0;
    }

    private static string ExecutionLevel(string path)
    {
        // Data/image-resource flags map resources only; they never execute the file.
        var module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
        if (module == IntPtr.Zero) throw new InvalidDataException("Cannot read native launcher resources.");
        try
        {
            var resource = FindResource(module, (IntPtr)1, (IntPtr)24);
            var size = SizeofResource(module, resource);
            if (resource == IntPtr.Zero || size is 0 or > 1_048_576) throw new InvalidDataException("Launcher manifest is missing.");
            var data = LockResource(LoadResource(module, resource));
            if (data == IntPtr.Zero) throw new InvalidDataException("Cannot read launcher manifest.");
            var bytes = new byte[(int)size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var xml = XDocument.Load(reader);
            XNamespace ns = "urn:schemas-microsoft-com:asm.v3";
            var requested = xml.Descendants(ns + "requestedExecutionLevel").SingleOrDefault();
            if (requested?.Attribute("uiAccess")?.Value != "false") throw new InvalidDataException("Unexpected launcher uiAccess setting.");
            return requested.Attribute("level")?.Value ?? "";
        }
        finally { FreeLibrary(module); }
    }

    private static string ServerUrl(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // .NET single-file bundle marker. Its preceding Int64 points at the manifest.
        ReadOnlySpan<byte> marker = [0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,
            0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae];
        var position = bytes.AsSpan().IndexOf(marker);
        if (position < 8) throw new InvalidDataException("Launcher is not a single-file bundle.");
        var header = BitConverter.ToInt64(bytes, position - 8);
        if (header <= 0 || header >= bytes.Length - 12) throw new InvalidDataException("Invalid bundle header.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        stream.Position = header;
        var major = reader.ReadUInt32();
        var minor = reader.ReadUInt32();
        var count = reader.ReadInt32();
        if (major != 6 || minor != 0 || count is < 1 or > 4096) throw new InvalidDataException("Unsupported bundle format.");
        _ = reader.ReadString(); // bundle ID
        stream.Position += 40; // deps/runtimeconfig locations and header flags
        byte[]? assembly = null;
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressed = reader.ReadInt64();
            _ = reader.ReadByte();
            var name = reader.ReadString();
            if (name != "Chispa.Launcher.dll") continue;
            var length = compressed > 0 ? compressed : size;
            if (assembly is not null || size is <= 0 or > 20_000_000 || length <= 0 ||
                offset < 0 || offset > bytes.LongLength - length || length > int.MaxValue)
                throw new InvalidDataException("Invalid bundled launcher assembly.");
            using var content = new MemoryStream(bytes, (int)offset, (int)length, writable: false);
            using var decoded = compressed > 0 ? new DeflateStream(content, CompressionMode.Decompress) : null;
            assembly = new byte[(int)size];
            (decoded as Stream ?? content).ReadExactly(assembly);
        }
        if (assembly is null) throw new InvalidDataException("Launcher assembly is missing from bundle.");
        using var pe = new PEReader(new MemoryStream(assembly, writable: false));
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection" ||
                metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) continue;
            var key = value.ReadSerializedString();
            var text = value.ReadSerializedString();
            if (key == "ChispaServerUrl") return text ?? "";
        }
        throw new InvalidDataException("Bundled server URL metadata is missing.");
    }
}
