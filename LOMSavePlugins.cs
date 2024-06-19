using BepInEx;
using BepInEx.Unity.Mono;
using Fungus;
using HarmonyLib;
using MoonSharp.Interpreter;
using Mortal.Core;
using Mortal.Story;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMSave
{
    [BepInPlugin("bk.plugins.LOMSave", "测试测试", "1.0.0.0")]
    public class LOMSavePlugins : BaseUnityPlugin
    {
        public static LOMSavePlugins Ins;
        // 在插件启动时会直接调用Awake()方法
        void Awake()
        {
            Ins = this;
            var harmony = new Harmony("bk.plugins.LOMSave");
            harmony.PatchAll(typeof(Fix));

            //harmony.PatchAll(typeof(TargetClassPatch));
        }

        void Start()
        {

        }

        void Update()
        {

            /*   var key = new BepInEx.Configuration.KeyboardShortcut(KeyCode.F11);

               if (key.IsUp())
               {
                   Debug.Log("||DO LUA||");
                   try
                   {
                       var txt = File.ReadAllText("D:/LUA.TXT");
                       LuaManager.Instance.ExecuteScript(txt);
                   }
                   catch (Exception e)
                   {
                       Debug.LogError(e.ToString());
                   }
               }*/

            if (IsLoading && data.NeedSpeed)
            {
                Time.timeScale = 50;
            }
            if (keepCloseLoad)//用于处理第二个loading无法关闭的异常
            {
                HideLoadScene();
            }




        }
        // 在插件关闭时会调用OnDestroy()方法
        void OnDestroy()
        {
            Ins = null;
        }

        public class SData
        {
            public class PortraitData
            {
                public string toPosition;
                public string face;
            }
            public Dictionary<string, string> KV = new Dictionary<string, string>();
            public string CurView;
            public string CurStory;
            public string LastScript;
            public string RealLastScript;
            public string Scene;
            public GameSave lastSave;
            public Dictionary<string, PortraitData> portrait = new Dictionary<string, PortraitData>();

            public bool NeedSpeed => portrait?.Count > 0 && Scene != "Free";

        }

        public SData data = new SData();

        public void SaveData(string file, GameSave gs)
        {
            var json = JsonConvert.SerializeObject(data);
            File.WriteAllText(file, json);
            var json2 = JsonConvert.SerializeObject(gs);
            File.WriteAllText(file + ".json", json2);
        }

        public void SetCurView(string name)
        {
            Debug.LogWarning("[SSS]SetCurView:" + name);
            data.CurView = name;
        }
        int loading = 0;

        public bool IsLoading => loading == 1;

        List<string> tempKeys = new List<string>();
        /// <summary>
        /// 加载记录故事选项的额外存档数据
        /// </summary>
        /// <param name="file"></param>
        /// <param name="gameSave"></param>
        public void LoadData(string file, GameSave gameSave)
        {
            if (!File.Exists(file))
            {
                data = new SData();
            }
            else
            {
                try
                {
                    data = JsonConvert.DeserializeObject<SData>(File.ReadAllText(file));
                    Debug.LogWarning("[SSSave]Load");
                }
                catch
                {
                    data = new SData();
                }
            }
            if (data.portrait == null)
                data.portrait = new Dictionary<string, SData.PortraitData>();
            data.Scene = gameSave.CurrentScene;
            //if(curGameSave.CurrentScene == "Story")
            loading = 1;
            fristSet = true;
            tempKeys = new List<string>(data.KV.Keys);
        }

        public static string GetMd5Hash(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public void SetKV(string k, string v, bool withscrip = true)
        {
            if (withscrip)
            {
                var scrip = PlayerStatManagerData.Instance.CurrentStoryScript;
                data.KV[scrip + "|" + k] = v;
            }
            else
            {
                data.KV[k] = v;
            }
        }

        public string GetValue(string k, bool withscrip = true)
        {
            Debug.Log("[SSS]GetValue:" + k);
            var scrip = PlayerStatManagerData.Instance.CurrentStoryScript;
            var key = scrip + "|" + k;
            if (!withscrip)
            {
                key = k;
            }
            if (!data.KV.TryGetValue(key, out var v))
            {
                if (withscrip && loading == 1)
                {
                    loading = -1;
                    //Debug.LogError("Loading Finish!");
                    if (data.NeedSpeed)
                    {
                        Time.timeScale = 1;
                        HideLoadScene();
                    }
                }

                return "";
            }
            tempKeys.Remove(key);
            if (tempKeys.Count == 0)
            {
                Time.timeScale = 1;
                HideLoadScene();
            }
            return v;
        }

        bool showLoading = false;
        public void ShowLoadScene()
        {
            Debug.Log("[SSS]ShowLoadScene");
            SceneManager.LoadScene("Loading1", new LoadSceneParameters(LoadSceneMode.Additive));
            showLoading = true;

        }

        bool keepCloseLoad = false;
        public void HideLoadScene()
        {
            if (!showLoading)
                return;
            bool close = false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if(scene.name == "Loading1")
                {
                    if (scene.IsValid())
                    {
                        SceneManager.UnloadSceneAsync(scene);
                        showLoading = false;
                        keepCloseLoad = false;
                        close = true;
                        Debug.Log("[SSS]HideLoadScene 1");
                    }
                }
            }
            if (close == false)
            {
                keepCloseLoad = true;
                Debug.Log("[SSS]HideLoadScene 2");
            }
        }


        //BepInEx无法注册新的方法到LUA，所以使用了一个LuaManager自带的方法用来作为协议处理逻辑
        public string LuaTool(string key)
        {
            var rs = "";
            // Debug.LogWarning($"[LOMS]LuaTool:{key}");
            key = key.Substring(8);
            var txts = key.Split('|');
            var cmd = txts[0].ToLower();
            var jdata = JsonConvert.DeserializeObject<JObject>(txts[1]);
            // Debug.LogWarning($"[LOMS]LuaTool data:{cmd} {jdata}");
            var scrip = PlayerStatManagerData.Instance.CurrentStoryScript;
            switch (cmd)
            {
                case "savekv":
                    SetKV(jdata["K"].ToString(), jdata["V"].ToString());
                    break;
                case "getkv":
                    var v = GetValue(jdata["K"].ToString());
                    rs = v;
                    break;
                case "hash":
                    //Debug.Log(data["K"]);
                    rs = GetMd5Hash(jdata["K"].ToString());
                    break;
                case "finish":
                    rs = IsLoading ? "0" : "1";
                    break;
                case "getportrait":
                    var k = jdata["K"].ToString();
                    data.portrait.TryGetValue(k, out var pdata);
                    if (pdata != null)
                    {
                        return JsonConvert.SerializeObject(new { rs = "1", to = pdata.toPosition, face = pdata.face });
                    }
                    rs = "0";
                    break;
            }

            var rsdata = new { rs = rs };
            return JsonConvert.SerializeObject(rsdata);
        }

        /// <summary>
        /// 跳到战斗的时候单独存下档
        /// </summary>
        public void SetRealScript()
        {

            if (!string.IsNullOrEmpty(data.RealLastScript))
            {
                data.LastScript = null;
                SaveSafe(data.RealLastScript);
            }
        }

        bool fristSet = true;
        /// <summary>
        /// 保存故事进行前的数据用于读档
        /// </summary>
        /// <param name="script"></param>
        public void SaveSafe(string script)
        {
            if (fristSet)
            {
                fristSet = false;
                return;
            }
            data.RealLastScript = script;

            try
            {

                var CreateSaveData = Traverse.Create(SaveSystem.Instance).Method("CreateSaveData");
                var GameSave = CreateSaveData.GetValue() as GameSave;

                List<string> rmls = new List<string>();
                if (!string.IsNullOrEmpty(data.LastScript))
                {
                    foreach (var kv in data.KV)
                    {
                        if (kv.Key.StartsWith(data.LastScript + "|"))
                        {
                            rmls.Add(kv.Key);
                        }
                    }

                    foreach (var k in rmls)
                    {
                        data.KV.Remove(k);
                    }
                }
                data.lastSave = GameSave;

            }
            catch
            {

            }
            data.LastScript = script;

        }


        public GameSave GetSave()
        {
            return data.lastSave;
        }

        public void RemovePortrait(string character)
        {
            data.portrait.Remove(character);
        }

        /// <summary>
        /// 用来记录存档时候的角色立绘状态
        /// </summary>
        /// <param name="character"></param>
        /// <returns></returns>
        public SData.PortraitData GetPortrait(string character)
        {
            data.portrait.TryGetValue(character, out var rs);
            if (rs == null)
            {
                rs = new SData.PortraitData();
                data.portrait[character] = rs;
            }
            return rs;
        }
    }


    public class Fix
    {
        /// <summary>
        /// 存档的时候把附加数据也存一份
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="slot"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SaveSystem), "SaveGameData", typeof(string))]
        static bool SaveGameDataPrefix(SaveSystem __instance, string slot)
        {
            Debug.LogWarning("|||SS存档！ ");
            var GetSaveDataFile = Traverse.Create(__instance).Method("GetSaveDataFile", new Type[] { typeof(string) });
            string saveDataFile = (string)GetSaveDataFile.GetValue(slot);
            saveDataFile = saveDataFile + ".ss";


            var CreateSaveData = Traverse.Create(__instance).Method("CreateSaveData");
            var GameSave = CreateSaveData.GetValue() as GameSave;

            LOMSavePlugins.Ins.SaveData(saveDataFile, GameSave);
            // 返回false以跳过原方法的执行
            return true;
        }

        /// <summary>
        /// 删除存档的时候把额外的数据删了
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="slot"></param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SaveSystem), "DeleteSaveData", typeof(string))]
        static void DeleteSaveDataPrefix(SaveSystem __instance, string slot)
        {
            var GetSaveDataFile = Traverse.Create(__instance).Method("GetSaveDataFile", new Type[] { typeof(string) });
            string saveDataFile = (string)GetSaveDataFile.GetValue(slot);
            saveDataFile = saveDataFile + ".ss";
            if (File.Exists(saveDataFile))
            {
                File.Delete(saveDataFile);
            }
        }

        /// <summary>
        /// 读档的时候读取附加数据
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="gameSave"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SaveSystem), "ExecuteLoadGameData", typeof(GameSave))]
        static bool ExecuteLoadGameDataPrefix(SaveSystem __instance, ref GameSave gameSave)
        {


            Debug.LogWarning("|||SS读档！ ");
            var slot = (string)Traverse.Create(__instance).Field("_currentSlot").GetValue();
            var GetSaveDataFile = Traverse.Create(__instance).Method("GetSaveDataFile", new Type[] { typeof(string) });
            string saveDataFile = (string)GetSaveDataFile.GetValue(slot);
            saveDataFile = saveDataFile + ".ss";
            LOMSavePlugins.Ins.LoadData(saveDataFile, gameSave);
            var old = LOMSavePlugins.Ins.GetSave();
            if (old != null)
            {
                //使用剧情前的存档数据，但是保留剧情相关的初始化数据。
                old.TimeTick = gameSave.TimeTick;
                old.CurrentStoryScript = LOMSavePlugins.Ins.data.LastScript;
                old.StartStoryScript = gameSave.StartStoryScript;
                old.CurrentScene = gameSave.CurrentScene;
                old.CurrentSceneKey = gameSave.CurrentSceneKey;
                old.CurrentNextScene = gameSave.CurrentNextScene;
                old.CurrentTravelScript = gameSave.CurrentTravelScript;

                if (!string.IsNullOrEmpty(old.CurrentStoryScript))
                    old.StartStoryScript = old.CurrentStoryScript;
                gameSave = old;
            }
            else
            if (!string.IsNullOrEmpty(gameSave.CurrentStoryScript))
                gameSave.StartStoryScript = gameSave.CurrentStoryScript;
            if (LOMSavePlugins.Ins.data.NeedSpeed)
            {
                LOMSavePlugins.Ins.ShowLoadScene();
            }
            return true;
        }


        /// <summary>
        /// 关闭锁存档的功能
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="active"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MenuPanel), "ToggleSaveButton", typeof(bool))]
        static bool ToggleSaveButtonPrefix(MenuPanel __instance, ref bool active)
        {
            active = true;
            return true;
        }

        /// <summary>
        /// 切换到战斗的时候额外存一次附加数据
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="name"></param>
        /// <param name="key"></param>
        /// <param name="nextScene"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LuaManager), "ChangeScene")]
        static bool ChangeScenePrefix(LuaManager __instance, string name, string key, string nextScene)
        {
            if (name == "Battle")
            {
                LOMSavePlugins.Ins.SetRealScript();
            }
            return true;
        }

        /// <summary>
        /// 当前记录剧情脚本
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="script"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerStatManagerData), "SetStoryScript")]
        static bool PlayerStatManagerData_Save_Prefix(PlayerStatManagerData __instance, string script)
        {
            LOMSavePlugins.Ins.SaveSafe(script);
            return true;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerStatManagerData), "Save", typeof(GameSave))]
        static bool PlayerStatManagerData_Save_Prefix(PlayerStatManagerData __instance, ref GameSave gameSave)
        {
            /* var old = LOMSavePlugins.Ins.GetSave();
             if(old != null)
             {
                 Debug.LogWarning("[SSSave]Safe SaveData");
             }
             gameSave = old ?? gameSave;*/
            var curStory = (string)Traverse.Create(__instance).Field("_currentStoryScript").GetValue();
            gameSave.CurrentStoryScript = curStory;
            return true;
        }


        /// <summary>
        /// 记录当前场景的背景图片
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ViewFlowchartController), "View", typeof(string))]
        static bool PViewFlowchartController_View_Prefix(ViewFlowchartController __instance, string name)
        {
            LOMSavePlugins.Ins.SetCurView(name);
            return true;
        }

        /// <summary>
        /// BepInEx无法注册新的方法到LUA，所以使用了一个LuaManager自带的方法来作为附加逻辑的处理，使用json来处理参数和返回值
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="__result"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LuaManager), "GetStoryText")]
        static bool GetStoryText_Prefix(LuaManager __instance, ref string __result, string key)
        {
            if (key.StartsWith("__LOMS__"))
            {
                __result = LOMSavePlugins.Ins.LuaTool(key);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 记录播放的音乐
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="name"></param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LuaManager), "PlayMusic")]
        static void LuaManagerPlayMusicHarmonyPostfix(LuaManager __instance, string name)
        {
            LOMSavePlugins.Ins.SetKV("Music", name, false);


        }

        /// <summary>
        /// 记录播放的环境音乐
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="name"></param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LuaManager), "PlayEnvSound")]
        static void LuaManagerPlayEnvSoundHarmonyPostfix(LuaManager __instance, string name)
        {
            LOMSavePlugins.Ins.SetKV("EnvSound", name, false);

        }

        /// <summary>
        /// 记录角色介绍
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="__result"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CharacterIntroPanel), "Show")]
        static bool CharacterIntroPanel_Show_Prefix(CharacterIntroPanel __instance, IEnumerator __result, string key)
        {
            var hashkey = LOMSavePlugins.GetMd5Hash("IntroShow_" + key);
            if (LOMSavePlugins.Ins.GetValue(hashkey) == "1")
                return false;
            else
            {
                LOMSavePlugins.Ins.SetKV(hashkey, "1");
                return true;
            }
        }

        static string RollLastKey = null;

        /// <summary>
        /// 跳过已经roll过的地方
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="__result"></param>
        /// <param name="options"></param>
        /// <param name="destinyPoint"></param>
        /// <param name="checkName"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceMenuDialog), "ExecuteRoll")]
        static bool ExecuteRoll_Prefix(DiceMenuDialog __instance, IEnumerator __result, string[] options, int destinyPoint, string checkName)
        {
            // Debug.LogError("ExecuteRoll_Postfix -1");
            var txt = "";
            foreach (var t in options)
            {
                txt = txt + "|" + t;
            }
            var hashkey = LOMSavePlugins.GetMd5Hash("Dice_" + txt);
            var selecttxt = LOMSavePlugins.Ins.GetValue(hashkey);
            if (!string.IsNullOrEmpty(selecttxt))
            {
                var select = int.Parse(selecttxt);
                Traverse.Create(__instance).Field("_resultSelection").SetValue(select);
                return false;
            }
            RollLastKey = hashkey;
            return true;
        }

        /// <summary>
        /// 记录roll点的结果
        /// </summary>
        /// <param name="__instance"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceMenuDialog), "OnDisable")]
        static bool ExecuteRoll_OnDisable_Prefix(DiceMenuDialog __instance)
        {
            LOMSavePlugins.Ins.SetKV(RollLastKey, __instance.ResultSelection.ToString());
            return true;
        }

        /// <summary>
        /// 有些脚本会直接get角色，因为在前置脚本进行了加载。
        /// 我们读档直接跳到这个脚本就会导致没有load，所以补一个load
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="__result"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CharacterPlaceholder), "Get")]
        static bool CharacterPlaceholderGet_Prefix(CharacterPlaceholder __instance, ref Character __result, string key)
        {
            var traverse = Traverse.Create(__instance);
            var characters = traverse.Field<Dictionary<string, Character>>("_characters").Value;

            if (!characters.ContainsKey(key))
            {
                LuaManager.Instance.StartCoroutine(__instance.LoadCharacterAsset(key));
                return true;
            }
            return true;
        }

        /// <summary>
        /// 记录立绘的位置
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="optionsTable"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PortraitController), "Show", typeof(Table))]
        static bool PortraitControllerShow_Prefix(PortraitController __instance, Table optionsTable)
        {
            var table = optionsTable;
            var character = table.Get("character").ToObject<Character>();
            if (character == null)
                return true;
            var data = LOMSavePlugins.Ins.GetPortrait(character.name);

            if (!table.Get("toPosition").IsNil())
            {
                data.toPosition = table.Get("toPosition").CastToString();
            }
            if (!table.Get("facing").IsNil())
            {
                data.face = table.Get("facing").CastToString();
            }
            return true;
        }

        /// <summary>
        /// 记录隐藏立绘
        /// </summary>
        /// <param name="__instance"></param>
        /// <param name="optionsTable"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PortraitController), "Hide", typeof(Table))]
        static bool PortraitController_Prefix(PortraitController __instance, Table optionsTable)
        {
            var table = optionsTable;
            var character = table.Get("character").ToObject<Character>();
            if (character == null)
                return true;
            LOMSavePlugins.Ins.RemovePortrait(character.name);
            return true;
        }

        /// <summary>
        /// 初始化lua的时候加载我们的lua脚本，并还原背景图片
        /// </summary>
        /// <param name="__instance"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LuaManager), "ExecuteLuaScript")]
        static bool ExecuteLuaScript_HarmonyPrefix(DiceMenuDialog __instance)
        {
            var txt = luatxt;// File.ReadAllText("D:/LUA.TXT");
            LuaManager.Instance.ExecuteScript(txt);
            Debug.Log("[SSS]CurView:" + LOMSavePlugins.Ins.data.CurView);
          
            return true;
        }

      
        /// <summary>
        /// 脚本初始化完成后重现存档时候的现场
        /// </summary>
        /// <param name="__instance"></param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LuaManager), "Init")]
        static void LuaManager_Postfix(DiceMenuDialog __instance)
        {

            var music = LOMSavePlugins.Ins.GetValue("Music", false);
            var EnvSound = LOMSavePlugins.Ins.GetValue("EnvSound", false);
            if (!string.IsNullOrEmpty(music))
            {
                LuaManager.Instance.PlayMusic(music);
                //LuaManager.Instance.ExecuteScript($"luamanager.PlayMusic(\"{music}\")");
            }
            if (!string.IsNullOrEmpty(EnvSound))
            {
                LuaManager.Instance.PlayMusic(EnvSound);
                //LuaManager.Instance.ExecuteScript($"luamanager.PlayEnvSound(\"{EnvSound}\")");
            }
            var flow = GameObject.FindObjectOfType<ViewFlowchartController>();
            flow.View(LOMSavePlugins.Ins.data.CurView);
          /*  var bglua = $@"getvar(flowcharts.view, ""ViewName"").value = ""{LOMSavePlugins.Ins.data.CurView}""
runblock(flowcharts.view, ""view"")";
            LuaManager.Instance.ExecuteScript(bglua);*/


        }


        /// <summary>
        /// lua主要就是在读档的时候跳过已经进行过的说话/等待/展示等环节
        /// </summary>
        static string luatxt = @"
--setcharacter(characters.Get(""player""), characters.GetPortrait(""player"", ""nervous1""))
if not SSSSD_LUA then
	SSSSD_LUA = true
    --跟C#的交互协议
	function LOMS_Command(cmd, tbParam)
		param =  json.serialize(tbParam)
		rs = luamanager.GetStoryText(""__LOMS__""..cmd..""|""..param)
		return json.parse(rs)
	end



	function LOMS_SaveKV(key, value)
		LOMS_Command(""SaveKV"",{K=key,V=value})
	end

	function LOMS_GetKV(key)
		return LOMS_Command(""GetKV"",{K=key})[""rs""]
	end


	function LOMS_Hash(key)
		return LOMS_Command(""Hash"",{K=key})[""rs""]
	end
	
	function LOMS_Finished()
		return LOMS_Command(""Finish"",{})[""rs""] == ""1""
	end

	function LOMS_GetPortrait(key)
		local data = LOMS_Command(""GetPortrait"",{K=key})
		if data[""rs""] ~= ""1"" then
			return nil
		end
		return data
	end

    --重载say，让say支持跳过
	oldsay = say
	function new_say(text, voiceclip)
		local key = LOMS_Hash(""Say_""..text)	
		if LOMS_GetKV(key) == ""1"" then
			return
		end
		if lastsetcharacter ~= nil then
			oldwait(0.1)
			for k,v in pairs(lastsetcharacter) do					
				local por = LOMS_GetPortrait(k.name)
				--print(""new_say"",k.name,por);
				if por ~= nil then
					runwait(characters.LoadCharacterAsset(k.name))
					stage.show{character=k, fromPosition=""M"", toPosition=por[""to""],facing=por[""face""], useDefaultSettings=false}
                    stage.showPortrait(k,v)
				end				
			end
			lastsetcharacter = nil
		end		
		oldsay(text,voiceclip)
		LOMS_SaveKV(key,1)		
	end

	say = new_say

    --重载choose，让其支持跳过
	oldchoose = choose

	function new_choose(options)
		print(""new_choose!!!!!!!!!!!!!"",options)
		local optxt = """"
		for k,v in pairs(options) do
			optxt = optxt ..""&""..v
		end
		local key =  LOMS_Hash(""Choose_""..optxt)	
		local lastS = tonumber(LOMS_GetKV(key))
		if lastS  != nil then
			return lastS
		end
		--print(""new_choose5"")
		local rs = oldchoose(options)
		LOMS_SaveKV(key,rs)
		return rs
	end
	choose = new_choose
	
    --重载wait，让其支持跳过
	oldwait = wait
	function new_wait(duration)
		if LOMS_Finished() then
			oldwait(duration)
			return
		end
	end
	wait = new_wait
	
	--用于处理读档后立绘消失的BUG
	lastsetcharacter = nil
	oldsetcharacter =  setcharacter
	function new_setcharacter(a,b)
		--print(""new_setcharacternew_setcharacternew_setcharacter"")
		if LOMS_Finished() == false then
			lastsetcharacter = lastsetcharacter or {}
			lastsetcharacter[a] = b
		end
		oldsetcharacter(a,b)
	end
	setcharacter = new_setcharacter
	
--[[
	oldchoosetimer = choosetimer

	function new_choosetimer(options, duration, defaultoption)
		--print(""new_choosetimer!!!!!!!!!!!!!"")
		local optxt = """"
		for _,v in pairs(options) do
			optxt = optxt ..""&""..v
		end
		local key =  LOMS_Hash(""Dice_""..optxt)	
		local lastS = tonumber(LOMS_GetKV(key))
		if lastS  != nil then
			return lastS
		end
		local rs = oldchoosetimer(options, duration, defaultoption)
		LOMS_SaveKV(key,rs)
		return rs
	end

	choosetimer = new_choosetimer
	--]]

end

";

    }


}