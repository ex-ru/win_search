using System;
using System.Collections.Generic;
using WAI.App;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class V123FeaturesTests
{
    // ==========================================
    // IDE-01: Editors line & column integration
    // ==========================================

    [Theory]
    [InlineData("code", @"C:\project\main.cs", 42, 10, "--goto \"C:\\project\\main.cs\":42:10")]
    [InlineData("cursor", @"C:\project\main.cs", 15, 3, "--goto \"C:\\project\\main.cs\":15:3")]
    [InlineData("idea64", @"C:\project\main.cs", 100, 5, "--line 100 --column 5 \"C:\\project\\main.cs\"")]
    [InlineData("pycharm64.exe", @"C:\project\script.py", 20, 1, "--line 20 --column 1 \"C:\\project\\script.py\"")]
    [InlineData("rider64.exe", @"C:\project\app.cs", 77, 12, "--line 77 --column 12 \"C:\\project\\app.cs\"")]
    [InlineData("notepad++", @"C:\project\doc.txt", 33, 4, "-n33 -c4 \"C:\\project\\doc.txt\"")]
    [InlineData("subl", @"C:\project\doc.txt", 8, 2, "\"C:\\project\\doc.txt\":8:2")]
    [InlineData("devenv", @"C:\project\sol.sln", 50, 1, "\"C:\\project\\sol.sln\" /command \"edit.goto 50\"")]
    [InlineData("kate", @"C:\project\file.cpp", 12, 6, "-l 12 -c 6 \"C:\\project\\file.cpp\"")]
    public void Editors_ForCode_FormatsArgumentsCorrectly(string editor, string path, int line, int col, string expectedArgs)
    {
        var target = Editors.ForCode(editor, path, line, col);
        Assert.Equal(expectedArgs, target.Args);
    }

    [Fact]
    public void Editors_IsJetBrains_IdentifiesFamily()
    {
        Assert.True(Editors.IsJetBrains("idea64.exe"));
        Assert.True(Editors.IsJetBrains("pycharm64.exe"));
        Assert.True(Editors.IsJetBrains("rider64.exe"));
        Assert.True(Editors.IsJetBrains("webstorm64.exe"));
        Assert.True(Editors.IsJetBrains("clion"));
        Assert.True(Editors.IsJetBrains("jetbrains"));
        Assert.False(Editors.IsJetBrains("code.exe"));
        Assert.False(Editors.IsJetBrains("notepad++.exe"));
    }

    [Theory]
    [InlineData("lines 42", 42, 1)]
    [InlineData("lines 42:15", 42, 15)]
    [InlineData("line 100:8", 100, 8)]
    [InlineData("", 1, 1)]
    [InlineData("none", 1, 1)]
    public void MainWindow_ParseLineAndColumn_ExtractsValues(string where, int expectedLine, int expectedCol)
    {
        var (line, col) = MainWindow.ParseLineAndColumn(where);
        Assert.Equal(expectedLine, line);
        Assert.Equal(expectedCol, col);
    }

    // ==========================================
    // PDF-01: PDF page target navigation
    // ==========================================

    [Fact]
    public void Editors_ResolvePdfTarget_IgnoresNullOrInvalidPage()
    {
        Assert.Null(Editors.ResolvePdfTarget(@"C:\docs\sample.pdf", null));
        Assert.Null(Editors.ResolvePdfTarget(@"C:\docs\sample.pdf", 0));
        Assert.Null(Editors.ResolvePdfTarget(@"C:\docs\sample.pdf", -5));
    }

    [Fact]
    public void Editors_ResolvePdfTarget_BuildsValidUriOrArgsWhenExecutablePresent()
    {
        var target = Editors.ResolvePdfTarget(@"C:\docs\sample.pdf", 14);
        if (target != null)
        {
            Assert.True(target.Args.Contains("14"));
            Assert.True(target.Args.Contains("sample.pdf"));
        }
    }

    // ==========================================
    // MED-01: Media exact seek target
    // ==========================================

    [Fact]
    public void Editors_ResolveMediaTarget_IgnoresZeroOrNegativeSeconds()
    {
        Assert.Null(Editors.ResolveMediaTarget(@"C:\media\test.mp4", null));
        Assert.Null(Editors.ResolveMediaTarget(@"C:\media\test.mp4", 0));
        Assert.Null(Editors.ResolveMediaTarget(@"C:\media\test.mp4", -10));
    }

    [Fact]
    public void Editors_ResolveMediaTarget_FormatsWhenPlayerAvailable()
    {
        var target = Editors.ResolveMediaTarget(@"C:\media\video.mkv", 65.5);
        if (target != null)
        {
            Assert.True(target.Args.Contains("video.mkv"));
            Assert.True(target.Args.Contains("65") || target.Args.Contains("65500"));
        }
    }

    // ==========================================
    // ETA-01: Pace Scaling & dynamic ETA
    // ==========================================

    [Fact]
    public void PaceCalibrator_AdjustsScale_WhenObservedDiffersFromExpected()
    {
        var calibrator = new PaceCalibrator();
        Assert.Equal(1.0, calibrator.CurrentScale);

        calibrator.Record(FileKind.Image, actualSeconds: 0.06, expectedSeconds: 0.12);
        calibrator.Record(FileKind.Image, actualSeconds: 0.06, expectedSeconds: 0.12);

        Assert.True(calibrator.CurrentScale < 0.9, $"Scale should be reduced, got {calibrator.CurrentScale}");
        double calibrated = calibrator.CalibrateSeconds(100.0);
        Assert.True(calibrated < 100.0, $"Calibrated seconds should be less than baseline, got {calibrated}");
    }

    [Theory]
    [InlineData(0, "< 1 мин")]
    [InlineData(30, "< 1 мин")]
    [InlineData(120, "~2 мин")]
    [InlineData(3600, "~1 ч 0 мин")]
    [InlineData(4500, "~1 ч 15 мин")]
    public void PaceCalibrator_FormatEta_ProducesReadableRuStrings(double seconds, string expected)
    {
        if (seconds == 0)
        {
            Assert.Equal("завершается…", PaceCalibrator.FormatEta(0, isRu: true));
        }
        else
        {
            Assert.Equal(expected, PaceCalibrator.FormatEta(seconds, isRu: true));
        }
    }

    // ==========================================
    // PWR-01: Adaptive Power & Battery Policy
    // ==========================================

    [Fact]
    public void Engine_EvaluatePowerPolicy_PausesOnBatterySaver()
    {
        var status = new Engine.SystemPowerStatus
        {
            AcLineStatus = 1, // AC online
            BatteryLifePercent = 90,
            SystemStatusFlag = 1 // Battery saver active
        };

        var (pause, reason) = Engine.EvaluatePowerPolicy(status);
        Assert.True(pause);
        Assert.Contains("экономии заряда", reason);
    }

    [Fact]
    public void Engine_EvaluatePowerPolicy_PausesOnCriticalBattery()
    {
        var status = new Engine.SystemPowerStatus
        {
            AcLineStatus = 0, // Battery offline (no AC)
            BatteryLifePercent = 15, // <= 20%
            SystemStatusFlag = 0
        };

        var (pause, reason) = Engine.EvaluatePowerPolicy(status);
        Assert.True(pause);
        Assert.Contains("критический уровень", reason);
        Assert.Contains("15%", reason);
    }

    [Fact]
    public void Engine_EvaluatePowerPolicy_ContinuesOnNormalBatteryOrAc()
    {
        // 1. AC online, normal battery
        var acStatus = new Engine.SystemPowerStatus
        {
            AcLineStatus = 1,
            BatteryLifePercent = 50,
            SystemStatusFlag = 0
        };
        var (acPause, _) = Engine.EvaluatePowerPolicy(acStatus);
        Assert.False(acPause);

        // 2. Battery power, sufficient charge (60%)
        var battStatus = new Engine.SystemPowerStatus
        {
            AcLineStatus = 0,
            BatteryLifePercent = 60,
            SystemStatusFlag = 0
        };
        var (battPause, _) = Engine.EvaluatePowerPolicy(battStatus);
        Assert.False(battPause);
    }

    // ==========================================
    // OPT-05: Vector Agreement & AutoTune
    // ==========================================

    [Fact]
    public void HardwareDetector_CalculateAgreement_IdenticalVectorsReturnOne()
    {
        float[] cpuVec = [1.0f, 0.0f, 0.0f, 0.0f];
        float[] gpuVec = [1.0f, 0.0f, 0.0f, 0.0f];

        double agreement = HardwareDetector.CalculateAgreement(cpuVec, gpuVec);
        Assert.InRange(agreement, 0.9999, 1.0001);
    }

    [Fact]
    public void HardwareDetector_CalculateAgreement_DivergingVectorsDetected()
    {
        float[] cpuVec = [1.0f, 0.0f, 0.0f, 0.0f];
        float[] gpuVec = [0.0f, 1.0f, 0.0f, 0.0f];

        double agreement = HardwareDetector.CalculateAgreement(cpuVec, gpuVec);
        Assert.True(agreement < HardwareDetector.VectorAgreementThreshold);
        Assert.InRange(agreement, -0.01, 0.01);
    }

    [Fact]
    public void HardwareDetector_AutoTune_FallsBackToCpu_WhenAgreementBelowThreshold()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "Experimental Vulkan Device", "Vendor", 0x1234, 4096, IsDiscrete: true, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(8, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: false);

        float[] cpuVec = [0.6f, 0.8f, 0.0f, 0.0f];
        float[] badGpuVec = [0.1f, 0.99f, 0.0f, 0.0f];

        var result = HardwareDetector.AutoTune(info, cpuVec, badGpuVec);

        Assert.True(result.FallbackToCpu);
        Assert.Equal("cpu", result.RecommendedBackend);
        Assert.Contains("Расхождение векторов", result.Reason);
    }

    [Fact]
    public void HardwareDetector_AutoTune_PrefersCpu_WhenIntelUhdOnMultiCore()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "Intel(R) UHD Graphics 630", "Intel", 0x8086, 128, IsDiscrete: false, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(16, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: false);

        var result = HardwareDetector.AutoTune(info);

        Assert.True(result.FallbackToCpu);
        Assert.Equal("cpu", result.RecommendedBackend);
        Assert.Contains("процессорный режим", result.Reason);
    }
}
