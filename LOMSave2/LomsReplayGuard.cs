using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Mortal.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMSave2
{
    // Unity invokes log callbacks from inside the operation that failed. Keep
    // those callbacks observational; unwind replay only from the next Update.
    public static class LomsReplayGuard
    {
        private const string LogPrefix = "[Loms] Replay guard";
        private static readonly object PendingLock = new object();
        private static PendingFailure _pending;
        private static bool _handlingFailure;
        private static long _lastPoll;
        private static double _activeSeconds;
        private static int _cursor;
        private static int _sceneHandle;
        private static string _boundary;
        private static string _checkpoint;

        public static float NoProgressTimeoutSeconds = 45f;
        public static bool RecoveryRequired { get; private set; }
        public static string LastFailureReportPath { get; private set; }
        public static string RecoveryMessage { get; private set; }

        private sealed class PendingFailure
        {
            public string Condition;
            public string StackTrace;
            public LogType Type;
            public string CapturedUtc;
        }

        public static void BeginReplay()
        {
            Reset();
            RememberProgress(SceneManager.GetActiveScene());
        }

        public static void Reset()
        {
            lock (PendingLock)
            {
                _pending = null;
                _handlingFailure = false;
            }
            RecoveryRequired = false;
            RecoveryMessage = null;
            // The report remains on disk, and its path is useful after a retry.
            _lastPoll = Stopwatch.GetTimestamp();
            _activeSeconds = 0;
            _cursor = -1;
            _sceneHandle = -1;
            _boundary = null;
            _checkpoint = null;
        }

        public static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (!LomsRuntime.Replaying || RecoveryRequired) return;
            if (type != LogType.Exception && (type != LogType.Error || !IsLuaError(condition, stackTrace))) return;
            if ((condition ?? "").StartsWith(LogPrefix, StringComparison.Ordinal)) return;
            lock (PendingLock)
            {
                if (_handlingFailure || _pending != null || !LomsRuntime.Replaying) return;
                _pending = new PendingFailure
                {
                    Condition = condition ?? "<empty>",
                    StackTrace = stackTrace ?? "<empty>",
                    Type = type,
                    CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
            }
        }

        private static bool IsLuaError(string condition, string stackTrace)
        {
            string message = condition ?? "";
            string trace = stackTrace ?? "";
            if (Contains(trace, "MoonSharp.") || Contains(trace, "Fungus.Lua") || Contains(trace, "LuaManager")) return true;
            return Contains(message, "ScriptRuntimeException")
                || Contains(message, "SyntaxErrorException")
                || Contains(message, "lua error") || Contains(message, "lua: error")
                || (Contains(message, "lua") && (Contains(message, "exception") || Contains(message, "stack traceback")));
        }

        private static bool Contains(string text, string fragment)
        {
            return text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Poll()
        {
            PendingFailure pending;
            lock (PendingLock)
            {
                if (_handlingFailure) return;
                pending = _pending;
                _pending = null;
            }
            // An error captured during replay still counts if another callback
            // happened to end replay before Update. FullReset clears stale errors.
            if (pending != null)
            {
                Fail("运行异常", pending);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = _lastPoll == 0 ? 0 : (double)(now - _lastPoll) / Stopwatch.Frequency;
            _lastPoll = now;
            if (!LomsRuntime.Replaying || RecoveryRequired) return;

            Scene scene = SceneManager.GetActiveScene();
            if (_cursor != LomsRuntime.Cursor || _sceneHandle != scene.handle
                || _boundary != LomsRuntime.BoundaryScript || _checkpoint != LomsRuntime.CurrentCheckpoint)
            {
                RememberProgress(scene);
                return;
            }

            if (NoProgressTimeoutSeconds <= 0 || float.IsNaN(NoProgressTimeoutSeconds)
                || float.IsInfinity(NoProgressTimeoutSeconds)) return;
            // Time spent in battles, loading, or player-operated panels must
            // never be treated as a hung fast-forward operation.
            if (!scene.isLoaded || (scene.name != "Story" && scene.name != "Break")
                || LomsRuntime.LoadInProgress || LomsRuntime.AwaitingReplayScript
                || IsActive(LomsRuntime.PausePanel) || IsActive(LomsRuntime.LiveBlockPanel)
                || SceneController.Instance == null || SceneController.Instance.IsPrepare || SceneController.Instance.IsLoading) return;

            // A frame blocked for seconds can be synchronous scene/asset loading
            // or an app suspension; only accumulate continuously observed time.
            if (elapsed <= 0 || elapsed > 5) return;
            _activeSeconds += elapsed;
            if (_activeSeconds >= NoProgressTimeoutSeconds)
                Fail("剧情回放连续 " + _activeSeconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒没有推进", null);
        }

        private static bool IsActive(GameObject panel)
        {
            return panel != null && panel.activeInHierarchy;
        }

        private static void RememberProgress(Scene scene)
        {
            _cursor = LomsRuntime.Cursor;
            _sceneHandle = scene.handle;
            _boundary = LomsRuntime.BoundaryScript;
            _checkpoint = LomsRuntime.CurrentCheckpoint;
            _activeSeconds = 0;
            _lastPoll = Stopwatch.GetTimestamp();
        }

        private static void Fail(string reason, PendingFailure failure)
        {
            lock (PendingLock)
            {
                if (_handlingFailure || RecoveryRequired) return;
                _handlingFailure = true;
            }
            RecoveryRequired = true;
            RecoveryMessage = "回放失败，已停止并解锁。当前进度不完整，暂不能保存。请重新读取当前存档，或从菜单读取其他存档。";
            string diagnostic = DescribeFailure(reason, failure);
            try
            {
                LomsVerification.ReplayIssue(diagnostic);
                // A timed-out coroutine can resume after resources arrive.
                // Cancel it before unlocking, so a failed replay cannot later
                // advance the story or execute an automatic save in the background.
                foreach (Fungus.LuaEnvironment environment in UnityEngine.Object.FindObjectsOfType<Fungus.LuaEnvironment>())
                    environment.StopAllCoroutines();
                foreach (Mortal.Story.LuaManager manager in UnityEngine.Object.FindObjectsOfType<Mortal.Story.LuaManager>())
                    manager.StopAllCoroutines();
                // ExecuteLoadGameData may have thrown before its postfix.
                LomsRuntime.LoadInProgress = false;
                LomsRuntime.EndReplay("guard: " + reason);
                LomsRuntime.LastLoadWarning = RecoveryMessage;
                WriteFailureReport(diagnostic);
                LomsRuntime.Log?.LogError(LogPrefix + " stopped replay:\n" + diagnostic
                    + (LastFailureReportPath == null ? "" : "\nReport: " + LastFailureReportPath));
                LomsRuntime.Notify?.Invoke(RecoveryMessage);
            }
            finally
            {
                lock (PendingLock)
                {
                    _pending = null;
                    _handlingFailure = false;
                }
            }
        }

        private static string DescribeFailure(string reason, PendingFailure failure)
        {
            Scene scene = SceneManager.GetActiveScene();
            string next = LomsRuntime.Cursor >= 0 && LomsRuntime.Cursor < LomsRuntime.Events.Count
                ? LomsRuntime.Events[LomsRuntime.Cursor]?.GetType().Name ?? "<null>" : "<none>";
            var report = new StringBuilder();
            report.AppendLine("回放保护停止：" + reason);
            report.AppendLine("UTC：" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            report.AppendLine("场景：" + scene.name + " (handle " + scene.handle + ", loaded " + scene.isLoaded + ")");
            report.AppendLine("BoundaryScript：" + (LomsRuntime.BoundaryScript ?? "<null>"));
            report.AppendLine("事件位置：" + LomsRuntime.Cursor + "/" + LomsRuntime.Events.Count + "；下个事件：" + next);
            report.AppendLine("CurrentCheckpoint：" + (LomsRuntime.CurrentCheckpoint ?? "<null>"));
            report.AppendLine("LoadInProgress：" + LomsRuntime.LoadInProgress + "；AwaitingReplayScript：" + LomsRuntime.AwaitingReplayScript);
            report.AppendLine("无进度有效秒数：" + _activeSeconds.ToString("F3", CultureInfo.InvariantCulture)
                + "；超时配置：" + NoProgressTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            if (failure != null)
            {
                report.AppendLine("异常记录 UTC：" + failure.CapturedUtc + "；日志类型：" + failure.Type);
                report.AppendLine("原始错误：" + failure.Condition);
                report.AppendLine("原始调用栈：\n" + failure.StackTrace);
            }
            report.AppendLine("恢复要求：读取其他存档或重试读取；当前状态禁止保存。未自动跳过剧情或修改存档。");
            return report.ToString();
        }

        private static void WriteFailureReport(string diagnostic)
        {
            LastFailureReportPath = null;
            try
            {
                if (string.IsNullOrEmpty(LomsVerification.ReportDirectory)) return;
                string directory = Path.GetFullPath(LomsVerification.ReportDirectory);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "replay-failure-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                File.WriteAllText(path, diagnostic, new UTF8Encoding(true));
                LastFailureReportPath = path;
            }
            catch (Exception e)
            {
                LomsRuntime.Log?.LogWarning(LogPrefix + " could not write diagnostic report: " + e);
            }
        }
    }
}
