using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class DesktopLaunchOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingProfileArgumentCanBeCombinedWithSilentLogonInEitherOrder(bool trayFirst)
    {
        var profile = Path.Combine(Path.GetTempPath(), "Local RAG test");
        var withSlash = profile + Path.DirectorySeparatorChar;
        string[] args = trayFirst ? ["--startup-tray", "--data-dir", withSlash] : ["--data-dir", withSlash, "--startup-tray"];
        Assert.Equal(new DesktopLaunchOptions(profile, true), DesktopLaunchOptions.Parse(args, Path.GetTempPath()));
        Assert.Equal(new DesktopLaunchOptions(profile, false), DesktopLaunchOptions.Parse(["--data-dir", withSlash], Path.GetTempPath()));
    }

    [Fact]
    public void NormalAndStartupLaunchUseTheSameDefaultProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), "profile");
        Assert.Equal(new DesktopLaunchOptions(profile, false), DesktopLaunchOptions.Parse([], profile));
        Assert.Equal(new DesktopLaunchOptions(profile, true), DesktopLaunchOptions.Parse(["--startup-tray"], profile));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--startup-tray --startup-tray")]
    [InlineData("--data-dir")]
    [InlineData("--data-dir --startup-tray")]
    [InlineData("--data-dir A --data-dir B")]
    public void InvalidArgumentsCannotLaunchWithDefaultSettings(string arguments) =>
        Assert.Null(DesktopLaunchOptions.Parse(arguments.Split(' '), Path.GetTempPath()));
}
