using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace LOMSave2
{
    [Serializable]
    public sealed class LomsLuaValue
    {
        public DataType Type;
        public string Text;
        public double Number;
        public bool Boolean;
    }

    public static class LomsLuaState
    {
        public static bool IsValid(Dictionary<string, LomsLuaValue> values)
        {
            if (values == null) return false;
            foreach (var pair in values)
            {
                LomsLuaValue value = pair.Value;
                if (value == null) return false;
                if (value.Type == DataType.String && value.Text != null) continue;
                if (value.Type == DataType.Number || value.Type == DataType.Boolean) continue;
                return false;
            }
            return true;
        }

        // The shipped story scripts carry strings/numbers in var1..var4 across
        // LuaManager.Init calls. Capture scalar globals, not Unity userdata,
        // closures or library tables bound to the previous scene.
        public static Dictionary<string, LomsLuaValue> Capture(Script script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            var values = new Dictionary<string, LomsLuaValue>();
            foreach (TablePair pair in script.Globals.Pairs)
            {
                if (pair.Key.Type != DataType.String || pair.Key.String == "__loms__") continue;
                DynValue value = pair.Value;
                if (value.Type != DataType.String && value.Type != DataType.Number && value.Type != DataType.Boolean) continue;
                values.Add(pair.Key.String, new LomsLuaValue
                {
                    Type = value.Type,
                    Text = value.Type == DataType.String ? value.String : null,
                    Number = value.Type == DataType.Number ? value.Number : 0,
                    Boolean = value.Type == DataType.Boolean && value.Boolean,
                });
            }
            return values;
        }

        public static void Restore(Script script, Dictionary<string, LomsLuaValue> values)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (!IsValid(values)) throw new ArgumentException("Invalid saved Lua globals", nameof(values));
            foreach (var pair in values)
            {
                if (pair.Key == "__loms__") continue;
                LomsLuaValue value = pair.Value;
                switch (value.Type)
                {
                    case DataType.String: script.Globals.Set(pair.Key, DynValue.NewString(value.Text)); break;
                    case DataType.Number: script.Globals.Set(pair.Key, DynValue.NewNumber(value.Number)); break;
                    case DataType.Boolean: script.Globals.Set(pair.Key, DynValue.NewBoolean(value.Boolean)); break;
                    default: throw new InvalidOperationException("Unsupported saved Lua global: " + pair.Key);
                }
            }
        }
    }
}
