// VrcfQolHotReload.cs
//
// Makes Unity pick up script changes while its window is open but not focused,
// and exports compile errors/warnings to a known log file so external tools
// (or a human over a terminal) can tail them without opening the Console.
//
// Behaviour:
//   * FileSystemWatcher on Application.dataPath (recursive, *.cs) flips a flag
//     whenever a script is written, created, renamed or deleted.
//   * EditorApplication.update debounces the flag (~0.4 s) and then calls
//     AssetDatabase.Refresh(). Unity happily refreshes from scripted call even
//     when the editor window isn't focused, which is the whole point.
//   * CompilationPipeline.assemblyCompilationFinished appends a one-line
//     summary per assembly plus one line per error to:
//         <ProjectRoot>/Logs/VrcfQolHotReload.log
//   * The startup banner, every Refresh(), and every compile result get logged
//     too, so you can see whether the watcher is actually firing.
//
// The feature is zero-config: drop the script in Assets/Editor and Unity will
// load it via [InitializeOnLoad]. If the watcher fails to start (permission
// error, Mono sandbox weirdness), the tool logs and silently degrades -
// Unity's normal focus-based refresh still works.

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UmeVrcfQol {

    [InitializeOnLoad]
    internal static class VrcfQolHotReload {
        private const string LogSubdir = "Logs";
        private const string LogFileName = "VrcfQolHotReload.log";
        private const double DebounceSeconds = 0.4;
        private const long MaxLogBytes = 512 * 1024; // roll at 512 KB

        private static FileSystemWatcher _watcher;
        private static volatile bool _pendingRefresh;
        private static double _refreshDueAt;
        private static int _refreshCounter;

        static VrcfQolHotReload() {
            string projectRoot;
            try { projectRoot = Directory.GetParent(Application.dataPath)?.FullName; }
            catch { projectRoot = null; }
            if (string.IsNullOrEmpty(projectRoot)) return;

            EnsureLogDir(projectRoot);
            RollIfLarge(projectRoot);
            Log("----- Startup -----");
            Log($"Unity={Application.unityVersion} project={projectRoot}");

            try {
                _watcher = new FileSystemWatcher(Application.dataPath) {
                    IncludeSubdirectories = true,
                    Filter = "*.cs",
                    NotifyFilter = NotifyFilters.LastWrite
                                 | NotifyFilters.FileName
                                 | NotifyFilters.Size
                                 | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnChange;
                _watcher.Created += OnChange;
                _watcher.Deleted += OnChange;
                _watcher.Renamed += OnRename;
                _watcher.Error   += (s, e) => Log("[Watcher] Error: " + e.GetException()?.Message);
                Log("[Watcher] Active on " + Application.dataPath);
            } catch (Exception ex) {
                Log("[Watcher] Failed to start: " + ex.Message);
            }

            EditorApplication.update += Tick;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationStarted += OnCompileStarted;
            CompilationPipeline.compilationFinished += OnCompileFinished;
            AssemblyReloadEvents.beforeAssemblyReload += () => Log("[Reload] beforeAssemblyReload");
            AssemblyReloadEvents.afterAssemblyReload  += () => Log("[Reload] afterAssemblyReload");
        }

        // ----- change detection -----

        private static void OnChange(object s, FileSystemEventArgs e) {
            if (ShouldIgnore(e.FullPath)) return;
            _pendingRefresh = true;
            _refreshDueAt = EditorApplication.timeSinceStartup + DebounceSeconds;
            Log($"[Watcher] {e.ChangeType} {TrimPath(e.FullPath)}");
        }

        private static void OnRename(object s, RenamedEventArgs e) {
            if (ShouldIgnore(e.FullPath) && ShouldIgnore(e.OldFullPath)) return;
            _pendingRefresh = true;
            _refreshDueAt = EditorApplication.timeSinceStartup + DebounceSeconds;
            Log($"[Watcher] Renamed {TrimPath(e.OldFullPath)} -> {TrimPath(e.FullPath)}");
        }

        private static bool ShouldIgnore(string path) {
            if (string.IsNullOrEmpty(path)) return true;
            var name = Path.GetFileName(path);
            // Unity's own temp artefacts.
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".TMP", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("~", StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Tick() {
            if (!_pendingRefresh) return;
            if (EditorApplication.timeSinceStartup < _refreshDueAt) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _pendingRefresh = false;
            _refreshCounter++;
            Log($"[Refresh] AssetDatabase.Refresh() #{_refreshCounter}");
            try { AssetDatabase.Refresh(); }
            catch (Exception ex) { Log("[Refresh] Failed: " + ex.Message); }
        }

        // ----- compile result logging -----

        private static void OnCompileStarted(object ctx) {
            Log("[Compile] Started");
        }

        private static void OnCompileFinished(object ctx) {
            Log("[Compile] Finished");
        }

        private static void OnAssemblyCompiled(string asmPath, CompilerMessage[] messages) {
            int errors = 0, warnings = 0;
            if (messages != null) {
                for (int i = 0; i < messages.Length; i++) {
                    if (messages[i].type == CompilerMessageType.Error) errors++;
                    else if (messages[i].type == CompilerMessageType.Warning) warnings++;
                }
            }
            var asmName = Path.GetFileName(asmPath ?? "(unknown)");
            Log($"[Asm] {asmName} errors={errors} warnings={warnings}");

            if (messages == null) return;
            foreach (var m in messages) {
                if (m.type != CompilerMessageType.Error) continue;
                var where = string.IsNullOrEmpty(m.file) ? "" : $" {TrimPath(m.file)}({m.line},{m.column})";
                Log($"[Error]{where}: {m.message}");
            }
            // Warnings stay off by default; flip this on if it becomes useful.
            // foreach (var m in messages) {
            //     if (m.type != CompilerMessageType.Warning) continue;
            //     var where = string.IsNullOrEmpty(m.file) ? "" : $" {TrimPath(m.file)}({m.line},{m.column})";
            //     Log($"[Warn]{where}: {m.message}");
            // }
        }

        // ----- logging plumbing -----

        private static string GetLogDir() {
            var root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            return Path.Combine(root, LogSubdir);
        }

        private static string GetLogPath() => Path.Combine(GetLogDir(), LogFileName);

        private static void EnsureLogDir(string projectRoot) {
            try {
                var dir = Path.Combine(projectRoot, LogSubdir);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            } catch { /* swallow */ }
        }

        private static void RollIfLarge(string projectRoot) {
            try {
                var path = Path.Combine(projectRoot, LogSubdir, LogFileName);
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length < MaxLogBytes) return;
                var rolled = Path.Combine(projectRoot, LogSubdir, LogFileName + ".old");
                if (File.Exists(rolled)) File.Delete(rolled);
                File.Move(path, rolled);
            } catch { /* swallow */ }
        }

        private static string TrimPath(string p) {
            if (string.IsNullOrEmpty(p)) return p;
            try {
                var root = Directory.GetParent(Application.dataPath)?.FullName;
                if (!string.IsNullOrEmpty(root) && p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                    return p.Substring(root.Length).TrimStart('/', '\\');
                }
            } catch { /* ignore */ }
            return p;
        }

        private static void Log(string line) {
            try {
                var sb = new StringBuilder(line.Length + 24);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(' ');
                sb.Append(line);
                sb.Append('\n');
                File.AppendAllText(GetLogPath(), sb.ToString());
            } catch { /* swallow - never let logging break anything */ }
        }
    }
}
