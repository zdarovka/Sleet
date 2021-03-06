using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Sleet
{
    /// <summary>
    /// PackageIndexFile is a simple json index of all ids and versions contained in the feed.
    /// </summary>
    public class PackageIndexFile : IndexFileBase, IAddRemovePackages, IPackagesLookup, ISymbolsAddRemovePackages, ISymbolsPackagesLookup, IApplyOperations
    {
        public PackageIndexFile(SleetContext context, string path)
            : this(context, path, persistWhenEmpty: false)
        {
        }

        public PackageIndexFile(SleetContext context, string path, bool persistWhenEmpty)
            : base(context, path, persistWhenEmpty)
        {
        }

        public PackageIndexFile(SleetContext context, ISleetFile file, bool persistWhenEmpty)
            : base(context, file, persistWhenEmpty)
        {
        }

        public Task AddPackageAsync(PackageInput packageInput)
        {
            return AddPackagesAsync(new[] { packageInput });
        }

        public Task RemovePackageAsync(PackageIdentity package)
        {
            return RemovePackagesAsync(new[] { package });
        }

        public async Task AddPackagesAsync(IEnumerable<PackageInput> packageInputs)
        {
            // Load existing index
            var sets = await GetPackageSetsAsync();

            // Add package
            await sets.Packages.AddPackagesAsync(packageInputs);

            // Write file
            await CreateAsync(sets);
        }

        public async Task RemovePackagesAsync(IEnumerable<PackageIdentity> packages)
        {
            // Load existing index
            var sets = await GetPackageSetsAsync();
            var save = false;

            // Remove package
            foreach (var package in packages)
            {
                save |= sets.Packages.Index.Remove(package);
            }

            if (save)
            {
                // Create updated index
                await CreateAsync(sets);
            }
        }

        public async Task AddSymbolsPackageAsync(PackageInput packageInput)
        {
            // Load existing index
            var sets = await GetPackageSetsAsync();

            // Add package
            await sets.Symbols.AddPackageAsync(packageInput);

            // Write file
            await CreateAsync(sets);
        }

        public async Task RemoveSymbolsPackageAsync(PackageIdentity package)
        {
            // Load existing index
            var sets = await GetPackageSetsAsync();

            // Remove package
            if (sets.Symbols.Index.Remove(package))
            {
                // Create updated index
                await CreateAsync(sets);
            }
        }

        /// <summary>
        /// Creates a set of all indexed packages
        /// </summary>
        public async Task<ISet<PackageIdentity>> GetPackagesAsync()
        {
            var sets = await GetPackageSetsAsync();

            return sets.Packages.Index;
        }

        public async Task<ISet<PackageIdentity>> GetSymbolsPackagesAsync()
        {
            var sets = await GetPackageSetsAsync();

            return sets.Symbols.Index;
        }

        public async Task<ISet<PackageIdentity>> GetSymbolsPackagesByIdAsync(string packageId)
        {
            var packages = await GetSymbolsPackagesAsync();

            return GetSetForId(packageId, packages);
        }

        public async Task<ISet<NuGetVersion>> GetPackageVersions(string packageId)
        {
            var packages = await GetPackagesByIdAsync(packageId);
            return new SortedSet<NuGetVersion>(packages.Select(e => e.Version));
        }

        public async Task<ISet<NuGetVersion>> GetSymbolsPackageVersions(string packageId)
        {
            var packages = await GetSymbolsPackagesByIdAsync(packageId);
            return new SortedSet<NuGetVersion>(packages.Select(e => e.Version));
        }

        /// <summary>
        /// Returns all packages in the feed.
        /// Id -> Version
        /// </summary>
        public async Task<PackageSets> GetPackageSetsAsync()
        {
            var index = new PackageSets();

            if (await File.ExistsWithFetch(Context.Log, Context.Token))
            {
                var json = await GetJsonOrTemplateAsync();

                var packagesNode = json["packages"] as JObject;

                if (packagesNode == null)
                {
                    throw new InvalidDataException("Packages node missing from sleet.packageindex.json");
                }

                index.Packages = GetPackageSetFromJson(packagesNode);

                var symbolsNode = json["symbols"] as JObject;

                // This node may not exist for older feeds
                if (symbolsNode != null)
                {
                    index.Symbols = GetPackageSetFromJson(symbolsNode);
                }
            }

            return index;
        }

        private static PackageSet GetPackageSetFromJson(JObject packagesNode)
        {
            var result = new PackageSet();

            foreach (var property in packagesNode.Properties())
            {
                var versions = (JArray)property.Value;
                var id = property.Name;

                foreach (var versionEntry in versions)
                {
                    var packageVersion = NuGetVersion.Parse(versionEntry.ToObject<string>());
                    result.Index.Add(new PackageIdentity(id, packageVersion));
                }
            }

            return result;
        }

        /// <summary>
        /// Find all versions of a package.
        /// </summary>
        public async Task<ISet<PackageIdentity>> GetPackagesByIdAsync(string packageId)
        {
            var sets = await GetPackageSetsAsync();

            return GetSetForId(packageId, sets.Packages.Index);
        }

        /// <summary>
        /// True if the package exists in the index.
        /// </summary>
        public async Task<bool> Exists(string packageId, NuGetVersion version)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            var byId = await GetPackagesByIdAsync(packageId);

            return byId.Contains(new PackageIdentity(packageId, version));
        }

        public Task<bool> Exists(PackageIdentity package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            return Exists(package.Id, package.Version);
        }

        /// <summary>
        /// True if the symbols package exists in the index.
        /// </summary>
        public async Task<bool> SymbolsExists(string packageId, NuGetVersion version)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            var byId = await GetSymbolsPackagesByIdAsync(packageId);

            return byId.Contains(new PackageIdentity(packageId, version));
        }

        public Task<bool> SymbolsExists(PackageIdentity package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            return SymbolsExists(package.Id, package.Version);
        }

        /// <summary>
        /// Create the file directly without loading the previous file.
        /// </summary>
        public async Task CreateAsync(PackageSets index)
        {
            using (var timer = PerfEntryWrapper.CreateModifyTimer(File, Context))
            {
                // Create updated index
                var json = CreateJson(index);
                var isEmpty = (index.Packages.Index.Count < 1) && (index.Symbols.Index.Count < 1);
                await SaveAsync(json, isEmpty);
            }
        }

        /// <summary>
        /// Empty json file.
        /// </summary>
        protected override Task<JObject> GetJsonTemplateAsync()
        {
            return Task.FromResult(new JObject
                {
                    { "packages", new JObject() },
                    { "symbols", new JObject() }
                });
        }

        private static JObject CreateJson(PackageSets index)
        {
            var json = new JObject
            {
                { "packages", CreatePackageSetJson(index.Packages) },
                { "symbols", CreatePackageSetJson(index.Symbols) }
            };

            return json;
        }

        private static JObject CreatePackageSetJson(PackageSet packages)
        {
            var json = new JObject();

            var groups = packages.Index.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var group in groups.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var versionArray = new JArray(group.OrderByDescending(e => e.Version)
                    .Select(e => e.Version.ToFullString()));

                if (versionArray.Count > 0)
                {
                    json.Add(group.Key, versionArray);
                }
            }

            return json;
        }

        private static SortedSet<PackageIdentity> GetSetForId(string packageId, IEnumerable<PackageIdentity> packages)
        {
            return new SortedSet<PackageIdentity>(packages.Where(e => StringComparer.OrdinalIgnoreCase.Equals(packageId, e.Id)));
        }

        public override async Task<bool> IsEmpty()
        {
            var sets = await GetPackageSetsAsync();
            return sets.Packages.Index.Count == 0 && sets.Symbols.Index.Count == 0;
        }

        public virtual Task ApplyOperationsAsync(SleetOperations operations)
        {
            return OperationsUtility.ApplyAddRemoveAsync(this, operations);
        }

        public Task PreLoadAsync(SleetOperations operations)
        {
            // Noop
            return Task.FromResult(true);
        }
    }
}