using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WAI.App;
using WAI.Core;
using Xunit;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace WAI.Tests;

public class GalleryViewTests
{
    [Fact]
    public void ResultItemViewModel_GalleryIcon_MatchesHitKind()
    {
        var imgHit = new Engine.Hit("C:\\photos\\beach.jpg", "Image", 0.95, "", "Sunset");
        var vidHit = new Engine.Hit("C:\\movies\\trip.mp4", "Video", 0.90, "0:15", "Trip");
        var audioHit = new Engine.Hit("C:\\music\\song.mp3", "Audio", 0.85, "1:30", "Music");
        var pdfHit = new Engine.Hit("C:\\docs\\manual.pdf", "Pdf", 0.80, "p.1", "Manual");
        var codeHit = new Engine.Hit("C:\\src\\main.cs", "Code", 0.75, "line 10", "Main");
        var docHit = new Engine.Hit("C:\\docs\\notes.txt", "Doc", 0.70, "", "Notes");

        var dummyBrush = Brushes.Transparent;
        var vmImg = CreateVm(imgHit, dummyBrush);
        var vmVid = CreateVm(vidHit, dummyBrush);
        var vmAudio = CreateVm(audioHit, dummyBrush);
        var vmPdf = CreateVm(pdfHit, dummyBrush);
        var vmCode = CreateVm(codeHit, dummyBrush);
        var vmDoc = CreateVm(docHit, dummyBrush);

        Assert.Equal("🖼️", vmImg.GalleryIcon);
        Assert.Equal("🎬", vmVid.GalleryIcon);
        Assert.Equal("🎵", vmAudio.GalleryIcon);
        Assert.Equal("📄", vmPdf.GalleryIcon);
        Assert.Equal("💻", vmCode.GalleryIcon);
        Assert.Equal("📝", vmDoc.GalleryIcon);
    }

    [Fact]
    public void ResultItemViewModel_Thumbnail_NotifiesPropertyChanged()
    {
        var hit = new Engine.Hit("C:\\test.png", "Image", 0.9, "", "test");
        var vm = CreateVm(hit, Brushes.Transparent);

        Assert.False(vm.HasThumbnail);
        Assert.Null(vm.Thumbnail);

        var changedProps = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        // Create a dummy 1x1 frozen bitmap
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[] { 0, 0, 0, 0 }, 4);
        bmp.Freeze();

        vm.Thumbnail = bmp;

        Assert.True(vm.HasThumbnail);
        Assert.NotNull(vm.Thumbnail);
        Assert.Contains(nameof(ResultItemViewModel.Thumbnail), changedProps);
        Assert.Contains(nameof(ResultItemViewModel.HasThumbnail), changedProps);
    }

    [Fact]
    public void Settings_PreferGallery_DefaultAndSerialization()
    {
        var settings = new Settings();
        Assert.False(settings.PreferGallery);

        settings.PreferGallery = true;
        string json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.PreferGallery);
    }

    private static ResultItemViewModel CreateVm(Engine.Hit hit, Brush brush) => new()
    {
        Hit = hit,
        Title = Path.GetFileName(hit.Path),
        Detail = hit.Excerpt ?? "",
        BadgeText = hit.Kind,
        BadgeBackground = brush,
        BadgeBorder = brush,
        BadgeForeground = brush,
        RelevancePercent = "90%",
        RelevanceTooltip = "90%",
        RelevanceBackground = brush,
        RelevanceForeground = brush
    };
}
