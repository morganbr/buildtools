﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;

namespace Microsoft.DotNet.Build.Tasks.Packaging
{
    public class GenerateNuSpec : Task
    {
        private const string NuSpecXmlNamespace = @"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";

        public string InputFileName { get; set; }

        [Required]
        public string OutputFileName { get; set; }

        public string MinClientVersion { get; set; }

        [Required]
        public string Id { get; set; }

        [Required]
        public string Version { get; set; }

        [Required]
        public string Title { get; set; }

        [Required]
        public string Authors { get; set; }

        [Required]
        public string Owners { get; set; }

        [Required]
        public string Description { get; set; }

        public string ReleaseNotes { get; set; }

        public string Summary { get; set; }

        public string Language { get; set; }

        public string ProjectUrl { get; set; }

        public string IconUrl { get; set; }

        public string LicenseUrl { get; set; }

        public string Copyright { get; set; }

        public bool RequireLicenseAcceptance { get; set; }

        public bool DevelopmentDependency { get; set; }

        public string Tags { get; set; }

        public ITaskItem[] Dependencies { get; set; }

        public ITaskItem[] References { get; set; }

        public ITaskItem[] FrameworkReferences { get; set; }

        public ITaskItem[] Files { get; set; }

        public override bool Execute()
        {
            try
            {
                WriteNuSpecFile();
            }
            catch (Exception ex)
            {
                Log.LogError(ex.ToString());
                Log.LogErrorFromException(ex);
            }

            return !Log.HasLoggedErrors;
        }

        private void WriteNuSpecFile()
        {
            var manifest = CreateManifest();

            if (!IsDifferent(manifest))
            {
                Log.LogMessage("Skipping generation of .nuspec because contents are identical.");
                return;
            }

            var directory = Path.GetDirectoryName(OutputFileName);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var file = File.Create(OutputFileName))
            {
                manifest.Save(file, false);
            }
        }

        private bool IsDifferent(Manifest newManifest)
        {
            if (!File.Exists(OutputFileName))
                return true;

            var oldSource = File.ReadAllText(OutputFileName);
            var newSource = "";
            using (var stream = new MemoryStream())
            {
                newManifest.Save(stream);
                stream.Seek(0, SeekOrigin.Begin);
                newSource = Encoding.UTF8.GetString(stream.ToArray());
            }

            return oldSource != newSource;
        }

        private Manifest CreateManifest()
        {
            Manifest manifest;
            ManifestMetadata manifestMetadata;
            if (!string.IsNullOrEmpty(InputFileName))
            {
                using (var stream = File.OpenRead(InputFileName))
                {
                    manifest = Manifest.ReadFrom(stream);
                }
                if (manifest.Metadata == null)
                {
                    manifest = new Manifest(new ManifestMetadata(), manifest.Files);
                }
            }
            else
            {
                manifest = new Manifest(new ManifestMetadata());
            }


            manifestMetadata = manifest.Metadata;

            manifestMetadata.UpdateMember(x => x.Authors, Authors?.Split(';'));
            manifestMetadata.UpdateMember(x => x.Copyright, Copyright);
            manifestMetadata.UpdateMember(x => x.DependencySets, GetDependencySets());
            manifestMetadata.UpdateMember(x => x.Description, Description);
            manifestMetadata.DevelopmentDependency |= DevelopmentDependency;
            manifestMetadata.UpdateMember(x => x.FrameworkAssemblies, GetFrameworkAssemblies());
            manifestMetadata.UpdateMember(x => x.IconUrl, IconUrl != null ? new Uri(IconUrl) : null);
            manifestMetadata.UpdateMember(x => x.Id, Id);
            manifestMetadata.UpdateMember(x => x.Language, Language);
            manifestMetadata.UpdateMember(x => x.LicenseUrl, new Uri(LicenseUrl));
            manifestMetadata.UpdateMember(x => x.MinClientVersionString, MinClientVersion);
            manifestMetadata.UpdateMember(x => x.Owners, Owners?.Split(';'));
            manifestMetadata.UpdateMember(x => x.ProjectUrl, ProjectUrl != null ? new Uri(ProjectUrl) : null);
            manifestMetadata.AddRangeToMember(x => x.PackageAssemblyReferences, GetReferenceSets());
            manifestMetadata.UpdateMember(x => x.ReleaseNotes, ReleaseNotes);
            manifestMetadata.RequireLicenseAcceptance |= RequireLicenseAcceptance;
            manifestMetadata.UpdateMember(x => x.Summary, Summary);
            manifestMetadata.UpdateMember(x => x.Tags, Tags);
            manifestMetadata.UpdateMember(x => x.Title, Title);
            manifestMetadata.UpdateMember(x => x.Version, Version != null ? new NuGetVersion(Version) : null);

            manifest.AddRangeToMember(x => x.Files, GetManifestFiles());

            return manifest;
        }

        private List<ManifestFile> GetManifestFiles()
        {
            return (from f in Files.NullAsEmpty()
                    select new ManifestFile(
                        f.GetMetadata(Metadata.FileSource),
                        f.GetMetadata(Metadata.FileTarget),
                        f.GetMetadata(Metadata.FileExclude)
                        )).OrderBy(f => f.Target, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private List<FrameworkAssemblyReference> GetFrameworkAssemblies()
        {
            return (from fr in FrameworkReferences.NullAsEmpty()
                    orderby fr.ItemSpec, StringComparer.Ordinal
                    select new FrameworkAssemblyReference(fr.ItemSpec, new[] { fr.GetTargetFramework() })
                    ).ToList();
        }

        private List<PackageDependencySet> GetDependencySets()
        {
            var dependencies = from d in Dependencies.NullAsEmpty()
                               select new Dependency
                               {
                                   Id = d.ItemSpec,
                                   Version = d.GetVersion(),
                                   TargetFramework = d.GetTargetFramework()
                               };

            return (from dependency in dependencies
                    group dependency by dependency.TargetFramework into dependenciesByFramework
                    select new PackageDependencySet(
                        dependenciesByFramework.Key,
                        from dependency in dependenciesByFramework
                                        where dependency.Id != "_._"
                                        orderby dependency.Id, StringComparer.Ordinal
                                        group dependency by dependency.Id into dependenciesById
                                        select new PackageDependency(
                                            dependenciesById.Key,
                                            VersionRange.Parse(
                                                dependenciesById.Select(x => x.Version)
                                                .Aggregate(AggregateVersions)
                                                .ToStringSafe())
                    ))).OrderBy(s => s?.TargetFramework?.GetShortFolderName(), StringComparer.Ordinal)
                    .ToList();
        }

        private ICollection<PackageReferenceSet> GetReferenceSets()
        {
            var references = from r in References.NullAsEmpty()
                             select new
                             {
                                 File = r.ItemSpec,
                                 TargetFramework = r.GetTargetFramework(),
                             };

            return (from reference in references
                    group reference by reference.TargetFramework into referencesByFramework
                    select new PackageReferenceSet(
                        referencesByFramework.Key,
                        from reference in referencesByFramework
                                       orderby reference.File, StringComparer.Ordinal
                                       select reference.File
                                       )
                    ).ToList();
        }

        private static VersionRange AggregateVersions(VersionRange aggregate, VersionRange next)
        {
            var versionRange = new VersionRange();
            SetMinVersion(ref versionRange, aggregate);
            SetMinVersion(ref versionRange, next);
            SetMaxVersion(ref versionRange, aggregate);
            SetMaxVersion(ref versionRange, next);

            if (versionRange.MinVersion == null && versionRange.MaxVersion == null)
            {
                versionRange = null;
            }

            return versionRange;
        }

        private static void SetMinVersion(ref VersionRange target, VersionRange source)
        {
            if (source == null || source.MinVersion == null)
            {
                return;
            }

            bool update = false;
            NuGetVersion minVersion = target.MinVersion;
            bool includeMinVersion = target.IsMinInclusive;

            if (target.MinVersion == null)
            {
                update = true;
                minVersion = source.MinVersion;
                includeMinVersion = source.IsMinInclusive;
            }

            if (target.MinVersion < source.MinVersion)
            {
                update = true;
                minVersion = source.MinVersion;
                includeMinVersion = source.IsMinInclusive;
            }

            if (target.MinVersion == source.MinVersion)
            {
                update = true;
                includeMinVersion = target.IsMinInclusive && source.IsMinInclusive;
            }

            if (update)
            {
                target = new VersionRange(minVersion, includeMinVersion, target.MaxVersion, target.IsMaxInclusive, target.IncludePrerelease, target.Float, target.OriginalString);
            }
        }

        private static void SetMaxVersion(ref VersionRange target, VersionRange source)
        {
            if (source == null || source.MaxVersion == null)
            {
                return;
            }

            bool update = false;
            NuGetVersion maxVersion = target.MaxVersion;
            bool includeMaxVersion = target.IsMaxInclusive;

            if (target.MaxVersion == null)
            {
                update = true;
                maxVersion = source.MaxVersion;
                includeMaxVersion = source.IsMaxInclusive;
            }

            if (target.MaxVersion > source.MaxVersion)
            {
                update = true;
                maxVersion = source.MaxVersion;
                includeMaxVersion = source.IsMaxInclusive;
            }

            if (target.MaxVersion == source.MaxVersion)
            {
                update = true;
                includeMaxVersion = target.IsMaxInclusive && source.IsMaxInclusive;
            }

            if (update)
            {
                target = new VersionRange(target.MinVersion, target.IsMinInclusive, maxVersion, includeMaxVersion, target.IncludePrerelease, target.Float, target.OriginalString);
            }
        }

        private class Dependency
        {
            public string Id { get; set; }

            public NuGetFramework TargetFramework { get; set; }

            public VersionRange Version { get; set; }
        }
    }
}