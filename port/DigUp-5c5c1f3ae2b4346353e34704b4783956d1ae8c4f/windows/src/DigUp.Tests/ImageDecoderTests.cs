using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using DigUp.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Xunit;

namespace DigUp.Tests;

public class ImageDecoderTests
{
    private static string GetProbeImagePath()
    {
        // Try locating testbed/Screenshot-probe.png relative to test execution dir
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "..", "testbed", "Screenshot-probe.png"));
        if (File.Exists(candidate))
            return candidate;

        // Fallback to absolute workspace location if available
        string workspaceCandidate = @"D:\_projects\win_search\testbed\Screenshot-probe.png";
        if (File.Exists(workspaceCandidate))
            return workspaceCandidate;

        throw new FileNotFoundException("Cannot find Screenshot-probe.png for testing");
    }

    [Fact]
    public void WicImageDecoder_DecodesAndDownscalesRealImage()
    {
        string imagePath = GetProbeImagePath();
        var decoder = new WicImageDecoder();
        string tempDir = Path.Combine(Path.GetTempPath(), "digup_wic_test_" + Guid.NewGuid().ToString("N"));

        try
        {
            var prepared = decoder.Decode(imagePath, maxPixel: 128, minSide: 10, outputFolder: tempDir, png: true);
            Assert.NotNull(prepared);
            Assert.True(File.Exists(prepared.Path), "Decoded thumbnail file must exist on disk");
            Assert.True(prepared.Width > 0 && prepared.Height > 0);
            Assert.True(new FileInfo(prepared.Path).Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WicImageDecoder_EncodesAsJpeg()
    {
        string imagePath = GetProbeImagePath();
        var decoder = new WicImageDecoder();
        string tempDir = Path.Combine(Path.GetTempPath(), "digup_wic_test_" + Guid.NewGuid().ToString("N"));

        try
        {
            var prepared = decoder.Decode(imagePath, maxPixel: 256, minSide: 10, outputFolder: tempDir, png: false);
            Assert.NotNull(prepared);
            Assert.True(File.Exists(prepared.Path));
            Assert.EndsWith(".jpg", prepared.Path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WicImageDecoder_ThrowsTooSmall_WhenImageUnderMinSide()
    {
        string imagePath = GetProbeImagePath();
        var decoder = new WicImageDecoder();
        string tempDir = Path.Combine(Path.GetTempPath(), "digup_wic_test_" + Guid.NewGuid().ToString("N"));

        try
        {
            var ex = Assert.Throws<ExtractError>(() =>
                decoder.Decode(imagePath, maxPixel: 1024, minSide: 99999, outputFolder: tempDir, png: true));

            Assert.Equal(ExtractError.FailureKind.TooSmall, ex.Failure);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WicImageDecoder_ThrowsUnreadable_WhenFileDoesNotExist()
    {
        var decoder = new WicImageDecoder();
        string tempDir = Path.Combine(Path.GetTempPath(), "digup_wic_test_" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<ExtractError>(() =>
            decoder.Decode("non_existent_file_path.xyz", maxPixel: 1024, minSide: 10, outputFolder: tempDir, png: true));

        Assert.Equal(ExtractError.FailureKind.Unreadable, ex.Failure);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void WicImageDecoder_RespectsExifOrientationWhenResizing(int orientation)
    {
        string tempDir = CreateTemporaryDirectory();
        try
        {
            string source = CreateOrientedJpeg(tempDir, orientation);
            var prepared = new WicImageDecoder().Decode(
                source, maxPixel: 256, minSide: 10, outputFolder: tempDir, png: true);

            // PreparedImage dimensions describe the encoded source, before EXIF orientation.
            Assert.Equal(1200, prepared.Width);
            Assert.Equal(600, prepared.Height);
            AssertPng(prepared.Path);

            using var output = new Bitmap(prepared.Path);
            Assert.Equal(128, output.Width);
            Assert.Equal(256, output.Height);
            AssertOrientedCorners(output, orientation);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticExifMetadata_IsRecognizedByWic()
    {
        string tempDir = CreateTemporaryDirectory();
        try
        {
            string source = CreateOrientedJpeg(tempDir, orientation: 6);
            var file = await StorageFile.GetFileFromPathAsync(source);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var properties = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });

            Assert.True(properties.TryGetValue("System.Photo.Orientation", out var orientationProperty));
            Assert.Equal(6, Convert.ToInt32(orientationProperty.Value));
            Assert.Equal(1200u, decoder.PixelWidth);
            Assert.Equal(600u, decoder.PixelHeight);
            Assert.Equal(600u, decoder.OrientedPixelWidth);
            Assert.Equal(1200u, decoder.OrientedPixelHeight);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void WicImageDecoder_RespectsExifOrientationWithoutResizing(int orientation)
    {
        string tempDir = CreateTemporaryDirectory();
        try
        {
            string source = CreateOrientedJpeg(tempDir, orientation);
            var prepared = new WicImageDecoder().Decode(
                source, maxPixel: 2048, minSide: 10, outputFolder: tempDir, png: false);

            // The input is a small JPEG, the path that used to bypass WIC with File.Copy.
            Assert.Equal(1200, prepared.Width);
            Assert.Equal(600, prepared.Height);
            Assert.EndsWith(".jpg", prepared.Path, StringComparison.OrdinalIgnoreCase);

            using var output = new Bitmap(prepared.Path);
            Assert.Equal(600, output.Width);
            Assert.Equal(1200, output.Height);
            AssertOrientedCorners(output, orientation);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WicImageDecoder_ConvertsSmallJpegToPngWhenPngIsRequested()
    {
        string tempDir = CreateTemporaryDirectory();
        try
        {
            string source = CreateOrientedJpeg(tempDir, orientation: 1);
            var prepared = new WicImageDecoder().Decode(
                source, maxPixel: 2048, minSide: 10, outputFolder: tempDir, png: true);

            Assert.EndsWith(".png", prepared.Path, StringComparison.OrdinalIgnoreCase);
            AssertPng(prepared.Path);
            using var output = new Bitmap(prepared.Path);
            Assert.Equal(1200, output.Width);
            Assert.Equal(600, output.Height);
            AssertOrientedCorners(output, orientation: 1);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "digup_wic_exif_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateOrientedJpeg(string folder, int orientation)
    {
        string path = Path.Combine(folder, "fixture.jpg");
        using (var bitmap = new Bitmap(1200, 600))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.FillRectangle(Brushes.Red, 0, 0, 600, 300);
            graphics.FillRectangle(Brushes.Green, 600, 0, 600, 300);
            graphics.FillRectangle(Brushes.Blue, 0, 300, 600, 300);
            graphics.FillRectangle(Brushes.Yellow, 600, 300, 600, 300);

            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                .Single(codec => codec.MimeType == "image/jpeg");
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 100L);
            bitmap.Save(path, jpeg, parameters);
        }

        File.WriteAllBytes(path, AddExifOrientation(File.ReadAllBytes(path), orientation));
        return path;
    }

    private static byte[] AddExifOrientation(byte[] jpeg, int orientation)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("Synthetic JPEG has no SOI marker.");
        if (orientation is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(orientation));

        // Minimal little-endian TIFF IFD with one SHORT Orientation entry.
        byte[] exif = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(exif, 0);
        const int tiff = 6;
        exif[tiff] = (byte)'I';
        exif[tiff + 1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(tiff + 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(tiff + 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(tiff + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(tiff + 10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(tiff + 12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(tiff + 14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(tiff + 18), (ushort)orientation);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(tiff + 22), 0);

        int segmentLength = exif.Length + 2;
        using var output = new MemoryStream(jpeg.Length + exif.Length + 6);
        output.WriteByte(0xff);
        output.WriteByte(0xd8);
        output.WriteByte(0xff);
        output.WriteByte(0xe1);
        output.WriteByte((byte)(segmentLength >> 8));
        output.WriteByte((byte)segmentLength);
        output.Write(exif, 0, exif.Length);
        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    private static void AssertPng(string path)
    {
        byte[] signature = File.ReadAllBytes(path).Take(8).ToArray();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
    }

    private static void AssertOrientedCorners(Bitmap image, int orientation)
    {
        string[] expected = orientation switch
        {
            1 => new[] { "red", "green", "blue", "yellow" },
            6 => new[] { "blue", "red", "yellow", "green" },
            8 => new[] { "green", "yellow", "red", "blue" },
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };

        AssertColorFamily(SampleCorner(image, 0.1, 0.1), expected[0], "top-left");
        AssertColorFamily(SampleCorner(image, 0.9, 0.1), expected[1], "top-right");
        AssertColorFamily(SampleCorner(image, 0.1, 0.9), expected[2], "bottom-left");
        AssertColorFamily(SampleCorner(image, 0.9, 0.9), expected[3], "bottom-right");
    }

    private static Color SampleCorner(Bitmap image, double xFraction, double yFraction)
    {
        int x = Math.Clamp((int)(image.Width * xFraction), 0, image.Width - 1);
        int y = Math.Clamp((int)(image.Height * yFraction), 0, image.Height - 1);
        return image.GetPixel(x, y);
    }

    private static void AssertColorFamily(Color actual, string expected, string corner)
    {
        bool matches = expected switch
        {
            "red" => actual.R > 150 && actual.R > actual.G * 1.7 && actual.R > actual.B * 1.7,
            "green" => actual.G > 80 && actual.G > actual.R * 1.7 && actual.G > actual.B * 1.7,
            "blue" => actual.B > 150 && actual.B > actual.R * 1.7 && actual.B > actual.G * 1.7,
            "yellow" => actual.R > 150 && actual.G > 150 && actual.B < 100,
            _ => false,
        };
        Assert.True(matches, $"Expected {expected} at {corner}, got R={actual.R} G={actual.G} B={actual.B}.");
    }
}
