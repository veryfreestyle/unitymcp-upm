using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Persistence;
using UnityCompilerMessage = UnityEditor.Compilation.CompilerMessage;

namespace VeryFS.UnityMCP.Editor.Compilation
{
    [InitializeOnLoad]
    internal static class CompilationTracker
    {
        private static PendingRequestStore store;
        private static IClock clock;
        private static ICurrentCompilerErrors currentErrors;
        private static string activeRequestId;
        private static bool idleObserved;
        private static bool compilationInProgress;

        static CompilationTracker()
        {
#pragma warning disable CS0618
            CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
#pragma warning restore CS0618
        }

        internal static string ActiveRequestId => activeRequestId;

        internal static void Configure(
            PendingRequestStore requestStore,
            IClock requestClock,
            ICurrentCompilerErrors requestCompilerErrors = null)
        {
            store = requestStore;
            clock = requestClock;
            currentErrors = requestCompilerErrors;
        }

        internal static void StartTracking(string originRequestId)
        {
            activeRequestId = originRequestId;
            idleObserved = false;
            compilationInProgress = false;
        }

        internal static void StopTracking(string originRequestId)
        {
            if (activeRequestId == originRequestId)
            {
                activeRequestId = null;
                idleObserved = false;
                compilationInProgress = false;
            }
        }

        internal static void MarkCompilationTriggered(string originRequestId)
        {
            var request = FindRequest(originRequestId);
            if (request == null || !IsRefreshStarted(request))
            {
                return;
            }

            request.CompilationTriggered = true;
            compilationInProgress = true;
            store.Save(request);
        }

        internal static void MarkCompilationFinished()
        {
            compilationInProgress = false;
        }

        internal static PendingRefreshRequest TryBuildTerminalReport(string originRequestId)
        {
            var request = FindRequest(originRequestId);
            if (request == null || !IsRefreshStarted(request))
            {
                return request;
            }

            var finishedAt = clock.UtcNow.ToString("O");
            if (request.CompilerErrors.Count > 0)
            {
                RefreshResultBuilder.MarkFailedFromCompilerErrors(request, finishedAt);
            }
            else if (TryMergeExistingCompileErrors(request))
            {
                RefreshResultBuilder.MarkFailedFromExistingErrors(request, finishedAt);
            }
            else
            {
                RefreshResultBuilder.MarkSucceeded(request, finishedAt, request.CompilationTriggered);
            }

            store.Save(request);
            if (activeRequestId == originRequestId)
            {
                activeRequestId = null;
            }

            return request;
        }

        private static bool TryMergeExistingCompileErrors(PendingRefreshRequest request)
        {
            if (currentErrors == null || !currentErrors.ProjectHasCompileErrors())
            {
                return false;
            }

            var existing = currentErrors.ReadCompileErrors();
            if (existing.Count == 0)
            {
                // The authoritative flag says the project is broken but the console
                // no longer holds the details (e.g. it was cleared). Surface one
                // informative entry so the refresh still reports failed instead of a
                // misleading success.
                existing.Add(new CompilerMessage(
                    string.Empty, string.Empty, 0, 0,
                    "Project has compile errors, but details are unavailable. Recompile to surface them.",
                    true));
            }

            request.CompilerErrors.AddRange(existing);
            return true;
        }

        internal static void ScheduleCompletion()
        {
            // Drive completion polling from EditorApplication.update, NOT
            // EditorApplication.delayCall. delayCall starves while the Editor sits
            // idle and unfocused in the background (the normal state while a client
            // drives Unity through MCP), so a refresh that triggers no compilation
            // stayed "processing" until the server-side timeout fired (~120s).
            // update keeps ticking in that state -- it is the same pump the
            // main-thread dispatcher relies on -- so the idle poll settles promptly.
            EditorApplication.update -= PollCompletion;
            EditorApplication.update += PollCompletion;
        }

        private static void PollCompletion()
        {
            if (CompleteWhenIdle(EditorApplication.isCompiling, EditorApplication.isUpdating))
            {
                EditorApplication.update -= PollCompletion;
            }
        }

        internal static bool CompleteWhenIdle(bool isCompiling, bool isUpdating)
        {
            var originRequestId = CurrentRequestId();
            if (string.IsNullOrEmpty(originRequestId))
            {
                return true;
            }

            var request = FindRequest(originRequestId);
            if (request == null || !IsRefreshStarted(request))
            {
                return true;
            }

            if (isCompiling || isUpdating || compilationInProgress)
            {
                return false;
            }

            if (request.CompilationTriggered)
            {
                TryBuildTerminalReport(originRequestId);
                return true;
            }

            if (!idleObserved)
            {
                idleObserved = true;
                return false;
            }

            TryBuildTerminalReport(originRequestId);
            return true;
        }

        internal static void AppendMessages(PendingRefreshRequest request, IEnumerable<CompilerMessage> messages)
        {
            foreach (var message in messages)
            {
                if (message.IsError)
                {
                    request.CompilerErrors.Add(message);
                }
                else
                {
                    request.CompilerWarnings.Add(message);
                }
            }
        }

        internal static void AppendMessages(string originRequestId, IEnumerable<CompilerMessage> messages)
        {
            var request = FindRequest(originRequestId);
            if (request == null || !IsRefreshStarted(request))
            {
                return;
            }

            AppendMessages(request, messages);
            store.Save(request);
        }

        private static void OnAssemblyCompilationStarted(string assemblyPath)
        {
            MarkCompilationTriggered(CurrentRequestId());
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, UnityCompilerMessage[] messages)
        {
            var originRequestId = CurrentRequestId();
            AppendMessages(originRequestId, ConvertMessages(assemblyPath, messages));
        }

        private static void OnCompilationFinished(object context)
        {
            MarkCompilationFinished();
            // compilationFinished fires when compilation is truly done, even when
            // EditorApplication.isCompiling is still true at the moment of the
            // callback. Pass explicit flags so CompleteWhenIdle does not re-read
            // the stale isCompiling value and loop forever in the error-no-reload
            // case (compile error without domain reload).
            CompleteWhenIdle(false, EditorApplication.isUpdating);
        }

        private static IEnumerable<CompilerMessage> ConvertMessages(string assemblyPath, IEnumerable<UnityCompilerMessage> messages)
        {
            var assembly = Path.GetFileNameWithoutExtension(assemblyPath);
            foreach (var message in messages)
            {
                yield return new CompilerMessage(
                    assembly,
                    ProjectRelativePath(message.file),
                    message.line,
                    message.column,
                    message.message,
                    message.type == CompilerMessageType.Error);
            }
        }

        private static string ProjectRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                return path;
            }

            var projectRoot = Directory.GetParent(Application.dataPath).FullName + Path.DirectorySeparatorChar;
            var relativePath = Uri.UnescapeDataString(
                new Uri(projectRoot).MakeRelativeUri(new Uri(path)).ToString()).Replace('/', Path.DirectorySeparatorChar);
            return relativePath.StartsWith("..", StringComparison.Ordinal) ? path : relativePath;
        }

        private static string CurrentRequestId()
        {
            if (!string.IsNullOrEmpty(activeRequestId))
            {
                return activeRequestId;
            }

            if (store == null)
            {
                return null;
            }

            foreach (var request in store.LoadAll())
            {
                if (IsRefreshStarted(request))
                {
                    return request.OriginRequestId;
                }
            }

            return null;
        }

        private static PendingRefreshRequest FindRequest(string originRequestId)
        {
            if (store == null || string.IsNullOrEmpty(originRequestId))
            {
                return null;
            }

            foreach (var request in store.LoadAll())
            {
                if (request.OriginRequestId == originRequestId)
                {
                    return request;
                }
            }

            return null;
        }

        private static bool IsRefreshStarted(PendingRefreshRequest request)
        {
            return request != null &&
                request.State == "processing" &&
                (request.ExecutionState == "refresh_started" || string.IsNullOrEmpty(request.ExecutionState));
        }
    }
}
