﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.DesignerAttribute
{
    [ExportWorkspaceService(typeof(DesignerAttributeComputer))]
    internal sealed partial class DesignerAttributeComputer : IWorkspaceService
    {
        /// <summary>
        /// Protects mutable state in this type.
        /// </summary>
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(initialCount: 1);

        /// <summary>
        /// Keep track of the last information we reported.  We will avoid notifying the host if we recompute and these
        /// don't change.
        /// </summary>
        private readonly ConcurrentDictionary<DocumentId, (string? category, VersionStamp projectVersion)> _documentToLastReportedInformation = new();

        public async ValueTask ProcessSolutionAsync(Solution solution, DocumentId? priorityDocumentId, ICallback callback, CancellationToken cancellationToken)
        {
            using (await _gate.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
            {
                // Remove any documents that are now gone.
                foreach (var docId in _documentToLastReportedInformation.Keys)
                {
                    if (!solution.ContainsDocument(docId))
                        _documentToLastReportedInformation.TryRemove(docId, out _);
                }

                // Handle the priority doc first.
                var priorityDocument = solution.GetDocument(priorityDocumentId);
                if (priorityDocument != null)
                {
                    await ProcessProjectAsync(priorityDocument.Project, priorityDocument, callback, cancellationToken).ConfigureAwait(false);

                    // now scan all the other files from that project in case they were affected by the edited document.
                    await ProcessProjectAsync(priorityDocument.Project, specificDocument: null, callback, cancellationToken).ConfigureAwait(false);
                }

                // Process the rest of the projects in dependency order so that their data is ready when we hit the 
                // projects that depend on them.
                var dependencyGraph = solution.GetProjectDependencyGraph();
                foreach (var projectId in dependencyGraph.GetTopologicallySortedProjects(cancellationToken))
                {
                    if (projectId != priorityDocumentId?.ProjectId)
                        await ProcessProjectAsync(solution.GetRequiredProject(projectId), specificDocument: null, callback, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessProjectAsync(Project project, Document? specificDocument, ICallback callback, CancellationToken cancellationToken)
        {
            if (!project.SupportsCompilation)
                return;

            // We need to reanalyze the project whenever it (or any of its dependencies) have
            // changed.  We need to know about dependencies since if a downstream project adds the
            // DesignerCategory attribute to a class, that can affect us when we examine the classes
            // in this project.
            var projectVersion = await project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

            // Now get all the values that actually changed and notify VS about them. We don't need
            // to tell it about the ones that didn't change since that will have no effect on the
            // user experience.
            var changedData = await ComputeChangedDataAsync(
                project, specificDocument, projectVersion, cancellationToken).ConfigureAwait(false);

            // Only bother reporting non-empty information to save an unnecessary RPC.
            if (!changedData.IsEmpty)
                await callback.ReportDesignerAttributeDataAsync(changedData, cancellationToken).ConfigureAwait(false);

            // Now, keep track of what we've reported to the host so we won't report unchanged files in the future. We
            // do this after the report has gone through as we want to make sure that if it cancels for any reason we
            // don't hold onto values that may not have made it all the way to the project system.
            foreach (var data in changedData)
                _documentToLastReportedInformation[data.DocumentId] = (data.Category, projectVersion);
        }

        private async Task<ImmutableArray<DesignerAttributeData>> ComputeChangedDataAsync(
            Project project, Document? specificDocument, VersionStamp projectVersion, CancellationToken cancellationToken)
        {
            var compilation = await project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);
            var designerCategoryType = compilation.DesignerCategoryAttributeType();

            using var _1 = ArrayBuilder<Task<DesignerAttributeData?>>.GetInstance(out var tasks);
            foreach (var document in project.Documents)
            {
                // If we're only analyzing a specific document, then skip the rest.
                if (specificDocument != null && document != specificDocument)
                    continue;

                // If we don't have a path for this document, we cant proceed with it.
                // We need that path to inform the project system which file we're referring to.
                if (document.FilePath == null)
                    continue;

                // If nothing has changed at the top level between the last time we analyzed this document and now, then
                // no need to analyze again.
                if (_documentToLastReportedInformation.TryGetValue(document.Id, out var existingInfo) &&
                    existingInfo.projectVersion == projectVersion)
                {
                    continue;
                }

                tasks.Add(ComputeDesignerAttributeDataAsync(designerCategoryType, document, cancellationToken));
            }

            using var _2 = ArrayBuilder<DesignerAttributeData>.GetInstance(tasks.Count, out var results);

            // Avoid unnecessary allocation of result array.
            await Task.WhenAll((IEnumerable<Task>)tasks).ConfigureAwait(false);

            foreach (var task in tasks)
            {
                var dataOpt = await task.ConfigureAwait(false);
                if (dataOpt == null)
                    continue;

                var data = dataOpt.Value;
                _documentToLastReportedInformation.TryGetValue(data.DocumentId, out var existingInfo);
                if (existingInfo.category != data.Category)
                    results.Add(data);
            }

            return results.ToImmutableAndClear();
        }

        private static async Task<DesignerAttributeData?> ComputeDesignerAttributeDataAsync(
            INamedTypeSymbol? designerCategoryType, Document document, CancellationToken cancellationToken)
        {
            try
            {
                Contract.ThrowIfNull(document.FilePath);

                // We either haven't computed the designer info, or our data was out of date.  We need
                // So recompute here.  Figure out what the current category is, and if that's different
                // from what we previously stored.
                var category = await DesignerAttributeHelpers.ComputeDesignerAttributeCategoryAsync(
                    designerCategoryType, document, cancellationToken).ConfigureAwait(false);

                return new DesignerAttributeData
                {
                    Category = category,
                    DocumentId = document.Id,
                    FilePath = document.FilePath,
                };
            }
            catch (Exception e) when (FatalError.ReportAndCatchUnlessCanceled(e, cancellationToken))
            {
                return null;
            }
        }
    }
}
