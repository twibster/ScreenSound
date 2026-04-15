// Rasterises an SVG at a set of Windows-standard icon sizes and packs the
// results into a single multi-size .ico file. Run from the repo root:
//
//   dotnet run --project design/tools/IconBuilder -- \
//       design/app-icon.svg \
//       AudioMonitorRouter/app.ico
//
// The .ico format spec is well documented; we hand-write the header +
// directory + PNG payloads rather than pulling in another dependency.
// Ref: https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)

using System.Drawing;
using System.Drawing.Imaging;
using Svg;

// Target sizes chosen to cover the surfaces Windows actually scales to:
//   16 = system tray / small list items
//   20 = tray at 125% DPI
//   24 = tray at 150% DPI / small toolbar
//   32 = standard icon list
//   48 = large icons view
//   64 = medium tile / 200% tray
//   128 = jumbo icons
//   256 = file-explorer "extra large" + Start menu scaling target
int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: IconBuilder <input.svg> <output.ico>");
    return 1;
}

string svgPath = args[0];
string icoPath = args[1];

if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"SVG not found: {svgPath}");
    return 1;
}

var svg = SvgDocument.Open(svgPath);
var pngBlobs = new List<byte[]>();

foreach (int size in sizes)
{
    // Rasterise at the exact target size. We re-render per size (rather
    // than scaling a single bitmap) so hinting + anti-aliasing adapt to
    // the actual pixel grid — critical for legibility at 16 and 20.
    using var bmp = svg.Draw(size, size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngBlobs.Add(ms.ToArray());
    Console.WriteLine($"  rendered {size,3}x{size,-3}  {ms.Length,6} B");
}

// --- Pack into .ico ------------------------------------------------------
//
// Layout:
//   ICONDIR       (6 bytes)
//   ICONDIRENTRY[N]  (16 bytes each)
//   PNG payloads  (contiguous, referenced by offsets in the entries)

using var outStream = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
using var w = new BinaryWriter(outStream);

// ICONDIR
w.Write((ushort)0);             // Reserved, must be 0
w.Write((ushort)1);             // Type: 1 = icon
w.Write((ushort)sizes.Length);  // Image count

// Data starts after the fixed-size header + all directory entries.
int dataOffset = 6 + 16 * sizes.Length;

for (int i = 0; i < sizes.Length; i++)
{
    int sz = sizes[i];
    byte[] png = pngBlobs[i];

    // Per-image directory entry (ICONDIRENTRY, 16 bytes).
    w.Write((byte)(sz == 256 ? 0 : sz));   // Width  (0 means 256)
    w.Write((byte)(sz == 256 ? 0 : sz));   // Height (0 means 256)
    w.Write((byte)0);                       // Colour palette count (0 = no palette)
    w.Write((byte)0);                       // Reserved
    w.Write((ushort)1);                     // Colour planes
    w.Write((ushort)32);                    // Bits per pixel
    w.Write((uint)png.Length);              // Byte size of the PNG payload
    w.Write((uint)dataOffset);              // Offset of the payload from file start

    dataOffset += png.Length;
}

// PNG payloads, in the same order as the directory entries.
foreach (byte[] png in pngBlobs)
    w.Write(png);

Console.WriteLine($"Wrote {icoPath} ({outStream.Length} bytes)");
return 0;
