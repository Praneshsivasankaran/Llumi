namespace AgentMeter.Tests;

public sealed class LlumiMigrationTests
{
    [Fact]
    public void MigrationIsAllowlistedTypedAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root,"old"); var current=Path.Combine(root,"current");
        try
        {
            Directory.CreateDirectory(old); Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(old,"v2-preferences.json"), "{\"Appearance\":2,\"CompactMonitor\":false,\"TrayIcon\":\"false\",\"token\":\"synthetic\"}");
            File.WriteAllText(Path.Combine(old,"setup-completed.json"), "true");
            File.WriteAllText(Path.Combine(current,"v2-preferences.json"), "{\"Appearance\":1}");
            LegacyPreferences.Migrate(old,current);
            var preferences=new PreferenceStore(Path.Combine(current,"v2-preferences.json")).Load();
            Assert.Equal(Appearance.Light,preferences.Appearance);Assert.False(preferences.CompactMonitor);Assert.True(preferences.TrayIcon);
            Assert.True(new SetupCompletionStore(Path.Combine(current,"setup-completed.json")).IsComplete());
            Assert.DoesNotContain("token",File.ReadAllText(Path.Combine(current,"v2-preferences.json")));
            Assert.True(new PreferenceStore(Path.Combine(current,"v2-preferences.json")).Save(preferences with { Appearance = Appearance.System }));
            LegacyPreferences.Migrate(old,current);
            Assert.Equal(Appearance.System,new PreferenceStore(Path.Combine(current,"v2-preferences.json")).Load().Appearance);
            Assert.True(File.Exists(Path.Combine(old,"v2-preferences.json")));
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact]
    public void InvalidAndDuplicateLegacyValuesDoNotCompleteSetup()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));var old=Path.Combine(root,"old");var current=Path.Combine(root,"new");
        try {
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old,"v2-preferences.json"),"{\"Appearance\":1,\"Appearance\":2}");
            File.WriteAllText(Path.Combine(old,"setup-completed.json"),"\"true\"");
            LegacyPreferences.Migrate(old,current);
            Assert.False(new SetupCompletionStore(Path.Combine(current,"setup-completed.json")).IsComplete());
            Assert.False(File.Exists(Path.Combine(current,"v2-preferences.json")));
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void DisplayProductAndStartupUseLlumiWhilePackageTaskRemainsCompatible()
    {
        var product=System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyProductAttribute>(typeof(AppIcon).Assembly);
        Assert.Equal("Llumi",product?.Product);
        Assert.Equal("Llumi",StartupRegistration.ValueName);
        Assert.Equal("AgentMeterStartup",WindowsStartupTaskAccess.TaskId);
    }
}

[Collection("Windows UI")]
public sealed class LlumiInstanceProcessTests
{
    [Theory]
    [InlineData("Local\\Llumi.V1.")]
    [InlineData("Local\\AgentMeter.V0.1.")]
    public void ExistingCurrentOrLegacyMutexStopsRealAppBeforeServices(string prefix)
    {
        using var owner = new Mutex(true, prefix + Environment.UserName, out var first);
        Assert.True(first);
        var children = new List<System.Diagnostics.Process>();
        var log = Path.Combine(PackagedEnvironment.DataDirectory, "logs", "llumi.log");
        var before = File.Exists(log) ? File.ReadAllText(log) : null;
        try
        {
            for (var i=0;i<4;i++)
                children.Add(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(AppContext.BaseDirectory,"Llumi.exe"),"--startup") { UseShellExecute=false })!);
            foreach(var child in children)
            {
                Assert.True(child.WaitForExit(10_000),"Duplicate must exit promptly");
                Assert.Equal(0,child.ExitCode);
            }
            Assert.Equal(before,File.Exists(log)?File.ReadAllText(log):null);
        }
        finally
        {
            foreach(var child in children) { if(!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); }
            owner.ReleaseMutex();
        }
    }
}
