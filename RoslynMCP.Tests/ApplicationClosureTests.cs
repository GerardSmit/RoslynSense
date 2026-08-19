using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The closure a legacy solution actually has: an application that references its libraries by
/// their build output rather than by project reference. Roslyn reports those as metadata, so the
/// edge only exists if it is matched back to the project that builds that assembly.
/// </summary>
public class ApplicationClosureTests : IDisposable
{
    private readonly string _binDirectory = Path.Combine(
        Path.GetTempPath(), "roslynsense-closure-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_binDirectory))
                Directory.Delete(_binDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ALibraryReferencedByItsDllIsStillPartOfTheApplication()
    {
        var solution = Solution(
            application: ["Auth", "Newtonsoft.Json"],
            libraries: ["Auth", "Logging"]);

        var application = solution.Projects.Single(p => p.Name == "Application");

        Assert.Equal(
            ["Application", "Auth"],
            ApplicationClosure.Of(application).Select(p => p.Name).Order());
    }

    [Fact]
    public void AReferenceThatMatchesNoProjectIsLeftAlone()
    {
        // Newtonsoft.Json has no source in the solution: there is nothing to walk into.
        var solution = Solution(application: ["Newtonsoft.Json"], libraries: ["Logging"]);

        var application = solution.Projects.Single(p => p.Name == "Application");

        Assert.Equal(["Application"], ApplicationClosure.Of(application).Select(p => p.Name));
    }

    [Fact]
    public void ALibraryReachedThroughAnotherLibrarysDllIsFoundToo()
    {
        var solution = Solution(application: ["Auth"], libraries: ["Auth", "Logging"]);

        // Auth itself references Logging by dll — the walk does not stop at the first hop.
        var auth = solution.Projects.Single(p => p.Name == "Auth");
        solution = auth.AddMetadataReference(Assembly("Logging")).Solution;

        var application = solution.Projects.Single(p => p.Name == "Application");

        Assert.Equal(
            ["Application", "Auth", "Logging"],
            ApplicationClosure.Of(application).Select(p => p.Name).Order());
    }

    [Fact]
    public void TwoProjectsBuildingTheSameAssemblyNameAreBothLeftOut()
    {
        var solution = Solution(application: ["Auth"], libraries: []);

        foreach (string name in new[] { "Auth.Old", "Auth.New" })
        {
            solution = solution.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, name, assemblyName: "Auth",
                LanguageNames.CSharp));
        }

        var application = solution.Projects.Single(p => p.Name == "Application");

        // Guessing would attribute one library's code to the other, so neither is claimed.
        Assert.Equal(["Application"], ApplicationClosure.Of(application).Select(p => p.Name));
    }

    /// <summary>
    /// An application referencing <paramref name="application"/> by assembly, alongside library
    /// projects that build the named assemblies. No project references anywhere.
    /// </summary>
    private Solution Solution(string[] application, string[] libraries)
    {
        var workspace = new AdhocWorkspace();

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Application", "Application",
            LanguageNames.CSharp,
            metadataReferences: application.Select(Assembly).ToImmutableArray()));

        foreach (string name in libraries)
        {
            solution = solution.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, name, name, LanguageNames.CSharp));
        }

        return solution;
    }

    /// <summary>
    /// A metadata reference shaped like one MSBuild hands over: a real assembly on disk under the
    /// name the reference uses, since the assembly name is read back off the path.
    /// </summary>
    private PortableExecutableReference Assembly(string name)
    {
        Directory.CreateDirectory(_binDirectory);

        string path = Path.Combine(_binDirectory, name + ".dll");
        if (!File.Exists(path))
            File.Copy(typeof(object).Assembly.Location, path);

        return MetadataReference.CreateFromFile(path);
    }
}
