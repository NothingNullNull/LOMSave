using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LOMSave2
{
    // Verification observes the restored state. It never copies the expected
    // values back into the game, which would conceal replay defects.
    public static class LomsVerification
    {
        private static LomsStateSnapshot _expected;
        private static string _slot;
        private static string _reason;
        private static bool _replayComplete;
        private static float _pendingSince = -1f;
        private static readonly List<string> ReplayIssues = new List<string>();
        public static string ReportDirectory;
        public static bool AutoCheck = true;
        public static bool AutoPending { get; private set; }
        public static string LastSummary { get; private set; }
        public static string LastReportPath { get; private set; }

        public static void Begin(LomsStateSnapshot expected, string slot)
        {
            Reset();
            _expected = expected;
            _slot = slot;
        }

        public static void Reset()
        {
            _expected = null;
            _slot = null;
            _reason = null;
            _replayComplete = false;
            AutoPending = false;
            _pendingSince = -1f;
            ReplayIssues.Clear();
            LastSummary = null;
            LastReportPath = null;
        }

        public static void ReplayIssue(string message)
        {
            if (!ReplayIssues.Contains(message)) ReplayIssues.Add(message);
        }

        public static void ReplayFinished(string reason, int cursor, int total)
        {
            _reason = reason;
            _replayComplete = cursor == total;
            if (!_replayComplete) ReplayIssue("回放提前停止：" + cursor + "/" + total);
            AutoPending = AutoCheck;
            _pendingSince = -1f;
        }

        public static bool Poll(Func<LomsStateSnapshot> capture, bool atInteraction, float unscaledTime)
        {
            if (!AutoPending) return false;
            if (_pendingSince < 0) _pendingSince = unscaledTime;
            // Wait for the first live dialogue/choice after fast-forwarding.
            // With no observable interaction, produce an incomplete result
            // after two seconds rather than claiming the state was verified.
            if (!atInteraction && unscaledTime - _pendingSince < 2f) return false;
            Verify(capture, "自动：回放结束");
            return true;
        }

        public static string Verify(Func<LomsStateSnapshot> capture, string trigger = "手动：F8")
        {
            AutoPending = false;
            if (_expected == null || !_expected.IsValid())
            {
                LastSummary = "此存档没有验证基准，请用新版重新存档再读档";
                return LastSummary;
            }
            LomsStateSnapshot actual = null;
            LomsStateComparison comparison = null;
            string error = null;
            try
            {
                actual = capture();
                comparison = LomsStateComparison.Compare(_expected, actual);
            }
            catch (Exception e) { error = e.Message; }

            bool positionKnown = !string.IsNullOrEmpty(_expected.Checkpoint) && actual != null && !string.IsNullOrEmpty(actual.Checkpoint);
            bool trustworthy = error == null && _replayComplete && ReplayIssues.Count == 0 && positionKnown;
            string status = !trustworthy ? "INCOMPLETE" : comparison.Differences.Count == 0 ? "PASS" : "DIFF";
            LastSummary = status == "PASS" ? "存读档验证一致（" + comparison.ComparedFields + " 项）"
                : status == "DIFF" ? "存读档有 " + comparison.Differences.Count + " 项差异，详见验证报告"
                : "存读档验证未完成，详见验证报告";
            try
            {
                LastReportPath = WriteReport(status, trigger, actual, comparison, error, positionKnown);
            }
            catch (Exception e)
            {
                LastReportPath = null;
                LastSummary = "验证报告写入失败：" + e.Message;
            }
            return LastSummary;
        }

        private static string WriteReport(string status, string trigger, LomsStateSnapshot actual,
            LomsStateComparison comparison, string error, bool positionKnown)
        {
            if (string.IsNullOrEmpty(ReportDirectory)) throw new InvalidOperationException("Report directory is not configured");
            var report = new StringBuilder();
            report.AppendLine("存读档状态验证");
            report.AppendLine("结果：" + status + " / " + LastSummary);
            report.AppendLine("存档槽：" + LomsStateSnapshot.Quote(_slot));
            report.AppendLine("触发：" + trigger + "；回放停止原因：" + _reason);
            report.AppendLine("保存快照时间（UTC）：" + _expected.CapturedUtc);
            report.AppendLine("读档后快照时间（UTC）：" + actual?.CapturedUtc);
            report.AppendLine("范围：GameSave 全部字段（含嵌入还原存档）、属性最终值、Lua 标量全局、当前剧情脚本、场景、音乐/环境音键、等待位置。");
            report.AppendLine("排除：GameSave.TimeTick 保存时间戳；不包含 UI 动画、语音播放位置和 Lua 局部变量/表/函数。");
            report.AppendLine("随机数状态也参与对比，不会被当作正常变动忽略。");
            report.AppendLine("对比字段数：" + (comparison?.ComparedFields ?? 0) + "；差异：" + (comparison?.Differences.Count ?? 0)
                + "；时间戳变动：" + (comparison?.IgnoredDifferences.Count ?? 0));
            if (!positionKnown) report.AppendLine("未完成原因：保存时或读档后没有可确认的对话/选项/等待位置。");
            if (!_replayComplete) report.AppendLine("未完成原因：回放事件尚未全部完成。");
            if (error != null) report.AppendLine("快照错误：" + error);
            foreach (string issue in ReplayIssues) report.AppendLine("回放异常：" + issue);
            report.AppendLine();
            report.AppendLine("差异明细：字段\t保存时\t读档后");
            if (comparison != null)
                foreach (var difference in comparison.Differences)
                    report.AppendLine(difference.Path + "\t" + difference.Saved + "\t" + difference.Loaded);
            report.AppendLine();
            report.AppendLine("完整快照对照：字段\t保存时\t读档后\t判定");
            var differing = new HashSet<string>(comparison?.Differences.Select(d => d.Path) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var ignored = new HashSet<string>(comparison?.IgnoredDifferences.Select(d => d.Path) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (string path in _expected.Values.Keys.Union(actual?.Values?.Keys ?? Enumerable.Empty<string>()).OrderBy(p => p, StringComparer.Ordinal))
            {
                bool beforeExists = _expected.Values.TryGetValue(path, out string before);
                string after = null;
                bool afterExists = actual?.Values != null && actual.Values.TryGetValue(path, out after);
                report.AppendLine(path + "\t" + (beforeExists ? before : "<missing>") + "\t" + (afterExists ? after : "<missing>")
                    + "\t" + (actual == null || comparison == null ? "未对比" : differing.Contains(path) ? "差异" : ignored.Contains(path) ? "时间戳" : "一致"));
            }
            string directory = Path.GetFullPath(ReportDirectory);
            Directory.CreateDirectory(directory);
            string safeSlot = new string((_slot ?? "unknown").Take(40).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            string pathName = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + safeSlot + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            File.WriteAllText(pathName, report.ToString(), new UTF8Encoding(true));
            string latest = Path.Combine(directory, "latest.txt");
            string temporary = latest + ".tmp";
            try
            {
                File.Copy(pathName, temporary, true);
                if (File.Exists(latest)) File.Replace(temporary, latest, null);
                else File.Move(temporary, latest);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return pathName;
        }
    }
}
