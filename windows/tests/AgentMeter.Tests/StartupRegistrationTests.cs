using AgentMeter.Core;
using Microsoft.Win32;

namespace AgentMeter.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void StartupCommandQuotesPathAndHasOnlyQuietStartupArgument()
    {
        Assert.Equal("\"C:\\Users\\Example User\\AgentMeter\\AgentMeter.exe\" --startup",
            StartupRegistration.Command(@"C:\Users\Example User\AgentMeter\AgentMeter.exe"));
    }

    [Theory]
    [InlineData("AgentMeter.exe")]
    [InlineData("C:\\AgentMeter.exe\" --show")]
    [InlineData("C:\\AgentMeter.cmd")]
    [InlineData("C:\\AgentMeter\n.exe")]
    public void StartupRejectsRelativePathsAndCommandInjection(string executable) =>
        Assert.Throws<ArgumentException>(() => StartupRegistration.Command(executable));

    [Fact]
    public void StartupCommandHonorsWindowsRunLengthLimit()
    {
        var maximum = @"C:\" + new string('a', 241) + ".exe";
        Assert.Equal(260, StartupRegistration.Command(maximum).Length);
        Assert.Throws<ArgumentException>(() => StartupRegistration.Command(@"C:\" + new string('a', 242) + ".exe"));
    }

    [Fact]
    public void PerUserSettingRoundTripsWithoutTouchingActualStartupOrCreatingDuplicates()
    {
        var keyPath = @"Software\AgentMeter.Tests\" + Guid.NewGuid().ToString("N");
        const string executable = @"C:\AgentMeter test\AgentMeter.exe";
        try
        {
            var registration = new StartupRegistration(executable, keyPath: keyPath);
            Assert.True(registration.TryRead(out var initial)); Assert.False(initial);
            Assert.True(registration.TrySet(true));
            Assert.True(registration.TrySet(true));
            Assert.True(new StartupRegistration(executable, keyPath: keyPath).TryRead(out var enabled)); Assert.True(enabled);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.NotNull(key);
                Assert.Equal([StartupRegistration.ValueName], key.GetValueNames());
                Assert.Equal(RegistryValueKind.String, key.GetValueKind(StartupRegistration.ValueName));
                Assert.Equal(StartupRegistration.Command(executable), key.GetValue(StartupRegistration.ValueName));
            }
            Assert.True(registration.TrySet(false));
            Assert.True(registration.TryRead(out enabled)); Assert.False(enabled);
            Assert.True(registration.TrySet(false));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData("Codex", "https://learn.chatgpt.com/docs/codex/cli")]
    [InlineData("Claude", "https://code.claude.com/docs/en/quickstart")]
    public void SetupDestinationsAreFixedOfficialHttpsInstructions(string provider, string expected)
    {
        foreach (var failure in new[] { FailureKind.NotInstalled, FailureKind.LoggedOut })
            Assert.Equal(expected, ProviderSetup.For(new(provider, ProviderStatus.Unavailable, Failure: failure))?.AbsoluteUri);
        Assert.Null(ProviderSetup.For(new(provider, ProviderStatus.Error, Failure: FailureKind.Network)));
        Assert.Null(ProviderSetup.For(new("unknown", ProviderStatus.Unavailable, Failure: FailureKind.NotInstalled)));
    }
}
