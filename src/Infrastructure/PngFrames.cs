using System.Buffers.Binary;
using System.IO.Compression;
namespace MovieRecap.Infrastructure;

// Validates the fixed RGB24/noninterlaced PNG profile, including chunk CRCs and bounded full pixel decoding.
public static class PngFrames
{
    public const int Width = 320, Height = 180;
    public static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public static byte[] DecodeRgb(ReadOnlySpan<byte> png)
    {
        if (png.Length is < 45 or > 4_194_304 || !png[..8].SequenceEqual(Signature)) throw new InvalidDataException("Invalid PNG profile.");
        var position = 8; var header = false; var end = false; using var compressed = new MemoryStream();
        while (position < png.Length)
        {
            if (png.Length - position < 12) throw new InvalidDataException("Truncated PNG.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position, 4)); if (length > png.Length - position - 12) throw new InvalidDataException("Invalid PNG chunk.");
            var count = (int)length; var type = png.Slice(position + 4, 4); var data = png.Slice(position + 8, count);
            if (Crc(png.Slice(position + 4, count + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position + count + 8, 4))) throw new InvalidDataException("PNG checksum mismatch.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (header || position != 8 || count != 13 || BinaryPrimitives.ReadUInt32BigEndian(data) != Width || BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != Height ||
                    data[8] != 8 || data[9] != 2 || data[10] != 0 || data[11] != 0 || data[12] != 0) throw new InvalidDataException("Invalid PNG dimensions/profile."); header = true;
            }
            else if (type.SequenceEqual("IDAT"u8)) { if (!header || end) throw new InvalidDataException("Invalid PNG order."); compressed.Write(data); }
            else if (type.SequenceEqual("IEND"u8)) { if (count != 0 || !header || compressed.Length == 0 || position + 12 != png.Length) throw new InvalidDataException("Invalid PNG end."); end = true; }
            else if ((type[0] & 32) == 0) throw new InvalidDataException("Unknown critical PNG chunk.");
            position += count + 12;
        }
        if (!end) throw new InvalidDataException("Incomplete PNG."); compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var filtered = new byte[(Width * 3 + 1) * Height]; zlib.ReadExactly(filtered); if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG pixel limit.");
        var pixels = new byte[Width * Height * 3]; var stride = Width * 3;
        for (var row = 0; row < Height; row++)
        {
            var filter = filtered[row * (stride + 1)]; if (filter > 4) throw new InvalidDataException("Invalid PNG filter.");
            for (var x = 0; x < stride; x++)
            {
                var index = row * stride + x; var left = x >= 3 ? pixels[index - 3] : 0; var above = row > 0 ? pixels[index - stride] : 0; var corner = row > 0 && x >= 3 ? pixels[index - stride - 3] : 0;
                var prediction = filter switch { 0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2, _ => Paeth(left, above, corner) };
                pixels[index] = unchecked((byte)(filtered[row * (stride + 1) + 1 + x] + prediction));
            }
        }
        return pixels;
    }
    static int Paeth(int left, int above, int corner) { var p = left + above - corner; var a = Math.Abs(p - left); var b = Math.Abs(p - above); var c = Math.Abs(p - corner); return a <= b && a <= c ? left : b <= c ? above : corner; }
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(x => { var c = (uint)x; for (var i = 0; i < 8; i++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    static uint Crc(ReadOnlySpan<byte> data) { var crc = uint.MaxValue; foreach (var b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8); return ~crc; }
}
