using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using NetPad.Common;
using NetPad.Utilities;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using INugetLogger = NuGet.Common.ILogger;
using NuGetNullLogger = NuGet.Common.NullLogger;
using Settings = NetPad.Configuration.Settings;

namespace NetPad.Packages;

public class NuGetPackageProvider : IPackageProvider
{
    private readonly Settings _settings;
    private readonly ILogger<NuGetPackageProvider> _logger;
    private readonly NuGetFramework _nuGetFramework;
    private const string NUGET_API_URI = "https://api.nuget.org/v3/index.json";

    public NuGetPackageProvider(Settings settings, ILogger<NuGetPackageProvider> logger)
    {
        _settings = settings;
        _logger = logger;
        _nuGetFramework = NuGetFramework.ParseFolder("net6.0");

        // hostDependencyContext = DependencyContext.Load(hostAssembly);
        // FrameworkName = hostDependencyContext.Target.Framework;
        // TargetFramework = NuGetFramework.ParseFrameworkName(FrameworkName, DefaultFrameworkNameProvider.Instance);
    }

    public async Task<CachedPackage[]> GetCachedPackagesAsync(bool loadMetadata = false)
    {
        var nuGetCacheDir = new DirectoryInfo(GetNuGetCacheDirectoryPath());

        return await GetCachedPackagesAsync(nuGetCacheDir.GetDirectories(), loadMetadata);
    }

    public async Task<CachedPackage[]> GetExplicitlyInstalledCachedPackagesAsync(bool loadMetadata = false)
    {
        var nuGetCacheDir = new DirectoryInfo(GetNuGetCacheDirectoryPath());

        var packageDirectories = nuGetCacheDir.GetDirectories()
            .Where(d => GetInstallInfo(d.FullName)?.InstallReason == PackageInstallReason.Explicit);

        return await GetCachedPackagesAsync(packageDirectories, loadMetadata);
    }

    private async Task<CachedPackage[]> GetCachedPackagesAsync(IEnumerable<DirectoryInfo> packageDirectories, bool loadMetadata = false)
    {
        var cachedPackages = new List<CachedPackage>();

        foreach (var packageDir in packageDirectories)
        {
            try
            {
                using var  packageReader = new PackageFolderReader(packageDir);
                var nuspecReader = packageReader.NuspecReader;
                var installInfo = GetInstallInfo(packageDir.FullName)!;

                var cachedPackage = new CachedPackage
                {
                    InstallReason = installInfo.InstallReason,
                    DirectoryPath = packageDir.FullName,
                    PackageId = nuspecReader.GetId(),
                    Version = nuspecReader.GetVersion().ToString(),
                    Title = nuspecReader.GetTitle().DefaultIfNullOrWhitespace(nuspecReader.GetId()),
                    Authors = nuspecReader.GetAuthors(),
                    Description = nuspecReader.GetDescription(),
                    IconUrl = StringUtils.ToUriOrDefault(nuspecReader.GetIconUrl()), // GetIcon()
                    ProjectUrl = StringUtils.ToUriOrDefault(nuspecReader.GetProjectUrl()),
                    PackageDetailsUrl = null, // Does not exist in nuspec file
                    LicenseUrl = StringUtils.ToUriOrDefault(nuspecReader.GetLicenseUrl()),
                    ReadmeUrl = StringUtils.ToUriOrDefault(nuspecReader.GetReadme()),
                    ReportAbuseUrl = null,
                    RequireLicenseAcceptance = nuspecReader.GetRequireLicenseAcceptance(),
                    Dependencies = nuspecReader.GetDependencyGroups().Select(dg =>
                            $"{dg.TargetFramework}\n{dg.Packages.Select(p => $"{p.Id} {p.VersionRange}").JoinToString("\n")}")
                        .ToArray(),
                    DownloadCount = null,
                    PublishedDate = null
                };

                cachedPackages.Add(cachedPackage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not gather info about package located in: {PackageDir}", packageDir.FullName);
            }
        }

        if (loadMetadata)
        {
            await HydrateMetadataAsync(cachedPackages);
        }

        return cachedPackages.ToArray();
    }

    public Task PurgePackageCacheAsync()
    {
        var nuGetCacheDir = new DirectoryInfo(GetNuGetCacheDirectoryPath());

        foreach (var directory in nuGetCacheDir.GetDirectories())
        {
            directory.Delete(true);
        }

        return Task.CompletedTask;
    }

    public async Task<HashSet<string>> GetCachedPackageAssembliesAsync(string packageId, string packageVersion)
    {
        var packageIdentity = new PackageIdentity(packageId, new NuGetVersion(packageVersion));
        return (await GetLibItemsAsync(packageIdentity, _nuGetFramework))
            .Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToHashSet();
    }

    public async Task<string[]> GetPackageVersionsAsync(string packageId)
    {
        using var sourceCacheContext = new SourceCacheContext();

        foreach (var repository in GetSourceRepositoryProvider().GetRepositories())
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
            var versions = await resource.GetAllVersionsAsync(
                packageId,
                sourceCacheContext,
                NuGetNullLogger.Instance,
                CancellationToken.None);

            if (versions.Any())
            {
                return versions.Select(v => v.ToString()).ToArray();
            }
        }

        return Array.Empty<string>();
    }

    public async Task<HashSet<string>> GetPackageAndDependanciesAssembliesAsync(string packageId, string packageVersion)
    {
        var packageIdentity = new PackageIdentity(packageId, new NuGetVersion(packageVersion));

        // Call install to make sure package and all its dependencies are installed if they aren't already
        await InstallPackageAsync(packageId, packageVersion);

        using var sourceCacheContext = new SourceCacheContext();
        var logger = new NuGetNullLogger();
        var cancellationToken = CancellationToken.None;
        var dependencyContext = DependencyContext.Default;

        var packageDependencyTree = await GetPackageDependencyTreeAsync(
            packageIdentity,
            _nuGetFramework,
            GetSourceRepositoryProvider().GetRepositories(),
            dependencyContext,
            sourceCacheContext,
            logger,
            cancellationToken
        );

        var allPackages = packageDependencyTree.GetAllPackages();
        var allLibAssemblies = new HashSet<string>();

        foreach (var package in allPackages)
        {
            var libAssemblies = await GetCachedPackageAssembliesAsync(package.Id, package.Version.ToString());

            foreach (var libAssembly in libAssemblies)
            {
                allLibAssemblies.Add(libAssembly);
            }
        }

        return allLibAssemblies;
    }

    public async Task<PackageMetadata[]> SearchPackagesAsync(
        string? term,
        int skip,
        int take,
        bool includePrerelease,
        CancellationToken? cancellationToken = null)
    {
        if (skip < 0) skip = 0;
        if (take < 0) take = 0;
        else if (take > 200) take = 200;

        var sourceRepositoryProvider = GetSourceRepositoryProvider();

        // TODO we'd want to show results from multiple repositories in the near future
        var repository = sourceRepositoryProvider.GetRepositories().First();
        var searchResource = await repository.GetResourceAsync<PackageSearchResource>().ConfigureAwait(false);

        var filter = new SearchFilter(includePrerelease);
        var searchResults = await searchResource.SearchAsync(
            term,
            filter,
            skip,
            take,
            NuGetNullLogger.Instance,
            cancellationToken ?? CancellationToken.None
        ).ConfigureAwait(false);

        var packages = new List<PackageMetadata>();

        foreach (var searchResult in searchResults)
        {
            var metadata = new PackageMetadata();
            await MapAsync(searchResult, metadata);
            packages.Add(metadata);
        }

        await HydrateMetadataAsync(packages);

        return packages.ToArray();
    }

    public async Task InstallPackageAsync(string packageId, string packageVersion)
    {
        var packageIdentity = new PackageIdentity(packageId, NuGetVersion.Parse(packageVersion));
        var nuGetCacheDir = GetNuGetCacheDirectoryPath();
        var nuGetSettings = NuGet.Configuration.Settings.LoadDefaultSettings(nuGetCacheDir);

        var sourceRepositoryProvider = GetSourceRepositoryProvider();
        var repositories = sourceRepositoryProvider.GetRepositories();

        using var sourceCacheContext = new SourceCacheContext();
        var logger = new NuGetNullLogger();
        var cancellationToken = CancellationToken.None;
        var dependencyContext = DependencyContext.Default;

        var packageDependencyTree = await GetPackageDependencyTreeAsync(
            packageIdentity,
            _nuGetFramework,
            repositories,
            dependencyContext,
            sourceCacheContext,
            logger,
            cancellationToken
        );

        var packagesToInstall = GetPackagesToInstallAsync(
            packageDependencyTree,
            sourceRepositoryProvider,
            logger);

        await InstallPackagesAsync(packageIdentity, packagesToInstall, sourceCacheContext, nuGetSettings, logger, cancellationToken);
    }

    public Task DeleteCachedPackageAsync(string packageId, string packageVersion)
    {
        var packageIdentity = new PackageIdentity(packageId, new NuGetVersion(packageVersion));
        var packagePathResolver = GetPackagePathResolver();

        var installPath = GetInstallPath(packagePathResolver, packageIdentity);

        if (installPath != null && Directory.Exists(installPath))
            Directory.Delete(installPath, recursive: true);

        return Task.CompletedTask;
    }

    private async Task<PackageDependencyTree> GetPackageDependencyTreeAsync(
        PackageIdentity package,
        NuGetFramework framework,
        IEnumerable<SourceRepository> repositories,
        DependencyContext hostDependencies,
        SourceCacheContext cacheContext,
        INugetLogger logger,
        CancellationToken cancellationToken)
    {
        var packageDependencyTree = new PackageDependencyTree(package);

        foreach (var repository in repositories)
        {
            // Get the dependency info for the package.
            var dependencyInfoResource = await repository.GetResourceAsync<DependencyInfoResource>();
            var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                package,
                framework,
                cacheContext,
                logger,
                cancellationToken);

            // No info for the package in this repository.
            if (dependencyInfo == null)
            {
                continue;
            }

            // Filter the dependency info. Don't bring in any dependencies that are provided by the host.
            var actualDependencyInfo = new SourcePackageDependencyInfo(
                dependencyInfo.Id,
                dependencyInfo.Version,
                dependencyInfo.Dependencies.Where(dep => !DependencySuppliedByHost(hostDependencies, dep)),
                dependencyInfo.Listed,
                dependencyInfo.Source);

            packageDependencyTree.DependencyInfo = actualDependencyInfo;

            // Recurse through each dependency package.
            foreach (var dep in actualDependencyInfo.Dependencies)
            {
                var depDependencyTree = await GetPackageDependencyTreeAsync(
                    new PackageIdentity(dep.Id, dep.VersionRange.MinVersion),
                    framework,
                    repositories,
                    hostDependencies,
                    cacheContext,
                    logger,
                    cancellationToken);

                packageDependencyTree.Dependencies.Add(depDependencyTree);
            }

            break;
        }

        return packageDependencyTree;
    }

    private IEnumerable<SourcePackageDependencyInfo> GetPackagesToInstallAsync(
        PackageDependencyTree packageDependencyTree,
        SourceRepositoryProvider sourceRepositoryProvider,
        INugetLogger logger)
    {
        var allPackages = packageDependencyTree.GetAllPackages();

        // Create a package resolver context (this is used to help figure out which actual package versions to install).
        var resolverContext = new PackageResolverContext(
            DependencyBehavior.Lowest,
            new[] { packageDependencyTree.Identity.Id },
            Enumerable.Empty<string>(),
            Enumerable.Empty<PackageReference>(),
            Enumerable.Empty<PackageIdentity>(),
            allPackages,
            sourceRepositoryProvider.GetRepositories().Select(s => s.PackageSource),
            logger);

        var resolver = new PackageResolver();

        // Work out the actual set of packages to install.
        var packagesToInstall = resolver
            .Resolve(resolverContext, CancellationToken.None)
            .Select(p => allPackages.Single(x => PackageIdentityComparer.Default.Equals(x, p)));

        return packagesToInstall;
    }

    private async Task InstallPackagesAsync(
        PackageIdentity explicitPackageToInstallIdentity,
        IEnumerable<SourcePackageDependencyInfo> packagesToInstall,
        SourceCacheContext sourceCacheContext,
        ISettings nuGetSettings,
        INugetLogger logger,
        CancellationToken cancellationToken)
    {
        var packagePathResolver = GetPackagePathResolver();
        var packageExtractionContext = new PackageExtractionContext(
            PackageSaveMode.Defaultv3,
            XmlDocFileSaveMode.None,
            ClientPolicyContext.GetClientPolicy(nuGetSettings, logger),
            logger);

        foreach (var packageToInstall in packagesToInstall)
        {
            var installPath = GetInstallPath(packagePathResolver, packageToInstall);

            if (installPath != null)
            {
                // Package is already installed
                continue;
            }

            var downloadResource = await packageToInstall.Source.GetResourceAsync<DownloadResource>(cancellationToken);

            // Download the package (might come from the shared package cache).
            var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                packageToInstall,
                new PackageDownloadContext(sourceCacheContext),
                SettingsUtility.GetGlobalPackagesFolder(nuGetSettings),
                logger,
                cancellationToken);

            // Extract the package into the target directory.
            await PackageExtractor.ExtractPackageAsync(
                downloadResult.PackageSource,
                downloadResult.PackageStream,
                packagePathResolver,
                packageExtractionContext,
                cancellationToken);

            installPath = GetInstallPath(packagePathResolver, packageToInstall)!;

            if (packageToInstall.Id == explicitPackageToInstallIdentity.Id &&
                packageToInstall.Version == explicitPackageToInstallIdentity.Version)
            {
                var installInfo = GetInstallInfo(installPath) ?? new PackageInstallInfo();
                installInfo.InstallReason = PackageInstallReason.Explicit;
                SaveInstallInfo(installPath, installInfo);
            }
            else
            {
                var installInfo = GetInstallInfo(installPath);
                if (installInfo == null)
                {
                    SaveInstallInfo(installPath, new PackageInstallInfo
                    {
                        InstallReason = PackageInstallReason.Dependency
                    });
                }
            }
        }
    }

    private async Task HydrateMetadataAsync(IEnumerable<PackageMetadata> packages)
    {
        var needsProcessing = packages.Where(p => p.IsSomeMetadataMetadataMissing()).ToList();
        if (!needsProcessing.Any())
            return;

        using var sourceCacheContext = new SourceCacheContext();
        var sourceRepositories = GetSourceRepositoryProvider().GetRepositories();

        foreach (var sourceRepository in sourceRepositories)
        {
            if (!needsProcessing.Any())
                break;

            var resource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();

            var found = new List<PackageMetadata>();

            foreach (var package in needsProcessing)
            {
                var metadata = await resource.GetMetadataAsync(
                    new PackageIdentity(package.PackageId, new NuGetVersion(package.Version)),
                    sourceCacheContext,
                    NuGetNullLogger.Instance,
                    CancellationToken.None);

                await MapAsync(metadata, package);
                found.Add(package);
            }

            foreach (var cachedPackage in found)
                needsProcessing.Remove(cachedPackage);
        }
    }

    private async Task<HashSet<string>> GetLibItemsAsync(PackageIdentity packageIdentity, NuGetFramework framework)
    {
        var installPath = GetInstallPath(GetPackagePathResolver(), packageIdentity);
        if (installPath == null)
            throw new Exception($"Package {packageIdentity} is not installed.");

        var frameworkReducer = new FrameworkReducer();
        using var packageReader = new PackageFolderReader(installPath);

        var libItems = await packageReader.GetLibItemsAsync(CancellationToken.None).ConfigureAwait(false);
        var nearestLibItemsFramework = frameworkReducer.GetNearest(framework, libItems.Select(x => x.TargetFramework));
        var nearestLibItems = libItems
            .Where(x => x.TargetFramework.Equals(nearestLibItemsFramework))
            .SelectMany(x => x.Items)
            .ToArray();

        return nearestLibItems
            .Select(i => Path.Combine(installPath, i))
            .ToHashSet();

        // var frameworkItems = await packageReader.GetFrameworkItemsAsync(cancellationToken).ConfigureAwait(false);
        // var nearestFrameworkItemsFramework = frameworkReducer.GetNearest(framework, frameworkItems.Select(x => x.TargetFramework));
        // var nearestFrameworkItems = frameworkItems
        //     .Where(x => x.TargetFramework.Equals(nearestFrameworkItemsFramework))
        //     .SelectMany(x => x.Items)
        //     .ToArray();
    }

    private bool DependencySuppliedByHost(DependencyContext hostDependencies, PackageDependency dep)
    {
        if (RuntimeProvidedPackages.IsPackageProvidedByRuntime(dep.Id))
        {
            return true;
        }

        // Check if there is a runtime library with the same ID as the package is available in the host's runtime libraries.
        var runtimeLibs = hostDependencies.RuntimeLibraries.Where(r => r.Name == dep.Id);

        return runtimeLibs.Any(r =>
        {
            // What version of the library is the host using?
            var parsedLibVersion = NuGetVersion.Parse(r.Version);

            if (parsedLibVersion.IsPrerelease)
            {
                // Always use pre-release versions from the host, otherwise it becomes
                // a nightmare to develop across multiple active versions.
                return true;
            }
            else
            {
                // Does the host version satisfy the version range of the requested package?
                // If so, we can provide it; otherwise, we cannot.
                return dep.VersionRange.Satisfies(parsedLibVersion);
            }
        });
    }

    private SourceRepositoryProvider GetSourceRepositoryProvider()
    {
        // TODO Give user ability to configure additional package sources
        var sourceProvider = new PackageSourceProvider(NullSettings.Instance, new[]
        {
            new PackageSource(NUGET_API_URI),
        });

        return new SourceRepositoryProvider(sourceProvider, Repository.Provider.GetCoreV3());
    }

    private PackagePathResolver GetPackagePathResolver()
    {
        return new PackagePathResolver(GetNuGetCacheDirectoryPath(), true);
    }

    private string? GetInstallPath(PackagePathResolver packagePathResolver, PackageIdentity packageIdentity)
    {
        bool TryGetPath(string? path, out string? newPath)
        {
            newPath = null;

            if (path == null)
                return false;

            if (Directory.Exists(path))
            {
                newPath = path;
                return true;
            }

            // When revision is 0 (x.x.x.0), directory on disk will most likely have been saved without
            // the revision number in the directory name (ie. x.x.x)
            if (packageIdentity.Version.Revision == 0)
            {
                path = path[..^2];
                if (Directory.Exists(path))
                {
                    newPath = path;
                    return true;
                }
            }

            return false;
        }

        if (TryGetPath(packagePathResolver.GetInstalledPath(packageIdentity), out string? installPath))
            return installPath;

        if (TryGetPath(packagePathResolver.GetInstallPath(packageIdentity), out installPath))
            return installPath;

        return null;
    }

    private string GetNuGetCacheDirectoryPath()
    {
        var path = Path.Combine(_settings.PackageCacheDirectoryPath, "NuGet");
        return Directory.CreateDirectory(path).FullName;
    }

    private async Task MapAsync(IPackageSearchMetadata searchMetadata, PackageMetadata packageMetadata)
    {
        packageMetadata.PackageId = searchMetadata.Identity.Id;
        packageMetadata.Title = searchMetadata.Title;
        packageMetadata.Authors = searchMetadata.Authors;
        packageMetadata.Description = searchMetadata.Description;
        packageMetadata.IconUrl = searchMetadata.IconUrl;
        packageMetadata.ProjectUrl = searchMetadata.ProjectUrl;
        packageMetadata.PackageDetailsUrl = searchMetadata.PackageDetailsUrl;
        packageMetadata.LicenseUrl = searchMetadata.LicenseUrl;
        packageMetadata.ReadmeUrl = searchMetadata.ReadmeUrl;
        packageMetadata.ReportAbuseUrl = searchMetadata.ReportAbuseUrl;
        packageMetadata.DownloadCount = searchMetadata.DownloadCount;
        packageMetadata.PublishedDate = searchMetadata.Published?.UtcDateTime;
        packageMetadata.RequireLicenseAcceptance = searchMetadata.RequireLicenseAcceptance;

        packageMetadata.Dependencies = searchMetadata.DependencySets.Select(dg =>
                $"{dg.TargetFramework}\n{dg.Packages.Select(p => $"{p.Id} {p.VersionRange}").JoinToString("\n")}")
            .ToArray();

        if (packageMetadata.Version == null)
        {
            packageMetadata.Version = (await searchMetadata.GetVersionsAsync().ConfigureAwait(false))?
                .LastOrDefault()?
                .Version.ToString();
        }
    }

    private PackageInstallInfo? GetInstallInfo(string installPath)
    {
        var infoFile = Path.Combine(installPath, "info.json");
        return !File.Exists(infoFile) ? null : JsonSerializer.Deserialize<PackageInstallInfo>(File.ReadAllText(infoFile));
    }

    private void SaveInstallInfo(string installPath, PackageInstallInfo info)
    {
        var json = JsonSerializer.Serialize(info, true);
        File.WriteAllText(Path.Combine(installPath, "info.json"), json);
    }
}