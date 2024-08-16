using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using NConsole;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Dnt.Commands.Packages
{
    [Command(Name = "convert-assemblies-to-packages",
        Description = "Convert assembly references to NuGet packages.")]
    public class ConvertAssemblyReferencesToPackagesCommand : ProjectCommandBase
    {
        [Argument(Position = 1, IsRequired = false)]
        public string ReferencePattern { get; set; } = null;

        [Argument(Name = nameof(IncludePrerelease), IsRequired = false)]
        public bool IncludePrerelease { get; set; } = false;

        [Argument(Name = nameof(IncludeWithoutHintPath), IsRequired = false)]
        public bool IncludeWithoutHintPath { get; set; } = false;

        private Regex _referencePattern = null;

        public override async Task<object> RunAsync(CommandLineProcessor processor, IConsoleHost host)
        {
            var globalProperties = TryGetGlobalProperties();
            if (!string.IsNullOrWhiteSpace(ReferencePattern))
            {
                _referencePattern = new Regex(ReferencePattern, RegexOptions.Compiled);
            }

            // get nuget config & different feeds available, incl. local ones
            var nugetSettings = NuGet.Configuration.Settings.LoadDefaultSettings(null);
            var packageSourceProvider = new PackageSourceProvider(nugetSettings);
            var sourceRepositoryProvider =
                new SourceRepositoryProvider(packageSourceProvider, Repository.Provider.GetCoreV3());
            var repositories = sourceRepositoryProvider.GetRepositories().ToList();

            await Task.WhenAll(GetProjectPaths().Select(projectPath =>
                ReplaceAssembliesWithPackageReference(projectPath, globalProperties, repositories, host)));
            return null;
        }

        private async Task ReplaceAssembliesWithPackageReference(string projectPath,
            IDictionary<string, string> globalProperties,
            List<SourceRepository> repositories, IConsoleHost host)
        {
            using (var projectInformation = ProjectExtensions.LoadProject(projectPath, globalProperties))
            {
                // get all project items that are a <Reference> and, if IncludeWithoutHintPath is false,
                // those that have a direct hint path to a DLL
                var tmpQuery = projectInformation.Project.Items
                    .Where(i =>
                        i.ItemType == "Reference" && (IncludeWithoutHintPath || i.DirectMetadata.Any(m =>
                            m.Name == "HintPath" && m.EvaluatedValue.EndsWith(".dll"))));

                if (_referencePattern != null)
                {
                    // ReferencePattern is a regex, might be simpler/faster to make it basic and only allow `*` etc.
                    tmpQuery = tmpQuery.Where(i => _referencePattern.IsMatch(i.EvaluatedInclude));
                }

                var assemblyReferences = tmpQuery.ToList();
                foreach (var assemblyReference in assemblyReferences)
                {
                    await ProcessAssemblyReference(repositories, assemblyReference,
                        projectInformation, host); // convert this into Task.WhenAll?
                    // if I do that then I need to add concurrent lists for
                    // adding and removing items from project
                }

                ProjectExtensions.SaveWithLineEndings(projectInformation);
            }
        }

        private async Task ProcessAssemblyReference(List<SourceRepository> repositories, ProjectItem assemblyReference,
            ProjectInformation projectInformation, IConsoleHost host)
        {
            var potentialPackageName = GetPotentialPackageName(assemblyReference);
            IPackageSearchMetadata package = null;
            foreach (var sourceRepository in repositories)
            {
                package = await SearchForPackage(sourceRepository, potentialPackageName);
                if (package != null) break;
            }

            if (package is null) return;
            var versions = (await package.GetVersionsAsync()).ToList();
            if (!versions.Any())
                host.WriteError(
                    $"Failed to retrieve any versions for {potentialPackageName}. The returned collection is empty.");

            projectInformation.Project.AddItem("PackageReference", package.Title,
                new[] { new KeyValuePair<string, string>("Version", versions[0].Version.ToString()) });
            projectInformation.Project.RemoveItem(assemblyReference);
        }

        private static string GetPotentialPackageName(ProjectItem assemblyReference)
        {
            return !assemblyReference.EvaluatedInclude.Contains(',')
                ? assemblyReference.EvaluatedInclude
                : assemblyReference.EvaluatedInclude.Split(',')[0];
        }

        private async Task<IPackageSearchMetadata> SearchForPackage(SourceRepository sourceRepository,
            string potentialPackageName)
        {
            var sourceResource = await sourceRepository.GetResourceAsync<PackageSearchResource>();
            return (await sourceResource.SearchAsync(potentialPackageName,
                    new SearchFilter(includePrerelease: IncludePrerelease), 0, 10, NullLogger.Instance,
                    CancellationToken.None))
                .FirstOrDefault(p => p.Title == potentialPackageName);
        }
    }
}