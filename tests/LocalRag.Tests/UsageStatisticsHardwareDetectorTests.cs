using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class UsageStatisticsHardwareDetectorTests
{
    [Fact]
    public void KnownDriverAliasesBecomeCanonicalNamesAndRamIsBucketed()
    {
        var result = UsageStatisticsHardwareDetector.Normalize("AMD Ryzen AI 9 HX 370 w/ Radeon 890M",
            ["AMD Radeon(TM) 890M Graphics"], ["AMD IPU Device"], 31L * 1024 * 1024 * 1024);
        Assert.Equal("AMD Ryzen AI 9 HX 370", result.Cpu);
        Assert.Equal("AMD Radeon 890M", result.Gpu);
        Assert.Equal("AMD IPU Device", result.Npu);
        Assert.Equal(32L * 1024 * 1024 * 1024, result.MemoryBytes);
    }

    [Fact]
    public void CpuAndGpuStayCataloguedWhileReportedNpuNameIsPreserved()
    {
        var result = UsageStatisticsHardwareDetector.Normalize("AMD Ryzen AI 9 HX 370 / Alice",
            ["Alice workstation GPU", "NVIDIA GeForce RTX 4060 / secret"], ["Company NPU", "Intel AI Boost - serial123"], 0);
        Assert.Equal("unknown", result.Cpu);
        Assert.Equal("unknown", result.Gpu);
        Assert.Equal("Company NPU", result.Npu);
        Assert.Equal(0, result.MemoryBytes);
    }

    [Fact]
    public void UnrecognizedAdapterDoesNotHideLaterKnownAdapter()
    {
        var result = UsageStatisticsHardwareDetector.Normalize(null,
            ["Unrecognized Adapter", "NVIDIA GeForce RTX 4060"], ["Intel(R) AI Boost"], 16L * 1024 * 1024 * 1024);
        Assert.Equal("NVIDIA GeForce RTX 4060", result.Gpu);
        Assert.Equal("Intel(R) AI Boost", result.Npu);
    }

    [Fact]
    public async Task CancelledProbeDoesNotReadHardware()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UsageStatisticsHardwareDetector.DetectAsync(cancelled.Token));
    }

    [Theory]
    [InlineData("AMD Ryzen AI 5 PRO 340 w/ Radeon 840M", "AMD Radeon(TM) 840M Graphics")]
    [InlineData("AMD Ryzen AI 5 PRO 340 w/ Radeon 840M Graphics", "AMD Radeon 840M Graphics")]
    public void ObservedRyzenProNamesAndGenericNpuRemainFactual(string cpu, string gpu)
    {
        var input = UsageStatisticsHardwareDetector.Normalize(cpu, [gpu],
            ["NPU Compute Accelerator Device"], 31L * 1024 * 1024 * 1024);
        var report = UsageStatisticsPrivacy.Hardware(input);
        Assert.Equal("AMD Ryzen AI 5 PRO 340", report.Cpu);
        Assert.Equal("AMD Radeon 840M", report.Gpu);
        // The driver does not expose a chip model; do not infer AMD XDNA from the CPU.
        Assert.Equal("NPU Compute Accelerator Device", report.Npu);
        Assert.Equal(32, report.MemoryGiB);
    }

    [Fact]
    public void NpuDriverSuffixIsPreservedWithoutWeakeningCpuAndGpuRules()
    {
        var result = UsageStatisticsHardwareDetector.Normalize("AMD Ryzen AI 5 PRO 340 w/ Radeon 840M / Alice",
            ["AMD Radeon(TM) 840M Graphics - serial123"], ["NPU Compute Accelerator Device for Alice"], 0);
        Assert.Equal("unknown", result.Cpu);
        Assert.Equal("unknown", result.Gpu);
        Assert.Equal("NPU Compute Accelerator Device for Alice", result.Npu);
    }

    [Theory]
    [InlineData("Vendor New Accelerator v7")]
    [InlineData("  日本語の演算装置  ")]
    [InlineData("한국어 신경망 처리 장치")]
    [InlineData("中文神经网络处理器")]
    public void RawNpuDriverNamesRemainExact(string name)
    {
        var result = UsageStatisticsHardwareDetector.Normalize(null, [], [name], 0, 1);
        Assert.Equal(name, result.Npu);
        Assert.Equal(name, UsageStatisticsPrivacy.Hardware(result).Npu);
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("  ")][InlineData("bad\nname")]
    [InlineData("bad\u0000name")][InlineData("bad\u007Fname")][InlineData("bad\u0085name")][InlineData("bad\u009Fname")]
    [InlineData(" \uFEFF ")]
    public void InvalidNpuNameRemainsUnknownRatherThanAbsent(string? name)
    {
        Assert.Equal("unknown", UsageStatisticsPrivacy.Hardware(new(Npu: name)).Npu);
        Assert.Equal("unknown", UsageStatisticsHardwareDetector.Normalize(null, [], [name!], 0, 1).Npu);
    }

    [Fact]
    public void NpuLengthLimitDoesNotSilentlyTruncateNames()
    {
        var name = new string('界', 160);
        Assert.Equal(name, UsageStatisticsPrivacy.NpuName(name));
        Assert.Equal("unknown", UsageStatisticsPrivacy.NpuName(name + "a"));
    }

    [Theory]
    [InlineData("Microsoft Input Configuration Device", "System", false)]
    [InlineData("Generic Input Device", "HIDClass", false)]
    [InlineData("NPU Compute Accelerator Device", "System", true)]
    [InlineData("AMD IPU Device", null, true)]
    [InlineData("Intel(R) AI Boost", null, true)]
    [InlineData("AMD XDNA 3", null, true)]
    [InlineData("Next Generation Device", "ComputeAccelerator", true)]
    [InlineData("Neural Processor", null, true)]
    public void CandidateFilteringDoesNotMistakeInputForNpu(string name, string? deviceClass, bool expected) =>
        Assert.Equal(expected, UsageStatisticsHardwareDetector.IsNpuCandidate(name, deviceClass));

    [Theory]
    [InlineData("{\"kind\":\"npu-complete\",\"count\":0}\n", "なし")]
    [InlineData("", "unknown")]
    [InlineData("{\"kind\":\"npu-complete\",\"count\":1}\n", "unknown")]
    [InlineData("{\"kind\":\"npu-complete\",\"count\":\"invalid\"}\n", "unknown")]
    [InlineData("{\"kind\":\"npu-complete\",\"count\":-1}\n", "unknown")]
    public async Task OnlySuccessfulEmptyEnumerationMeansNoNpu(string output, string expected)
    {
        var gpu = new List<string>(); var npu = new List<string>(); int? candidates = null;
        await UsageStatisticsHardwareDetector.ReadDeviceOutputAsync(new StringReader(output), gpu, npu,
            CancellationToken.None, count => candidates = count);
        Assert.Equal(expected, UsageStatisticsHardwareDetector.Normalize(null, gpu, npu, 0, candidates).Npu);
    }

    [Fact]
    public async Task ProbeOutputRetainsClassBasedNamesAndIgnoresInputDevice()
    {
        var gpu = new List<string>(); var npu = new List<string>(); int? candidates = null;
        const string output = "{\"kind\":\"npu\",\"name\":\"Microsoft Input Configuration Device\",\"class\":\"System\"}\n" +
            "{\"kind\":\"npu\",\"name\":\"日本語の演算装置\",\"class\":\"ComputeAccelerator\"}\n" +
            "{\"kind\":\"npu-complete\",\"count\":1}\n";
        await UsageStatisticsHardwareDetector.ReadDeviceOutputAsync(new StringReader(output), gpu, npu,
            CancellationToken.None, count => candidates = count);
        Assert.Equal("日本語の演算装置", Assert.Single(npu));
        Assert.Equal(1, candidates);
    }

    [Fact]
    public async Task CompletionMarkerAfterSixteenDeviceLinesIsRead()
    {
        var gpu = new List<string>(); var npu = new List<string>(); int? candidates = null;
        var output = string.Concat(Enumerable.Repeat("{\"kind\":\"gpu\",\"name\":\"AMD Radeon 840M\"}\n", 8)) +
            string.Concat(Enumerable.Repeat("{\"kind\":\"npu\",\"name\":\"Future NPU\"}\n", 8)) +
            "{\"kind\":\"npu-complete\",\"count\":8}\n";
        await UsageStatisticsHardwareDetector.ReadDeviceOutputAsync(new StringReader(output), gpu, npu,
            CancellationToken.None, count => candidates = count);
        Assert.Equal(8, candidates); Assert.Equal(8, npu.Count);
    }

    [Fact]
    public async Task CompleteNpuNameSurvivesLaterProbeTimeout()
    {
        var gpu = new List<string>(); var npu = new List<string>();
        using var cancel = new CancellationTokenSource();
        using var reader = new InterruptedProbeReader(cancel, "{\"kind\":\"npu\",\"name\":\"Future NPU v7\"}\n");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UsageStatisticsHardwareDetector.ReadDeviceOutputAsync(reader, gpu, npu, cancel.Token));
        Assert.Equal("Future NPU v7", UsageStatisticsHardwareDetector.Normalize(null, gpu, npu, 0).Npu);
    }

    [Fact]
    public async Task CompletedGpuLineSurvivesNpuQueryTimeout()
    {
        var gpu = new List<string>();
        var npu = new List<string>();
        using var cancel = new CancellationTokenSource();
        using var reader = new InterruptedProbeReader(cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UsageStatisticsHardwareDetector.ReadDeviceOutputAsync(reader, gpu, npu, cancel.Token));
        var result = UsageStatisticsHardwareDetector.Normalize(null, gpu, npu, 0);
        Assert.Equal("AMD Radeon 840M", result.Gpu);
        Assert.Equal("unknown", result.Npu);
    }

    private sealed class InterruptedProbeReader(CancellationTokenSource cancellation, string? suppliedOutput = null) : TextReader
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            if (_read)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<int>(token);
            }
            _read = true;
            var output = suppliedOutput ?? "{\"kind\":\"gpu\",\"name\":\"AMD Radeon(TM) 840M Graphics\"}\n{\"kind\":\"npu\",\"name\":";
            output.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(output.Length);
        }
    }
}
