// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Dependencies
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Polly;
    using VirtualClient.Common;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// Provides functionality for downloading and installing Yum packages
    /// on the system.
    /// </summary>
    [SupportedPlatforms("linux-arm64,linux-x64")]
    public class YumPackageInstallation : VirtualClientComponent
    {
        private const string YumCommand = "yum";
        private ISystemManagement systemManagement;

        /// <summary>
        /// Initializes a new instance of the <see cref="YumPackageInstallation"/> class.
        /// </summary>
        /// <param name="dependencies">Provides all of the required dependencies to the Virtual Client component.</param>
        /// <param name="parameters">
        /// Parameters defined in the execution profile or supplied to the Virtual Client on the command line.
        /// </param>
        public YumPackageInstallation(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters = null)
            : base(dependencies, parameters)
        {
            this.systemManagement = this.Dependencies.GetService<ISystemManagement>();
        }

        /// <summary>
        /// The retry policy to apply to package install for handling transient errors.
        /// </summary>
        public IAsyncPolicy InstallRetryPolicy { get; set; } = Policy
            .Handle<WorkloadException>(exc => exc.Reason == ErrorReason.DependencyInstallationFailed)
            .WaitAndRetryAsync(5, (retries) => TimeSpan.FromSeconds(retries * 2));

        /// <summary>
        /// The name of the Yum package to download and install from the feed.
        /// </summary>
        public string Packages
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(YumPackageInstallation.Packages), string.Empty).Trim();
            }

            set
            {
                this.Parameters[nameof(YumPackageInstallation.Packages)] = value;
            }
        }

        /// <summary>
        /// Repository to add, if not in the default sources.list.d
        /// It could only be add one by one. And could look like this: Yum-add-repository 'deb http://myserver/path/to/repo stable myrepo'
        /// </summary>
        public string Repositories
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(YumPackageInstallation.Repositories), string.Empty).Trim();
            }
        }

        /// <summary>
        /// The name of the Yum package to download and install from the feed.
        /// </summary>
        public bool AllowUpgrades
        {
            get
            {
                return this.Parameters.GetValue<bool>(nameof(YumPackageInstallation.AllowUpgrades), true);
            }
        }

        /// <summary>
        /// Executes the Yum package download/installation operation.
        /// </summary>
        protected override async Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            telemetryContext.AddContext("packages", this.Packages);
            telemetryContext.AddContext("allowUpgrades", this.AllowUpgrades);

            List<string> packages = this.Packages.Split(',', ';', StringSplitOptions.RemoveEmptyEntries).ToList();
            ISystemManagement systemManagement = this.Dependencies.GetService<ISystemManagement>();

            // Yum installtion only applies to Linux.
            if (this.Platform != PlatformID.Unix || packages == null || !packages.Any())
            {
                return;
            }

            if (!string.IsNullOrEmpty(this.Repositories))
            {
                List<string> repos = this.Packages.Split(',', ';').ToList();
                // Repo could only be add one by one
                foreach (string repo in repos)
                {
                    // https://www.redhat.com/sysadmin/add-yum-repository
                    await this.ExecuteCommandAsync("yum-config-manager", $"--enable {repo} -y", Environment.CurrentDirectory, telemetryContext, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Determine which packages should be installed, and which can be skipped.
            List<string> toInstall = new List<string>();
            foreach (string package in packages)
            {
                if (!this.AllowUpgrades && await this.IsPackageInstalledAsync(package, cancellationToken))
                {
                    this.Logger.LogTraceMessage($"Package '{package}' is already installed, skipping.");
                }
                else
                {
                    toInstall.Add(package);
                }
            }

            // Nothing to install.
            if (toInstall.Count == 0)
            {
                return;
            }

            string formattedArguments = $"install {string.Join(' ', toInstall)} -y --quiet{(this.AllowUpgrades ? string.Empty : " --no-upgrade")}";

            await this.InstallRetryPolicy.ExecuteAsync(async () =>
            {
                // Runs Yum update first.
                await this.ExecuteCommandAsync(YumPackageInstallation.YumCommand, $"update -y", Environment.CurrentDirectory, telemetryContext, cancellationToken)
                    .ConfigureAwait(false);

                // Runs the installation command with retries and throws if the command fails after all
                // retries are expended.
                await this.ExecuteCommandAsync(YumPackageInstallation.YumCommand, formattedArguments, Environment.CurrentDirectory, telemetryContext, cancellationToken)
                    .ConfigureAwait(false);

            }).ConfigureAwait(false);

            this.Logger.LogTraceMessage($"VirtualClient installed Yum package(s): '[{string.Join(' ', toInstall)}]'.");

            // Then, confirms that the packages were installed.
            List<string> failedPackages = toInstall.Where(package => !(this.IsPackageInstalledAsync(package, cancellationToken).GetAwaiter().GetResult())).ToList();

            if (failedPackages?.Count > 0)
            {
                throw new ProcessException(
                    $"Packages were supposedly successfully installed, but cannot be found! Packages: '{string.Join(", ", failedPackages)}'",
                    ErrorReason.DependencyInstallationFailed);
            }
        }

        private async Task<bool> IsPackageInstalledAsync(string packageName, CancellationToken cancellationToken)
        {
            ISystemManagement systemManagement = this.Dependencies.GetService<ISystemManagement>();

            using (IProcessProxy process = systemManagement.ProcessManager.CreateElevatedProcess(this.Platform, YumPackageInstallation.YumCommand, $"list {packageName}"))
            {
                this.CleanupTasks.Add(() => process.SafeKill());

                await process.StartAndWaitAsync(cancellationToken)
                       .ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested)
                {
                    await this.LogProcessDetailsAsync(process, EventContext.Persisted(), "Yum")
                        .ConfigureAwait(false);

                    process.ThrowIfErrored<DependencyException>(ProcessProxy.DefaultSuccessCodes, errorReason: ErrorReason.DependencyInstallationFailed);
                }

                return process.ExitCode == 0;
            }
        }

        private Task ExecuteCommandAsync(string pathToExe, string commandLineArguments, string workingDirectory, EventContext telemetryContext, CancellationToken cancellationToken)
        {
            EventContext relatedContext = telemetryContext.Clone();
            return this.InstallRetryPolicy.ExecuteAsync(async () =>
            {
                string output = string.Empty;
                using (IProcessProxy process = this.systemManagement.ProcessManager.CreateElevatedProcess(this.Platform, pathToExe, commandLineArguments, workingDirectory))
                {
                    this.CleanupTasks.Add(() => process.SafeKill());
                    this.LogProcessTrace(process);

                    await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await this.LogProcessDetailsAsync(process, telemetryContext, "Yum")
                            .ConfigureAwait(false);

                        process.ThrowIfErrored<DependencyException>(ProcessProxy.DefaultSuccessCodes, errorReason: ErrorReason.DependencyInstallationFailed);
                    }
                }
            });
        }
    }
}
