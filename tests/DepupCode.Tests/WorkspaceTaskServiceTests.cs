using DepupCode.Services;
using Xunit;

namespace DepupCode.Tests;

public sealed class WorkspaceTaskServiceTests
{
    [Fact]
    public void Starter_configuration_has_a_default_build_task()
    {
        var root = CreateWorkspace();
        try
        {
            WriteTasks(root, WorkspaceTaskService.CreateStarterConfig());
            var service = new WorkspaceTaskService(root);

            Assert.Equal("Build workspace with .NET", service.DefaultBuildTaskLabel);
            Assert.Contains(service.Tasks, task => task.Label == service.DefaultBuildTaskLabel && task.Type == "process");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Loads_parallel_dependencies_and_safe_single_line_problem_matcher()
    {
        var root = CreateWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Program.cs"), "class Program {}\n");
            WriteTasks(root, """
                {
                  "version": "2.0.0",
                  "tasks": [
                    { "label": "lint", "type": "process", "command": "node", "args": ["lint.js"] },
                    { "label": "types", "type": "process", "command": "tsc", "args": ["--noEmit"] },
                    {
                      "label": "build", "type": "process", "command": "dotnet", "args": ["build"],
                      "dependsOn": ["lint", "types"], "dependsOrder": "parallel",
                      "group": { "kind": "build", "isDefault": true },
                      "problemMatcher": {
                        "fileLocation": ["relative", "${workspaceFolder}/src"],
                        "pattern": {
                          "regexp": "^(.*)\\((\\d+),(\\d+)\\): (error|warning) ([A-Z]+\\d+): (.*)$",
                          "file": 1, "line": 2, "column": 3, "severity": 4, "code": 5, "message": 6
                        }
                      }
                    }
                  ]
                }
                """);

            var service = new WorkspaceTaskService(root);
            var build = Assert.Single(service.Tasks, task => task.Label == "build");

            Assert.Equal("build", service.DefaultBuildTaskLabel);
            Assert.True(build.ParallelDependencies);
            Assert.Equal(new[] { "lint", "types" }, build.Dependencies);
            var matcher = Assert.Single(build.ProblemPatterns);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "src")), matcher.FileBaseDirectory);
            var match = matcher.Expression.Match("Program.cs(14,7): error CS1002: ; expected");
            Assert.True(match.Success);
            Assert.Equal("Program.cs", match.Groups[matcher.FileGroup].Value);
            Assert.Equal("14", match.Groups[matcher.LineGroup].Value);
            Assert.Equal("7", match.Groups[matcher.ColumnGroup].Value);
            Assert.Equal("error", match.Groups[matcher.SeverityGroup].Value);
            Assert.Equal("CS1002", match.Groups[matcher.CodeGroup].Value);
            Assert.Equal("; expected", match.Groups[matcher.MessageGroup].Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_problem_matcher_does_not_hide_valid_tasks()
    {
        var root = CreateWorkspace();
        try
        {
            WriteTasks(root, """
                { "tasks": [
                  { "label": "build", "type": "process", "command": "dotnet",
                    "group": { "kind": "build", "isDefault": true },
                    "problemMatcher": { "pattern": { "regexp": "[", "file": 1, "line": 2, "message": 3 } }
                  }
                ] }
                """);

            var service = new WorkspaceTaskService(root);
            var build = Assert.Single(service.Tasks);

            Assert.Equal("build", service.DefaultBuildTaskLabel);
            Assert.Empty(build.ProblemPatterns);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveExecutable_accepts_an_existing_absolute_executable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        Assert.Equal(Path.GetFullPath(executable), LanguageServerService.ResolveExecutable(Path.GetTempPath(), executable));
    }

    [Fact]
    public void Sandbox_environment_validation_accepts_windows_builtin_names()
    {
        Assert.True(WindowsExtensionSandbox.IsValidEnvironmentName("ProgramFiles(x86)"));
        Assert.True(WindowsExtensionSandbox.IsValidEnvironmentName("CommonProgramFiles(x86)"));
        Assert.False(WindowsExtensionSandbox.IsValidEnvironmentName("BAD=NAME"));
        Assert.False(WindowsExtensionSandbox.IsValidEnvironmentName("BAD\0NAME"));
        Assert.False(WindowsExtensionSandbox.IsValidEnvironmentValue("bad\0value"));
    }

    private static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "DepupCode.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteTasks(string root, string contents)
    {
        var config = Path.Combine(root, ".vscode", "tasks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, contents);
    }
}
