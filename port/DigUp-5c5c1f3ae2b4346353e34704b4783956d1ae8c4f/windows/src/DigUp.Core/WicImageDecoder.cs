using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DigUp.Core;

/// <summary>
/// Native Windows Imaging Component (WIC) decoder using Windows.Graphics.Imaging.
/// Decodes images (including JPEG, PNG, BMP, GIF, TIFF, WebP, and HEIC) with hardware-assisted
/// downscaling, EXIF orientation correction, and metadata extraction. Falls back to GDI+ if needed.
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    private readonly GdiImageDecoder _gdiFallback = new();

    public PreparedImage Decode(string sourcePath, int maxPixel, int minSide, string outputFolder, bool png)
    {
        try
        {
            return DecodeAsync(sourcePath, maxPixel, minSide, outputFolder, png).GetAwaiter().GetResult();
        }
        catch (ExtractError)
        {
            // Propagate ExtractError (such as TooSmall or Unreadable)
            throw;
        }
        catch
        {
            // If WIC failed (e.g. rare unsupported format or WinRT path issue), try GDI+ fallback
            return _gdiFallback.Decode(sourcePath, maxPixel, minSide, outputFolder, png);
        }
    }

    private static async Task<PreparedImage> DecodeAsync(string sourcePath, int maxPixel, int minSide, string outputFolder, bool png)
    {
        string fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
            throw ExtractError.Unreadable("file does not exist");

        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(fullSource);
        }
        catch (Exception ex)
        {
            throw ExtractError.Unreadable("cannot access file: " + ex.Message);
        }

        using var inStream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(inStream);
        }
        catch (Exception ex)
        {
            throw ExtractError.Unreadable("not an image Windows can read: " + ex.Message);
        }

        uint w = decoder.PixelWidth;
        uint h = decoder.PixelHeight;
        if (Math.Max(w, h) < minSide)
            throw ExtractError.TooSmall((int)w, (int)h);
        bool hasNonNormalExifOrientation = await HasNonNormalExifOrientation(decoder);

        string? taken = null;
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.DateTaken" });
            if (props.TryGetValue("System.Photo.DateTaken", out var val) && val?.Value is DateTimeOffset dto)
            {
                taken = dto.ToString("yyyy:MM:dd HH:mm:ss");
            }
        }
        catch
        {
            // Metadata is optional
        }

        Directory.CreateDirectory(outputFolder);
        string destFileName = Guid.NewGuid().ToString("N") + (png ? ".png" : ".jpg");
        string destPath = Path.Combine(outputFolder, destFileName);

        uint orientedWidth = decoder.OrientedPixelWidth;
        uint orientedHeight = decoder.OrientedPixelHeight;
        double scale = Math.Min(1.0, (double)maxPixel / Math.Max(orientedWidth, orientedHeight));
        bool outputMatchesSource = png ? IsPng(sourcePath) : IsJpeg(sourcePath);
        if (scale >= 1.0 && !hasNonNormalExifOrientation && outputMatchesSource)
        {
            // Already small enough: direct copy
            File.Copy(fullSource, destPath, overwrite: true);
            return new PreparedImage(destPath, (int)w, (int)h, taken);
        }

        uint tw = Math.Max(1u, (uint)Math.Round(orientedWidth * scale));
        uint th = Math.Max(1u, (uint)Math.Round(orientedHeight * scale));
        uint scaledSourceWidth = Math.Max(1u, (uint)Math.Round(w * scale));
        uint scaledSourceHeight = Math.Max(1u, (uint)Math.Round(h * scale));

        var transform = new BitmapTransform
        {
            // WIC scales in the source coordinate space, before applying EXIF rotation.
            ScaledWidth = scaledSourceWidth,
            ScaledHeight = scaledSourceHeight,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        byte[] pixels = pixelData.DetachPixelData();

        var outFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(outputFolder));
        var outFile = await outFolder.CreateFileAsync(destFileName, CreationCollisionOption.ReplaceExisting);
        using (var outStream = await outFile.OpenAsync(FileAccessMode.ReadWrite))
        {
            Guid encoderId = png ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;
            BitmapEncoder encoder;
            if (!png)
            {
                var propertySet = new BitmapPropertySet();
                var qualityValue = new BitmapTypedValue(0.9f, Windows.Foundation.PropertyType.Single);
                propertySet.Add("ImageQuality", qualityValue);
                encoder = await BitmapEncoder.CreateAsync(encoderId, outStream, propertySet);
            }
            else
            {
                encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);
            }

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                tw,
                th,
                96,
                96,
                pixels);

            await encoder.FlushAsync();
        }

        return new PreparedImage(destPath, (int)w, (int)h, taken);
    }

    private static bool IsJpeg(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static bool IsPng(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HasNonNormalExifOrientation(BitmapDecoder decoder)
    {
        try
        {
            var properties = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
            if (!properties.TryGetValue("System.Photo.Orientation", out var property) || property?.Value is null)
                return false;

            return property.Value switch
            {
                ushort orientation => orientation != 1,
                short orientation => orientation != 1,
                uint orientation => orientation != 1,
                int orientation => orientation != 1,
                byte orientation => orientation != 1,
                _ => true,
            };
        }
        catch
        {
            // If orientation metadata cannot be read, avoid copying pixels that may need correction.
            return true;
        }
    }
}
