namespace MsBuildMcp.Engine;

public sealed class ProjectSnapshot
{
    public required string FullPath { get; init; }
    public required string Configuration { get; init; }
    public required string Platform { get; init; }
    public required Dictionary<string, string> Properties { get; init; }
    public required Dictionary<string, string> AllProperties { get; init; }
    public required Dictionary<string, List<string>> Items { get; init; }
    public required Dictionary<string, Dictionary<string, string>> ItemDefinitions { get; init; }
    public required List<ProjectReference> ProjectReferences { get; init; }
    public required List<PackageReference> PackageReferences { get; init; }
    public required List<ProjectImport> Imports { get; init; }
}

public sealed class ProjectReference
{
    public required string Include { get; init; }
    public required string FullPath { get; init; }
    public required string Name { get; init; }
}

public sealed class PackageReference
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}

public sealed class ProjectImport
{
    public required string ImportedProject { get; init; }
    public required string ImportingElement { get; init; }
    public required string Condition { get; init; }
    public required bool IsImported { get; init; }
}
