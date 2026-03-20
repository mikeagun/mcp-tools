using MsBuildMcp.Engine;

namespace MsBuildMcp.Tests;

public class SolutionEngineTests : IDisposable
{
    private readonly string _testSlnDir;
    private readonly string _testSlnPath;

    public SolutionEngineTests()
    {
        _testSlnDir = Path.Combine(Path.GetTempPath(), "msbuild-mcp-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testSlnDir);
        _testSlnPath = Path.Combine(_testSlnDir, "test.sln");

        File.WriteAllText(_testSlnPath, TestSolutionContent);

        var libDir = Path.Combine(_testSlnDir, "lib");
        var appDir = Path.Combine(_testSlnDir, "app");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(libDir, "lib.vcxproj"), MinimalVcxproj("lib"));
        File.WriteAllText(Path.Combine(appDir, "app.vcxproj"), MinimalVcxprojWithRef("app", @"..\lib\lib.vcxproj"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_testSlnDir, recursive: true); } catch { }
    }

    [Fact]
    public void ParsesProjectCount()
    {
        var engine = new SolutionEngine();
        var info = engine.Parse(_testSlnPath);

        var realProjects = info.Projects.Where(p => !p.IsSolutionFolder).ToList();
        Assert.Equal(2, realProjects.Count);
    }

    [Fact]
    public void ParsesSolutionFolders()
    {
        var engine = new SolutionEngine();
        var info = engine.Parse(_testSlnPath);

        var folders = info.Projects.Where(p => p.IsSolutionFolder).ToList();
        Assert.Single(folders);
        Assert.Equal("libs", folders[0].Name);
    }

    [Fact]
    public void ParsesConfigurations()
    {
        var engine = new SolutionEngine();
        var info = engine.Parse(_testSlnPath);

        Assert.Equal(2, info.Configurations.Count);
        Assert.Contains(info.Configurations, c => c.Configuration == "Debug" && c.Platform == "x64");
        Assert.Contains(info.Configurations, c => c.Configuration == "Release" && c.Platform == "x64");
    }

    [Fact]
    public void ResolvesSolutionFolderNesting()
    {
        var engine = new SolutionEngine();
        var info = engine.Parse(_testSlnPath);

        var lib = info.Projects.First(p => p.Name == "lib");
        Assert.Equal("libs", lib.SolutionFolder);
    }

    [Fact]
    public void ParsesProjectDependencies()
    {
        var engine = new SolutionEngine();
        var info = engine.Parse(_testSlnPath);

        // app depends on lib via ProjectSection(ProjectDependencies)
        var app = info.Projects.First(p => p.Name == "app");
        Assert.Single(app.SolutionDependencies);
        Assert.Equal("lib", app.SolutionDependencies[0]);

        // lib has no dependencies
        var lib = info.Projects.First(p => p.Name == "lib");
        Assert.Empty(lib.SolutionDependencies);
    }

    [Fact]
    public void ThrowsOnMissingFile()
    {
        var engine = new SolutionEngine();
        Assert.Throws<FileNotFoundException>(() => engine.Parse(@"C:\nonexistent\fake.sln"));
    }

    [Fact]
    public void CachesResultByMtime()
    {
        var engine = new SolutionEngine();
        var info1 = engine.Parse(_testSlnPath);
        var info2 = engine.Parse(_testSlnPath);

        // Same object returned from cache (reference equality)
        Assert.Same(info1, info2);

        // After touching the file, cache should be invalidated
        File.SetLastWriteTimeUtc(_testSlnPath, DateTime.UtcNow.AddSeconds(1));
        var info3 = engine.Parse(_testSlnPath);
        Assert.NotSame(info1, info3);
        Assert.Equal(info1.Projects.Count, info3.Projects.Count);
    }

    // The test .sln includes ProjectSection(ProjectDependencies) for app → lib
    private const string TestSolutionContent = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "libs", "libs", "{AAAA0000-0000-0000-0000-000000000001}"
        EndProject
        Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "lib", "lib\lib.vcxproj", "{BBBB0000-0000-0000-0000-000000000001}"
        EndProject
        Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "app", "app\app.vcxproj", "{BBBB0000-0000-0000-0000-000000000002}"
        	ProjectSection(ProjectDependencies) = postProject
        		{BBBB0000-0000-0000-0000-000000000001} = {BBBB0000-0000-0000-0000-000000000001}
        	EndProjectSection
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|x64 = Debug|x64
        		Release|x64 = Release|x64
        	EndGlobalSection
        	GlobalSection(NestedProjects) = preSolution
        		{BBBB0000-0000-0000-0000-000000000001} = {AAAA0000-0000-0000-0000-000000000001}
        	EndGlobalSection
        EndGlobal
        """;

    private static string MinimalVcxproj(string name) =>
        "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <ItemGroup Label=\"ProjectConfigurations\">\n" +
        "    <ProjectConfiguration Include=\"Debug|x64\"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration>\n" +
        "    <ProjectConfiguration Include=\"Release|x64\"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration>\n" +
        "  </ItemGroup>\n" +
        $"  <PropertyGroup Label=\"Globals\"><ProjectGuid>{{00000000-0000-0000-0000-000000000000}}</ProjectGuid><RootNamespace>{name}</RootNamespace></PropertyGroup>\n" +
        "  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='Debug|x64'\"><ConfigurationType>StaticLibrary</ConfigurationType><OutDir>$(SolutionDir)x64\\Debug\\</OutDir></PropertyGroup>\n" +
        $"  <ItemGroup><ClCompile Include=\"{name}.cpp\" /></ItemGroup>\n" +
        "</Project>";

    private static string MinimalVcxprojWithRef(string name, string refPath) =>
        "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <ItemGroup Label=\"ProjectConfigurations\">\n" +
        "    <ProjectConfiguration Include=\"Debug|x64\"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration>\n" +
        "    <ProjectConfiguration Include=\"Release|x64\"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration>\n" +
        "  </ItemGroup>\n" +
        $"  <PropertyGroup Label=\"Globals\"><ProjectGuid>{{00000000-0000-0000-0000-000000000001}}</ProjectGuid><RootNamespace>{name}</RootNamespace></PropertyGroup>\n" +
        "  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='Debug|x64'\"><ConfigurationType>Application</ConfigurationType><OutDir>$(SolutionDir)x64\\Debug\\</OutDir></PropertyGroup>\n" +
        $"  <ItemGroup><ClCompile Include=\"{name}.cpp\" /></ItemGroup>\n" +
        $"  <ItemGroup><ProjectReference Include=\"{refPath}\" /></ItemGroup>\n" +
        "</Project>";
}
