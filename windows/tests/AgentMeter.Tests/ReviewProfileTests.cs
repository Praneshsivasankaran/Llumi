using System.Diagnostics;
using System.Text;

namespace AgentMeter.Tests;

public sealed class ReviewProfileTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Llumi-review-profile-tests-").FullName;
    private readonly List<string> junctions = [];

    [Fact]
    public void FreshProfileGetsMarkerAndCanResumeWithoutResettingSettings()
    {
        var profile = Path.Combine(root, "fresh");
        Assert.Equal(profile, LiveFirstRunReview.ValidateProfile(profile));
        Assert.False(Directory.Exists(profile));
        Assert.Equal(profile, LiveFirstRunReview.PrepareProfile(profile));
        Assert.Equal(LiveFirstRunReview.MarkerContent, File.ReadAllText(Path.Combine(profile, LiveFirstRunReview.MarkerName)));
        var preferences = Path.Combine(profile, "v2-preferences.json");
        var completed = Path.Combine(profile, "setup-completed.json");
        File.WriteAllText(preferences, "synthetic existing preferences");
        File.WriteAllText(completed, "synthetic completed setup");
        var before = Directory.GetFiles(profile).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(profile, LiveFirstRunReview.PrepareProfile(profile, resume: true));
        Assert.All(before, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
        Assert.Equal(before.Count, Directory.GetFiles(profile).Length);
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.PrepareProfile(profile));
    }

    [Fact]
    public void FirstRunPreservesAndRejectsAnyExistingFiles()
    {
        var file = Path.Combine(root, "keep.txt");
        File.WriteAllText(file, "synthetic fixture");
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.PrepareProfile(root));
        Assert.Equal("synthetic fixture", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(root, LiveFirstRunReview.MarkerName)));
    }

    [Fact]
    public void ResumeRequiresExistingDirectoryAndAnExactBoundedMarker()
    {
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(Path.Combine(root, "absent"), resume: true));
        Assert.ThrowsAny<IOException>(() => LiveFirstRunReview.ValidateProfile(root, resume: true));
        var marker = Path.Combine(root, LiveFirstRunReview.MarkerName);
        foreach (var text in new[] { "", "wrong marker", new string('x', 4096), LiveFirstRunReview.MarkerContent.Replace("v1", "v2") })
        {
            File.WriteAllText(marker, text, new UTF8Encoding(false));
            Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(root, resume: true));
            Assert.Equal(text, File.ReadAllText(marker));
        }
        File.WriteAllText(marker, LiveFirstRunReview.MarkerContent, new UTF8Encoding(true));
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(root, resume: true));
    }

    [Theory]
    [InlineData("Llumi", false)]
    [InlineData("AgentMeter", false)]
    [InlineData("Packages", false)]
    [InlineData("Llumi", true)]
    [InlineData("AgentMeter", true)]
    [InlineData("Packages", true)]
    public void ProductionDataIsExcludedInBothModes(string name, bool resume)
    {
        foreach (var specialFolder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var appData = Environment.GetFolderPath(specialFolder);
            Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(Path.Combine(appData, name), resume));
            Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(Path.Combine(appData, name, "synthetic-profile"), resume));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeNetworkAndFilePathsAreRejected(bool resume)
    {
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile("relative-review", resume));
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(@"\\localhost\review\profile", resume));
        var file = Path.Combine(root, "file");
        File.WriteAllText(file, "synthetic fixture");
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(file, resume));
    }

    [Fact]
    public void ResumeRejectsChildDirectoryLinksBeforeTheyCanRedirectWrites()
    {
        var profile = LiveFirstRunReview.PrepareProfile(Path.Combine(root, "profile"));
        var external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
        var sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "untouched synthetic destination");
        CreateJunction(Path.Combine(profile, "logs"), external);
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.PrepareProfile(profile, resume: true));
        Assert.Equal("untouched synthetic destination", File.ReadAllText(sentinel));
    }

    [Fact]
    public void BothModesRejectLinkedProfileAncestors()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var link = Path.Combine(root, "linked");
        CreateJunction(link, target);
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(Path.Combine(link, "new")));
        Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(Path.Combine(link, "new"), resume: true));
    }

    [Fact]
    public void ResumeRejectsLinkedFilesIncludingTheMarker()
    {
        var profile = LiveFirstRunReview.PrepareProfile(Path.Combine(root, "profile"));
        var external = Path.Combine(root, "external-marker");
        File.WriteAllText(external, LiveFirstRunReview.MarkerContent);
        var preferencesLink = Path.Combine(profile, "v2-preferences.json");
        File.CreateSymbolicLink(preferencesLink, external);
        try { Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(profile, resume: true)); }
        finally { File.Delete(preferencesLink); }
        var marker = Path.Combine(profile, LiveFirstRunReview.MarkerName);
        File.Delete(marker);
        File.CreateSymbolicLink(marker, external);
        try { Assert.Throws<ArgumentException>(() => LiveFirstRunReview.ValidateProfile(profile, resume: true)); }
        finally { File.Delete(marker); }
        Assert.Equal(LiveFirstRunReview.MarkerContent, File.ReadAllText(external));
    }

    private void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:LLUMI_REVIEW_TEST_LINK -Target $env:LLUMI_REVIEW_TEST_TARGET -ErrorAction Stop | Out-Null");
        start.Environment["LLUMI_REVIEW_TEST_LINK"] = path;
        start.Environment["LLUMI_REVIEW_TEST_TARGET"] = target;
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Fixture junction creation timed out.");
        }
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        junctions.Add(path);
    }

    public void Dispose()
    {
        foreach (var junction in junctions) Directory.Delete(junction);
        Directory.Delete(root, recursive: true);
    }
}
