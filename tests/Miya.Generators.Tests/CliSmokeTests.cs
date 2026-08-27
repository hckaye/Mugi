using System.Diagnostics;

namespace Miya.Generators.Tests;

public sealed class CliSmokeTests
{
    [Fact]
    public async Task Generated_files_build_and_run_without_generator_reference()
    {
        var root = FindRepositoryRoot();
        var fixture = Path.Combine(root, "tests", "Miya.Generators.Tests", "fixtures", "CliSmoke", "CliSmoke.csproj");
        var output = Path.Combine(Path.GetTempPath(), "miya-gen-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var restore = await Run(root, "restore", fixture);
            Assert.Equal(0, restore.ExitCode);

            var staleFile = Path.Combine(output, "Miya.Stale.g.cs");
            await File.WriteAllTextAsync(staleFile, "// stale");
            var generate = await Run(
                root,
                "run", "--project", Path.Combine(root, "src", "Miya.Gen", "Miya.Gen.csproj"), "--",
                "--project", fixture, "--output", output);
            Assert.Equal(0, generate.ExitCode);
            Assert.Contains("Miya.JsonCodecs.g.cs", generate.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Miya.RouteTemplates.g.cs", generate.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Miya.Interceptors.g.cs", generate.StandardOutput, StringComparison.Ordinal);
            Assert.False(File.Exists(staleFile));

            var execute = await Run(root, "run", "--project", fixture, "-p:GeneratedOutput=" + output);
            Assert.Equal(0, execute.ExitCode);
            Assert.Contains("{\"name\":\"cli\",\"count\":4}", execute.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("cli:4", execute.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Fails_loudly_when_the_project_does_not_compile()
    {
        var root = FindRepositoryRoot();
        var projectDirectory = Path.Combine(Path.GetTempPath(), "miya-gen-broken-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(projectDirectory, "generated");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(output);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Broken.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Program.cs"),
                "MissingType.Method();");

            var generate = await RunAllowingFailure(
                root,
                "run", "--project", Path.Combine(root, "src", "Miya.Gen", "Miya.Gen.csproj"), "--",
                "--project", Path.Combine(projectDirectory, "Broken.csproj"), "--output", output);

            Assert.NotEqual(0, generate.ExitCode);
            Assert.Contains("miya-gen:", generate.StandardError, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(output));
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static async Task<ProcessResult> Run(string workingDirectory, params string[] arguments)
    {
        var result = await RunAllowingFailure(workingDirectory, arguments);
        Assert.True(result.ExitCode == 0, result.StandardOutput + Environment.NewLine + result.StandardError);
        return result;
    }

    private static async Task<ProcessResult> RunAllowingFailure(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Miya.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Miya.slnx was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
