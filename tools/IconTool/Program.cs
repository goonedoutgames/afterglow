using ImageMagick;

// Usage: IconTool <source.ico|png|webp> <dest.ico> [dest.png] [pngSize]
// Builds a multi-resolution .ico via Magick (BMP for small sizes, PNG for 256) so
// Windows Shell + Inno Setup SetupIconFile both pick up the icon reliably.
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

using (var master = LoadMaster(source))
using (var collection = new MagickImageCollection())
{
    foreach (var size in sizes)
    {
        var frame = master.Clone();
        frame.FilterType = FilterType.Lanczos;
        frame.Resize((uint)size, (uint)size);
        // Magick ICO writer: BMP/DIB for <=128 is more compatible with Inno SetupIconFile
        // and older Shell extractors; keep 256 as PNG.
        if (size >= 256)
            frame.Format = MagickFormat.Png32;
        else
            frame.Format = MagickFormat.Bgra;
        collection.Add(frame);
    }

    collection.Write(dest, MagickFormat.Ico);
    Console.WriteLine($"Wrote {dest} ({new FileInfo(dest).Length:N0} bytes, {collection.Count} sizes)");

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
