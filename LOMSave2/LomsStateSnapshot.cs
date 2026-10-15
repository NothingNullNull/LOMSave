using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Mortal.Core;

namespace LOMSave2
{
    [Serializable]
    public sealed class LomsStateSnapshot
    {
        public int Version = 1;
        public string CapturedUtc;
        public string Checkpoint;
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();

        public static LomsStateSnapshot Capture(GameSave save, Dictionary<string, LomsLuaValue> globals,
            string storyScript, string checkpoint, Func<string, GameSave> parseRestoreData = null)
        {
            if (save == null) throw new ArgumentNullException(nameof(save));
            if (!LomsLuaState.IsValid(globals)) throw new ArgumentException("Lua context unavailable", nameof(globals));
            var snapshot = new LomsStateSnapshot
            {
                CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Checkpoint = checkpoint,
            };
            snapshot.Flatten("Game", save, parseRestoreData, 0);
            snapshot.Values.Add("Runtime.CurrentStoryScript", Quote(storyScript));
            snapshot.Values.Add("Runtime.PendingInteraction", Quote(checkpoint));
            snapshot.Values.Add("Lua.$count", globals.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var pair in globals)
            {
                string path = "Lua[" + Quote(pair.Key) + "]";
                snapshot.Values.Add(path + ".Type", pair.Value.Type.ToString());
                switch (pair.Value.Type)
                {
                    case MoonSharp.Interpreter.DataType.String: snapshot.Values.Add(path + ".Value", Quote(pair.Value.Text)); break;
                    case MoonSharp.Interpreter.DataType.Number: snapshot.Values.Add(path + ".Value", pair.Value.Number.ToString("R", CultureInfo.InvariantCulture)); break;
                    case MoonSharp.Interpreter.DataType.Boolean: snapshot.Values.Add(path + ".Value", pair.Value.Boolean ? "true" : "false"); break;
                }
            }
            return snapshot;
        }

        public bool IsValid() => Version == 1 && Values != null
            && Values.ContainsKey("Game.CurrentScene") && Values.ContainsKey("Runtime.CurrentStoryScript")
            && Values.ContainsKey("Runtime.PendingInteraction") && Values.ContainsKey("Lua.$count")
            && Values.All(p => p.Value != null)
            && Values["Runtime.PendingInteraction"] == Quote(Checkpoint);

        private void Flatten(string path, object value, Func<string, GameSave> parseRestoreData, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("State nesting exceeds verification limit");
            if (value == null) { Values.Add(path, "null"); return; }
            Type type = value.GetType();
            if (value is string text) { Values.Add(path, Quote(text)); return; }
            if (value is bool flag) { Values.Add(path, flag ? "true" : "false"); return; }
            if (type.IsEnum) { Values.Add(path, value.ToString()); return; }
            if (value is double number) { Values.Add(path, number.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is float single) { Values.Add(path, single.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (type.IsPrimitive || value is decimal)
            {
                Values.Add(path, Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is IList list)
            {
                Values.Add(path + ".$count", list.Count.ToString(CultureInfo.InvariantCulture));
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    FieldInfo identity = item == null ? null : GetFields(item.GetType())
                        .FirstOrDefault(f => f.FieldType == typeof(string) && (f.Name == "Key" || f.Name == "Id" || f.Name == "Name"));
                    string id = identity?.GetValue(item) as string;
                    string key;
                    if (id == null) key = i.ToString(CultureInfo.InvariantCulture);
                    else
                    {
                        occurrences.TryGetValue(id, out int occurrence);
                        occurrences[id] = occurrence + 1;
                        key = Quote(id) + "#" + occurrence.ToString(CultureInfo.InvariantCulture);
                    }
                    Flatten(path + "[" + key + "]", item, parseRestoreData, depth + 1);
                }
                return;
            }
            FieldInfo[] fields = GetFields(type);
            if (fields.Length == 0) throw new InvalidOperationException("Unsupported state type: " + type.FullName);
            foreach (FieldInfo field in fields)
            {
                object child = field.GetValue(value);
                if (value is GameSave && field.Name == nameof(GameSave.RestoreData)
                    && child is string restore && !string.IsNullOrEmpty(restore) && parseRestoreData != null)
                {
                    child = parseRestoreData(restore) ?? throw new InvalidOperationException("Invalid RestoreData");
                }
                Flatten(path + "." + field.Name, child, parseRestoreData, depth + 1);
            }
        }

        private static FieldInfo[] GetFields(Type type)
        {
            if (!Fields.TryGetValue(type, out FieldInfo[] fields))
            {
                fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
                Fields.Add(type, fields);
            }
            return fields;
        }

        public static string Quote(string value)
        {
            if (value == null) return "null";
            var text = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': text.Append("\\\\"); break;
                    case '"': text.Append("\\\""); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (char.IsControl(c)) text.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            return text.Append('"').ToString();
        }
    }

    public sealed class LomsStateDifference
    {
        public string Path;
        public string Saved;
        public string Loaded;
        public bool Ignored;
    }

    public sealed class LomsStateComparison
    {
        public int ComparedFields;
        public readonly List<LomsStateDifference> Differences = new List<LomsStateDifference>();
        public readonly List<LomsStateDifference> IgnoredDifferences = new List<LomsStateDifference>();

        public static string IgnoreReason(string path)
        {
            if (!path.StartsWith("Game.", StringComparison.Ordinal)) return null;
            if (path.EndsWith(".TimeTick", StringComparison.Ordinal)) return "保存时间戳";
            // Match GameSave members only, including embedded restore saves.
            // A flag/Lua variable whose name contains FreeRandomState still counts.
            string member = path.Substring("Game.".Length);
            while (member.StartsWith("RestoreData.", StringComparison.Ordinal))
                member = member.Substring("RestoreData.".Length);
            return member == "FreeRandomState" || member.StartsWith("FreeRandomState.", StringComparison.Ordinal)
                ? "FreeRandomState" : null;
        }

        public static LomsStateComparison Compare(LomsStateSnapshot saved, LomsStateSnapshot loaded)
        {
            if (saved == null || !saved.IsValid() || loaded == null || !loaded.IsValid())
                throw new ArgumentException("Verification snapshot missing or unsupported");
            var result = new LomsStateComparison();
            foreach (string path in saved.Values.Keys.Union(loaded.Values.Keys).OrderBy(p => p, StringComparer.Ordinal))
            {
                bool hasSaved = saved.Values.TryGetValue(path, out string before);
                bool hasLoaded = loaded.Values.TryGetValue(path, out string after);
                bool ignored = IgnoreReason(path) != null;
                if (!ignored) result.ComparedFields++;
                if (hasSaved == hasLoaded && before == after) continue;
                var difference = new LomsStateDifference { Path = path, Saved = hasSaved ? before : "<missing>", Loaded = hasLoaded ? after : "<missing>", Ignored = ignored };
                if (ignored) result.IgnoredDifferences.Add(difference);
                else result.Differences.Add(difference);
            }
            return result;
        }
    }
}
