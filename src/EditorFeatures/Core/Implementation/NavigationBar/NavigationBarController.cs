﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Tagging;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.NavigationBar
{
    /// <summary>
    /// The controller for navigation bars.
    /// </summary>
    /// <remarks>
    /// The threading model for this class is simple: all non-static members are affinitized to the
    /// UI thread.
    /// </remarks>
    internal partial class NavigationBarController : ForegroundThreadAffinitizedObject, INavigationBarController
    {
        private static readonly NavigationBarModel EmptyModel = new(
                SpecializedCollections.EmptyList<NavigationBarItem>(),
                semanticVersionStamp: default,
                itemService: null);

        private static readonly NavigationBarSelectedTypeAndMember EmptySelectedInfo = new(typeItem: null, memberItem: null);

        private readonly INavigationBarPresenter _presenter;
        private readonly ITextBuffer _subjectBuffer;
        private readonly IWaitIndicator _waitIndicator;
        private readonly IAsynchronousOperationListener _asyncListener;

        private bool _disconnected = false;
        private Workspace? _workspace;

        public NavigationBarController(
            IThreadingContext threadingContext,
            INavigationBarPresenter presenter,
            ITextBuffer subjectBuffer,
            IWaitIndicator waitIndicator,
            IAsynchronousOperationListener asyncListener)
            : base(threadingContext)
        {
            _presenter = presenter;
            _subjectBuffer = subjectBuffer;
            _waitIndicator = waitIndicator;
            _asyncListener = asyncListener;

            presenter.CaretMoved += OnCaretMoved;
            presenter.ViewFocused += OnViewFocused;

            presenter.DropDownFocused += OnDropDownFocused;
            presenter.ItemSelected += OnItemSelected;

            subjectBuffer.PostChanged += OnSubjectBufferPostChanged;

            // Initialize the tasks to be an empty model so we never have to deal with a null case.
            _modelTask = Task.FromResult(EmptyModel);
            _selectedItemInfoTask = Task.FromResult(EmptySelectedInfo);

            _lastModelAndSelectedInfo_OnlyAccessOnUIThread = (EmptyModel, EmptySelectedInfo);
        }

        public void SetWorkspace(Workspace? newWorkspace)
        {
            DisconnectFromWorkspace();

            if (newWorkspace != null)
            {
                ConnectToWorkspace(newWorkspace);
            }
        }

        private void ConnectToWorkspace(Workspace workspace)
        {
            // If we disconnected before the workspace ever connected, just disregard
            if (_disconnected)
            {
                return;
            }

            _workspace = workspace;
            _workspace.WorkspaceChanged += this.OnWorkspaceChanged;

            if (IsForeground())
            {
                ConnectToNewWorkspace();
            }
            else
            {
                var asyncToken = _asyncListener.BeginAsyncOperation(nameof(ConnectToWorkspace));
                Task.Run(async () =>
                {
                    await ThreadingContext.JoinableTaskFactory.SwitchToMainThreadAsync();

                    ConnectToNewWorkspace();
                }).CompletesAsyncOperation(asyncToken);
            }

            return;

            void ConnectToNewWorkspace()
            {
                // For the first time you open the file, we'll start immediately
                StartModelUpdateAndSelectedItemUpdateTasks(modelUpdateDelay: 0, selectedItemUpdateDelay: 0);
            }
        }

        private void DisconnectFromWorkspace()
        {
            if (_workspace != null)
            {
                _workspace.WorkspaceChanged -= this.OnWorkspaceChanged;
                _workspace = null;
            }
        }

        public void Disconnect()
        {
            AssertIsForeground();
            DisconnectFromWorkspace();

            _subjectBuffer.PostChanged -= OnSubjectBufferPostChanged;

            _presenter.CaretMoved -= OnCaretMoved;
            _presenter.ViewFocused -= OnViewFocused;

            _presenter.DropDownFocused -= OnDropDownFocused;
            _presenter.ItemSelected -= OnItemSelected;

            _presenter.Disconnect();

            _disconnected = true;

            // Cancel off any remaining background work
            _modelTaskCancellationSource.Cancel();
            _selectedItemInfoTaskCancellationSource.Cancel();
        }

        private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs args)
        {
            // We're getting an event for a workspace we already disconnected from
            if (args.NewSolution.Workspace != _workspace)
            {
                return;
            }

            // If the displayed project is being renamed, retrigger the update
            if (args.Kind == WorkspaceChangeKind.ProjectChanged && args.ProjectId != null)
            {
                var oldProject = args.OldSolution.GetRequiredProject(args.ProjectId);
                var newProject = args.NewSolution.GetRequiredProject(args.ProjectId);

                if (oldProject.Name != newProject.Name)
                {
                    var currentContextDocumentId = _workspace.GetDocumentIdInCurrentContext(_subjectBuffer.AsTextContainer());

                    if (currentContextDocumentId != null && currentContextDocumentId.ProjectId == args.ProjectId)
                    {
                        StartModelUpdateAndSelectedItemUpdateTasks(modelUpdateDelay: 0, selectedItemUpdateDelay: 0);
                    }
                }
            }

            if (args.Kind == WorkspaceChangeKind.DocumentChanged &&
                args.OldSolution == args.NewSolution)
            {
                var currentContextDocumentId = _workspace.GetDocumentIdInCurrentContext(_subjectBuffer.AsTextContainer());
                if (currentContextDocumentId != null && currentContextDocumentId == args.DocumentId)
                {
                    // The context has changed, so update everything.
                    StartModelUpdateAndSelectedItemUpdateTasks(modelUpdateDelay: 0, selectedItemUpdateDelay: 0);
                }
            }
        }

        private void OnSubjectBufferPostChanged(object? sender, EventArgs e)
        {
            AssertIsForeground();

            StartModelUpdateAndSelectedItemUpdateTasks(modelUpdateDelay: TaggerConstants.MediumDelay, selectedItemUpdateDelay: 0);
        }

        private void OnCaretMoved(object? sender, EventArgs e)
        {
            AssertIsForeground();
            StartSelectedItemUpdateTask(delay: TaggerConstants.NearImmediateDelay);
        }

        private void OnViewFocused(object? sender, EventArgs e)
        {
            AssertIsForeground();
            StartSelectedItemUpdateTask(delay: TaggerConstants.ShortDelay);
        }

        private void OnDropDownFocused(object? sender, EventArgs e)
        {
            AssertIsForeground();

            var document = _subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
                return;

            // Just present whatever information we have at this point.  We don't want to block the user from
            // being able to open the dropdown list.
            GetProjectItems(out var projectItems, out var selectedProjectItem);

            var (lastModel, selectedInfo) = _lastModelAndSelectedInfo_OnlyAccessOnUIThread;
            _presenter.PresentItems(
                projectItems,
                selectedProjectItem,
                lastModel.Types,
                selectedInfo.TypeItem,
                selectedInfo.MemberItem);
        }

        private void GetProjectItems(out IList<NavigationBarProjectItem> projectItems, out NavigationBarProjectItem? selectedProjectItem)
        {
            var documents = _subjectBuffer.CurrentSnapshot.GetRelatedDocumentsWithChanges();
            if (!documents.Any())
            {
                projectItems = SpecializedCollections.EmptyList<NavigationBarProjectItem>();
                selectedProjectItem = null;
                return;
            }

            projectItems = documents.Select(d =>
                new NavigationBarProjectItem(
                    d.Project.Name,
                    d.Project.GetGlyph(),
                    workspace: d.Project.Solution.Workspace,
                    documentId: d.Id,
                    language: d.Project.Language)).OrderBy(projectItem => projectItem.Text).ToList();

            projectItems.Do(i => i.InitializeTrackingSpans(_subjectBuffer.CurrentSnapshot));

            var document = _subjectBuffer.AsTextContainer().GetOpenDocumentInCurrentContext();
            selectedProjectItem = document != null
                ? projectItems.FirstOrDefault(p => p.Text == document.Project.Name) ?? projectItems.First()
                : projectItems.First();
        }

        private void PushSelectedItemsToPresenter(NavigationBarSelectedTypeAndMember selectedItems)
        {
            AssertIsForeground();

            var oldLeft = selectedItems.TypeItem;
            var oldRight = selectedItems.MemberItem;

            NavigationBarItem? newLeft = null;
            NavigationBarItem? newRight = null;
            var listOfLeft = new List<NavigationBarItem>();
            var listOfRight = new List<NavigationBarItem>();

            if (oldRight != null)
            {
                newRight = new NavigationBarPresentedItem(oldRight.Text, oldRight.Glyph, oldRight.Spans, oldRight.ChildItems, oldRight.Bolded, oldRight.Grayed || selectedItems.ShowMemberItemGrayed)
                {
                    TrackingSpans = oldRight.TrackingSpans
                };
                listOfRight.Add(newRight);
            }

            if (oldLeft != null)
            {
                newLeft = new NavigationBarPresentedItem(oldLeft.Text, oldLeft.Glyph, oldLeft.Spans, listOfRight, oldLeft.Bolded, oldLeft.Grayed || selectedItems.ShowTypeItemGrayed)
                {
                    TrackingSpans = oldLeft.TrackingSpans
                };
                listOfLeft.Add(newLeft);
            }

            GetProjectItems(out var projectItems, out var selectedProjectItem);

            _presenter.PresentItems(
                projectItems,
                selectedProjectItem,
                listOfLeft,
                newLeft,
                newRight);
        }

        private void OnItemSelected(object? sender, NavigationBarItemSelectedEventArgs e)
        {
            AssertIsForeground();
            _ = OnItemSelectedAsync(e.Item);
        }

        private async Task OnItemSelectedAsync(NavigationBarItem item)
        {
            AssertIsForeground();
            using var waitContext = _waitIndicator.StartWait(
                EditorFeaturesResources.Navigation_Bars,
                EditorFeaturesResources.Refreshing_navigation_bars,
                allowCancel: true,
                showProgress: false);

            try
            {
                await ProcessItemSelectionAsync(item, waitContext.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (FatalError.ReportAndCatch(e))
            {
            }
        }

        /// <summary>
        /// Process the selection of an item synchronously inside a wait context.
        /// </summary>
        /// <param name="item">The selected item.</param>
        /// <param name="cancellationToken">A cancellation token from the wait context.</param>
        private async Task ProcessItemSelectionAsync(NavigationBarItem item, CancellationToken cancellationToken)
        {
            AssertIsForeground();
            if (item is NavigationBarPresentedItem)
            {
                // Presented items are not navigable, but they may be selected due to a race
                // documented in Bug #1174848. Protect all INavigationBarItemService implementers
                // from this by ignoring these selections here.
                return;
            }

            if (item is NavigationBarProjectItem projectItem)
            {
                projectItem.SwitchToContext();
            }
            else
            {
                // When navigating, just use the partial semantics workspace.  Navigation doesn't need the fully bound
                // compilations to be created, and it can save us a lot of costly time building skeleton assemblies.
                var document = _subjectBuffer.CurrentSnapshot.AsText().GetDocumentWithFrozenPartialSemantics(cancellationToken);
                if (document != null)
                {
                    var languageService = document.GetRequiredLanguageService<INavigationBarItemService>();
                    var snapshot = _subjectBuffer.CurrentSnapshot;
                    item.Spans = item.TrackingSpans.Select(ts => ts.GetSpan(snapshot).Span.ToTextSpan()).ToList();
                    var view = _presenter.TryGetCurrentView();

                    // ConfigureAwait(true) as we have to come back to UI thread in order to kick of the refresh task below.
                    await languageService.NavigateToItemAsync(document, item, view, cancellationToken).ConfigureAwait(true);
                }
            }

            // Now that the edit has been done, refresh to make sure everything is up-to-date.
            // Have to make sure we come back to the main thread for this.
            AssertIsForeground();
            StartModelUpdateAndSelectedItemUpdateTasks(modelUpdateDelay: 0, selectedItemUpdateDelay: 0);
        }
    }
}
