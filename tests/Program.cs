using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using HarmonyLib;
using LOMSave2;
using MoonSharp.Interpreter;
using Mortal.Core;
using Patches = LOMSave2.Patches;

internal static class Program
{
    private static string _gameDir;
    private static string _root;
    private static string _output;
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: LOMSave2.Tests <game-directory> <project-directory> <test-output-directory>");
            return 2;
        }
        _gameDir = Path.GetFullPath(args[0]);
        _root = Path.GetFullPath(args[1]);
        _output = Path.GetFullPath(args[2]);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
        Directory.CreateDirectory(_output);
        if (args.Length == 5 && args[3] == "--legacy") return WriteLegacyFixture(args[4]);
        if (args.Length == 5 && args[3] == "--legacy3") return WriteLegacy3Fixture(args[4]);
        return Run();
    }

    private static Assembly ResolveGameAssembly(object sender, ResolveEventArgs args)
    {
        string filename = new AssemblyName(args.Name).Name + ".dll";
        foreach (string folder in new[] { "Mortal_Data/Managed", "BepInEx/core" })
        {
            string path = Path.Combine(_gameDir, folder, filename);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        Test("native commit verification", () =>
        {
            var native = Native(101);
            string path = SaveNative("commit", native);
            Check(LomsSave.WasCommitted(path, native, out _), "The game's serialized bytes must match.");
            Check(!LomsSave.WasCommitted(path, Native(102), out _), "An unwritten attempt must not match.");
        });
        Test("new story save round trip", () =>
        {
            var native = Native(201);
            string path = SaveNative("roundtrip", native);
            var sidecar = Sidecar();
            sidecar.Events.Add(new SayEvent { Hash = "test" });
            Check(LomsSave.CompleteSave(path, native, sidecar), "Commit rejected.");
            var loaded = LomsSave.TryReadFile(path, native, out string warning);
            Check(warning == null && loaded != null && loaded.Events.Count == 1, "Round trip failed: " + warning);
        });
        Test("fate bonus choices, cost, social and flags survive format 4 serialization", () =>
        {
            var before = FateValues();
            var after = FateValues();
            after["stat:命運"] = 1;
            after["stat:體力"] = 10;
            after["social:掌門人"] = 20;
            after["flag:bonus"] = 1;
            var value = LomsFateBonus.Difference(before, after);
            Check(value.Changes.Count == 4, "Confirmed panel effects missing.");
            var native = Native(251);
            string path = SaveNative("fate-roundtrip", native);
            var data = Sidecar();
            data.Events.Add(value);
            LomsSave.CompleteSave(path, native, data);
            var loaded = LomsSave.TryReadFile(path, native, out string warning);
            Check(warning == null && loaded?.FormatVersion == 4, "New format unreadable: " + warning);
            var replay = loaded.Events.Single() as FateBonusEvent;
            Check(LomsFateBonus.Matches(replay, before, out _), "Recorded starting state rejected.");
            foreach (var change in replay.Changes) before[change.Key] = change.After;
            Check(before.All(p => after[p.Key] == p.Value), "Panel result lost on serialization.");
        });
        Test("reset choices leave an empty fate event and spend no points", () =>
        {
            var value = LomsFateBonus.Difference(FateValues(), FateValues());
            Check(value.Changes.Count == 0 && LomsFateBonus.IsValid(value), "A canceled allocation was retained.");
        });
        Test("fate bonus starting state and missing targets are rejected before application", () =>
        {
            var before = FateValues();
            var after = FateValues();
            after["stat:命運"] = 3;
            after["stat:體力"] = 10;
            var value = LomsFateBonus.Difference(before, after);
            before["stat:命運"] = 4;
            Check(!LomsFateBonus.Matches(value, before, out string reason) && reason.Contains("起始值"), "Wrong starting state accepted.");
            Check(before["stat:體力"] == 0, "Precondition check mutated state.");
            before["stat:命運"] = 5;
            before.Remove("stat:體力");
            Check(!LomsFateBonus.Matches(value, before, out reason) && reason.Contains("不存在"), "Removed panel target accepted.");
        });
        Test("separate fate panels retain their own confirmed changes", () =>
        {
            var first = FateValues();
            var second = FateValues();
            second["stat:命運"] = 3;
            second["stat:體力"] = 10;
            var third = new Dictionary<string, int>(second);
            third["stat:命運"] = 2;
            third["flag:bonus"] = 1;
            var one = LomsFateBonus.Difference(first, second);
            var two = LomsFateBonus.Difference(second, third);
            Check(LomsFateBonus.Matches(one, first, out _) && LomsFateBonus.Matches(two, second, out _), "Panel sequence does not match.");
            Check(!LomsFateBonus.Matches(two, first, out _), "A later panel accepted the first panel's baseline.");
            Check(one.Changes.All(c => c.Key != "flag:bonus"), "Later edit changed an earlier event.");
        });
        Test("malformed fate events fall back to native save without altering files", () =>
        {
            var native = Native(252);
            string path = SaveNative("bad-fate", native);
            var data = Sidecar();
            var value = new FateBonusEvent();
            value.Changes.Add(new FateBonusChange { Key = "stat:命運", Before = 5, After = 3 });
            value.Changes.Add(new FateBonusChange { Key = "stat:命運", Before = 5, After = 3 });
            Check(!LomsFateBonus.IsValid(value), "Duplicate edits accepted.");
            data.Events.Add(value);
            LomsSave.CompleteSave(path, native, data);
            byte[] saved = File.ReadAllBytes(path + ".loms");
            Check(LomsSave.TryReadFile(path, native, out string warning) == null && warning != null, "Invalid replay accepted.");
            Check(saved.SequenceEqual(File.ReadAllBytes(path + ".loms")), "Rejected save was changed.");
            value.Changes.Clear();
            value.Changes.Add(new FateBonusChange { Key = "Game.Stats", Before = 0, After = 1 });
            Check(!LomsFateBonus.IsValid(value), "An unrelated field was accepted as a panel effect.");
            value.Changes[0].Key = "stat:";
            Check(!LomsFateBonus.IsValid(value), "Empty target accepted.");
            value.Changes[0] = new FateBonusChange { Key = "stat:體力", Before = int.MinValue, After = int.MaxValue };
            Check(!LomsFateBonus.IsValid(value), "Overflowing operation accepted.");
        });
        Test("legacy fate panel leaves the next dialogue event available for manual replay", () =>
        {
            LomsRuntime.Replaying = true;
            LomsRuntime.ReplayFormatVersion = 3;
            LomsRuntime.Cursor = 0;
            LomsRuntime.Events.Clear();
            var dialogue = new SayEvent { Hash = "unchanged" };
            LomsRuntime.Events.Add(dialogue);
            try
            {
                Check(!LomsFateBonus.TryReplay(null), "Old save attempted to invent panel choices.");
                Check(LomsRuntime.Replaying && LomsRuntime.Cursor == 0 && ReferenceEquals(LomsRuntime.Events[0], dialogue), "Legacy panel consumed a dialogue.");
            }
            finally { LomsRuntime.Replaying = false; LomsRuntime.ReplayFormatVersion = 0; LomsRuntime.Events.Clear(); }
        });
        Test("Free overwrite removes old sidecar", () =>
        {
            var native = Native(301);
            string path = SaveNative("free", native);
            LomsSave.CompleteSave(path, native, Sidecar());
            var free = Native(302, "Free");
            WriteNative(path, free);
            Check(LomsSave.CompleteSave(path, free, null), "Free commit rejected.");
            Check(!File.Exists(path + ".loms"), "Old replay survived a Free save.");
        });
        Test("new game overwrite removes old sidecar", () =>
        {
            var native = Native(401);
            string path = SaveNative("newgame", native);
            LomsSave.CompleteSave(path, native, Sidecar());
            var fresh = Native(402);
            WriteNative(path, fresh);
            Check(LomsSave.CompleteSave(path, fresh, null), "New game commit rejected.");
            Check(!File.Exists(path + ".loms"), "Old replay survived NewGameData.");
        });
        Test("failed native write preserves the previous replay", () =>
        {
            var native = Native(501);
            string path = SaveNative("failedwrite", native);
            LomsSave.CompleteSave(path, native, Sidecar());
            byte[] before = File.ReadAllBytes(path + ".loms");
            Check(!LomsSave.CompleteSave(path, Native(502, "Free"), null), "Uncommitted save accepted.");
            Check(before.SequenceEqual(File.ReadAllBytes(path + ".loms")), "Previous replay was removed.");
            Check(!LomsSave.CompleteSave(path, Native(503), Sidecar()), "Uncommitted story save accepted.");
            Check(before.SequenceEqual(File.ReadAllBytes(path + ".loms")), "Previous replay was overwritten.");
        });
        Test("replaced native save rejects stale sidecar", () =>
        {
            var native = Native(601);
            string path = SaveNative("mismatch", native);
            LomsSave.CompleteSave(path, native, Sidecar());
            var replacement = Native(602);
            WriteNative(path, replacement);
            Check(LomsSave.TryReadFile(path, replacement, out string warning) == null && warning != null,
                "Stale sidecar accepted.");
            Check(File.Exists(path + ".loms"), "Rejected user data must not be deleted on load.");
        });
        Test("missing sidecar uses native save without warning", () =>
        {
            var native = Native(701);
            string path = SaveNative("missing", native);
            Check(LomsSave.TryReadFile(path, native, out string warning) == null && warning == null, "Unexpected warning.");
        });
        Test("truncated sidecar falls back without altering files", () =>
        {
            var native = Native(801);
            string path = SaveNative("truncated", native);
            File.WriteAllBytes(path + ".loms", new byte[] { 0, 1, 2 });
            Check(LomsSave.TryReadFile(path, native, out string warning) == null && warning != null, "Corrupt data accepted.");
            Check(File.ReadAllBytes(path + ".loms").Length == 3, "Corrupt file modified.");
        });
        Test("future sidecar version falls back", () =>
        {
            var native = Native(901);
            string path = SaveNative("future", native);
            var data = Sidecar();
            data.FormatVersion = 100;
            LomsSave.CompleteSave(path, native, data);
            Check(LomsSave.TryReadFile(path, native, out string warning) == null && warning != null, "Future format accepted.");
        });
        Test("incomplete Lua context falls back", () =>
        {
            var native = Native(1001);
            string path = SaveNative("noglobals", native);
            var data = Sidecar();
            data.LuaGlobals = null;
            LomsSave.CompleteSave(path, native, data);
            Check(LomsSave.TryReadFile(path, native, out string warning) == null && warning != null, "Incomplete data accepted.");
        });
        Test("invalid Lua context falls back before starting replay", () =>
        {
            var native = Native(1051);
            string path = SaveNative("badglobal", native);
            var data = Sidecar();
            data.LuaGlobals["var1"] = new LomsLuaValue { Type = DataType.Function };
            LomsSave.CompleteSave(path, native, data);
            Check(LomsSave.TryReadFile(path, native, out string warning) == null && warning != null, "Invalid data accepted.");
        });
        Test("failed sidecar serialization preserves previous file", () =>
        {
            var native = Native(1101);
            string path = SaveNative("atomic", native);
            LomsSave.CompleteSave(path, native, Sidecar());
            byte[] before = File.ReadAllBytes(path + ".loms");
            var broken = Sidecar();
            broken.Events.Add(new NonSerializableEvent());
            Expect<SerializationException>(() => LomsSave.WriteFile(path, broken));
            Check(before.SequenceEqual(File.ReadAllBytes(path + ".loms")), "Old sidecar truncated.");
            Check(!File.Exists(path + ".loms.tmp"), "Partial temporary file left behind.");
        });
        Test("successful sidecar overwrite is atomic", () =>
        {
            var native = Native(1201);
            string path = SaveNative("replace", native);
            LomsSave.CompleteSave(path, native, Sidecar());
            var data = Sidecar();
            data.Events.Add(new ChooseEvent { Sel = 2 });
            Check(LomsSave.CompleteSave(path, native, data), "Replace rejected.");
            Check(LomsSave.TryReadFile(path, native, out _)?.Events.Count == 1, "Replacement not readable.");
            Check(!File.Exists(path + ".loms.tmp"), "Temporary file left behind.");
        });
        Test("Lua scalars survive save serialization and a fresh interpreter", () =>
        {
            var script = new Script();
            script.DoString("var1='back_work_001'; var2='back_02_501'; var3=42.5; var4=false; __loms__=true");
            var data = Sidecar();
            data.LuaGlobals = LomsLuaState.Capture(script);
            var native = Native(1301);
            string path = SaveNative("lua", native);
            LomsSave.CompleteSave(path, native, data);
            var loaded = LomsSave.TryReadFile(path, native, out _);
            var fresh = new Script();
            DynValue originalMath = fresh.Globals.Get("math");
            LomsLuaState.Restore(fresh, loaded.LuaGlobals);
            Check(fresh.Globals.Get("var1").String == "back_work_001", "String global lost.");
            Check(fresh.Globals.Get("var3").Number == 42.5, "Numeric global lost.");
            Check(fresh.Globals.Get("var4").Type == DataType.Boolean && !fresh.Globals.Get("var4").Boolean, "Boolean global lost.");
            Check(ReferenceEquals(originalMath.Table, fresh.Globals.Get("math").Table), "Library binding replaced.");
            Check(fresh.Globals.Get("__loms__").Type == DataType.Nil, "Injection marker restored from old interpreter.");
        });
        Test("actual back_02_501 branch works after restoring its predecessor globals", () =>
        {
            string code = File.ReadAllText(Path.Combine(_root, "assets/story/back_02_501.txt"));
            var missing = BackStoryEnvironment();
            Expect<InterpreterException>(() => missing.DoString(code));
            var original = new Script();
            original.DoString("var1='back_work_001'; var2='back_02_501'");
            var restored = BackStoryEnvironment();
            LomsLuaState.Restore(restored, LomsLuaState.Capture(original));
            restored.DoString(code);
            Check(restored.Globals.Get("next_script").String == "back_work_001", "Lost cross-script destination.");
        });
        Test("load source scopes isolate automatic/internal loads", () =>
        {
            Check(LomsIoContext.ManualLoadSlot == null, "Default load was treated as manual.");
            var instance = (SaveSystem)FormatterServices.GetUninitializedObject(typeof(SaveSystem));
            instance.SetSlot("001");
            Patches.SaveSystem_LoadGameData_Prefix(instance, out var scope);
            Check(LomsIoContext.ManualLoadSlot == "001", "Manual slot not captured.");
            using (LomsIoContext.EnterLoad(null)) Check(LomsIoContext.ManualLoadSlot == null, "Internal scope inherited manual slot.");
            Patches.SaveSystem_LoadGameData_Finalizer(scope);
            Check(LomsIoContext.ManualLoadSlot == null, "Following automatic load inherited the manual slot.");
        });
        Test("save/load context is restored after an exception", () =>
        {
            using (LomsIoContext.EnterLoad("outer"))
            {
                try { using (LomsIoContext.EnterLoad("inner")) throw new InvalidOperationException(); }
                catch (InvalidOperationException) { }
                Check(LomsIoContext.ManualLoadSlot == "outer", "Nested context leaked.");
                using (LomsIoContext.EnterSave("save"))
                    Check(LomsIoContext.ManualLoadSlot == "outer" && LomsIoContext.ManualSaveSlot == "save", "Save/load scopes interfered.");
            }
            Check(LomsIoContext.ManualLoadSlot == null && LomsIoContext.ManualSaveSlot == null, "Context leaked.");
        });
        Test("actual mid-script AutoSave does not discard preceding dialogue events", MidScriptBoundary);
        Test("real v2 fixture deserializes and safely falls back", LegacyFixture);
        Test("Harmony targets and injected arguments match shipped game assemblies", PatchBindings);
        Test("state snapshot survives serialization and is independent of live objects", () =>
        {
            var native = Native(1501);
            native.Stats.Add(new StatSave { Key = "金錢", Value = 100 });
            var globals = new Dictionary<string, LomsLuaValue> { ["var1"] = new LomsLuaValue { Type = DataType.String, Text = "next" } };
            var data = Sidecar();
            data.ValidationSnapshot = LomsStateSnapshot.Capture(native, globals, "story", "Say:test");
            string path = SaveNative("validation-roundtrip", native);
            LomsSave.CompleteSave(path, native, data);
            native.Stats[0].Value = 0;
            globals["var1"].Text = "changed";
            var frozen = LomsSave.TryReadFile(path, Native(1501), out _)?.ValidationSnapshot;
            Check(frozen != null && frozen.IsValid(), "Snapshot did not deserialize.");
            Check(frozen.Values["Game.Stats[\"金錢\"#0].Value"] == "100", "Snapshot changed with live stats.");
            Check(frozen.Values["Lua[\"var1\"].Value"] == "\"next\"", "Snapshot changed with live Lua context.");
        });
        Test("equivalent save state ignores collection ordering and timestamps", () =>
        {
            var before = Native(1601);
            before.Stats.Add(new StatSave { Key = "金錢", Value = 100 });
            before.Stats.Add(new StatSave { Key = "體力", Value = 50 });
            before.Books.Add(new BookSave { Id = "book1", Count = 2, MartialLevel = 3 });
            var after = Native(1602);
            after.Stats.Add(new StatSave { Key = "體力", Value = 50 });
            after.Stats.Add(new StatSave { Key = "金錢", Value = 100 });
            after.Books.Add(new BookSave { Id = "book1", Count = 2, MartialLevel = 3 });
            var result = LomsStateComparison.Compare(Snapshot(before), Snapshot(after));
            Check(result.Differences.Count == 0, "Ordering/timestamp caused a false difference.");
            Check(result.IgnoredDifferences.Count == 1, "Timestamp was not documented.");
        });
        Test("state diff identifies stats, inventory, relationships, flags and random state", () =>
        {
            var before = Native(1701);
            before.Stats.Add(new StatSave { Key = "金錢", Value = 100 });
            before.Books.Add(new BookSave { Id = "book1", Count = 2 });
            before.Relationships.Add(new RelationshipSave { Key = "friend", Value = 30, Active = true });
            before.EventFlags.Add(new EventFlagSave { Name = "story_flag", State = 1 });
            var after = Native(1702);
            after.Stats.Add(new StatSave { Key = "金錢", Value = 90 });
            after.Books.Add(new BookSave { Id = "book1", Count = 1 });
            after.Relationships.Add(new RelationshipSave { Key = "friend", Value = 30, Active = false });
            after.EventFlags.Add(new EventFlagSave { Name = "story_flag", State = 2 });
            after.FreeRandomState.s0 = 123;
            var result = LomsStateComparison.Compare(Snapshot(before), Snapshot(after));
            Check(result.Differences.Count == 5, "Expected five meaningful differences.");
            Check(result.Differences.Any(d => d.Path == "Game.Stats[\"金錢\"#0].Value" && d.Saved == "100" && d.Loaded == "90"), "Money diff is not actionable.");
            Check(result.Differences.Any(d => d.Path == "Game.FreeRandomState.s0"), "Random state difference hidden.");
        });
        Test("missing items and Lua globals remain visible in the diff", () =>
        {
            var before = Native(1801);
            before.Miscs.Add(new MiscSave { Id = "item1", Count = 1 });
            var globals = new Dictionary<string, LomsLuaValue> { ["var1"] = new LomsLuaValue { Type = DataType.String, Text = "next" } };
            var saved = LomsStateSnapshot.Capture(before, globals, "story", "Say:test");
            var result = LomsStateComparison.Compare(saved, Snapshot(Native(1802)));
            Check(result.Differences.Any(d => d.Path == "Game.Miscs[\"item1\"#0].Count" && d.Loaded == "<missing>"), "Missing inventory item concealed.");
            Check(result.Differences.Any(d => d.Path == "Lua[\"var1\"].Value" && d.Loaded == "<missing>"), "Missing cross-script global concealed.");
        });
        Test("embedded restore save is compared semantically with only its timestamp ignored", () =>
        {
            var before = Native(1901);
            before.RestoreData = "old JSON";
            var after = Native(1902);
            after.RestoreData = "new JSON";
            var embeddedBefore = Native(1);
            var embeddedAfter = Native(2);
            var saved = LomsStateSnapshot.Capture(before, new Dictionary<string, LomsLuaValue>(), "story", "Say:test", _ => embeddedBefore);
            var loaded = LomsStateSnapshot.Capture(after, new Dictionary<string, LomsLuaValue>(), "story", "Say:test", _ => embeddedAfter);
            var result = LomsStateComparison.Compare(saved, loaded);
            Check(result.Differences.Count == 0 && result.IgnoredDifferences.Count == 2, "Restore timestamps caused a false difference.");
            embeddedAfter.CurrentYear = 2;
            loaded = LomsStateSnapshot.Capture(after, new Dictionary<string, LomsLuaValue>(), "story", "Say:test", _ => embeddedAfter);
            Check(LomsStateComparison.Compare(saved, loaded).Differences.Any(d => d.Path == "Game.RestoreData.CurrentYear"), "Embedded gameplay state difference hidden.");
        });
        Test("effective stat modifiers participate even when saved base values match", () =>
        {
            var saved = Snapshot(Native(2001));
            var loaded = Snapshot(Native(2002));
            saved.Values["EffectiveStats[\"體力\"].FinalValue"] = "100";
            loaded.Values["EffectiveStats[\"體力\"].FinalValue"] = "80";
            Check(LomsStateComparison.Compare(saved, loaded).Differences.Count == 1, "Lost modifier concealed.");
        });
        Test("verification produces PASS and a full latest report at the same checkpoint", () =>
        {
            LomsVerification.ReportDirectory = Path.Combine(_output, "reports");
            var saved = Snapshot(Native(2101));
            LomsVerification.Begin(saved, "../slot");
            LomsVerification.ReplayFinished("cursor exhausted", 5, 5);
            LomsVerification.Verify(() => Snapshot(Native(2102)));
            string report = File.ReadAllText(LomsVerification.LastReportPath);
            Check(report.Contains("结果：PASS") && report.Contains("Game.Stats.$count"), "PASS/full state report missing.");
            Check(File.ReadAllText(Path.Combine(LomsVerification.ReportDirectory, "latest.txt")) == report, "Latest report differs.");
            Check(Path.GetDirectoryName(LomsVerification.LastReportPath) == Path.GetFullPath(LomsVerification.ReportDirectory), "Slot escaped report directory.");
        });
        Test("verification reports changed state and changed waiting location", () =>
        {
            LomsVerification.Begin(Snapshot(Native(2201)), "slot");
            LomsVerification.ReplayFinished("cursor exhausted", 2, 2);
            var changed = LomsStateSnapshot.Capture(Native(2202), new Dictionary<string, LomsLuaValue>(), "other_story", "Choose:2");
            LomsVerification.Verify(() => changed);
            string report = File.ReadAllText(LomsVerification.LastReportPath);
            Check(report.Contains("结果：DIFF") && report.Contains("Runtime.PendingInteraction"), "Different position claimed as consistent.");
        });
        Test("replay anomalies cannot produce PASS even when field values match", () =>
        {
            LomsVerification.Begin(Snapshot(Native(2301)), "slot");
            LomsVerification.ReplayIssue("對話錯位");
            LomsVerification.ReplayFinished("cursor exhausted", 2, 2);
            LomsVerification.Verify(() => Snapshot(Native(2302)));
            string report = File.ReadAllText(LomsVerification.LastReportPath);
            Check(report.Contains("结果：INCOMPLETE") && report.Contains("對話錯位"), "Replay anomaly produced PASS.");
        });
        Test("verification without a waiting checkpoint remains incomplete", () =>
        {
            var saved = LomsStateSnapshot.Capture(Native(2401), new Dictionary<string, LomsLuaValue>(), "story", null);
            LomsVerification.Begin(saved, "slot");
            LomsVerification.ReplayFinished("exhausted at script start", 0, 0);
            LomsVerification.Verify(() => LomsStateSnapshot.Capture(Native(2402), new Dictionary<string, LomsLuaValue>(), "story", null));
            Check(File.ReadAllText(LomsVerification.LastReportPath).Contains("结果：INCOMPLETE"), "Unknown save position produced PASS.");
        });
        Test("early replay termination cannot produce PASS", () =>
        {
            LomsVerification.Begin(Snapshot(Native(2451)), "slot");
            LomsVerification.ReplayFinished("type mismatch", 1, 2);
            LomsVerification.Verify(() => Snapshot(Native(2452)));
            Check(File.ReadAllText(LomsVerification.LastReportPath).Contains("结果：INCOMPLETE"), "Partial replay produced PASS.");
        });
        Test("capture failure produces an explicit incomplete report", () =>
        {
            LomsVerification.Begin(Snapshot(Native(2501)), "slot");
            LomsVerification.ReplayFinished("cursor exhausted", 0, 0);
            LomsVerification.Verify(() => throw new InvalidOperationException("test capture failed"));
            string report = File.ReadAllText(LomsVerification.LastReportPath);
            Check(report.Contains("结果：INCOMPLETE") && report.Contains("test capture failed"), "Snapshot failure produced PASS.");
        });
        Test("automatic verification waits for a live interaction and runs once", () =>
        {
            int captures = 0;
            LomsVerification.AutoCheck = true;
            LomsVerification.Begin(Snapshot(Native(2601)), "slot");
            LomsVerification.ReplayFinished("cursor exhausted", 0, 0);
            Func<LomsStateSnapshot> capture = () => { captures++; return Snapshot(Native(2602)); };
            Check(!LomsVerification.Poll(capture, false, 10), "Compared before reaching an interaction.");
            Check(LomsVerification.Poll(capture, true, 10.5f), "Live interaction did not trigger verification.");
            Check(!LomsVerification.Poll(capture, true, 11) && captures == 1, "Automatic report repeated.");
        });
        Test("new load cancels stale verification and missing baseline cannot produce PASS", () =>
        {
            LomsVerification.Begin(Snapshot(Native(2701)), "slot");
            LomsVerification.ReplayFinished("cursor exhausted", 0, 0);
            LomsVerification.Begin(null, "old slot");
            Check(!LomsVerification.AutoPending && LomsVerification.LastReportPath == null, "Old load retained verification.");
            Check(LomsVerification.Verify(() => Snapshot(Native(2702))).Contains("没有验证基准"), "Legacy save claimed as verified.");
        });
        Test("live Lua wrappers retain the waiting checkpoint across coroutine yields", LuaWaitCheckpoints);
        Test("MoonSharp can call the real static verification bindings from the preamble", () =>
        {
            var script = new Script();
            UserData.RegisterType(typeof(LomsLua));
            script.Globals["loms"] = UserData.CreateStatic(typeof(LomsLua));
            script.DoString(@"
              say=function(text) coroutine.yield('say') end
              choose=function(options) return 1 end
              choosetimer=choose
              wait=function(t) coroutine.yield('wait') end
            ");
            script.DoString(LomsPreamble.Code);
            LomsRuntime.SetLuaScript(script);
            var coroutine = script.CreateCoroutine(script.LoadString("say('binding test')"));
            coroutine.Coroutine.Resume();
            Check(LomsRuntime.CurrentCheckpoint == "Say:" + LomsRuntime.HashText("binding test"), "Static wait_begin binding failed.");
            LomsRuntime.SetLuaScript(null);
        });
        Test("real previous format-3 save still loads without an optional diagnostic baseline", Legacy3Fixture);
        LomsVerification.Reset();
        // Installing the detours in this standalone CLR was attempted, but
        // Unity's internal ECall methods cannot be JIT-compiled outside its
        // player (SecurityException). Metadata checks do not replace that run.
        Console.WriteLine("NOT RUN: Unity player patch installation and interactive save/load scenarios. See tests/README.md.");
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed. Output: {_output}");
        return _failed == 0 ? 0 : 1;
    }

    private static LomsStateSnapshot Snapshot(GameSave save) => LomsStateSnapshot.Capture(save,
        new Dictionary<string, LomsLuaValue>(), "story", "Say:test");

    private static void LuaWaitCheckpoints()
    {
        var script = new Script();
        LomsRuntime.SetLuaScript(script);
        var bindings = new Table(script);
        bindings["on_say"] = (Func<string, bool>)(_ => false);
        bindings["on_choose"] = (Func<int, int>)(_ => 0);
        bindings["on_random"] = (Func<double, double>)(value => value);
        bindings["replaying"] = (Func<bool>)(() => false);
        bindings["wait_begin"] = (Func<string, string, int>)LomsLua.wait_begin;
        bindings["wait_end"] = (Action<int>)LomsLua.wait_end;
        bindings["say_done"] = (Action<string>)(_ => { });
        bindings["choose_done"] = (Action<int>)(_ => { });
        script.Globals["loms"] = bindings;
        script.DoString(@"
          say=function(text) wait(0.1); coroutine.yield('say') end
          choose=function(options) coroutine.yield('choose'); return 1 end
          choosetimer=function(options, duration, default) coroutine.yield('timer'); return default end
          wait=function(t) coroutine.yield('wait') end
        ");
        script.DoString(LomsPreamble.Code);
        var coroutine = script.CreateCoroutine(script.LoadString("say('Hello'); choose({'a','b'}); choosetimer({'a'}, 5, 1); wait(2)"));
        coroutine.Coroutine.Resume();
        string say = "Say:" + LomsRuntime.HashText("Hello");
        Check(LomsRuntime.CurrentCheckpoint == say, "Nested wait replaced the dialogue checkpoint.");
        coroutine.Coroutine.Resume();
        Check(LomsRuntime.CurrentCheckpoint == say, "Say marker lost after nested wait resumed.");
        coroutine.Coroutine.Resume();
        Check(LomsRuntime.CurrentCheckpoint == "Choose:" + LomsRuntime.HashText("2:1:a:1:b"), "Choose marker missing.");
        coroutine.Coroutine.Resume();
        Check(LomsRuntime.CurrentCheckpoint == "ChooseTimer:" + LomsRuntime.HashText("1:1:a:5:1"), "Timed choice marker missing.");
        coroutine.Coroutine.Resume();
        Check(LomsRuntime.CurrentCheckpoint == "Wait:2", "Timed wait marker missing.");
        coroutine.Coroutine.Resume();
        Check(LomsRuntime.CurrentCheckpoint == null, "Completed wait leaked into later snapshots.");
        var different = script.CreateCoroutine(script.LoadString("choose({'a','c'})"));
        different.Coroutine.Resume();
        Check(LomsRuntime.CurrentCheckpoint != "Choose:" + LomsRuntime.HashText("2:1:a:1:b"), "Different choices with the same count shared a checkpoint.");
        LomsRuntime.SetLuaScript(null);
    }

    private static void Legacy3Fixture()
    {
        string legacy = Path.Combine(_root, "tests/fixtures/format3/LOMSave2.dll");
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            Quote(_gameDir) + " " + Quote(_root) + " " + Quote(_output) + " --legacy3 " + Quote(legacy))
        { UseShellExecute = false, CreateNoWindow = true };
        using (var process = Process.Start(start))
        {
            Check(process.WaitForExit(15000) && process.ExitCode == 0, "Previous format-3 fixture generation failed.");
        }
        var data = LomsSave.TryReadFile(Path.Combine(_output, "legacy3.dat"), Native(2801), out string warning);
        Check(data != null && warning == null && data.ValidationSnapshot == null, "Previous format-3 save did not remain compatible: " + warning);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WriteLegacy3Fixture(string dll)
    {
        Assembly old = Assembly.LoadFrom(dll);
        Type dataType = old.GetType("LOMSave2.LomsSaveData", true);
        object data = Activator.CreateInstance(dataType);
        var native = Native(2801);
        string path = Path.Combine(_output, "legacy3.dat");
        WriteNative(path, native);
        dataType.GetField("BoundarySave").SetValue(data, Native(1));
        dataType.GetField("BoundaryScript").SetValue(data, "back_02_501");
        dataType.GetField("Scene").SetValue(data, "Story");
        Type globalsType = typeof(Dictionary<,>).MakeGenericType(typeof(string), old.GetType("LOMSave2.LomsLuaValue", true));
        dataType.GetField("LuaGlobals").SetValue(data, Activator.CreateInstance(globalsType));
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) dataType.GetField("NativeSaveHash").SetValue(data, Convert.ToBase64String(sha.ComputeHash(stream)));
        using (var file = File.Create(path + ".loms")) new BinaryFormatter().Serialize(file, data);
        return 0;
    }

    private static void PatchBindings()
    {
        int checkedMethods = 0;
        var methods = typeof(Patches).GetMethods(BindingFlags.Public | BindingFlags.Static);
        foreach (MethodInfo patch in methods)
        {
            var annotation = patch.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().SingleOrDefault();
            if (annotation == null) continue;
            HarmonyMethod target = annotation.info;
            MethodInfo original = AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes);
            Check(original != null, "Missing target for " + patch.Name);
            foreach (ParameterInfo parameter in patch.GetParameters())
            {
                string name = parameter.Name;
                Type expected;
                if (name == "__instance") expected = original.DeclaringType;
                else if (name == "__result") expected = original.ReturnType;
                else if (name == "__args") expected = typeof(object[]);
                else if (name == "__state")
                {
                    // Harmony transfers this value only between patches on
                    // the same target and in the same declaring patch class.
                    var prefix = methods.Single(m => m.IsDefined(typeof(HarmonyPrefix), false)
                        && m.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().Any(a =>
                            AccessTools.Method(a.info.declaringType, a.info.methodName, a.info.argumentTypes) == original));
                    var state = prefix.GetParameters().Single(p => p.Name == "__state");
                    Check(state.IsOut, "Prefix does not initialize state: " + prefix.Name);
                    expected = state.ParameterType;
                }
                else if (name.StartsWith("___", StringComparison.Ordinal))
                {
                    FieldInfo field = AccessTools.Field(original.DeclaringType, name.Substring(3));
                    Check(field != null, "Missing injected field: " + name);
                    expected = field.FieldType;
                }
                else
                {
                    ParameterInfo source = original.GetParameters().SingleOrDefault(p => p.Name == name);
                    Check(source != null, "Missing original argument for " + patch.Name + ": " + name);
                    expected = source.ParameterType;
                }
                Type actual = parameter.ParameterType;
                if (actual.IsByRef) actual = actual.GetElementType();
                if (expected.IsByRef) expected = expected.GetElementType();
                Check(actual.IsAssignableFrom(expected), "Incompatible argument for " + patch.Name + ": " + name);
            }
            checkedMethods++;
        }
        Check(checkedMethods > 0, "No patches inspected.");
        Console.WriteLine("  Checked " + checkedMethods + " patch methods.");
    }

    private static void MidScriptBoundary()
    {
        // The production patch must not intercept this checkpoint setter: it
        // can occur after dialogue, unlike ExecuteLuaScript's true entry.
        var patchAttributes = typeof(Patches).GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>());
        Check(!patchAttributes.Any(p => p.info.methodName == "SetStartScript"), "Mid-script checkpoint still refreshes the boundary.");
        var script = new Script();
        var events = new List<string>();
        script.Globals["record_say"] = (Action<string>)(_ => events.Add("say"));
        script.Globals["record_choice"] = (Action)(() => events.Add("choose"));
        script.DoString(@"
          local noop=function(...) return {} end
          characters=setmetatable({}, {__index=function() return noop end})
          stage=characters
          flowcharts={view={}, LoadView=noop}
          runblock=noop; runwait=noop; wait=noop; getvar=noop
          setsaydialog=noop; setcharacter=noop; setmenudialog=noop
          sayoptions={}; saydialogs={center={},character={}}; narrative={}
          menudialogs={Options={SetActive=noop}}
          luamanager={PlayMusic=noop, SetGameTime=noop, AutoSave=noop, GetStoryText=function(k) return k end}
          statmodifymanager={SetFlag=noop, Player=noop, GetDisplayTime=function() return 0 end}
          say=function(t) record_say(t) end
          choose=function(t) record_choice(); return 1 end
        ");
        // End just after the first selected branch's dialogue; close that if.
        string code = string.Join("\n", File.ReadAllLines(Path.Combine(_root, "assets/story/ch2_4_5_3.txt")).Take(81)) + "\nend";
        script.DoString(code);
        Check(string.Join(",", events) == "say,say,choose,say", "Dialogue prefix is missing or reordered.");
    }

    private static Script BackStoryEnvironment()
    {
        var script = new Script();
        script.DoString(@"
          local noop=function(...) end
          statmodifymanager={Player=noop, GetDisplayTime=function() return 0 end}
          wait=noop; sayoptions={}; saydialogs={narrative={}}; narrative={}
          setsaydialog=noop; setcharacter=noop; say=noop; setmenudialog=noop
          menudialogs={Options={SetActive=noop}}
          choose=function(options) return 1 end
          luamanager={GetStoryText=function(k) return k end,
            SetNextScript=function(k) assert(k ~= nil, 'missing next script'); next_script=k end, Init=noop}
        ");
        return script;
    }

    private static void LegacyFixture()
    {
        string legacy = Path.Combine(_root, "tests/fixtures/format2/LOMSave2.dll");
        Check(File.Exists(legacy), "Baseline DLL required for compatibility fixture.");
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            Quote(_gameDir) + " " + Quote(_root) + " " + Quote(_output) + " --legacy " + Quote(legacy))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var process = Process.Start(start))
        {
            Check(process.WaitForExit(15000), "Legacy fixture generator timed out.");
            Check(process.ExitCode == 0, "Legacy fixture generation failed.");
        }
        string path = Path.Combine(_output, "legacy.dat");
        WriteNative(path, Native(1401));
        byte[] before = File.ReadAllBytes(path + ".loms");
        Check(LomsSave.TryReadFile(path, Native(1401), out string warning) == null, "Unverifiable legacy replay accepted.");
        Check(warning != null && warning.Contains("版本"), "Optional legacy fields failed deserialization: " + warning);
        Check(before.SequenceEqual(File.ReadAllBytes(path + ".loms")), "Legacy file was modified.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WriteLegacyFixture(string dll)
    {
        // Run in a separate CLR process, loading the untouched v2 assembly.
        Assembly old = Assembly.LoadFrom(dll);
        object data = Activator.CreateInstance(old.GetType("LOMSave2.LomsSaveData", true));
        using (var file = File.Create(Path.Combine(_output, "legacy.dat.loms"))) new BinaryFormatter().Serialize(file, data);
        return 0;
    }

    private static GameSave Native(long tick, string scene = "Story") => new GameSave
    {
        TimeTick = tick, CurrentScene = scene, StartStoryScript = "back_02_501", Version = "test",
    };

    private static Dictionary<string, int> FateValues() => new Dictionary<string, int> {
        ["stat:命運"] = 5, ["stat:體力"] = 0, ["social:掌門人"] = 10, ["flag:bonus"] = 0,
    };

    private static LomsSaveData Sidecar() => new LomsSaveData
    {
        BoundarySave = Native(1), BoundaryScript = "back_02_501", Scene = "Story",
        LuaGlobals = new Dictionary<string, LomsLuaValue>(),
    };

    private static string SaveNative(string name, GameSave data)
    {
        string path = Path.Combine(_output, name + ".dat");
        WriteNative(path, data);
        return path;
    }

    private static void WriteNative(string path, GameSave data)
    {
        using (var file = File.Create(path)) new BinaryFormatter().Serialize(file, data);
    }

    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";

    private sealed class NonSerializableEvent : LomsEvent { }
}
