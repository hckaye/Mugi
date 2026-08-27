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
        if (args.Length != 0 && args[0] == "import")
        {
            return await RunImport(args).ConfigureAwait(false);
        }

        if (!TryParseArguments(args, out var options, out var argumentError))
        {
            Console.Error.WriteLine("miya-gen: " + argumentError);
            WriteUsage(args.Length != 0 && args[0] == "openapi");
            return 2;
        }

        var projectPath = Path.GetFullPath(options!.ProjectPath);
        var outputPath = Path.GetFullPath(options.OutputPath);
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

            var projectSettings = ReadProjectSettings(projectPath, project);
            if (options.Command == Command.OpenApi)
            {
                var document = OpenApiDocumentBuilder.Build(
                    compilation,
                    new OpenApiSettings(
                        projectSettings.ProjectName,
                        projectSettings.Version,
                        projectSettings.Naming));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(
                    outputPath,
                    document,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
                Console.WriteLine(outputPath);
            }
            else
            {
                var result = GeneratorCore.Generate(
                    compilation,
                    new GeneratorSettings(projectSettings.Naming, emitInterceptors: false));
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

    private static async Task<int> RunImport(string[] args)
    {
        if (!TryParseImportArguments(args, out var input, out var output, out var importNamespace, out var error))
        {
            Console.Error.WriteLine("miya-gen: " + error);
            Console.Error.WriteLine(
                "Usage: miya-gen import --input <openapi.json> --output <directory> [--namespace <namespace>]");
            return 2;
        }

        var inputPath = Path.GetFullPath(input!);
        var outputPath = Path.GetFullPath(output!);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine("miya-gen: OpenAPI document does not exist: " + inputPath);
            return 2;
        }

        try
        {
            var content = await File.ReadAllTextAsync(inputPath).ConfigureAwait(false);
            var result = OpenApiImportGenerator.Generate(
                new OpenApiImportInput(
                    inputPath,
                    content,
                    string.IsNullOrWhiteSpace(importNamespace) ? "Generated" : importNamespace!,
                    JsonNaming.CamelCase),
                CancellationToken.None);

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic.Severity + " " + diagnostic.Id + ": " + diagnostic.GetMessage());
            }

            if (result.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                Console.Error.WriteLine("miya-gen: import failed because the OpenAPI document has errors.");
                return 4;
            }

            Directory.CreateDirectory(outputPath);
            foreach (var oldFile in Directory.EnumerateFiles(
                         outputPath,
                         "Miya.OpenApi.*.g.cs",
                         SearchOption.TopDirectoryOnly))
            {
                File.Delete(oldFile);
            }

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));
            var sources = result.Sources;
            for (var index = 0; index < sources.Length; index++)
            {
                var suffix = sources.Length == 1 ? string.Empty : "." + index;
                var destination = Path.Combine(outputPath, "Miya.OpenApi." + baseName + suffix + ".g.cs");
                await File.WriteAllTextAsync(destination, sources[index].Source, encoding).ConfigureAwait(false);
                Console.WriteLine(destination);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("miya-gen: " + exception.Message);
            return 5;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.Length == 0 ? "openapi" : builder.ToString();
    }

    private static bool TryParseImportArguments(
        string[] args,
        out string? input,
        out string? output,
        out string? importNamespace,
        out string? error)
    {
        input = null;
        output = null;
        importNamespace = null;
        error = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument != "--input" && argument != "--output" && argument != "--namespace")
            {
                error = "unknown argument '" + argument + "'.";
                return false;
            }

            if (++index >= args.Length)
            {
                error = "argument '" + argument + "' requires a value.";
                return false;
            }

            switch (argument)
            {
                case "--input":
                    input = args[index];
                    break;
                case "--output":
                    output = args[index];
                    break;
                default:
                    importNamespace = args[index];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            error = "both --input and --output are required.";
            return false;
        }

        return true;
    }

    private static ProjectSettings ReadProjectSettings(string projectPath, Project project)
    {
        var naming = JsonNaming.CamelCase;
        if (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.MiyaJsonNaming",
                out var analyzerNaming))
        {
            naming = ParseNaming(analyzerNaming);
        }

        using var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection();
        var evaluatedProject = projectCollection.LoadProject(projectPath);
        if (string.IsNullOrWhiteSpace(analyzerNaming))
        {
            naming = ParseNaming(evaluatedProject.GetPropertyValue("MiyaJsonNaming"));
        }

        var projectName = evaluatedProject.GetPropertyValue("MSBuildProjectName");
        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = project.Name;
        }

        var version = evaluatedProject.GetPropertyValue("PackageVersion");
        if (string.IsNullOrWhiteSpace(version))
        {
            version = evaluatedProject.GetPropertyValue("Version");
        }

        return new ProjectSettings(projectName, version, naming);
    }

    private static JsonNaming ParseNaming(string? naming)
    {
        return string.Equals(naming, "PascalCase", StringComparison.OrdinalIgnoreCase)
            ? JsonNaming.PascalCase
            : JsonNaming.CamelCase;
    }

    private static bool TryParseArguments(
        string[] args,
        out CommandOptions? options,
        out string? error)
    {
        options = null;
        error = null;
        var command = Command.Generate;
        var startIndex = 0;
        if (args.Length != 0 && args[0] == "openapi")
        {
            command = Command.OpenApi;
            startIndex = 1;
        }

        string? project = null;
        string? output = null;
        for (var index = startIndex; index < args.Length; index++)
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

        options = new CommandOptions(command, project, output);
        return true;
    }

    private static void WriteUsage(bool openApi)
    {
        Console.Error.WriteLine(openApi
            ? "Usage: miya-gen openapi --project <project.csproj> --output <openapi.json>"
            : "Usage: miya-gen --project <project.csproj> --output <directory>");
    }

    private static void WriteWorkspaceFailures(IEnumerable<string> failures)
    {
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("miya-gen: MSBuild workspace: " + failure);
        }
    }

    private enum Command
    {
        Generate,
        OpenApi,
    }

    private sealed record CommandOptions(Command Command, string ProjectPath, string OutputPath);

    private sealed record ProjectSettings(string ProjectName, string Version, JsonNaming Naming);
}
