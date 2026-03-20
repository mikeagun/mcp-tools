namespace MsBuildMcp.Engine;

public sealed class SolutionInfo
{
    public required string Path { get; init; }
    public required string Directory { get; init; }
    public required List<SolutionProject> Projects { get; init; }
    public required List<SolutionConfig> Configurations { get; init; }
}

public sealed class SolutionProject
{
    public required string TypeGuid { get; init; }
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string Guid { get; init; }
    public required bool IsSolutionFolder { get; init; }
    public string SolutionFolder { get; set; } = "";
    public List<string> SolutionDependencies { get; set; } = [];
}

public sealed class SolutionConfig
{
    public required string Configuration { get; init; }
    public required string Platform { get; init; }
}
