using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using MSProject = Microsoft.Build.Evaluation.Project;
using Project = Models.Project;

namespace Commands;
public sealed class SolutionParserCommand : Command<SolutionParserCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("The solution file (.sln) path.")]
        [CommandArgument(0, "<SOLUTION>")]
        public required string Solution { get; init; }
    }

    record ProjectRecord(string Name, string Path);

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        var solutionFilePath = Path.GetFullPath(settings.Solution);
        string? solutionFolderPath;

        if (!solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(solutionFilePath))
        {
            solutionFolderPath = solutionFilePath;
            solutionFilePath = null;
        }
        else if (File.Exists(solutionFilePath))
        {
            solutionFolderPath = Path.GetDirectoryName(solutionFilePath);
        }
        else
        {
            Console.Error.WriteLine($"Invalid solution path \"{solutionFilePath}\"");
            return 1;
        }

        // Lookup a MSBuild instance from the installed dotnet SDKs.
        // The results should NOT be ordered: the first one matches the global.json if present.
        var msbuildInstance = MSBuildLocator
            .QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = solutionFolderPath
            })
            .FirstOrDefault();

        if (msbuildInstance is null)
        {
            Console.Error.WriteLine($"Could not find a matching .NET SDK for {solutionFolderPath}");
            return 2;
        }

        MSBuildLocator.RegisterInstance(msbuildInstance);

        ExecuteCore(solutionFilePath, solutionFolderPath);
        return 0;
    }

    private void ExecuteCore(string? solutionFilePath, string? solutionFolderPath)
    {
        IEnumerable<ProjectRecord>? projFiles;

        if (solutionFilePath is not null)
        {
            var sln = SolutionFile.Parse(solutionFilePath);
            projFiles = sln.ProjectsInOrder
                .Where(prj => prj.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
                .Select(prj => new ProjectRecord(prj.ProjectName, prj.AbsolutePath));
        }
        else if (solutionFolderPath is not null)
        {
            string[] projFileGlobs = ["*.csproj", "*.fsproj"];
            projFiles = projFileGlobs
                .SelectMany(glob => Directory.GetFiles(solutionFolderPath, glob))
                .Select(p => new ProjectRecord(Path.GetFileNameWithoutExtension(p), p));
        }
        else
        {
            throw new InvalidOperationException("Invalid solution path");
        }

        var projects = new ConcurrentBag<Project>();
        Parallel.ForEach(projFiles, proj =>
        {
            var projectDetails = GetProjectDetails(proj.Name, proj.Path);
            if (projectDetails != null)
                projects.Add(projectDetails);
        });

        var allProjects = projects.ToList();

        List<ProjectFile> designerFiles = new();

        foreach (var proj in allProjects)
        {
            proj.CoreProject?.GetItems("AvaloniaXaml").ToList().ForEach(item =>
            {
                var filePath = Path.GetFullPath(item.EvaluatedInclude, proj.DirectoryPath ?? "");
                var designerFile = new ProjectFile
                {
                    Path = filePath,
                    TargetPath = proj.TargetPath,
                    ProjectPath = proj.Path
                };
                designerFiles.Add(designerFile);
            });
        }

        var solution = solutionFilePath ?? solutionFolderPath;
        var json = new { Solution = solution, Projects = allProjects, Files = designerFiles };

        var jsonStr = JsonSerializer.Serialize(json, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var jsonFilePath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(solution)}.json");
        File.WriteAllText(jsonFilePath, jsonStr);

        Console.WriteLine(jsonStr);
    }

    static string? GetFirstFramework(MSProject proj, out bool hasMultipleFrameworks) 
    {
        hasMultipleFrameworks = false;
        var targetfx = proj.GetPropertyValue("TargetFramework");
        if (!string.IsNullOrEmpty(targetfx))
        {
            return targetfx;
        }
        var targetFrameworks = proj.GetPropertyValue("TargetFrameworks"); 
        if (string.IsNullOrEmpty(targetFrameworks))
        {
           return null; 
        }
        hasMultipleFrameworks = true;
        return targetFrameworks.Split(";").FirstOrDefault();
    }

    Project? GetProjectDetails(string name, string projPath)
    {
        try
        {
            var loadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.FailOnUnresolvedSdk;
            var proj = MSProject.FromFile(projPath, new ProjectOptions
            {
                LoadSettings = loadSettings
            });

            var targetfx = GetFirstFramework(proj, out var hasMultipleFrameworks);

            if (hasMultipleFrameworks && !string.IsNullOrEmpty(targetfx))
            {
                proj = MSProject.FromFile(projPath, new ProjectOptions {
                    LoadSettings = loadSettings,
                    GlobalProperties = new Dictionary<string, string> {
                        {"TargetFramework", targetfx}
                    }
                });
            }        

            var assembly = proj.GetPropertyValue("TargetPath");
            var outputType = proj.GetPropertyValue("outputType");
            var desingerHostPath = proj.GetPropertyValue("AvaloniaPreviewerNetCoreToolPath");

            var projectDepsFilePath = proj.GetPropertyValue("ProjectDepsFilePath");
            var projectRuntimeConfigFilePath = proj.GetPropertyValue("ProjectRuntimeConfigFilePath");

            var references = proj.GetItems("ProjectReference");
            var referencesPath = references.Select(p => Path.GetFullPath(p.EvaluatedInclude, projPath)).ToArray();
            desingerHostPath = string.IsNullOrEmpty(desingerHostPath) ? "" : Path.GetFullPath(desingerHostPath);

            var intermediateOutputPath = GetIntermediateOutputPath(proj);

            return new Project
            {
                Name = name,
                Path = projPath,
                TargetPath = assembly,
                OutputType = outputType,
                DesignerHostPath = desingerHostPath,

                TargetFramework = targetfx ?? "",
                DepsFilePath = projectDepsFilePath,
                RuntimeConfigFilePath = projectRuntimeConfigFilePath,

                CoreProject = proj,
                ProjectReferences = referencesPath,
                IntermediateOutputPath = intermediateOutputPath

            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error parsing project {name}: {ex.Message}");
            return null;
        }
    }

    static string GetIntermediateOutputPath(MSProject proj)
    {
        var intermediateOutputPath = proj.GetPropertyValue("IntermediateOutputPath");
        var iop = Path.Combine(intermediateOutputPath, "Avalonia", "references");

        if (!Path.IsPathRooted(intermediateOutputPath))
        {
            iop = Path.Combine(proj.DirectoryPath ?? "", iop);
            if (Path.DirectorySeparatorChar == '/')
                iop = iop.Replace("\\", "/");
        }

        return iop;
    }
}
