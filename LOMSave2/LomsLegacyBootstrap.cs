namespace LOMSave2
{
    // Old sidecars captured this child after its parent loaded the kitchen and
    // the two actors. Recover only presentation resources; never re-execute the
    // parent's choices, rewards, missions or stat changes.
    public static class LomsLegacyBootstrap
    {
        private static string _pending;

        public static void BeginReplay(string boundary)
        {
            _pending = boundary == "S8300_01_01" ? boundary : null;
        }

        public static void Reset() { _pending = null; }

        public static string Prepend(string source, string friendlyName)
        {
            if (_pending == null || friendlyName != _pending + ".LuaScript") return source;
            _pending = null;
            LomsRuntime.Log?.LogInfo("[Loms] Restoring legacy kitchen child resources before replay (visual setup only)");
            return KitchenPrelude + "\n" + source;
        }

        // runwait stays inside the original Lua coroutine and waits until
        // Addressables and Character.Setup have fully completed.
        public const string KitchenPrelude = @"
runwait(flowcharts.LoadView(""kitchen""))
flowcharts.ShowView(""kitchen"")
mainui.DisplayTitle(""P_Kitchen_01"")
runwait(characters.LoadCharacterAsset(""player""))
characters.LoadCharacterPortrait(""player"", ""nervous1"")
stage.show{character=characters.Get(""player""), portrait=characters.GetPortrait(""player"", ""nervous1""), fromPosition=""L2"", toPosition=""L2"", facing=""right"", fadeDuration=0, moveDuration=0, useDefaultSettings=false}
characters.ResetRotation(""player"")
runwait(characters.LoadCharacterAsset(""brother1""))
characters.LoadCharacterPortrait(""brother1"", ""laugh1"")
stage.show{character=characters.Get(""brother1""), portrait=characters.GetPortrait(""brother1"", ""laugh1""), fromPosition=""R2"", toPosition=""R2"", facing=""left"", fadeDuration=0, moveDuration=0, useDefaultSettings=false}
characters.ResetRotation(""brother1"")
";
    }
}
