// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.AppVeyor;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.CI.TeamCity;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Tools.Slack;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.ControlFlow;
using static Nuke.Common.Gitter.GitterTasks;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Nuke.Common.Tools.Slack.SlackTasks;

[CheckBuildProjectConfigurations]
[DotNetVerbosityMapping]
[UnsetVisualStudioEnvironmentVariables]
[TeamCitySetDotCoverHomePath]
[TeamCity(
    TeamCityAgentPlatform.Windows,
    DefaultBranch = DevelopBranch,
    VcsTriggeredTargets = new[] { nameof(Pack), nameof(Test) },
    NightlyTriggeredTargets = new[] { nameof(Pack), nameof(Test) },
    ManuallyTriggeredTargets = new[] { nameof(Publish) },
    NonEntryTargets = new[] { nameof(Restore) },
    ExcludedTargets = new[] { nameof(Clean) })]
[GitHubActions(
    "continuous",
    GitHubActionsImage.MacOs1014,
    GitHubActionsImage.Ubuntu1604,
    GitHubActionsImage.Ubuntu1804,
    GitHubActionsImage.WindowsServer2016R2,
    GitHubActionsImage.WindowsServer2019,
    On = new[] { GitHubActionsTrigger.Push },
    InvokedTargets = new[] { nameof(Test), nameof(Pack) },
    ImportGitHubTokenAs = nameof(GitHubToken),
    ImportSecrets = new[] { nameof(SlackWebhook), nameof(GitterAuthToken) })]
[AppVeyor(
    AppVeyorImage.VisualStudio2019,
    AppVeyorImage.Ubuntu1804,
    SkipTags = true,
    InvokedTargets = new[] { nameof(Test), nameof(Pack) })]
[AzurePipelines(
    AzurePipelinesImage.UbuntuLatest,
    AzurePipelinesImage.WindowsLatest,
    AzurePipelinesImage.MacOsLatest,
    InvokedTargets = new[] { nameof(Test), nameof(Pack) },
    NonEntryTargets = new[] { nameof(Restore) },
    ExcludedTargets = new[] { nameof(Clean), nameof(Coverage) })]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Pack);

    [BuildServer] readonly TeamCity TeamCity;
    [BuildServer] readonly AzurePipelines AzurePipelines;

    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;
    [Solution] readonly Solution Solution;

    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath SourceDirectory => RootDirectory / "source";

    const string MasterBranch = "master";
    const string DevelopBranch = "develop";
    const string ReleaseBranchPrefix = "release";
    const string HotfixBranchPrefix = "hotfix";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("*/bin", "*/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    [Parameter] bool IgnoreFailedSources;

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetProjectFile(Solution)
                .SetIgnoreFailedSources(IgnoreFailedSources));
        });

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    Project GlobalToolProject => Solution.GetProject("Nuke.GlobalTool");
    Project MSBuildTaskRunnerProject => Solution.GetProject("Nuke.MSBuildTaskRunner");

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetNoRestore(ExecutingTargets.Contains(Restore))
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion));

            DotNetPublish(_ => _
                    .SetNoRestore(ExecutingTargets.Contains(Restore))
                    .SetConfiguration(Configuration)
                    .SetAssemblyVersion(GitVersion.AssemblySemVer)
                    .SetFileVersion(GitVersion.AssemblySemFileVer)
                    .SetInformationalVersion(GitVersion.InformationalVersion)
                    .CombineWith(
                        from project in new[] { GlobalToolProject, MSBuildTaskRunnerProject }
                        from framework in project.GetTargetFrameworks()
                        select new { project, framework }, (_, v) => _
                            .SetProject(v.project)
                            .SetFramework(v.framework)),
                degreeOfParallelism: 10);
        });

    string ChangelogFile => RootDirectory / "CHANGELOG.md";

    IEnumerable<string> ChangelogSectionNotes => ExtractChangelogSectionNotes(ChangelogFile);

    Target Pack => _ => _
        .DependsOn(Compile)
        .Produces(OutputDirectory / "*.nupkg")
        .Executes(() =>
        {
            DotNetPack(_ => _
                .SetProject(Solution)
                .SetNoBuild(ExecutingTargets.Contains(Compile))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(OutputDirectory)
                .SetVersion(GitVersion.NuGetVersionV2)
                .SetPackageReleaseNotes(GetNuGetReleaseNotes(ChangelogFile, GitRepository)));
        });

    [Partition(2)] readonly Partition TestPartition;

    Target Test => _ => _
        .DependsOn(Compile)
        .Produces(OutputDirectory / "*.trx")
        .Produces(OutputDirectory / "*.xml")
        .Partition(() => TestPartition)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetConfiguration(Configuration)
                .SetNoBuild(ExecutingTargets.Contains(Compile))
                .ResetVerbosity()
                .SetResultsDirectory(OutputDirectory)
                .When(InvokedTargets.Contains(Coverage), _ => _
                    .SetProperty("CollectCoverage", propertyValue: true)
                    .SetProperty("CoverletOutputFormat", "teamcity%2ccobertura")
                    .SetProperty("ExcludeByFile", "*.Generated.cs")
                    .When(IsServerBuild, _ => _
                        .SetProperty("UseSourceLink", propertyValue: true)))
                .CombineWith(
                    TestPartition.GetCurrent(Solution.GetProjects("*.Tests")), (_, v) => _
                        .SetProjectFile(v)
                        .SetLogger($"trx;LogFileName={v.Name}.trx")
                        .When(InvokedTargets.Contains(Coverage), _ => _
                            .SetProperty("CoverletOutput", OutputDirectory / $"{v.Name}.xml"))));

            OutputDirectory.GlobFiles("*.trx")
                .ForEach(x => AzurePipelines?.PublishTestResults(
                    new[] { x.ToString() },
                    $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.StageDisplayName})"));
        });

    string CoverageReportDirectory => OutputDirectory / "coverage-report";
    string CoverageReportArchive => OutputDirectory / "coverage-report.zip";

    Target Coverage => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {
            ReportGenerator(_ => _
                .SetReports(OutputDirectory / "*.xml")
                .SetReportTypes(ReportTypes.HtmlInline)
                .SetTargetDirectory(CoverageReportDirectory));

            CompressZip(
                directory: CoverageReportDirectory,
                archiveFile: CoverageReportArchive,
                fileMode: FileMode.Create);
        });

    Target Analysis => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            InspectCode(_ => _
                .SetTargetPath(Solution)
                .SetOutput(OutputDirectory / "inspectCode.xml")
                .AddExtensions(
                    "EtherealCode.ReSpeller",
                    "PowerToys.CyclomaticComplexity",
                    "ReSharper.ImplicitNullability",
                    "ReSharper.SerializationInspections",
                    "ReSharper.XmlDocInspections"));
        });

    [Parameter("NuGet Api Key")] readonly string ApiKey;
    [Parameter("NuGet Source for Packages")] readonly string Source = "https://api.nuget.org/v3/index.json";

    [Parameter("GitHub Token")] readonly string GitHubToken;
    [Parameter("Gitter Auth Token")] readonly string GitterAuthToken;
    [Parameter("Slack Webhook")] readonly string SlackWebhook;

    Target Publish => _ => _
        .DependsOn(Clean, Test, Pack)
        .Consumes(Pack)
        .Requires(() => ApiKey)
        .Requires(() => SlackWebhook)
        .Requires(() => GitterAuthToken)
        .Requires(() => GitHasCleanWorkingCopy())
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Requires(() => GitRepository.Branch.EqualsOrdinalIgnoreCase(MasterBranch) ||
                        GitRepository.Branch.EqualsOrdinalIgnoreCase(DevelopBranch) ||
                        GitRepository.Branch.StartsWithOrdinalIgnoreCase(ReleaseBranchPrefix) ||
                        GitRepository.Branch.StartsWithOrdinalIgnoreCase(HotfixBranchPrefix))
        .Executes(() =>
        {
            var packages = OutputDirectory.GlobFiles("*.nupkg");
            Assert(packages.Count == 4, "packages.Count == 4");

            DotNetNuGetPush(_ => _
                    .SetSource(Source)
                    .SetApiKey(ApiKey)
                    .CombineWith(
                        packages, (_, v) => _
                            .SetTargetPath(v)),
                degreeOfParallelism: 5,
                completeOnFailure: true);
        });

    Target Announce => _ => _
        .TriggeredBy(Publish)
        .AssuredAfterFailure()
        .OnlyWhenStatic(() => GitRepository.IsOnMasterBranch())
        .Executes(() =>
        {
            SendSlackMessage(_ => _
                    .SetText(new StringBuilder()
                        .AppendLine($"<!here> :mega::shipit: *NUKE {GitVersion.SemVer} IS OUT!!!*")
                        .AppendLine()
                        .AppendLine(ChangelogSectionNotes.Select(x => x.Replace("- ", "• ")).JoinNewLine()).ToString()),
                SlackWebhook);

            SendGitterMessage(new StringBuilder()
                    .AppendLine($"@/all :mega::shipit: **NUKE {GitVersion.SemVer} IS OUT!!!**")
                    .AppendLine()
                    .AppendLine(ChangelogSectionNotes.Select(x => x.Replace("- ", "* ")).JoinNewLine()).ToString(),
                "593f3dadd73408ce4f66db89",
                GitterAuthToken);
        });

    Target Install => _ => _
        .DependsOn(Pack)
        .Executes(() =>
        {
            SuppressErrors(() => DotNet($"tool uninstall -g {GlobalToolProject.Name}"));
            DotNet($"tool install -g {GlobalToolProject.Name} --add-source {OutputDirectory} --version {GitVersion.NuGetVersionV2}");
        });
}
