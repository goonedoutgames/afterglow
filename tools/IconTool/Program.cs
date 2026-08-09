using System.Buffers.Binary;
using ImageMagick;

// Usage: IconTool <source.ico|png|webp> <dest.ico> [dest.png] [pngSize]
// Builds a multi-resolution .ico (PNG-compressed frames). Optionally writes a square PNG for in-app UI.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: IconTool <source> <dest.ico> [dest.png] [pngSize=512]");
    return 1;
}

var source = Path.GetFullPath(args[0]);
var dest = Path.GetFullPath(args[1]);
string? destPng = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
var pngSize = 512;
if (args.Length >= 4 && (!int.TryParse(args[3], out pngSize) || pngSize < 16 || pngSize > 2048))
{
    Console.Error.WriteLine("pngSize must be 16-2048");
    return 1;
}

if (!File.Exists(source))
{
    Console.Error.WriteLine("Source not found: " + source);
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
foreach (var junk in Directory.GetFiles(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest) + "-*.ico"))
    try { File.Delete(junk); } catch { /* ignore */ }

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var frames = new List<(int Size, byte[] Png)>(sizes.Length);

using (var master = LoadMaster(source))
{
    foreach (var size in sizes)
    {
        using var frame = master.Clone();
        frame.FilterType = FilterType.Lanczos;
        frame.Resize((uint)size, (uint)size);
        frame.Format = MagickFormat.Png32;
        frames.Add((size, frame.ToByteArray()));
    }

    if (destPng is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPng)!);
        using var ui = master.Clone();
        ui.FilterType = FilterType.Lanczos;
        ui.Resize((uint)pngSize, (uint)pngSize);
        ui.Format = MagickFormat.Png32;
        ui.Write(destPng);
        Console.WriteLine($"Wrote {destPng} ({new FileInfo(destPng).Length:N0} bytes, {pngSize}px)");
    }
}

WritePngIcon(dest, frames);
Console.WriteLine($"Wrote {dest} ({new FileInfo(dest).Length:N0} bytes, {frames.Count} sizes)");
return 0;

static MagickImage LoadMaster(string path)
{
    try
    {
        using var loaded = new MagickImageCollection(path);
        var best = loaded.OrderByDescending(i => (long)i.Width * i.Height).First();
        return new MagickImage(best);
    }
    catch
    {
        return new MagickImage(path);
    }
}

static void WritePngIcon(string path, IReadOnlyList<(int Size, byte[] Png)> frames)
{
    // ICONDIR + ICONDIRENTRY* + PNG blobs (Windows Vista+ style)
    var headerSize = 6;
    var entrySize = 16;
    var dataOffset = headerSize + entrySize * frames.Count;

    using var fs = File.Create(path);
    Span<byte> hdr = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0);       // reserved
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..], 1);  // type = icon
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], (ushort)frames.Count);
    fs.Write(hdr);

    var offset = dataOffset;
    Span<byte> entry = stackalloc byte[16];
    foreach (var (size, png) in frames)
    {
        entry.Clear();
        entry[0] = (byte)(size >= 256 ? 0 : size);
        entry[1] = (byte)(size >= 256 ? 0 : size);
        entry[2] = 0; // palette
        entry[3] = 0; // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bit count
        BinaryPrimitives.WriteInt32LittleEndian(entry[8..], png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
        fs.Write(entry);
        offset += png.Length;
    }

    foreach (var (_, png) in frames)
        fs.Write(png);
}
