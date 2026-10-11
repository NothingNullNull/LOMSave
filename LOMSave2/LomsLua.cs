namespace LOMSave2
{
    /// <summary>
    /// Exposed to Lua as static userdata `loms`. Method names are snake_case on purpose.
    /// </summary>
    public static class LomsLua
    {
        public static bool on_say(string text)
        {
            if (!LomsRuntime.Replaying) return false;
            SayEvent e = LomsRuntime.Consume<SayEvent>();
            if (e == null) return false;
            string h = LomsRuntime.HashText(text);
            if (!string.Equals(h, e.Hash, System.StringComparison.OrdinalIgnoreCase))
            {
                LomsRuntime.Log?.LogWarning($"[Loms] Say hash mismatch @{LomsRuntime.Cursor - 1}: {h} != {e.Hash} (positional skip still applied)");
                LomsVerification.ReplayIssue($"对话错位 @{LomsRuntime.Cursor - 1}: {h} != {e.Hash}");
            }
            return true;
        }

        public static void say_done(string text)
        {
            LomsRuntime.Record(new SayEvent { Hash = LomsRuntime.HashText(text) });
        }

        public static int on_choose(int count)
        {
            if (!LomsRuntime.Replaying) return 0;
            ChooseEvent e = LomsRuntime.Consume<ChooseEvent>();
            if (e != null && (e.Sel < 1 || e.Sel > count))
                LomsVerification.ReplayIssue($"选项越界：{e.Sel}/{count}");
            return e != null ? e.Sel : 0;
        }

        public static void choose_done(int v)
        {
            LomsRuntime.Record(new ChooseEvent { Sel = v });
        }

        public static double on_random(double v)
        {
            if (!LomsRuntime.Replaying)
            {
                LomsRuntime.Record(new RandEvent { V = v });
                return v;
            }
            RandEvent e = LomsRuntime.Consume<RandEvent>();
            if (e == null)
            {
                LomsRuntime.Record(new RandEvent { V = v });
                return v;
            }
            return e.V;
        }

        public static bool replaying()
        {
            return LomsRuntime.Replaying;
        }

        public static int wait_begin(string kind, string key)
        {
            if (kind == "Say" || kind == "Choose" || kind == "ChooseTimer") key = LomsRuntime.HashText(key);
            return LomsRuntime.EnterWait(kind, key);
        }

        public static void wait_end(int token) => LomsRuntime.LeaveWait(token);
    }

    public static class LomsPreamble
    {
        public const string Code = @"
-- LOMSave2 preamble: record/replay wrappers around story-facing globals.
if say and choose and choosetimer and wait and math and math.random then
  local loms_raw_say = say
  local loms_raw_choose = choose
  local loms_raw_choosetimer = choosetimer
  local loms_raw_wait = wait
  local loms_raw_random = math.random

  local function loms_options_key(options)
    local key = tostring(#options)
    for i=1,#options do
      local text = tostring(options[i])
      key = key .. ':' .. tostring(#text) .. ':' .. text
    end
    return key
  end

  say = function(text, voice)
    if loms.on_say(text) then return end
    local token = loms.wait_begin('Say', text)
    loms_raw_say(text, voice)
    loms.wait_end(token)
    loms.say_done(text)
  end

  choose = function(options)
    local r = loms.on_choose(#options)
    if r > 0 then return r end
    local token = loms.wait_begin('Choose', loms_options_key(options))
    local v = loms_raw_choose(options)
    loms.wait_end(token)
    loms.choose_done(v)
    return v
  end

  choosetimer = function(options, duration, default)
    local r = loms.on_choose(#options)
    if r > 0 then return r end
    local token = loms.wait_begin('ChooseTimer', loms_options_key(options) .. ':' .. tostring(duration) .. ':' .. tostring(default))
    local v = loms_raw_choosetimer(options, duration, default)
    loms.wait_end(token)
    loms.choose_done(v)
    return v
  end

  wait = function(t)
    if loms.replaying() then return end
    local token = loms.wait_begin('Wait', tostring(t))
    loms_raw_wait(t)
    loms.wait_end(token)
  end

  math.random = function(a, b)
    local v
    if a == nil then v = loms_raw_random()
    elseif b == nil then v = loms_raw_random(a)
    else v = loms_raw_random(a, b) end
    return loms.on_random(v)
  end
end
";
    }
}
