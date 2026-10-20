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
        var imgHit = new Engine.Hit("C:\\photos\\beach.jpg", "Image", "", "Sunset", 0.95);
        var vidHit = new Engine.Hit("C:\\movies\\trip.mp4", "Video", "0:15", "Trip", 0.90);
        var audioHit = new Engine.Hit("C:\\music\\song.mp3", "Audio", "1:30", "Music", 0.85);
        var pdfHit = new Engine.Hit("C:\\docs\\manual.pdf", "Pdf", "p.1", "Manual", 0.80);
        var codeHit = new Engine.Hit("C:\\src\\main.cs", "Code", "line 10", "Main", 0.75);
        var docHit = new Engine.Hit("C:\\docs\\notes.txt", "Doc", "", "Notes", 0.70);

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
        var hit = new Engine.Hit("C:\\test.png", "Image", "", "test", 0.9);
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
