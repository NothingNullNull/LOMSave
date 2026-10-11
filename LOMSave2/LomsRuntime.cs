using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;
using MoonSharp.Interpreter;
using OBB.Framework.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace LOMSave2
{
    public static class LomsRuntime
    {
        public static ManualLogSource Log;

        // ---- live boundary ----
        public static string BoundaryScript;
        public static GameSave BoundarySave;
        public static Dictionary<string, LomsLuaValue> BoundaryLuaGlobals;
        public static readonly List<LomsEvent> Events = new List<LomsEvent>();

        // ---- load / replay ----
        public static bool Replaying;
        public static int Cursor;
        public static int ReplayFormatVersion;
        public static bool LoadInProgress;
        private static bool _restoreLuaPending;
        public static bool AwaitingReplayScript => Replaying && _restoreLuaPending;
        public static bool LastSaveSucceeded;
        public static string LastLoadWarning;
        private static Script _currentLua;
        private static int _waitToken;
        private static readonly List<WaitPoint> WaitPoints = new List<WaitPoint>();

        private sealed class WaitPoint
        {
            public int Token;
            public string Marker;
        }

        // Preserve the outer say/choose marker if its implementation also waits.
        public static string CurrentCheckpoint => WaitPoints.Count == 0 ? null : WaitPoints[0].Marker;

        public static void SetLuaScript(Script script)
        {
            _currentLua = script;
            WaitPoints.Clear();
        }

        public static int EnterWait(string kind, string key)
        {
            int token = ++_waitToken;
            WaitPoints.Add(new WaitPoint { Token = token, Marker = kind + ":" + key });
            return token;
        }

        public static void LeaveWait(int token)
        {
            WaitPoints.RemoveAll(point => point.Token == token);
        }

        public static LomsStateSnapshot CaptureState(GameSave saved = null)
        {
            if (_currentLua == null) throw new InvalidOperationException("Current Lua interpreter unavailable");
            SaveSystem saveSystem = SaveSystem.Instance;
            if (saved == null)
            {
                saved = (GameSave)_createSaveData.Invoke(saveSystem, null);
                // CreateSaveData does not fill this flag; SaveGameData does.
                saved.IsDemo = (bool)AccessTools.Field(typeof(SaveSystem), "_isDemo").GetValue(saveSystem);
            }
            PlayerStatManagerData player = PlayerStatManagerData.Instance;
            var snapshot = LomsStateSnapshot.Capture(saved, LomsLuaState.Capture(_currentLua),
                player.CurrentStoryScript, CurrentCheckpoint,
                json => JsonUtility.FromJson<GameSave>(json));
            // GameSave contains base values. Compare the effective values too,
            // so a lost modifier can be detected even when base stats match.
            foreach (GameStat stat in player.Stats.List)
                snapshot.Values.Add("EffectiveStats[" + LomsStateSnapshot.Quote(stat.StatType.ToString()) + "].FinalValue",
                    stat.FinalValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            snapshot.Values.Add("Runtime.ActiveScene", LomsStateSnapshot.Quote(SceneManager.GetActiveScene().name));
            snapshot.Values.Add("Runtime.Music", LomsStateSnapshot.Quote(CurMusic));
            snapshot.Values.Add("Runtime.EnvironmentSound", LomsStateSnapshot.Quote(CurEnv));
            return snapshot;
        }

        // ---- dice live tracking ----
        public static bool RollPending;
        public static int FateCount;

        // ---- interaction panels ----
        public static GameObject PausePanel;
        public static GameObject LiveBlockPanel;

        // ---- visual tracking ----
        public static string CurMusic;
        public static string CurEnv;

        // ---- input lock during replay (prevent UI opened through the overlay) ----
        private static EventSystem _lockedEventSystem;
        private static readonly List<BaseInputModule> DisabledInputModules = new List<BaseInputModule>();

        private static void DisableInput()
        {
            EventSystem es = EventSystem.current;
            if (es == null) return;
            if (_lockedEventSystem != null && _lockedEventSystem != es) RestoreInput();
            _lockedEventSystem = es;
            // Keep EventSystem.current available: the game's panels call
            // SetSelectedGameObject while opening during scene initialization.
            // Disabling the modules blocks player input without breaking those calls.
            foreach (BaseInputModule module in es.GetComponents<BaseInputModule>())
            {
                if (!module.enabled) continue;
                if (!DisabledInputModules.Contains(module)) DisabledInputModules.Add(module);
                module.enabled = false;
            }
        }

        private static void RestoreInput()
        {
            foreach (BaseInputModule module in DisabledInputModules)
                if (module != null) module.enabled = true;
            DisabledInputModules.Clear();
            _lockedEventSystem = null;
        }

        public static bool InputLocked => _lockedEventSystem != null;

        // ---- config ----
        public static float ReplayTimeScale = 12f;
        public static bool ShowOverlay = true;

        // ---- UI hook (set by plugin; used by patches that run outside MonoBehaviour) ----
        public static Action<string> Notify;

        private static readonly MethodInfo _createSaveData = AccessTools.Method(typeof(SaveSystem), "CreateSaveData");

        public static bool InStoryScene
        {
            get
            {
                string n = SceneManager.GetActiveScene().name;
                return n == "Story" || n == "Break";
            }
        }

        public static void RefreshBoundary(Script script)
        {
            try
            {
                string name = PlayerStatManagerData.Instance.CurrentStoryScript;
                GameSave save = (GameSave)_createSaveData.Invoke(SaveSystem.Instance, null);
                var globals = LomsLuaState.Capture(script);
                BoundaryScript = name;
                BoundarySave = save;
                BoundaryLuaGlobals = globals;
                Events.Clear();
                Log.LogInfo($"[Loms] Boundary -> {BoundaryScript}, events cleared");
            }
            catch (Exception e)
            {
                BoundaryScript = null;
                BoundarySave = null;
                BoundaryLuaGlobals = null;
                Events.Clear();
                Log.LogWarning("[Loms] RefreshBoundary failed: " + e);
            }
        }

        public static void RestoreLuaBoundary(Script script)
        {
            if (!_restoreLuaPending) return;
            LomsLuaState.Restore(script, BoundaryLuaGlobals);
            _restoreLuaPending = false;
        }

        public static void BeginReplay(LomsSaveData sc, GameSave boundary, string slot = null)
        {
            EndReplay("superseded by new load", silent: true);
            LomsVerification.Begin(sc.ValidationSnapshot, slot);
            Events.Clear();
            if (sc.Events != null) Events.AddRange(sc.Events);
            Cursor = 0;
            ReplayFormatVersion = sc.FormatVersion;
            Replaying = true;
            LoadInProgress = true;
            BoundaryScript = sc.BoundaryScript;
            BoundarySave = boundary;
            BoundaryLuaGlobals = sc.LuaGlobals;
            _restoreLuaPending = true;
            RollPending = false;
            FateCount = 0;
            PausePanel = null;
            LiveBlockPanel = null;
            CurMusic = sc.Visual?.Music;
            CurEnv = sc.Visual?.Env;
            Time.timeScale = ReplayTimeScale;
            DisableInput();
            Log.LogInfo($"[Loms] Replay begin: boundary={sc.BoundaryScript}, events={Events.Count}");
        }

        public static T Consume<T>() where T : LomsEvent
        {
            if (!Replaying) return null;
            if (Cursor >= Events.Count)
            {
                EndReplay("cursor exhausted");
                return null;
            }
            LomsEvent e = Events[Cursor];
            if (e is T t)
            {
                Cursor++;
                return t;
            }
            Log.LogWarning($"[Loms] Cursor type mismatch @{Cursor}: expected {typeof(T).Name}, got {e.GetType().Name}");
            LomsVerification.ReplayIssue($"事件类型错位 @{Cursor}: {typeof(T).Name} != {e.GetType().Name}");
            EndReplay("type mismatch");
            return null;
        }

        public static void Record(LomsEvent e)
        {
            if (Replaying) return;
            if (!InStoryScene) return;
            Events.Add(e);
        }

        public static void EndReplay(string reason, bool silent = false)
        {
            PausePanel = null;
            RestoreInput();
            if (!Replaying) return;
            Replaying = false;
            _restoreLuaPending = false;
            Time.timeScale = 1f;
            RestoreVisual();
            if (!silent)
            {
                Log.LogInfo($"[Loms] Replay end ({reason}) at cursor {Cursor}/{Events.Count}");
                LomsVerification.ReplayFinished(reason, Cursor, Events.Count);
            }
        }

        public static void PauseForInteraction(GameObject go)
        {
            PausePanel = go;
            Time.timeScale = 1f;
            RestoreInput();
            Log.LogInfo("[Loms] Interaction pause: " + go.name);
        }

        public static void Poll()
        {
            if (PausePanel != null && !PausePanel.activeInHierarchy)
            {
                PausePanel = null;
                Log.LogInfo("[Loms] Interaction resumed");
            }
            if (LiveBlockPanel != null && !LiveBlockPanel.activeInHierarchy)
            {
                LiveBlockPanel = null;
            }
            // Enforce replay speed every frame so that scene changes (Battle, Loading1,
            // Free) entered mid-replay run at normal speed, and Story resumes boosted.
            if (Replaying)
            {
                bool boost = PausePanel == null && InStoryScene;
                Time.timeScale = boost ? ReplayTimeScale : 1f;
                // Keep input locked while fast-forwarding; free it whenever the player
                // must interact (pause panel) or the scene left Story (Battle etc).
                // EventSystem.current is re-fetched each frame: scene reloads create a
                // fresh EventSystem which must be locked again.
                if (boost)
                {
                    DisableInput();
                }
                else
                {
                    RestoreInput();
                }
            }
            else if (InputLocked)
            {
                RestoreInput();
            }
        }

        public static void FullReset()
        {
            Replaying = false;
            LoadInProgress = false;
            Cursor = 0;
            ReplayFormatVersion = 0;
            LomsFateBonus.Reset();
            Events.Clear();
            BoundaryScript = null;
            BoundarySave = null;
            BoundaryLuaGlobals = null;
            _restoreLuaPending = false;
            RollPending = false;
            FateCount = 0;
            PausePanel = null;
            LiveBlockPanel = null;
            CurMusic = null;
            CurEnv = null;
            LastLoadWarning = null;
            _currentLua = null;
            WaitPoints.Clear();
            LomsVerification.Reset();
            Time.timeScale = 1f;
            RestoreInput();
        }

        public static void RestoreVisual()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType<LuaManager>();
                if (arr == null || arr.Length == 0) return;
                LuaManager lm = arr[0];
                if (!string.IsNullOrEmpty(CurMusic)) lm.PlayMusic(CurMusic);
                if (!string.IsNullOrEmpty(CurEnv)) lm.PlayEnvSound(CurEnv);
            }
            catch (Exception e)
            {
                Log.LogWarning("[Loms] RestoreVisual failed: " + e.Message);
            }
        }

        public static void ApplyFateDeduction()
        {
            try
            {
                PlayerStatManagerData p = PlayerStatManagerData.Instance;
                if (!p.AlphaTest)
                {
                    p.Stats.Get(GameStatType.命運).AddValue(-1);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Loms] ApplyFateDeduction failed: " + e.Message);
            }
        }

        public static string HashText(string s)
        {
            if (s == null) s = "";
            using (var md5 = MD5.Create())
            {
                byte[] b = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                return b[0].ToString("X2") + b[1].ToString("X2") + b[2].ToString("X2") + b[3].ToString("X2");
            }
        }

        public static IEnumerator EmptyEnumerator()
        {
            yield break;
        }

        /// <summary>
        /// Instant replacement for CharacterIntroPanel.Show during replay:
        /// applies the only persistent state effect (relationshipStat.SetActive(true))
        /// without showing the panel or waiting for the close click.
        /// </summary>
        public static IEnumerator InstantIntro(string key)
        {
            try
            {
                if (EnumUtils.TryParseByStringValue<RelationshipStatType>(key, out RelationshipStatType t))
                {
                    RelationshipStat rs = PlayerStatManagerData.Instance.Relationships.Get(t);
                    if (!rs.Active) rs.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Loms] InstantIntro failed for '" + key + "': " + e.Message);
            }
            yield break;
        }
    }
}
