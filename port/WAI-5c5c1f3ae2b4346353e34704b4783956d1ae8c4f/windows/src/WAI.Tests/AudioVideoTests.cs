using System;
using System.Collections.Generic;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class AudioVideoTests
{
    [Fact]
    public void ComputeStep_CalculatesExpectedInterval()
    {
        // 100 seconds, interval 10s, max 20 frames -> step should be 10s
        double step = AudioVideoExtractor.ComputeStep(100.0, 10.0, 20);
        Assert.Equal(10.0, step);

        // 1000 seconds, interval 10s, max 20 frames -> step must scale up to 50s
        double stepScaled = AudioVideoExtractor.ComputeStep(1000.0, 10.0, 20);
        Assert.Equal(50.0, stepScaled);
    }

    [Fact]
    public void CandidateTimes_GeneratesCorrectMidpoints()
    {
        var times = AudioVideoExtractor.CandidateTimes(duration: 10.0, step: 2.5);
        Assert.Equal(4, times.Count);
        Assert.Equal(0.5, times[0], precision: 2);
        Assert.Equal(3.0, times[1], precision: 2);
        Assert.Equal(5.5, times[2], precision: 2);
        Assert.Equal(8.0, times[3], precision: 2);
    }

    [Fact]
    public void BuildArgs_ContainPerformanceAndFilterFlags()
    {
        string pcm = AudioVideoExtractor.BuildPcmArgs("test.mp4");
        Assert.Contains("-nostdin", pcm);
        Assert.Contains("-vn -sn -dn", pcm);

        string cand = AudioVideoExtractor.BuildCandidatesArgs("video.mkv", 0.5, 768, "out%04d.jpg");
        Assert.Contains("-nostdin", cand);
        Assert.Contains("-threads 0", cand);
        Assert.Contains("-an -sn -dn", cand);
        Assert.Contains("scale=768:768", cand);

        string gray = AudioVideoExtractor.BuildGrayArgs("video.mkv", 1.0);
        Assert.Contains("-nostdin", gray);
        Assert.Contains("-threads 0", gray);
        Assert.Contains("scale=8:8", gray);
    }
}
