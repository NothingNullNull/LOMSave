using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mortal.Core;
using Mortal.Free;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMSave2
{
    [BepInPlugin(PluginGuid, "LOMSave2", PluginVersion)]
    public class LOMSave2Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.nn.lomsave2";
        public const string PluginVersion = "2.0.0";

        private ConfigEntry<KeyboardShortcut> _quickSaveKey;
        private ConfigEntry<KeyboardShortcut> _quickLoadKey;
        private ConfigEntry<float> _replayTimeScale;
        private ConfigEntry<bool> _showOverlay;
        private ConfigEntry<KeyboardShortcut> _verifyKey;
        private ConfigEntry<bool> _autoVerify;

        private static readonly List<string> FlashLines = new List<string>();
        private static float _flashUntil;

        private void Awake()
        {
            // This player clears ordinary bootstrap objects while loading its
            // first scene, even after BepInEx calls DontDestroyOnLoad. Use the
            // same protection as BepInEx's HideManagerGameObject option.
            gameObject.hideFlags |= HideFlags.HideAndDontSave;
            LomsRuntime.Log = Logger;
            _quickSaveKey = Config.Bind("Hotkeys", "QuickSave", new KeyboardShortcut(KeyCode.F5), "快速存档");
            _quickLoadKey = Config.Bind("Hotkeys", "QuickLoad", new KeyboardShortcut(KeyCode.F9), "快速读档");
            _replayTimeScale = Config.Bind("Replay", "TimeScale", 12f, "回放期间的 timeScale");
            _showOverlay = Config.Bind("Replay", "ShowOverlay", true, "回放期间显示遮罩");
            _verifyKey = Config.Bind("Hotkeys", "Verify", new KeyboardShortcut(KeyCode.F8), "对比读档后的属性和状态");
            _autoVerify = Config.Bind("Verification", "AutoCheck", true, "回放结束后自动对比保存时和当前状态");
            LomsVerification.ReportDirectory = Config.Bind("Verification", "ReportDirectory",
                System.IO.Path.Combine(Paths.PluginPath, "LOMSave2", "verification"), "验证报告目录").Value;
            LomsVerification.AutoCheck = _autoVerify.Value;
            LomsRuntime.ReplayTimeScale = _replayTimeScale.Value;
            LomsRuntime.ShowOverlay = _showOverlay.Value;
            LomsRuntime.Notify = Flash;

            Harmony.CreateAndPatchAll(typeof(Patches), PluginGuid);
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("[Loms] LOMSave2 loaded");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Start()
        {
            Logger.LogInfo($"[Loms] Hotkeys ready: save={_quickSaveKey.Value}, load={_quickLoadKey.Value}, verify={_verifyKey.Value}; input={UnityInput.Current.GetType().Name}");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // The game's SceneController loads even Title/GameOver additively.
            string n = scene.name;
            if (n == "Title" || n == "GameOver" || n == "End" || n == "DemoEnd")
            {
                LomsRuntime.FullReset();
                Logger.LogInfo($"[Loms] Reset on scene {n}");
            }
        }

        private void Update()
        {
            LomsRuntime.ReplayTimeScale = _replayTimeScale.Value;
            LomsVerification.AutoCheck = _autoVerify.Value;
            LomsRuntime.Poll();

            if (_quickSaveKey.Value.IsDown()) TryQuickSave();
            if (_quickLoadKey.Value.IsDown()) TryQuickLoad();
            if (_verifyKey.Value.IsDown()) TryVerify();
        }

        private void LateUpdate()
        {
            if (!LomsVerification.AutoPending || LomsRuntime.Replaying || LomsRuntime.LoadInProgress) return;
            if (LomsVerification.Poll(() => LomsRuntime.CaptureState(), LomsRuntime.CurrentCheckpoint != null, Time.unscaledTime))
                ShowVerificationResult();
        }

        private void TryVerify()
        {
            if (LomsRuntime.Replaying || LomsRuntime.LoadInProgress)
            {
                Flash("请等回放完成后再验证");
                return;
            }
            LomsVerification.Verify(() => LomsRuntime.CaptureState());
            ShowVerificationResult();
        }

        private void ShowVerificationResult()
        {
            Flash(LomsVerification.LastSummary);
            if (LomsVerification.LastReportPath != null)
                Logger.LogInfo("[Loms] Verification report: " + LomsVerification.LastReportPath);
        }

        private void TryQuickSave()
        {
            bool freeScene = SceneManager.GetActiveScene().name == "Free";
            if (!LomsRuntime.InStoryScene && !freeScene)
            {
                Flash("仅剧情/歇息/养成场景可快速存档");
                return;
            }
            if (SceneController.Instance.IsPrepare || (freeScene && (FreeManager.Instance == null || FreeManager.Instance.IsProcessing)))
            {
                Flash("请等场景或行动完成后再存档");
                return;
            }
            if (LomsRuntime.Replaying)
            {
                Flash("回放中不可存档");
                return;
            }
            if (LomsRuntime.LiveBlockPanel != null)
            {
                Flash("面板开启中不可存档");
                return;
            }
            if (LomsRuntime.RollPending)
            {
                Flash("掷骰中不可存档");
                return;
            }
            if (!freeScene && (LomsRuntime.BoundarySave == null || string.IsNullOrEmpty(LomsRuntime.BoundaryScript)))
            {
                Flash("存档边界尚未初始化");
                return;
            }
            SaveSystem saveSystem = SaveSystem.Instance;
            if (string.IsNullOrEmpty(saveSystem.CurrentSlot))
            {
                Flash("尚未选定存档槽，请先用游戏存档菜单保存");
                return;
            }
            // Match MenuPanel.ConfirmSaveButtonClick: overwrite the current
            // native slot, so shortcuts and the game's UI share one save.
            saveSystem.SaveGameData();
            if (LomsRuntime.LastSaveSucceeded) saveSystem.SaveUniverseData();
            Flash(LomsRuntime.LastSaveSucceeded ? "已快速存档" : "快速存档未完成，请查看日志");
        }

        private void TryQuickLoad()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (!LomsRuntime.InStoryScene && scene != "Free" && scene != "Title")
            {
                Flash("仅标题/剧情/歇息/养成场景可快速读档");
                return;
            }
            if (LomsRuntime.Replaying)
            {
                Flash("回放中不可读档");
                return;
            }
            if (SceneController.Instance.IsPrepare || (scene == "Free" && (FreeManager.Instance == null || FreeManager.Instance.IsProcessing)))
            {
                Flash("请等场景或行动完成后再读档");
                return;
            }
            SaveSystem saveSystem = SaveSystem.Instance;
            if (string.IsNullOrEmpty(saveSystem.CurrentSlot) || !LomsSave.Exists(saveSystem.CurrentSlot))
            {
                Flash("没有快速存档");
                return;
            }
            // Match the existing recent-save entry; never select a separate
            // shortcut-only slot behind the player's back.
            saveSystem.LoadGameData();
            saveSystem.SaveUniverseData();
            MissionManagerData.Instance.UpdateCheckMissions();
            SceneController.Instance.LoadCurrentScene();
            Flash(LomsRuntime.LastLoadWarning ?? "已快速读档");
        }

        private void OnGUI()
        {
            if (LomsRuntime.Replaying && LomsRuntime.ShowOverlay)
            {
                if (LomsRuntime.PausePanel != null)
                {
                    // Interaction pause: keep the game UI fully visible & clickable,
                    // only show a small corner hint.
                    var hintStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 18,
                        alignment = TextAnchor.UpperRight,
                        normal = { textColor = new Color(1f, 0.85f, 0.3f) },
                    };
                    GUI.Label(new Rect(Screen.width - 460, 12, 440, 30),
                        "交互暂停中，操作完成后继续回放", hintStyle);
                }
                else
                {
                    float w = Screen.width;
                    float h = Screen.height;
                    var prev = GUI.color;
                    GUI.color = new Color(0f, 0f, 0f, 0.65f);
                    GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
                    GUI.color = prev;
                    string txt = $"回放中… ({LomsRuntime.Cursor}/{LomsRuntime.Events.Count})";
                    var style = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 28,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.white },
                    };
                    GUI.Label(new Rect(0, h - 120, w, 60), txt, style);
                }
            }

            if (Time.unscaledTime < _flashUntil && FlashLines.Count > 0)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.yellow },
                };
                float y = 20f;
                foreach (string line in FlashLines)
                {
                    GUI.Label(new Rect(0, y, Screen.width, 36), line, style);
                    y += 38f;
                }
            }
            else if (FlashLines.Count > 0)
            {
                FlashLines.Clear();
            }
        }

        private static void Flash(string msg)
        {
            FlashLines.Clear();
            FlashLines.Add(msg);
            _flashUntil = Time.unscaledTime + 2.5f;
            LomsRuntime.Log.LogInfo("[Loms] " + msg);
        }
    }
}
