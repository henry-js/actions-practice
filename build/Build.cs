using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.MinVer;
using Nuke.Common.Tools.ReportGenerator;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;

[GitHubActions(
    "continuous",
    GitHubActionsImage.UbuntuLatest,
    AutoGenerate = true,
    OnPushBranchesIgnore = ["main"],
    InvokedTargets = [nameof(Test)],
    FetchDepth = 0)]
[GitHubActions(
        "merge",
        GitHubActionsImage.UbuntuLatest,
        AutoGenerate = true,
        OnPullRequestBranches = ["main"],
        InvokedTargets = [nameof(Test)],
        FetchDepth = 0)]
[GitHubActions(
            "after-merge",
            GitHubActionsImage.UbuntuLatest,
            AutoGenerate = true,
            OnPushBranches = ["main"],
            InvokedTargets = [nameof(BumpVersion), nameof(Publish)],
            FetchDepth = 0
        )]
[GitHubActions(
    "bumpversion",
    GitHubActionsImage.UbuntuLatest,
    AutoGenerate = false,
    OnPullRequestBranches = ["main"],
    FetchDepth = 0,
    InvokedTargets = [nameof(BumpVersion)]
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
    [MinVer] MinVer MinVer;
    AbsolutePath ProjectDirectory => SourceDirectory / "Cli";
    AbsolutePath ArtifactsDirectory => RootDirectory / ".artifacts";
    AbsolutePath PublishDirectory => RootDirectory / "publish";
    AbsolutePath PackDirectory => RootDirectory / "packages";
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestDirectory => RootDirectory / "tests";
    IEnumerable<string> Projects => Solution.AllProjects.Select(x => x.Name);

    Target Print => _ => _
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
        .DependsOn(Clean, Restore)
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

            var coverageReport = (RootDirectory / "TestResults").GetFiles("coverage.cobertura.xml", 2).FirstOrDefault();

            if (coverageReport is not null)
            {
                ReportGenerator(_ => _
                    .AddReports(coverageReport)
                    .SetTargetDirectory(ResultsDirectory / "coveragereport")
                );
            }
        });

    Target BumpVersion => _ => _
        .Before(Compile)
        .Executes(() =>
        {
            Log.Information("Minver FileVersion = {Value}", MinVer.FileVersion);
            Log.Information("Commit = {Value}", Repository.Commit);
            Log.Information("Branch = {Value}", Repository.Branch);
            Log.Information("Tags = {Value}", Repository.Tags);
            // GitTasks.Git("checkout main");
            string tag = "";

            MinVerTasks.MinVer("-i", logger: (outType, version) =>
            {
                if (outType == OutputType.Std)
                    tag = version;
            });
            Log.Information("Minver Last Tag Version = {Value}", tag);

            GitTasks.Git($"tag {tag} -f");
            GitTasks.Git($"push --tags -f");
            (MinVer, var output) = MinVerTasks.MinVer(_ => _);
            Log.Information("Minver Version = {Value}", MinVer.Version);
            Log.Information("Commit = {Value}", Repository.Commit);
            Log.Information("Branch = {Value}", Repository.Branch);
            Log.Information("Tags = {Value}", Repository.Tags);
        });

    Target Publish => _ => _
        .After(Test)
        .DependsOn(Compile)
        .Triggers(Pack)
        .Produces(PackDirectory)
        .Executes(() =>
        {
            PublishDirectory.CreateOrCleanDirectory();

            DotNetPublish(_ => _
                .EnableNoLogo()
                .EnableNoBuild()
                .EnableNoRestore()
                .SetProject(ProjectDirectory)
                .SetOutput(PublishDirectory)
                .SetConfiguration(Configuration)
            );

            PublishDirectory.ZipTo(PackDirectory / $"{Solution.Name}.zip", fileMode: FileMode.Create);
        });

    Target Pack => _ => _
        // .Requires(() => Repository.IsOnMainOrMasterBranch())
        .After(Test)
        .DependsOn(Compile)
        .Produces(PackDirectory)
        .OnlyWhenDynamic(() => SolutionContainsPackableProject())
        .Executes(() =>
        {
            DotNetPack(_ => _
                .EnableNoLogo()
                .EnableNoBuild()
                .EnableNoRestore()
                .SetProject(ProjectDirectory)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackDirectory / MinVer.Version)
            );
        });

    private bool SolutionContainsPackableProject()
    {
        var packableProjects = Solution.AllProjects.Count(p => p.GetProperty<bool?>("PackAsTool") ?? false);
        Log.Information("Packable projects found = {Value}", packableProjects);
        return packableProjects > 0;
    }

    Target Push => _ => _
        .Requires(() => Repository.IsOnMainOrMasterBranch())
        .DependsOn(Pack)
        .Executes(() =>
        {
            DotNetNuGetPush(_ => _
                .SetApiKey(NuGetApiKey)
                .SetTargetPath(PackDirectory / MinVer.Version / $"commitizen.NET.{MinVer.Version}.nupkg")
                .SetSource("https://api.nuget.org/v3/index.json")
            );
        });
}
