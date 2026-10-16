using System.Collections;
using Fungus;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace LOMSave2
{
    [HarmonyPatch]
    public static class Patches
    {
        // ============ Lua injection ============

        [HarmonyPatch(typeof(LuaEnvironment), "InitEnvironment")]
        [HarmonyPostfix]
        public static void LuaEnvironment_InitEnvironment_Postfix(LuaEnvironment __instance)
        {
            try
            {
                var script = __instance.Interpreter;
                if (script == null) return;
                if (script.Globals.Get("__loms__").Type != MoonSharp.Interpreter.DataType.Nil) return;
                script.Globals.Set("__loms__", MoonSharp.Interpreter.DynValue.NewBoolean(true));

                if (!_luaRegistered)
                {
                    MoonSharp.Interpreter.UserData.RegisterType(typeof(LomsLua));
                    _luaRegistered = true;
                }
                script.Globals["loms"] = MoonSharp.Interpreter.UserData.CreateStatic(typeof(LomsLua));
                script.DoString(LomsPreamble.Code);
                LomsRuntime.Log?.LogInfo("[Loms] Lua preamble injected");
            }
            catch (System.Exception e)
            {
                LomsRuntime.Log?.LogWarning("[Loms] Lua inject failed: " + e);
            }
        }

        private static bool _luaRegistered;

        // ============ Boundary refresh ============

        [HarmonyPatch(typeof(LuaManager), "ExecuteLuaScript")]
        [HarmonyPrefix]
        public static void LuaManager_ExecuteLuaScript_Prefix(LuaManager __instance, LuaEnvironment ____luaEnvironment)
        {
            LomsRuntime.SetLuaScript(____luaEnvironment.Interpreter);
            bool keepRoot = LomsRuntime.BindBoundaryOwner(__instance);
            if (LomsRuntime.LoadInProgress) return;
            if (LomsRuntime.Replaying)
            {
                // A load creates a new Lua interpreter. Restore variables from
                // the boundary before even the first instruction is executed.
                LomsRuntime.RestoreLuaBoundary(____luaEnvironment.Interpreter);
                if (LomsRuntime.Cursor >= LomsRuntime.Events.Count)
                {
                    LomsRuntime.EndReplay("exhausted at script start");
                }
                else
                {
                    return; // still replaying into the next script
                }
            }
            // SetStartScript/AutoSave may run halfway through this same file.
            // Only the actual script entry is a restartable replay boundary.
            // SetNextScript + Init reuses this scene's characters, background and
            // stage. A child script is not a self-contained restart point.
            if (keepRoot)
            {
                LomsRuntime.Log?.LogInfo($"[Loms] Continue script {__instance.ScriptName}, keeping scene root {LomsRuntime.BoundaryScript} ({LomsRuntime.Events.Count} events)");
                return;
            }
            LomsRuntime.RefreshBoundary(____luaEnvironment.Interpreter);
        }

        [HarmonyPatch(typeof(LuaEnvironment), "LoadLuaFunction", new System.Type[] { typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static void LuaEnvironment_LoadLuaFunction_Prefix(ref string luaString, string friendlyName)
        {
            luaString = LomsLegacyBootstrap.Prepend(luaString, friendlyName);
        }

        [HarmonyPatch(typeof(LuaEnvironment), "LogException")]
        [HarmonyPrefix]
        public static void LuaEnvironment_LogException_Prefix(string decoratedMessage, string debugInfo)
        {
            LomsReplayGuard.OnLogMessage(decoratedMessage, "Fungus.LuaEnvironment.LogException\n" + debugInfo, LogType.Error);
        }

        // ============ Save: gate + write sidecar ============

        [HarmonyPatch(typeof(SaveSystem), "ExecuteSaveData")]
        [HarmonyPrefix]
        public static bool SaveSystem_ExecuteSaveData_RecoveryPrefix()
        {
            return !LomsReplayGuard.RecoveryRequired;
        }

        [HarmonyPatch(typeof(SaveSystem), "ExecuteSaveJsonData")]
        [HarmonyPrefix]
        public static bool SaveSystem_ExecuteSaveJsonData_RecoveryPrefix()
        {
            return !LomsReplayGuard.RecoveryRequired;
        }

        [HarmonyPatch(typeof(SaveSystem), "SaveGameData", new System.Type[] { typeof(string) })]
        [HarmonyPrefix]
        public static bool SaveSystem_SaveGameData_Prefix(string slot, out LomsIoContext.Scope __state)
        {
            __state = null;
            LomsRuntime.LastSaveSucceeded = false;
            if (LomsReplayGuard.RecoveryRequired)
            {
                LomsRuntime.Notify?.Invoke(LomsReplayGuard.RecoveryMessage);
                return false;
            }
            if (LomsRuntime.Replaying)
            {
                LomsRuntime.Notify?.Invoke("回放中不可存档");
                return false;
            }
            if (LomsRuntime.LiveBlockPanel != null)
            {
                LomsRuntime.Notify?.Invoke("面板开启中不可存档");
                return false;
            }
            if (LomsRuntime.RollPending)
            {
                // mid-roll save loses the fate-point deduction (it only happened live)
                // and would desync the dice event stream.
                LomsRuntime.Notify?.Invoke("掷骰中不可存档");
                return false;
            }
            __state = LomsIoContext.EnterSave(slot);
            return true;
        }

        [HarmonyPatch(typeof(SaveSystem), "SaveGameData", new System.Type[] { typeof(string) })]
        [HarmonyFinalizer]
        public static void SaveSystem_SaveGameData_Finalizer(LomsIoContext.Scope __state)
        {
            __state?.Dispose();
        }

        [HarmonyPatch(typeof(SaveSystem), "ExecuteSaveData")]
        [HarmonyPostfix]
        public static void SaveSystem_ExecuteSaveData_Postfix(string saveFile, object gameSave)
        {
            // Harmony also runs postfixes when the recovery prefix skips the
            // native write. Keep the existing sidecar untouched in that case.
            if (LomsReplayGuard.RecoveryRequired) return;
            if (!(gameSave is GameSave nativeSave)) return;
            try
            {
                string slot = LomsIoContext.ManualSaveSlot;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                LomsSaveData data = null;
                if (slot != null && LomsRuntime.InStoryScene && nativeSave.CurrentScene == scene
                    && LomsRuntime.BoundarySave != null && LomsRuntime.BoundaryLuaGlobals != null
                    && !string.IsNullOrEmpty(LomsRuntime.BoundaryScript))
                {
                    data = new LomsSaveData
                    {
                        BoundaryScript = LomsRuntime.BoundaryScript,
                        BoundarySave = LomsRuntime.BoundarySave,
                        LuaGlobals = LomsRuntime.BoundaryLuaGlobals,
                        Events = new System.Collections.Generic.List<LomsEvent>(LomsRuntime.Events),
                        Scene = scene,
                        Visual = new LomsVisual { Music = LomsRuntime.CurMusic, Env = LomsRuntime.CurEnv },
                    };
                    try { data.ValidationSnapshot = LomsRuntime.CaptureState(nativeSave); }
                    catch (System.Exception e)
                    {
                        // A diagnostic failure must not prevent the save itself.
                        LomsRuntime.Log?.LogWarning("[Loms] Verification snapshot unavailable: " + e);
                    }
                }
                // A Free save or NewGameData can overwrite a former story
                // slot. Remove its old replay only after the native commit.
                if (!LomsSave.CompleteSave(saveFile, nativeSave, data))
                {
                    LomsRuntime.Log?.LogWarning("[Loms] Native save was not committed; keeping the existing sidecar: " + saveFile);
                    return;
                }
                if (data != null)
                    LomsRuntime.Log?.LogInfo($"[Loms] Sidecar written: slot={slot}, boundary={data.BoundaryScript}, events={data.Events.Count}");
                if (slot != null) LomsRuntime.LastSaveSucceeded = true;
            }
            catch (System.Exception e)
            {
                LomsRuntime.Log?.LogWarning("[Loms] Sidecar update failed: " + e);
            }
        }

        [HarmonyPatch(typeof(SaveSystem), "DeleteSaveData")]
        [HarmonyPostfix]
        public static void SaveSystem_DeleteSaveData_Postfix(string slot)
        {
            LomsSave.Delete(slot);
        }

        // ============ Load: swap boundary + begin replay ============

        [HarmonyPatch(typeof(SaveSystem), "LoadGameData")]
        [HarmonyPrefix]
        public static void SaveSystem_LoadGameData_Prefix(SaveSystem __instance, out LomsIoContext.Scope __state)
        {
            __state = LomsIoContext.EnterLoad(__instance.CurrentSlot);
        }

        [HarmonyPatch(typeof(SaveSystem), "LoadGameData")]
        [HarmonyFinalizer]
        public static void SaveSystem_LoadGameData_Finalizer(LomsIoContext.Scope __state)
        {
            __state?.Dispose();
        }

        [HarmonyPatch(typeof(SaveSystem), "ExecuteLoadGameData")]
        [HarmonyPrefix]
        public static void SaveSystem_ExecuteLoadGameData_Prefix(ref GameSave gameSave)
        {
            try
            {
                if (gameSave == null) return;
                LomsRuntime.FullReset();
                // AutoLoadGameData and LoadRestoreSaveData share this method,
                // but do not enter the manual LoadGameData scope.
                string slot = LomsIoContext.ManualLoadSlot;
                if (slot == null) return;
                LomsSaveData sc = LomsSave.TryRead(slot, gameSave, out string warning);
                if (warning != null)
                {
                    LomsRuntime.LastLoadWarning = warning;
                    LomsRuntime.Log?.LogWarning("[Loms] " + warning);
                    LomsRuntime.Notify?.Invoke(warning);
                }
                if (sc == null) return;
                GameSave b = sc.BoundarySave;

                // Replay must begin with boundary state, including travel and
                // restore pointers; the script will reproduce later changes.
                b.TimeTick = gameSave.TimeTick;
                b.CurrentScene = sc.Scene;
                gameSave = b;

                LomsRuntime.BeginReplay(sc, b, slot);
            }
            catch (System.Exception e)
            {
                LomsRuntime.Log?.LogWarning("[Loms] Load prefix failed: " + e);
            }
        }

        [HarmonyPatch(typeof(SaveSystem), "ExecuteLoadGameData")]
        [HarmonyPostfix]
        public static void SaveSystem_ExecuteLoadGameData_Postfix(GameSave gameSave, PlayerStatManagerData ____playerStat)
        {
            // Native Load uses StartStoryScript for the running script but
            // leaves its saved checkpoint field at the previous session's value.
            ____playerStat.SetStartScript(gameSave.StartStoryScript);
            // A replay entry can differ from that native checkpoint. Select it
            // only for execution, without rewriting the boundary save itself.
            if (LomsRuntime.LoadInProgress && LomsRuntime.Replaying)
                ____playerStat.SetStoryScript(LomsRuntime.BoundaryScript);
            LomsRuntime.LoadInProgress = false;
        }

        [HarmonyPatch(typeof(MissionManagerData), "UpdateCheckMissions")]
        [HarmonyPrefix]
        public static bool MissionManagerData_UpdateCheckMissions_Prefix()
        {
            // Native load menus check missions before entering the scene.
            // A replay boundary already includes that check; repeating it can
            // change flags, time and the starting script before replay starts.
            // Checks from later replayed story transitions still run normally.
            return !LomsRuntime.AwaitingReplayScript;
        }

        // ============ Dice ============

        [HarmonyPatch(typeof(DiceMenuDialog), "ExecuteRoll")]
        [HarmonyPrefix]
        public static bool DiceMenuDialog_ExecuteRoll_Prefix(DiceMenuDialog __instance, ref IEnumerator __result)
        {
            if (LomsRuntime.Replaying)
            {
                DiceEvent e = LomsRuntime.Consume<DiceEvent>();
                if (e != null)
                {
                    try
                    {
                        AccessTools.Field(typeof(DiceMenuDialog), "_resultSelection").SetValue(__instance, e.Sel);
                        if (e.Fate) LomsRuntime.ApplyFateDeduction();
                        // In replay nothing was ever shown (Setup only assigns fields and the
                        // button/option UI is built inside the original ExecuteRoll body), so
                        // Clear() is unnecessary — and unsafe unless Awake has run at least
                        // once (cachedButtons would be null). Only clear if genuinely active.
                        if (__instance.gameObject.activeSelf)
                        {
                            InvokeOn(__instance, "Clear");
                        }
                        __instance.gameObject.SetActive(false);
                        InvokeOn(__instance, "HideSayDialog");
                    }
                    catch (System.Exception ex)
                    {
                        LomsRuntime.Log?.LogWarning("[Loms] Dice replay cleanup failed: " + ex);
                    }
                    __result = LomsRuntime.EmptyEnumerator();
                    return false;
                }
            }
            LomsRuntime.RollPending = true;
            LomsRuntime.FateCount = 0;
            return true;
        }

        [HarmonyPatch(typeof(DiceMenuDialog), "UseFatePoint")]
        [HarmonyPostfix]
        public static void DiceMenuDialog_UseFatePoint_Postfix()
        {
            if (LomsRuntime.RollPending) LomsRuntime.FateCount++;
        }

        [HarmonyPatch(typeof(DiceMenuDialog), "OnDisable")]
        [HarmonyPostfix]
        public static void DiceMenuDialog_OnDisable_Postfix(DiceMenuDialog __instance)
        {
            if (LomsRuntime.RollPending && !LomsRuntime.Replaying)
            {
                int sel = (int)AccessTools.Field(typeof(DiceMenuDialog), "_resultSelection").GetValue(__instance);
                LomsRuntime.Record(new DiceEvent { Sel = sel, Fate = LomsRuntime.FateCount > 0 });
                LomsRuntime.RollPending = false;
            }
        }

        private static void InvokeOn(Object obj, string method)
        {
            var mi = AccessTools.Method(obj.GetType(), method);
            if (mi != null) mi.Invoke(obj, null);
        }

        [HarmonyPatch(typeof(CustomMenuDialog), "InteractableCacheButton")]
        [HarmonyPrefix]
        public static bool CustomMenuDialog_InteractableCacheButton_Prefix(UnityEngine.UI.Button[] ___cachedButtons)
        {
            // Replayed choices never activate this dialog, so Awake may not
            // have built its button cache yet. There are no choice buttons to
            // lock in that state; let panel open/close initialization continue.
            // Once the dialog is used, its native button handling still runs.
            return ___cachedButtons != null;
        }

        // ============ Save button unlock ============

        [HarmonyPatch(typeof(MenuPanel), "ToggleSaveButton")]
        [HarmonyPrefix]
        public static void MenuPanel_ToggleSaveButton_Prefix(ref bool active)
        {
            if (!active) active = true;
        }

        [HarmonyPatch(typeof(LuaManager), "ToggleSaveButton")]
        [HarmonyPrefix]
        public static bool LuaManager_ToggleSaveButton_Prefix(int active)
        {
            // suppress the disable path (keeps toast away); enable path still runs
            // without moving the replay boundary away from the script entry.
            return active != 0;
        }

        [HarmonyPatch(typeof(RecentSaveSlotPanel), "Setup")]
        [HarmonyPostfix]
        public static void RecentSaveSlotPanel_Setup_Postfix(string ____slot, UnityEngine.UI.Text ____slotText)
        {
            // Older releases may have persisted lomsquick as CurrentSlot.
            // Keep that save accessible through the game's existing recent
            // entry; no copy, new row, or overwrite of a numbered slot is needed.
            if (____slot != "lomsquick") return;
            ____slotText.text = "快速存档";
        }

        // ============ View panels (record / skip) ============

        [HarmonyPatch(typeof(CgPanel), "Open")]
        [HarmonyPrefix]
        public static bool CgPanel_Open_Prefix(ref IEnumerator __result, object[] __args)
        {
            return HandleViewPanel("cg:" + FirstStringArg(__args), ref __result);
        }

        [HarmonyPatch(typeof(CgVideoPanel), "Open")]
        [HarmonyPrefix]
        public static bool CgVideoPanel_Open_Prefix(ref IEnumerator __result, object[] __args)
        {
            return HandleViewPanel("cgvideo:" + FirstStringArg(__args), ref __result);
        }

        [HarmonyPatch(typeof(CreditPanel), "Open")]
        [HarmonyPrefix]
        public static bool CreditPanel_Open_Prefix(ref IEnumerator __result)
        {
            return HandleViewPanel("credit", ref __result);
        }

        [HarmonyPatch(typeof(EndGamePanel), "Open")]
        [HarmonyPrefix]
        public static bool EndGamePanel_Open_Prefix(ref IEnumerator __result, object[] __args)
        {
            return HandleViewPanel("endgame:" + FirstStringArg(__args), ref __result);
        }

        private static bool HandleViewPanel(string key, ref IEnumerator __result)
        {
            if (LomsRuntime.Replaying)
            {
                PanelEvent e = LomsRuntime.Consume<PanelEvent>();
                if (e != null)
                {
                    if (e.K != key) LomsVerification.ReplayIssue("面板错位：" + key + " != " + e.K);
                    __result = LomsRuntime.EmptyEnumerator();
                    return false;
                }
                LomsRuntime.EndReplay("panel at cursor end");
            }
            LomsRuntime.Record(new PanelEvent { K = key });
            return true;
        }

        private static string FirstStringArg(object[] args)
        {
            if (args == null) return "";
            foreach (object a in args)
            {
                if (a is string s) return s;
            }
            return "";
        }

        // ============ Interaction panels (pause replay / block saving) ============

        [HarmonyPatch(typeof(FateBonusPanel), "Open")]
        [HarmonyPrefix]
        public static bool FateBonusPanel_Open_Prefix(FateBonusPanel __instance, ref IEnumerator __result, out bool __state)
        {
            __state = !LomsFateBonus.TryReplay(__instance);
            if (__state) return true;
            __result = LomsRuntime.EmptyEnumerator();
            return false;
        }

        [HarmonyPatch(typeof(FateBonusPanel), "Open")]
        [HarmonyPostfix]
        public static void FateBonusPanel_Open_Postfix(FateBonusPanel __instance, ref IEnumerator __result, bool __state)
        {
            if (__state) __result = LomsFateBonus.Track(__instance, __result);
        }

        // Open returns an iterator before the panel becomes active. Capture
        // before InitData changes values, then record only after normal closure.
        [HarmonyPatch(typeof(FateBonusPanel), "OnPanelOpen")]
        [HarmonyPrefix]
        public static void FateBonusPanel_OnPanelOpen_Prefix(FateBonusPanel __instance, CommonPanel ____commonPanel)
        {
            LomsFateBonus.Begin(__instance);
            HandleInteractionPanel(____commonPanel.gameObject);
        }

        [HarmonyPatch(typeof(ShopPanel), "Open")]
        [HarmonyPrefix]
        public static void ShopPanel_Open_Prefix(ShopPanel __instance)
        {
            HandleInteractionPanel(__instance.gameObject);
        }

        [HarmonyPatch(typeof(MartialPanel), "Open")]
        [HarmonyPrefix]
        public static void MartialPanel_Open_Prefix(MartialPanel __instance)
        {
            HandleInteractionPanel(__instance.gameObject);
        }

        [HarmonyPatch(typeof(UpgradePanel), "Open")]
        [HarmonyPrefix]
        public static void UpgradePanel_Open_Prefix(UpgradePanel __instance)
        {
            HandleInteractionPanel(__instance.gameObject);
        }

        // The one-argument overload delegates here. Mode 0 activates the
        // relationship; mode 1 only displays the introduction.
        [HarmonyPatch(typeof(CharacterIntroPanel), "Show", new System.Type[] { typeof(string), typeof(int) })]
        [HarmonyPrefix]
        public static bool CharacterIntroPanel_Show_Prefix(string key, int mode, ref IEnumerator __result)
        {
            // Preserve existing mode-0 events so pre-update saves still replay.
            string eventKey = mode == 0 ? "intro:" + key : "intro:" + mode + ":" + key;
            if (LomsRuntime.Replaying)
            {
                PanelEvent e = LomsRuntime.Consume<PanelEvent>();
                if (e != null)
                {
                    if (e.K != eventKey) LomsVerification.ReplayIssue("角色介绍错位：" + eventKey + " != " + e.K);
                    __result = mode == 0 ? LomsRuntime.InstantIntro(key) : LomsRuntime.EmptyEnumerator();
                    return false;
                }
                LomsRuntime.EndReplay("intro at cursor end");
            }
            LomsRuntime.Record(new PanelEvent { K = eventKey });
            return true;
        }

        private static void HandleInteractionPanel(GameObject go)
        {
            if (LomsRuntime.Replaying)
            {
                LomsRuntime.PauseForInteraction(go);
            }
            else
            {
                LomsRuntime.LiveBlockPanel = go;
            }
        }

        // ============ Visual tracking ============

        [HarmonyPatch(typeof(LuaManager), "PlayMusic")]
        [HarmonyPostfix]
        public static void LuaManager_PlayMusic_Postfix(string name)
        {
            LomsRuntime.CurMusic = name;
        }

        [HarmonyPatch(typeof(LuaManager), "PlayEnvSound")]
        [HarmonyPostfix]
        public static void LuaManager_PlayEnvSound_Postfix(string name)
        {
            LomsRuntime.CurEnv = name;
        }
    }
}
