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
            ParseRaidWarning(msg.Text.Trim());
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

    private void ParseRaidWarning(string text)
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
        _rwVisible = true;
        _rwTimer   = 5.0;
        _hud.MarkDirty();
    }

    private string FormatRemaining()
    {
        double r = Math.Max(0d, _remaining);
        int secs   = (int)r;
        int centis = (int)((r - secs) * 100);
        return $"{secs}.{centis:D2}";
    }

    private void StartCountdown(int seconds)
    {
        _remaining = seconds;
        _running   = true;
        _hud.MarkDirty();
    }

    private void StopCountdown()
    {
        _running = false;
        _hud.MarkDirty();
    }

    private void OnUpdate(float deltaTime)
    {
        if (_running)
        {
            _remaining -= deltaTime;
            if (_remaining <= 0d) StopCountdown();
            else _hud.MarkDirty();
        }
        if (_rwVisible)
        {
            _rwTimer -= deltaTime;
            if (_rwTimer <= 0d) { _rwVisible = false; _hud.MarkDirty(); }
            else _hud.MarkDirty();
        }
    }
}
