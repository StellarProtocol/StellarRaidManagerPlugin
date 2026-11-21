using System;

namespace Stellar.RaidManager;

// "Replace game countdown" + "Countdown sounds" (approved addenda 2026-11-17; research KB Team-Confirm-Countdown.md).
//
// The game's leader countdown (team window → fixed 10 s, notify → TeamData:StartTeamConfirmTimer(startSec)) is detected
// and hidden from the Lua side, and shown as OUR countdown card instead:
//   • TeamData StartTeamConfirmTimer / StopTeamConfirmTimer are wrapped ON THE CLASS (require "ui.model.team_data", the
//     DOT path DataMgr itself requires → the same table): the wrapper parks {kind, startSec, total, nowMs} + a sequence
//     counter in rawset globals, then calls the original. The game's 1 s data timer keeps playing ITS sounds (they're
//     owned by the data timer, not the view). class() instances copy no methods (zutil/class.lua new →
//     setmetatableindex(instance, cls)), so every instance reaches the wrapper through __index.
//     ⚠️ We must NEVER call Z.DataMgr.Get("team_data") from the install timer (v2.9.0 did): DataMgr caches the object
//     BEFORE Init(), so if we are the first caller while TeamTargetTableMgr isn't registered yet (tables load async;
//     world-active ≠ tables ready) Init throws inside our pcall and a half-built team_data sticks for the session —
//     My Party then shows "Loading" forever (KB Lua-Injection-from-CSharp.md "Z.DataMgr.Get caches BEFORE Init()").
//     The same 2 s install also SELF-HEALS such a session (ours from 2.9.0, or another mod's): it peeks DataMgr's
//     cached_data upvalue via debug.getupvalue (never creates team_data), and if the cached instance lacks
//     TeamTargetTableDatas once the table is registered, re-runs InitCfgData once and bumps _rm_td_healed (logged once).
//   • mainui_team_countdown_ui_comp.refreshTeamCountDownView is overridden on the module table (DOT require — the form
//     mainui_view uses; a slash require would be a second, unused copy): while replace is ON it only DeActives the
//     game's main_team_time_sub view; OFF calls the original. The StartConfirmCountDown handler is NOT touched (it is
//     bound into Z.EventMgr at Init and calls refreshTeamCountDownView by name, so the override reaches it).
//   • C# never receives a Lua callback (they crash IL2CPP): it polls the sequence global ≤10 Hz with one typed read.
//   • PHASE LOCK: the game's beeps come from ITS 1 s timer, which starts when the notify ARRIVES — an arbitrary
//     0..1 s phase vs the server second. Computing remaining from startSec + server clock therefore flipped our number
//     up to ~1 s off the beep. Instead a pure-Lua listener on Team.UpdateCountDown (dispatched by that same timer right
//     after its sound) parks teamCountDown in _rm_gct_count and bumps _rm_gct_tick; the start wrapper parks the initial
//     teamCountDown. The card shows exactly that count and flips on the tick (see AdvanceLockedCountdown).
// Everything is defined ONCE as rawset globals (the game's _G drops plain global writes) and re-defined only if a Lua
// reset lost them; install is idempotent (per-module markers) and re-run every 2 s so a reloaded module gets wrapped
// again.
public sealed partial class Plugin
{
    private const string GctDefChunk = @"pcall(function()
rawset(_G, '__rm_gctObj', rawget(_G, '__rm_gctObj') or {})
rawset(_G, '__rm_gctOnTick', function()
  pcall(function()
    local c = tonumber(Z.DataMgr.Get('team_data').teamCountDown)
    if c ~= nil then
      rawset(_G, '_rm_gct_count', c)
      rawset(_G, '_rm_gct_tick', (rawget(_G, '_rm_gct_tick') or 0) + 1)
    end
  end)
end)
rawset(_G, '__rm_gctListen', function()
  pcall(function()
    Z.EventMgr:Add(Z.ConstValue.Team.UpdateCountDown, rawget(_G, '__rm_gctOnTick'), rawget(_G, '__rm_gctObj'))
  end)
end)
rawset(_G, '__rm_gctInstall', function()
  __rm_gctListen()
  pcall(function()
    local C = require('ui.model.team_data')
    if type(C) == 'table' and rawget(C, '__rm_gct') ~= true then
      local start, stop = rawget(C, 'StartTeamConfirmTimer'), rawget(C, 'StopTeamConfirmTimer')
      if start ~= nil and stop ~= nil then
        rawset(C, '__rm_gctStart', start)
        rawset(C, '__rm_gctStop', stop)
        rawset(C, 'StartTeamConfirmTimer', function(self, startSec)
          pcall(function()
            rawset(_G, '_rm_gct_start', tonumber(startSec) or 0)
            rawset(_G, '_rm_gct_total', tonumber(Z.Global.TeamConfirmTotalTime) or 10)
            rawset(_G, '_rm_gct_now', tonumber(Z.TimeTools.Now()) or 0)
          end)
          local r = start(self, startSec)
          pcall(function()
            rawset(_G, '_rm_gct_count', tonumber(self.teamCountDown) or -1)
            rawset(_G, '_rm_gct_kind', 1)
            rawset(_G, '_rm_gct_seq', (rawget(_G, '_rm_gct_seq') or 0) + 1)
            __rm_gctListen()
          end)
          return r
        end)
        rawset(C, 'StopTeamConfirmTimer', function(self)
          pcall(function()
            rawset(_G, '_rm_gct_kind', 2)
            rawset(_G, '_rm_gct_seq', (rawget(_G, '_rm_gct_seq') or 0) + 1)
          end)
          return stop(self)
        end)
        rawset(C, '__rm_gct', true)
      end
    end
  end)
  pcall(function()
    if Z.TableMgr.GetTable('TeamTargetTableMgr') == nil then return end
    local up = debug ~= nil and debug.getupvalue or nil
    if up == nil then return end
    local cache
    for i = 1, 8 do
      local n, v = up(Z.DataMgr.Get, i)
      if n == nil then break end
      if n == 'cached_data' then cache = v break end
    end
    local td = type(cache) == 'table' and rawget(cache, 'team_data') or nil
    if td ~= nil and rawget(td, 'TeamTargetTableDatas') == nil and rawget(td, '__rm_healed') ~= true then
      rawset(td, '__rm_healed', true)
      td:InitCfgData()
      if rawget(td, 'TeamTargetTableDatas') ~= nil then
        rawset(_G, '_rm_td_healed', (rawget(_G, '_rm_td_healed') or 0) + 1)
      end
    end
  end)
  pcall(function()
    local C = require('ui.view.main.mainui_team_countdown_ui_comp')
    if type(C) == 'table' and rawget(C, '__rm_orig') == nil then
      local orig = C.refreshTeamCountDownView
      if orig ~= nil then
        rawset(C, '__rm_orig', orig)
        rawset(C, 'refreshTeamCountDownView', function(self)
          rawset(_G, '__rm_gctComp', self)
          if rawget(_G, '_rm_gct_replace') == true then
            local v = self.teamTimeSubView_
            if v ~= nil then v:DeActive() end
            return
          end
          return orig(self)
        end)
      end
    end
  end)
  if rawget(_G, '_rm_gct_seq') == nil then rawset(_G, '_rm_gct_seq', 0) end
  if rawget(_G, '_rm_gct_tick') == nil then rawset(_G, '_rm_gct_tick', 0) end
end)
rawset(_G, '__rm_gctApply', function(on)
  rawset(_G, '_rm_gct_replace', on == true)
  pcall(function()
    local c = rawget(_G, '__rm_gctComp')
    if c ~= nil and c.uiBinder ~= nil then c:refreshTeamCountDownView() end
  end)
end)
rawset(_G, '__rm_ctSound', function(zero)
  pcall(function()
    if zero == 1 then Z.AudioMgr:Play('sys_parkour_racing_0') else Z.AudioMgr:Play('sys_parkour_racing_321') end
  end)
end)
rawset(_G, '_rm_gct_def', true)
end)";

    // One-line calls into the defined functions — constant strings, so nothing is built per call. pcall(nil) after a
    // Lua reset just returns false (the next EnsureGctLua re-defines).
    private const string GctInstallCall = "pcall(__rm_gctInstall)";
    private const string GctApplyOn     = "pcall(__rm_gctApply, true)";
    private const string GctApplyOff    = "pcall(__rm_gctApply, false)";
    private const string CtSoundTick    = "pcall(__rm_ctSound, 0)";
    private const string CtSoundZero    = "pcall(__rm_ctSound, 1)";

    private const float GctInstallEvery = 2f;
    private const float GctPollEvery    = 0.1f;   // ≤10 Hz
    private float _gctInstallAcc = GctInstallEvery;   // first world tick installs at once
    private float _gctPollAcc;
    private double _gctSeq = -1d;                     // last seen sequence; < 0 = take a baseline first
    private double _tdHealed;                         // last seen _rm_td_healed (team_data self-heal count)

    // Defines the Lua side if it isn't there (first use, or a Lua reset dropped the globals) and re-asserts the replace
    // flag. A typed bool read when it is already defined.
    private bool EnsureGctLua()
    {
        var lua = _services.Lua;
        if (!lua.Ready) return false;
        if (lua.TryReadGlobalBool("_rm_gct_def", out var def) && def) return true;
        lua.DoString(GctDefChunk);
        lua.DoString(_ctReplaceGame ? GctApplyOn : GctApplyOff);
        return true;
    }

    // OnUpdate (world-gated, main thread). Does nothing while "Replace game countdown" is OFF.
    private void TickGameCountdown(float dt)
    {
        if (!_ctReplaceGame) return;
        if ((_gctInstallAcc += dt) >= GctInstallEvery)
        {
            _gctInstallAcc = 0f;
            if (EnsureGctLua())
            {
                _services.Lua.DoString(GctInstallCall);
                // Self-heal report: one typed read at the 2 s cadence, a log line only when the count goes up.
                if (_services.Lua.TryReadGlobalNumber("_rm_td_healed", out var healed) && healed > _tdHealed)
                {
                    _tdHealed = healed;
                    _services.Log.Info("[RaidManager] repaired party data (team target table)");
                }
            }
        }
        if ((_gctPollAcc += dt) < GctPollEvery) return;
        _gctPollAcc = 0f;
        PollGameCountdown(resume: false);
    }

    private void PollGameCountdown(bool resume)
    {
        var lua = _services.Lua;
        if (!lua.Ready || !lua.TryReadGlobalNumber("_rm_gct_seq", out var seq)) return;
        // Baseline (first look, or the counter went backwards after a Lua reset): events from before don't count —
        // except on a resume (replace switched ON), where a still-running game countdown is picked up below.
        bool changed = _gctSeq >= 0d && seq > _gctSeq;
        _gctSeq = seq;
        if (!changed && !resume) return;
        if (!lua.TryReadGlobalNumber("_rm_gct_kind", out var kind)) return;
        if (kind == 2d)
        {
            // Quit team / scene change / the game's own end (1 s after 0): end a GAME countdown still running; a GO!
            // already showing finishes its second; a /ct countdown is not the game's to stop.
            if (_running && _ctFromGame && !resume) StopCountdown();
            return;
        }
        if (kind != 1d) return;
        lua.TryReadGlobalNumber("_rm_gct_total", out var total);
        if (total <= 0d) total = 10d;
        // Phase-locked path: the game's own counter (initial value from the start wrapper, then each timer tick).
        if (lua.TryReadGlobalNumber("_rm_gct_count", out var count) && lua.TryReadGlobalNumber("_rm_gct_tick", out var tick))
        {
            if (count <= 0d || count > total + 1d) return;   // late / stale notify: the game shows no view either
            BeginCountdown(count, total, fromGame: true, endServerMs: 0);
            _gctLocked = true;
            _gctTick   = tick;   // baseline: the start wrapper bumps seq, not tick → the next tick is the first beep
            _gctCount  = count;
            _gctTickAge = 0f;
            return;
        }
        lua.TryReadGlobalNumber("_rm_gct_start", out var startSec);
        lua.TryReadGlobalNumber("_rm_gct_now", out var luaNowMs);
        StartGameCountdown(startSec, total, luaNowMs, resume);
    }

    // ── phase lock (game countdown on our card) ───────────────────────────────────────────────────────────────────
    // ⚠️ Deliberate exception to "≤10 Hz" (Plugin-Performance-Rules §1): while a locked game countdown runs (≤ ~11 s,
    // bounded by GO! / the game's stop / the 1.5 s fallback) we read ONE typed Lua number (_rm_gct_tick) per frame, so
    // our number flips on the very frame the game's timer ticks (= its beep). A 10 Hz poll would put it up to 100 ms
    // behind. Outside that window only the ≤10 Hz seq poll runs. No allocation: constant key, typed double read.
    private const float GctTickTimeout = 1.5f;   // no tick for this long → decay locally so a missed event can't freeze
    private bool _gctLocked;
    private double _gctTick;
    private double _gctCount;
    private float _gctTickAge;

    // OnUpdate, only while _running && _ctFromGame && _gctLocked. Sets _remaining; ≤0 lets OnUpdate finish → GO!
    // on the same frame the game plays its "0" sound.
    private void AdvanceLockedCountdown(float dt)
    {
        var lua = _services.Lua;
        if (lua.Ready && lua.TryReadGlobalNumber("_rm_gct_tick", out var tick) && tick != _gctTick
            && lua.TryReadGlobalNumber("_rm_gct_count", out var count))
        {
            _gctTick = tick;
            _gctCount = count;
            _gctTickAge = 0f;
            _remaining = count;   // ceil(count) = count → shown number changes exactly now, together with the beep
            return;
        }
        _gctTickAge += dt;
        _remaining -= dt;   // the bar keeps draining between ticks…
        // …but the shown second stays the game's count until its next tick (ceil(count − 1 + ε) = count). After the
        // timeout we let it run free (a missed / unregistered listener); a later tick re-syncs it.
        if (_gctTickAge < GctTickTimeout) _remaining = Math.Max(_remaining, _gctCount - 1d + 1e-3);
    }

    // FALLBACK only (no _rm_gct_count — the listener/wrapper didn't record one). remaining = startSec + total − now.
    // Preferred clock: the framework's interpolated server clock (CombatSnapshot.ServerNowMs); before it has synced
    // (0), or if it disagrees with the game's own clock by > 3 s (a domain mismatch we haven't validated), fall back to
    // the game's clock at the notify + a local tick.
    private void StartGameCountdown(double startSec, double total, double luaNowMs, bool resume)
    {
        long endMs = (long)((startSec + total) * 1000d);
        double viaLua = luaNowMs > 0d ? total - (luaNowMs / 1000d - startSec) : total;
        long now = _services.CombatSnapshot.ServerNowMs;
        double viaServer = now > 0 ? (endMs - now) / 1000d : double.NaN;
        bool useServer = !double.IsNaN(viaServer) && (resume || Math.Abs(viaServer - viaLua) <= 3d);
        double remaining = useServer ? viaServer : viaLua;
        if (remaining <= 0d || remaining > total + 1d) return;   // late / stale notify: the game shows no view either
        BeginCountdown(remaining, total, fromGame: true, endServerMs: useServer ? endMs : 0);
    }

    // Settings row "Replace game countdown". OFF: the game's view comes straight back (Apply re-runs the original refresh
    // on the live component, which shows a countdown still in progress) and our card drops a game countdown.
    private void SetReplaceGame(bool on)
    {
        _ctReplaceGame = on;
        SaveCtBool("ct_replace_game", on);
        if (!on && _ctFromGame && (_running || _goTimer > 0d)) StopCountdown();
        if (!EnsureGctLua()) return;   // not in world yet: the first world tick defines + installs
        var lua = _services.Lua;
        if (on) lua.DoString(GctInstallCall);
        lua.DoString(on ? GctApplyOn : GctApplyOff);
        if (on) PollGameCountdown(resume: true);   // a game countdown already running moves onto our card
    }

    // "Countdown sounds" — /ct and Test only (the game plays its own for its countdown). Main thread (OnUpdate).
    private void PlayCountdownSound(bool zero)
    {
        if (!_ctSounds || !EnsureGctLua()) return;
        _services.Lua.DoString(zero ? CtSoundZero : CtSoundTick);
    }

    private void SetCtSounds(bool on)
    {
        _ctSounds = on;
        SaveCtBool("ct_sounds", on);
    }

    // Plugin unload: give the game its own countdown view back (the wrappers stay, they're pass-through).
    private void DisposeGameCountdown()
    {
        try { if (_services.Lua.Ready) _services.Lua.DoString(GctApplyOff); } catch { }
    }
}
