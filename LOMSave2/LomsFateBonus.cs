using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Core;

namespace LOMSave2
{
    // Only the panel's editable stats and its fate cost belong to this event.
    // Verification snapshots are never used to restore gameplay state.
    public static class LomsFateBonus
    {
        private sealed class Target
        {
            public Func<int> Read;
            public Action<int> Write;
            public Func<int, bool> CanWrite = value => true;
        }

        private sealed class Session
        {
            public FateBonusPanel Panel;
            public Dictionary<string, int> Before;
            public bool LegacyReplay;
            public int Cursor;
        }

        private static Session _session;
        public static void Reset() => _session = null;

        public static bool IsValid(FateBonusEvent value)
        {
            if (value?.Changes == null) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            return value.Changes.All(c => c != null && !string.IsNullOrEmpty(c.Key)
                && (c.Key.StartsWith("stat:", StringComparison.Ordinal) || c.Key.StartsWith("social:", StringComparison.Ordinal)
                    || c.Key.StartsWith("flag:", StringComparison.Ordinal))
                && c.Key.IndexOf(':') < c.Key.Length - 1 && c.Before != c.After
                && (long)c.After - c.Before <= int.MaxValue && (long)c.After - c.Before >= int.MinValue
                && keys.Add(c.Key));
        }

        public static FateBonusEvent Difference(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            if (before.Count != after.Count || before.Keys.Any(k => !after.ContainsKey(k)))
                throw new InvalidOperationException("Fate panel targets changed while open");
            var result = new FateBonusEvent();
            foreach (string key in before.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (before[key] != after[key])
                    result.Changes.Add(new FateBonusChange { Key = key, Before = before[key], After = after[key] });
            if (!IsValid(result)) throw new InvalidOperationException("Invalid fate panel result");
            return result;
        }

        public static bool Matches(FateBonusEvent value, Dictionary<string, int> current, out string reason)
        {
            reason = null;
            if (!IsValid(value)) { reason = "命运点数记录不完整"; return false; }
            foreach (FateBonusChange change in value.Changes)
            {
                if (!current.TryGetValue(change.Key, out int actual))
                { reason = "命运点数项目不存在：" + change.Key; return false; }
                if (actual != change.Before)
                { reason = "命运点数起始值不同：" + change.Key + "，预期 " + change.Before + "，实际 " + actual; return false; }
            }
            return true;
        }

        public static bool TryReplay(FateBonusPanel panel)
        {
            if (!LomsRuntime.Replaying) return false;
            if (LomsRuntime.Cursor >= LomsRuntime.Events.Count)
            {
                LomsRuntime.EndReplay("fate panel at cursor end");
                return false;
            }
            var value = LomsRuntime.Events[LomsRuntime.Cursor] as FateBonusEvent;
            if (value == null && LomsRuntime.ReplayFormatVersion == 3)
            {
                // Old saves contain no panel choices. Once the player confirms
                // them here, insert that known operation into the replay prefix.
                LomsRuntime.Notify?.Invoke("旧存档未记录命运点数；本次选择后重新存档即可自动回放");
                return false;
            }
            try
            {
                var targets = Bind(panel);
                if (!Matches(value, Read(targets), out string reason)) throw new InvalidOperationException(reason);
                foreach (FateBonusChange change in value.Changes)
                    if (!targets[change.Key].CanWrite(change.After))
                        throw new InvalidOperationException("命运点数超出目前上限：" + change.Key);
                foreach (FateBonusChange change in value.Changes) targets[change.Key].Write(change.After);
                foreach (FateBonusChange change in value.Changes)
                    if (targets[change.Key].Read() != change.After)
                        throw new InvalidOperationException("命运点数应用结果不同：" + change.Key);
                LomsRuntime.Consume<FateBonusEvent>();
                LomsRuntime.Log?.LogInfo("[Loms] Fate bonus replayed: changes=" + value.Changes.Count);
                return true;
            }
            catch (Exception e)
            {
                LomsVerification.ReplayIssue(e.Message);
                LomsRuntime.Log?.LogWarning("[Loms] Fate bonus replay stopped: " + e);
                LomsRuntime.EndReplay("fate bonus mismatch");
                // Do not retain unexecuted events in a subsequently saved run.
                LomsRuntime.Events.RemoveRange(LomsRuntime.Cursor, LomsRuntime.Events.Count - LomsRuntime.Cursor);
                LomsRuntime.Notify?.Invoke("命运点数回放异常，请手动确认；详情见验证报告");
                return false;
            }
        }

        public static void Begin(FateBonusPanel panel)
        {
            _session = null;
            try
            {
                _session = new Session { Panel = panel, Before = Read(Bind(panel)),
                    LegacyReplay = LomsRuntime.Replaying, Cursor = LomsRuntime.Cursor };
            }
            catch (Exception e)
            {
                LomsRuntime.Log?.LogWarning("[Loms] Fate bonus capture failed: " + e);
                LomsRuntime.Notify?.Invoke("命运点数记录失败，读档后需手动确认");
            }
        }

        public static IEnumerator Track(FateBonusPanel panel, IEnumerator inner)
        {
            Session tracked = null;
            try
            {
                while (inner.MoveNext())
                {
                    if (_session?.Panel == panel) tracked = _session;
                    yield return inner.Current;
                }
                if (tracked != null && ReferenceEquals(_session, tracked)) Complete(tracked);
            }
            finally
            {
                (inner as IDisposable)?.Dispose();
                if (tracked != null && ReferenceEquals(_session, tracked)) _session = null;
            }
        }

        private static void Complete(Session session)
        {
            try
            {
                FateBonusEvent value = Difference(session.Before, Read(Bind(session.Panel)));
                if (session.LegacyReplay)
                {
                    if (!LomsRuntime.Replaying || LomsRuntime.Cursor != session.Cursor) return;
                    LomsRuntime.Events.Insert(LomsRuntime.Cursor++, value);
                }
                else LomsRuntime.Record(value);
                LomsRuntime.Log?.LogInfo("[Loms] Fate bonus recorded: changes=" + value.Changes.Count);
            }
            catch (Exception e)
            {
                if (session.LegacyReplay) LomsVerification.ReplayIssue("命运点数记录失败：" + e.Message);
                LomsRuntime.Log?.LogWarning("[Loms] Fate bonus capture failed: " + e);
            }
        }

        private static Dictionary<string, int> Read(Dictionary<string, Target> targets)
            => targets.ToDictionary(p => p.Key, p => p.Value.Read(), StringComparer.Ordinal);

        private static Dictionary<string, Target> Bind(FateBonusPanel panel)
        {
            var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
            Action<GameStat> addStat = null;
            addStat = stat =>
            {
                string key = "stat:" + stat.StatType;
                if (targets.ContainsKey(key)) return;
                targets.Add(key, new Target { Read = () => stat.Value, Write = stat.SetValue,
                    CanWrite = value => value >= stat.Min && value <= stat.Max });
                // Positive native additions may also update a cumulative stat.
                // Capture that related value separately, so repeated commits
                // are reproduced without counting the addition twice.
                var total = (GameStat)AccessTools.Field(typeof(GameStat), "_totalAddData").GetValue(stat);
                if (total != null) addStat(total);
            };
            addStat(PlayerStatManagerData.Instance.Stats.Get(GameStatType.命運));
            var properties = (PlayerBonusStatData)AccessTools.Field(typeof(FateBonusPanel), "_propertyData").GetValue(panel);
            foreach (var model in properties.List) addStat(model.Stat);
            var social = (SocialBonusStatData)AccessTools.Field(typeof(FateBonusPanel), "_socialData").GetValue(panel);
            foreach (var model in social.List)
            {
                RelationshipStat stat = model.Stat;
                targets.Add("social:" + stat.Type, new Target { Read = () => stat.Value, Write = stat.SetValue });
            }
            var flags = (FlagBonusStatData)AccessTools.Field(typeof(FateBonusPanel), "_flagData").GetValue(panel);
            foreach (var model in flags.List)
            {
                FlagData stat = model.Stat;
                targets.Add("flag:" + stat.name, new Target { Read = () => stat.State, Write = value => stat.State = value });
            }
            return targets;
        }
    }
}
