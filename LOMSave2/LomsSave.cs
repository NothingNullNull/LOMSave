using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using HarmonyLib;
using Mortal.Core;

namespace LOMSave2
{
    [Serializable]
    public abstract class LomsEvent
    {
    }

    [Serializable]
    public class SayEvent : LomsEvent
    {
        public string Hash;
    }

    [Serializable]
    public sealed class WaitEvent : LomsEvent
    {
        public string Duration;
    }

    [Serializable]
    public class ChooseEvent : LomsEvent
    {
        public int Sel;
    }

    [Serializable]
    public class DiceEvent : LomsEvent
    {
        public int Sel;
        public bool Fate;
    }

    [Serializable]
    public class RandEvent : LomsEvent
    {
        public double V;
    }

    [Serializable]
    public sealed class NativeCheckpointEvent : LomsEvent
    {
        public string Kind;
        public string Key;
        public string Result;
    }

    [Serializable]
    public class PanelEvent : LomsEvent
    {
        public string K;
    }

    [Serializable]
    public class FateBonusEvent : LomsEvent
    {
        public List<FateBonusChange> Changes = new List<FateBonusChange>();
    }

    [Serializable]
    public class FateBonusChange
    {
        public string Key;
        public int Before;
        public int After;
    }

    [Serializable]
    public class LomsVisual
    {
        public string Music;
        public string Env;
    }

    [Serializable]
    public class LomsSaveData
    {
        public const int CurrentFormatVersion = 5;
        public int FormatVersion = CurrentFormatVersion;
        public string BoundaryScript;
        public GameSave BoundarySave;
        public List<LomsEvent> Events = new List<LomsEvent>();
        public string Scene;
        public LomsVisual Visual;
        [OptionalField] public string NativeSaveHash;
        [OptionalField] public Dictionary<string, LomsLuaValue> LuaGlobals;
        // Diagnostic data is optional: older format-3 saves still replay.
        [OptionalField] public LomsStateSnapshot ValidationSnapshot;
    }

    public static class LomsSave
    {
        public static string NativePath(string slot)
        {
            var mi = AccessTools.Method(typeof(SaveSystem), "GetSaveDataFile");
            return (string)mi.Invoke(SaveSystem.Instance, new object[] { slot });
        }

        public static string SidecarPath(string slot) => NativePath(slot) + ".loms";

        public static bool Exists(string slot)
        {
            try
            {
                return File.Exists(NativePath(slot));
            }
            catch
            {
                return false;
            }
        }

        public static LomsSaveData TryRead(string slot, GameSave nativeSave, out string warning)
        {
            return TryReadFile(NativePath(slot), nativeSave, out warning);
        }

        public static LomsSaveData TryReadFile(string nativePath, GameSave nativeSave, out string warning)
        {
            warning = null;
            try
            {
                string path = nativePath + ".loms";
                if (!File.Exists(path)) return null;
                var fmt = new BinaryFormatter();
                LomsSaveData data;
                using (var fs = File.OpenRead(path))
                {
                    data = (LomsSaveData)fmt.Deserialize(fs);
                }
                if (data == null || (data.FormatVersion != 3 && data.FormatVersion != 4
                    && data.FormatVersion != LomsSaveData.CurrentFormatVersion))
                {
                    warning = "附加存档版本不兼容，已按原生存档读取；重新存档后启用精确回放";
                    return null;
                }
                if (nativeSave == null || string.IsNullOrEmpty(data.NativeSaveHash)
                    || !string.Equals(data.NativeSaveHash, HashFile(nativePath), StringComparison.Ordinal))
                {
                    warning = "附加存档与原生存档不匹配，已按原生存档读取";
                    return null;
                }
                if ((data.Scene != "Story" && data.Scene != "Break") || data.Scene != nativeSave.CurrentScene
                    || data.BoundarySave == null || string.IsNullOrEmpty(data.BoundaryScript)
                    || !LomsLuaState.IsValid(data.LuaGlobals) || data.Events == null
                    || data.Events.Exists(e => e == null || (e is WaitEvent wait && wait.Duration == null)
                        || (e is FateBonusEvent fate && !LomsFateBonus.IsValid(fate))
                        || (e is NativeCheckpointEvent checkpoint && !IsValidCheckpoint(checkpoint))))
                {
                    warning = "附加存档资料不完整，已按原生存档读取";
                    return null;
                }
                return data;
            }
            catch (Exception e)
            {
                warning = "附加存档无法读取，已按原生存档读取 (" + e.Message + ")";
                return null;
            }
        }

        // The game catches its own IO errors. Verify the actual committed bytes
        // against the attempted GameSave before updating or removing a sidecar.
        public static bool WasCommitted(string nativePath, GameSave attempted, out string fingerprint)
        {
            fingerprint = HashFile(nativePath);
            using (var stream = new MemoryStream())
            using (var sha = SHA256.Create())
            {
                new BinaryFormatter().Serialize(stream, attempted);
                stream.Position = 0;
                string expected = Convert.ToBase64String(sha.ComputeHash(stream));
                return string.Equals(fingerprint, expected, StringComparison.Ordinal);
            }
        }

        public static bool IsValidCheckpoint(NativeCheckpointEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Result)) return false;
            if (e.Kind == "Position") return true;
            if (e.Kind == "Condition") return e.Result == "true" || e.Result == "false";
            return e.Kind == "Switch" && int.TryParse(e.Result, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int result) && result >= 0;
        }

        public static bool CompleteSave(string nativePath, GameSave attempted, LomsSaveData storyData)
        {
            if (!WasCommitted(nativePath, attempted, out string fingerprint)) return false;
            if (storyData == null) DeleteFile(nativePath);
            else
            {
                storyData.NativeSaveHash = fingerprint;
                WriteFile(nativePath, storyData);
            }
            return true;
        }

        public static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(stream));
            }
        }

        public static void WriteFile(string nativePath, LomsSaveData data)
        {
            string path = nativePath + ".loms";
            string temp = path + ".tmp";
            try
            {
                using (var fs = File.Create(temp))
                {
                    new BinaryFormatter().Serialize(fs, data);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public static void DeleteFile(string nativePath)
        {
            string path = nativePath + ".loms";
            if (File.Exists(path)) File.Delete(path);
        }

        public static void Delete(string slot)
        {
            try
            {
                DeleteFile(NativePath(slot));
            }
            catch (Exception e)
            {
                LomsRuntime.Log?.LogWarning("[LomsSave] Delete failed: " + e.Message);
            }
        }
    }
}
