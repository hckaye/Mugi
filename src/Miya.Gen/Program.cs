using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Miya.Generators.Core;

return await ProgramEntry.Run(args).ConfigureAwait(false);

internal static class ProgramEntry
{
    internal static async Task<int> Run(string[] args)
    {
        if (!TryParseArguments(args, out var projectPath, out var outputPath, out var argumentError))
        {
            Console.Error.WriteLine("miya-gen: " + argumentError);
            Console.Error.WriteLine("Usage: miya-gen --project <project.csproj> --output <directory>");
            return 2;
        }

        projectPath = Path.GetFullPath(projectPath!);
        outputPath = Path.GetFullPath(outputPath!);
        if (!File.Exists(projectPath))
        {
            Console.Error.WriteLine("miya-gen: project file does not exist: " + projectPath);
            return 2;
        }

        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            using var workspace = MSBuildWorkspace.Create();
            var workspaceFailures = new List<string>();
            workspace.WorkspaceFailed += (_, eventArgs) => workspaceFailures.Add(eventArgs.Diagnostic.Message);
            var project = await workspace.OpenProjectAsync(projectPath).ConfigureAwait(false);
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation is null)
            {
                Console.Error.WriteLine("miya-gen: MSBuild did not produce a compilation for " + projectPath);
                WriteWorkspaceFailures(workspaceFailures);
                return 3;
            }

            var compilationErrors = compilation
                .GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .ToList();
            if (compilationErrors.Count != 0)
            {
                foreach (var error in compilationErrors)
                {
                    Console.Error.WriteLine(error.ToString());
                }

                WriteWorkspaceFailures(workspaceFailures);
                Console.Error.WriteLine(
                    "miya-gen: the project does not compile, so generation would be incomplete. " +
                    "Restore and build the project first (dotnet build), then re-run miya-gen.");
                return 6;
            }

            var naming = ReadNaming(projectPath, project);
            var result = GeneratorCore.Generate(
                compilation,
                new GeneratorSettings(naming, emitInterceptors: false));
            foreach (var diagnostic in result.Diagnostics.OrderBy(static item => item.Location.SourceSpan.Start))
            {
                Console.Error.WriteLine(diagnostic.ToString());
            }

            if (result.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                Console.Error.WriteLine("miya-gen: generation failed because the project contains Miya generator errors.");
                return 4;
            }

            Directory.CreateDirectory(outputPath);
            foreach (var oldFile in Directory.EnumerateFiles(
                         outputPath,
                         "Miya.*.g.cs",
                         SearchOption.TopDirectoryOnly))
            {
                File.Delete(oldFile);
            }

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            foreach (var source in result.Sources)
            {
                var destination = Path.Combine(outputPath, source.HintName);
                await File.WriteAllTextAsync(destination, source.Source, encoding).ConfigureAwait(false);
                Console.WriteLine(destination);
            }

            if (workspaceFailures.Count != 0)
            {
                WriteWorkspaceFailures(workspaceFailures);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("miya-gen: " + exception.Message);
            return 5;
        }
    }

    private static JsonNaming ReadNaming(string projectPath, Project project)
    {
        if (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.MiyaJsonNaming",
                out var analyzerNaming))
        {
            return ParseNaming(analyzerNaming);
        }

        using var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection();
        var evaluatedProject = projectCollection.LoadProject(projectPath);
        return ParseNaming(evaluatedProject.GetPropertyValue("MiyaJsonNaming"));
    }

    private static JsonNaming ParseNaming(string? naming)
    {
        return string.Equals(naming, "PascalCase", StringComparison.OrdinalIgnoreCase)
            ? JsonNaming.PascalCase
            : JsonNaming.CamelCase;
    }

    private static bool TryParseArguments(
        string[] args,
        out string? project,
        out string? output,
        out string? error)
    {
        project = null;
        output = null;
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument != "--project" && argument != "--output")
            {
                error = "unknown argument '" + argument + "'.";
                return false;
            }

            if (++index >= args.Length)
            {
                error = "argument '" + argument + "' requires a value.";
                return false;
            }

            if (argument == "--project")
            {
                if (project is not null)
                {
                    error = "--project may only be specified once.";
                    return false;
                }

                project = args[index];
            }
            else
            {
                if (output is not null)
                {
                    error = "--output may only be specified once.";
                    return false;
                }

                output = args[index];
            }
        }

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(output))
        {
            error = "both --project and --output are required.";
            return false;
        }

        return true;
    }

    private static void WriteWorkspaceFailures(IEnumerable<string> failures)
    {
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("miya-gen: MSBuild workspace: " + failure);
        }
    }
}
