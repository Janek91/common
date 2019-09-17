// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Linq;
using Nuke.Common.Utilities;

namespace Nuke.Common.CI.GitHubActions.Configuration
{
    public abstract class GitHubActionsConfigurationEntity
    {
        public abstract void Write(CustomFileWriter writer);

        protected static string ConvertToString(GitHubActionsOn githubActionsOn)
        {
            return githubActionsOn == GitHubActionsOn.Push
                ? "push:"
                : "pull_request:";
        }

        protected static string ConvertToString(GitHubActionsVirtualEnvironments environment)
        {
            return environment switch
            {
                GitHubActionsVirtualEnvironments.WindowsServer2019 => "windows-2019",
                GitHubActionsVirtualEnvironments.WindowsServer2016R2 => "windows-2016",
                GitHubActionsVirtualEnvironments.Ubuntu1804 => "ubuntu-18.04",
                GitHubActionsVirtualEnvironments.Ubuntu1604 => "ubuntu-16.04",
                GitHubActionsVirtualEnvironments.MacOs1014 => "macOS-10.14",
                _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, message: null)
            };
        }
    }
}
