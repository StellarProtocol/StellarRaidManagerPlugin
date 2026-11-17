using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

public sealed partial class Plugin
{
    private void OnMessage(ChatMessage msg)
    {
        if (msg.IsHistory) return;

        if (_ctEnabled && IsCtChannel(msg.Channel))
            ParseCountdown(msg.Text.Trim());

        if (_rwEnabled && IsRwChannel(msg.Channel))
            ParseRaidWarning(msg.Text.Trim(), msg.SenderName);
    }

    private bool IsCtChannel(ChatChannel ch) => ch switch
    {
        ChatChannel.Party => _ctParty,
        ChatChannel.Guild => _ctGuild,
        ChatChannel.Say   => _ctLocal,
        _                 => false,
    };

    private bool IsRwChannel(ChatChannel ch) => ch switch
    {
        ChatChannel.Party => _rwParty,
        ChatChannel.Guild => _rwGuild,
        ChatChannel.Say   => _rwLocal,
        _                 => false,
    };

    private void ParseCountdown(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower is "/countdown stop" or "/ct stop" or
                     "/countdown cancel" or "/ct cancel" or
                     "/countdown abort" or "/ct abort")
        { StopCountdown(); return; }

        string? arg = null;
        if (lower.StartsWith("/countdown ")) arg = text["/countdown ".Length..].Trim();
        else if (lower.StartsWith("/ct "))    arg = text["/ct ".Length..].Trim();

        if (arg is not null && int.TryParse(arg, out int seconds) && seconds >= 1 && seconds <= 30)
            StartCountdown(seconds);
    }

    private void ParseRaidWarning(string text, string? sender)
    {
        if (!text.StartsWith("/rw ", StringComparison.OrdinalIgnoreCase)) return;
        var msg = text["/rw ".Length..].Trim();
        if (msg.Length == 0) return;

        if (_rwUseIngameWarning)
        {
            _services.NoticeTips
                .Create(NoticeTipType.Special)
                .WithContent(msg)
                .WithAudio(NoticeTipAudio.DungeonVictory)
                .WithDuration(5f)
                .Show();
            return;
        }

        _rwText    = msg;
        _rwSender  = sender ?? string.Empty;   // caption "RAID WARNING · {0}" (Plugin.Hud.Card.cs)
        _rwVisible = true;
        _rwTimer   = 5.0;
        _hud.MarkDirty();
    }

    // ── countdown state machine ───────────────────────────────────────────────────────────────────────────────────
    // running (number + draining bar) → at 0: "GO!" for 1 s → hidden. "/ct stop" (or the game's stop for a game
    // countdown) hides at once, no GO!. Whichever countdown started LAST owns the card (/ct, Test, or the game's).

    // Whole seconds rounded UP: 12.3 s left → 12; anything above 0 shows at least 1.
    private int ShownSec() => (int)Math.Clamp(Math.Ceiling(_remaining), 0d, 99d);

    private void StartCountdown(int seconds) => BeginCountdown(seconds, seconds, fromGame: false, endServerMs: 0);

    // Called from the chat handler too, so NO Lua here: sounds fire from OnUpdate (main thread) when the shown second
    // first changes (_lastShownSec = -1 → the opening second counts, so "/ct 5" ticks at once).
    private void BeginCountdown(double remaining, double total, bool fromGame, long endServerMs)
    {
        _remaining     = remaining;
        _total         = Math.Max(1d, total);
        _ctFromGame    = fromGame;
        _ctEndServerMs = endServerMs;
        _gctLocked     = false;   // the game path re-sets it right after (Plugin.GameCountdown.cs phase lock)
        _goTimer       = 0d;
        _lastShownSec  = -1;
        _running       = true;
        _hud.MarkDirty();
    }

    private void StopCountdown()
    {
        _running = false;
        _goTimer = 0d;
        _hud.MarkDirty();
    }

    private void FinishCountdown()
    {
        _running   = false;
        _remaining = 0d;
        _goTimer   = 1.0;
        if (!_ctFromGame) PlayCountdownSound(zero: true);   // the game plays its own "0" for its countdown
        _hud.MarkDirty();
    }

    private float _hudRedrawAcc;
    private const float HudRedrawEvery = 0.1f;   // bar drain / raid-warning fade: ≤10 Hz (Plugin-Performance-Rules §7)

    private void OnUpdate(float deltaTime)
    {
        // Punctuate Mark Presets: drain the load queue (one mark/frame) + poll the live placed-count.
        TickMarks(deltaTime);
        TickHelp();   // help popover: first-open re-anchor + close with its window (Plugin.Help.cs)
        TickGameCountdown(deltaTime);   // game leader countdown: install hooks + poll ≤10 Hz (Plugin.GameCountdown.cs)

        bool redraw = false;
        if (_running)
        {
            // A game countdown is phase-locked to the game's own 1 s timer (its beeps) — Plugin.GameCountdown.cs.
            // Fallback when no count was recorded: the framework server clock (never ZServerTime —
            // Server-Clock-and-Ping-HUD.md). /ct and Test count down locally; their sound and number flip on the same
            // frame below, so they need no lock.
            if (_ctFromGame && _gctLocked) AdvanceLockedCountdown(deltaTime);
            else
            {
                long now = _ctEndServerMs > 0 ? _services.CombatSnapshot.ServerNowMs : 0;
                _remaining = now > 0 ? (_ctEndServerMs - now) / 1000d : _remaining - deltaTime;
            }
            if (_remaining <= 0d) FinishCountdown();
            else
            {
                int sec = ShownSec();
                if (sec != _lastShownSec)
                {
                    _lastShownSec = sec;
                    if (sec <= 5 && !_ctFromGame) PlayCountdownSound(zero: false);
                    _hud.MarkDirty();   // a new number / colour step lands on this frame, not the next 10 Hz beat
                }
                else redraw = true;
            }
        }
        else if (_goTimer > 0d)
        {
            _goTimer -= deltaTime;
            if (_goTimer <= 0d) _hud.MarkDirty();
        }
        if (_rwVisible)
        {
            _rwTimer -= deltaTime;
            if (_rwTimer <= 0d) { _rwVisible = false; _hud.MarkDirty(); }
            else redraw = true;
        }
        if (redraw && (_hudRedrawAcc += deltaTime) >= HudRedrawEvery) { _hudRedrawAcc = 0f; _hud.MarkDirty(); }
    }
}
