using System.Diagnostics;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A real DNN 7 checkout, cloned from the upstream repository at a pinned tag, for the outer
/// incrementality loop to run against.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a fixture, and deliberately not the developer's own working copy. The value of
/// this corpus is that nobody here curated it: DNN 7 is a WebForms site of the size and shape the
/// incremental paths exist for — hundreds of <c>.ascx</c>, hundreds of <c>.resx</c>, thousands of
/// <c>.cs</c>, spread over dozens of projects — and a fixture that reproduced those proportions
/// would be a fixture somebody wrote to pass, not a site somebody shipped.
/// </para>
/// <para>
/// Pinned to a tag rather than tracking a branch. An outer test whose corpus moves under it stops
/// being a regression test: a run that fails after upstream reorganises a folder says nothing about
/// this repository, and a run that passes because upstream deleted the files says less. DNN 7 in
/// particular is finished, so the tag will not move again.
/// </para>
/// <para>
/// Distinct from <see cref="DnnPlatform"/>, which resolves whatever checkout happens to sit beside
/// the repository and pins a golden <c>.resx</c> count against it. The two want opposite things
/// from a corpus — that one wants today's file names, this one wants the same bytes every run — so
/// pointing them at one directory would break whichever assertion the checkout disagreed with.
/// </para>
/// </remarks>
internal static class DnnCorpus
{
    /// <summary>The last DNN 7 release: the newest tag that is still WebForms all the way
    /// down.</summary>
    public const string Tag = "v7.4.2";

    public const string RepositoryUrl = "https://github.com/dnnsoftware/Dnn.Platform.git";

    /// <summary>Points at an existing clone to skip the download. Must be a checkout of
    /// <see cref="Tag"/>; nothing verifies that, so pointing it elsewhere only makes the
    /// measurements meaningless rather than wrong.</summary>
    public const string PathVariable = "ROSLYNSENSE_TEST_DNN7";

    /// <summary>
    /// The checkout, cloning it on first use. Clones take minutes and a gigabyte, which is why
    /// everything built on this sits behind <see cref="DnnOuterFactAttribute"/>.
    /// </summary>
    public static string Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
            {
                if (!IsCheckout(configured))
                {
                    throw new InvalidOperationException(
                        $"{PathVariable} is set to '{configured}', which is not a DNN checkout — "
                        + "no 'DNN Platform' directory under it.");
                }

                return configured;
            }

            return s_cloned.Value;
        }
    }

    private static readonly Lazy<string> s_cloned = new(Clone, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Outside the repository, and outside its build output: the clone is far larger than
    /// everything here put together and survives a <c>git clean</c> on purpose, so a second run
    /// does not pay for it again.
    /// </summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RoslynSense", "test-corpora", $"Dnn.Platform-{Tag}");

    private static string Clone()
    {
        string target = CacheDirectory;
        if (IsCheckout(target))
            return target;

        // A previous run that died mid-clone leaves a directory that is not a checkout. Cloning
        // into it fails on "not empty", and the failure looks like a network problem rather than
        // the leftover it is.
        if (System.IO.Directory.Exists(target))
            System.IO.Directory.Delete(target, recursive: true);

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Shallow and single-branch: the corpus is one commit's worth of files, and DNN's full
        // history is several gigabytes of releases nothing here reads.
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
                 {
                     "clone", "--depth", "1", "--single-branch",
                     "--branch", Tag, RepositoryUrl, target,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git to clone the DNN corpus.");

        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !IsCheckout(target))
        {
            throw new InvalidOperationException(
                $"Cloning {RepositoryUrl} at {Tag} into '{target}' failed with exit code "
                + $"{process.ExitCode}. Set {PathVariable} to an existing checkout to skip the "
                + $"download.{Environment.NewLine}{error}");
        }

        return target;
    }

    /// <summary>The platform directory rather than the repository root, so a directory that merely
    /// has the right name is not mistaken for a checkout.</summary>
    private static bool IsCheckout(string path) =>
        System.IO.Directory.Exists(Path.Combine(path, "DNN Platform"));
}

/// <summary>
/// Gates the outer incrementality loop, which clones a real DNN 7 checkout and loads it.
/// </summary>
/// <remarks>
/// Opt-in for the same reason <see cref="RoslynSenseBenchFactAttribute"/> is: a test that downloads
/// a gigabyte and then hands a fifty-project WebForms solution to MSBuild has no business running
/// inside an ordinary <c>dotnet test</c>, but it still has to live in the suite so it keeps
/// compiling and can be run deliberately. The unit tests in
/// <see cref="IncrementalInvalidationTests"/> cover the same policies on fixtures and do run every
/// time; this is what checks that those policies still hold at a scale no fixture reaches.
/// </remarks>
public sealed class DnnOuterFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "ROSLYNSENSE_OUTER";

    public DnnOuterFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Set {EnvironmentVariable}=1 to run; this clones DNN {DnnCorpus.Tag} "
                + $"(~1 GB, cached in {DnnCorpus.CacheDirectory}) and loads it, which takes minutes.";
        }
    }
}
