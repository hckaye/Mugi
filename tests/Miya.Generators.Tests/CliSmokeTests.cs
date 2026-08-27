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

    private static async Task<ProcessResult> Run(string workingDirectory, params string[] arguments)
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
        var result = new ProcessResult(process.ExitCode, await output, await error);
        Assert.True(result.ExitCode == 0, result.StandardOutput + Environment.NewLine + result.StandardError);
        return result;
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
