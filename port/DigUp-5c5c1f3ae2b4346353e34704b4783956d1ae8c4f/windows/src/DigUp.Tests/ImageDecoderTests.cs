using System;
using System.IO;
using DigUp.Core;
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
}
