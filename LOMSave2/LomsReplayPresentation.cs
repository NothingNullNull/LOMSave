using System;
using System.Reflection;

namespace LOMSave2
{
    // Skipped dialogue can allocate hundreds of native portrait tweens in one
    // Lua resume. Let Unity update them before the fixed-size pool is full.
    // Do not cancel/complete tweens: their native callbacks still own cleanup.
    public static class LomsReplayPresentation
    {
        private static readonly FieldInfo Tweens = typeof(LeanTween).GetField("tweens",
            BindingFlags.Static | BindingFlags.NonPublic);

        public static bool Busy()
        {
            if (!LomsRuntime.Replaying || LomsReplayGuard.RecoveryRequired || Tweens == null) return false;
            return HasPressure(Tweens.GetValue(null) as LTDescr[]);
        }

        public static bool HasPressure(LTDescr[] tweens)
        {
            if (tweens == null || tweens.Length == 0) return false;
            // Leave a third of the pool for the next native story command.
            // Count the actual array, not tweensRunning: this game's getter
            // can index one past its end when its last slot has been used.
            int limit = Math.Max(1, tweens.Length * 2 / 3);
            int active = 0;
            foreach (LTDescr tween in tweens)
                if (tween != null && tween.toggle && ++active >= limit) return true;
            return false;
        }
    }
}
