using System.Diagnostics;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class ProgramInvocationTests
{
    [Theory]
    [InlineData("--probe", false)]
    [InlineData("--probe", true)]
    [InlineData("--startup", true)]
    [InlineData("--unknown", false)]
    public void UnsupportedProductionArgumentsExitBeforeServicesAndCreateNoExport(string argument, bool extraArgument)
    {
        // A second safety boundary prevents real provider collection even if a
        // regression allows an unsupported invocation through the entry guard.
        using var owner = new Mutex(true, "Local\\Llumi.V1." + Environment.UserName, out var first);
        Assert.True(first);
        var directory = Path.Combine(Path.GetTempPath(), "Llumi-argument-fixture-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "synthetic-usage.json");
        Directory.CreateDirectory(directory);
        try
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Llumi.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            info.ArgumentList.Add(argument);
            if (argument == "--probe" || extraArgument) info.ArgumentList.Add(output);
            if (argument == "--probe" && extraArgument) info.ArgumentList.Add("extra");
            using var child = Process.Start(info)!;
            try
            {
                Assert.True(child.WaitForExit(10_000), "Unsupported invocation must exit promptly.");
                Assert.Equal(1, child.ExitCode);
                Assert.False(File.Exists(output));
                Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
            }
        }
        finally
        {
            // This synthetic directory should still be empty; never recurse.
            Directory.Delete(directory, recursive: false);
            owner.ReleaseMutex();
        }
    }
}
