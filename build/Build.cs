using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.CI.GitHubActions.Configuration;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MinVer;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;

[GitHubActions(
    "continuous",
    GitHubActionsImage.UbuntuLatest,
    AutoGenerate = true,
    OnPushBranchesIgnore = ["main", "master"],
    InvokedTargets = [nameof(Test)],
    FetchDepth = 0)]
[GitHubActions(
        "merge",
        GitHubActionsImage.UbuntuLatest,
        AutoGenerate = true,
        OnPullRequestBranches = ["main"],
        InvokedTargets = [nameof(Publish), nameof(Pack)],
        FetchDepth = 0
        // ImportSecrets = [nameof(NuGetApiKey)])
        )]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = true)] readonly Solution Solution;
    [Parameter][Secret] readonly string NuGetApiKey;
    [GitRepository] readonly GitRepository Repository;
    [MinVer] readonly MinVer MinVer;
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ProjectDirectory => SourceDirectory / "Cli";
    AbsolutePath ArtifactsDirectory => RootDirectory / ".artifacts";
    AbsolutePath PublishDirectory => RootDirectory / "publish";
    AbsolutePath PackDirectory => RootDirectory / "packages";
    AbsolutePath TestDirectory => RootDirectory / "tests";
    IEnumerable<string> Projects => Solution.AllProjects.Select(x => x.Name);

    Target Print => _ => _
    .Before(Clean)
    .Executes(() =>
    {
        Log.Information("Minver Version = {Value}", MinVer.Version);
        Log.Information("Commit = {Value}", Repository.Commit);
        Log.Information("Branch = {Value}", Repository.Branch);
        Log.Information("Tags = {Value}", Repository.Tags);

        Log.Information("main branch = {Value}", Repository.IsOnMainBranch());
        Log.Information("main/master branch = {Value}", Repository.IsOnMainOrMasterBranch());
        Log.Information("release/* branch = {Value}", Repository.IsOnReleaseBranch());
        Log.Information("hotfix/* branch = {Value}", Repository.IsOnHotfixBranch());
        Log.Information("feature/* branch = {Value}", Repository.IsOnFeatureBranch());

        Log.Information("Https URL = {Value}", Repository.HttpsUrl);
        Log.Information("SSH URL = {Value}", Repository.SshUrl);
    });

    Target Clean => _ => _
        .Executes(() =>
        {
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
    .After(Clean)
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetForce(true)
                .SetProjectFile(Solution.Directory));
        });

    Target Compile => _ => _
        .DependsOn(Clean, Restore, Print)
        .After(Print)
        .Executes(() =>
        {
            Log.Information("Building version {Value}", MinVer.Version);
            DotNetBuild(_ => _
                .EnableNoLogo()
                .EnableNoRestore()
                .SetProjectFile(Solution.Directory)
                .SetConfiguration(Configuration)
            );
        });
    IReadOnlyCollection<Output> Outputs;
    Target Test => _ => _
        .DependsOn(Compile)
        .Before(Publish, Pack)
        .Executes(() =>
        {
            Log.Information($"RootDir: {RootDirectory}");
            Log.Information($"TestDir: {TestDirectory}");

            var ResultsDirectory = RootDirectory / "TestResults";
            ResultsDirectory.CreateOrCleanDirectory();
            Outputs = DotNetTest(_ => _
                .EnableNoLogo()
                .EnableNoBuild()
                .EnableNoRestore()
                .SetConfiguration(Configuration)
                .SetDataCollector("XPlat Code Coverage")
                .SetResultsDirectory(ResultsDirectory)
                .SetRunSetting(
                    "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByAttribute",
                     "Obsolete,GeneratedCodeAttribute,CompilerGeneratedAttribute")
                );

            Log.Information($"Outputs: {Outputs.Count}");

            var coverageReport = (RootDirectory / "TestResults").GetFiles("coverage.cobertura.xml", 2).FirstOrDefault();

            if (coverageReport is not null)
            {
                ReportGenerator(_ => _
                    .AddReports(coverageReport)
                    .SetTargetDirectory(ResultsDirectory / "coveragereport")
                );
            }
        });

    Target Pack => _ => _
        .OnlyWhenStatic(() => SolutionContainsPackableProject())
        .Requires(() => !IsLocalBuild && IsReleaseBranch)
        .WhenSkipped(DependencyBehavior.Skip)
        .After(Test)
        .DependsOn(Compile)
        .Executes(() =>
        {
            var packableProjects = Solution.GetAllProjects("*").Where(p => p.GetProperty("PackAsTool") == "true").ToList();
            var proj = packableProjects.First();
            proj.GetProperty<bool>("IsPackable");
            DotNetPack(_ => _
                .EnableNoLogo()
                .EnableNoBuild()
                .EnableNoRestore()
                .CombineWith(packableProjects, (_, p) => _
                .SetProject(p.Path)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackDirectory / MinVer.Version))
            );
        });

    private bool SolutionContainsPackableProject()
    {
        var projects = Solution.GetAllProjects("*");
        var first = projects.First();
        var packAsTool = first.GetProperty("PackAsTool");
        return projects.Any(p => p.GetProperty("PackAsTool") == "true");
    }

    Target Push => _ => _
        // .Requires(() => !IsLocalBuild)
        // .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetNuGetPush(_ => _
                .SetApiKey(NuGetApiKey)
                .SetTargetPath(PackDirectory / MinVer.Version / $"commitizen.NET.{MinVer.Version}.nupkg")
                .SetSource("https://api.nuget.org/v3/index.json")
            );
        });

    Target Publish => _ => _
        // .Requires(requirement: () => Repository.IsOnMainOrMasterBranch())
        .Requires(() => !IsLocalBuild && IsReleaseBranch)
        .WhenSkipped(DependencyBehavior.Skip)
        .DependsOn(Compile)
        .Produces(PackDirectory)
        .Executes(() =>
        {
            PublishDirectory.CreateOrCleanDirectory();

            DotNetPublish(_ => _
                .EnableNoLogo()
                .EnableNoBuild()
                .EnableNoRestore()
                .SetProject(ProjectDirectory)
                .SetConfiguration(Configuration)
                .SetOutput(PublishDirectory)
            );
            var zipFile = PackDirectory / MinVer.Version / $"{Solution.Name}.zip";
            PublishDirectory.ZipTo(zipFile, fileMode: FileMode.Create);
        });

    private bool RunFromGithubActionOnReleaseBranch => !IsLocalBuild && Repository.IsOnMainOrMasterBranch();
    private bool IsReleaseBranch => Repository.IsOnMainOrMasterBranch() || Repository.IsOnReleaseBranch();

    bool RepoIsMainOrDevelop => Repository.IsOnDevelopBranch() || Repository.IsOnMainOrMasterBranch();
}
