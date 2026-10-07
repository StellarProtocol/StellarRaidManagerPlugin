using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// "Hit Offsets" window (opened from the Mechanic Callouts window): how long BEFORE its debuff expires each MECHANIC
// actually lands — the shown countdown is remain − offset (Mechanics/MechanicCalloutTracker.HitOffset.cs). Listed per
// scene → group → one line per mechanic: buff mechanics from the scene table (key b<baseId>, label shown), then each
// rule group as one mechanic (key g_<group>). Each line: a 0–4 s slider (step 0.1; 0 = NO offset), the value + its
// built-in default, and a "Reset" button back to the default. No auto-learning (user decision — see HitOffset.cs).
// Keys/groups stay ENGLISH (config keys mechhit_<key>); names are localized at display via McText.T.
public sealed partial class Plugin
{
    private IWindowControl _mechHitWindow = null!;
    private int _hitSceneIdx;
    private const int HitSlots = 32;
    private const float HitWinW = 880f;                    // label 300 (wraps) + slider (rest, ~240) + status 190 + reset 70 + gaps + scrollbar
    private const float HitListH = 700f;                   // scroll viewport height for the mechanic list

    private sealed record HitMech(string Key, string Group, string Label);

    // Rule-produced groups per scene (the static table's groups are added from MechanicCalloutData).
    private static readonly Dictionary<SceneKind, string[]> HitRuleGroups = new()
    {
        [SceneKind.Raid]           = new[] { "Phase Mapping — Danger", "Divine Scale - Preset Return", "Pinball", "Electromagnetic Ring Sequence" },
        [SceneKind.CursedTomb]     = new[] { "Blue Tower", "Charge Clone" },
        [SceneKind.GiantTower]     = new[] { "Portal", "Gravity Blast" },
        [SceneKind.Tina]           = new[] { "Tina · Mechanic", "Pizza Danger Zone" },
        [SceneKind.SeaReef]        = new[] { "Nabo Matrix Callout", "Ice/Sea Wave Safe Zone", "Pizza Danger Zone" },
        [SceneKind.WastelandCourt] = new[] { "Void-Mark Swap", "Swap Orbs", "Shadow of Heluga", "Near/Far Chain" },
    };

    // Per scene (one entry per SceneKind, stable order): table mechanics in table order, then rule groups. LAZY: it
    // reads MechanicCalloutData.Scenes and HitRuleGroups, static fields whose initialisation order relative to this one
    // is undefined across partial files / classes (Mechanic-Callouts.md gotcha) — never a static initialiser.
    private static (string Name, HitMech[] Mechs)[]? _hitScenes;
    private static (string Name, HitMech[] Mechs)[] HitScenes => _hitScenes ??= Scenes.Values
        .GroupBy(d => d.Kind).Select(g => g.First())
        .Select(def =>
        {
            var list = def.Buffs.OrderBy(kv => kv.Value.Order)
                          .Select(kv => new HitMech("b" + kv.Key, kv.Value.Group, kv.Value.Label)).ToList();
            var tableGroups = new HashSet<string>(list.Select(m => m.Group));
            foreach (var g in HitRuleGroups.TryGetValue(def.Kind, out var rg) ? rg : Array.Empty<string>())
                if (!tableGroups.Contains(g)) list.Add(new HitMech(MechanicCalloutTracker.GroupHitKey(g), g, ""));
            return (def.Name, list.ToArray());
        }).ToArray();

    private void InitMechanicHitOffsets()
    {
        _mechHitWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "raidmanager.mech.hitoffsets",
                Title: _loc.T("rm.mech.hit.title"),
                DefaultRect: new WindowRect(840f, 160f, HitWinW, 0f),
                Category: WindowCategory.Tools,
                Style: WindowPanelStyle.GlassMenu)
            {
                Draggable = true, Closable = true, StartVisible = false,
                ShouldRender = () => MechInWorld,
            },
            Root: BuildHitOffsetsRoot(),
            OnClose: () => _mechHitWindow.SetVisible(false)));
        _mechWindows.Add(_mechHitWindow);
    }

    private string HitSceneName => McText.T(HitScenes[_hitSceneIdx % HitScenes.Length].Name);

    // The current scene's mechanics minus those that never show a countdown (offset meaningless). Cached ~4×/s.
    private HitMech[] _hitVisible = Array.Empty<HitMech>();
    private long _hitVisibleAt = -1; private int _hitVisibleScene = -1;
    private HitMech[] HitVisible()
    {
        long bucket = Environment.TickCount64 / 250;
        if (bucket != _hitVisibleAt || _hitSceneIdx != _hitVisibleScene)
        {
            _hitVisibleAt = bucket; _hitVisibleScene = _hitSceneIdx;
            _hitVisible = HitScenes[_hitSceneIdx % HitScenes.Length].Mechs
                .Where(m => _mechTracker.HitMechanicTimed(m.Key, m.Group)).ToArray();
        }
        return _hitVisible;
    }
    private HitMech? HitMechAt(int i) { var m = HitVisible(); return i >= 0 && i < m.Length ? m[i] : null; }

    // Row label: the mechanic's localized name, or "(rule)" for a rule group (the group heading names it).
    private string HitLabel(HitMech m) => m.Label.Length > 0 ? McText.T(m.Label) : _loc.T("rm.mech.hit.rule");

    private HudElement BuildHitOffsetsRoot()
    {
        var items = new List<HudElement>
        {
            new TextElement(() => _loc.T("rm.mech.hit.title"), Emphasis: true),
            new TextElement(() => _loc.T("rm.mech.hit.help"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new ButtonElement(() => _loc.TFormat("rm.mech.hit.scene", HitSceneName),
                OnClick: () => _hitSceneIdx = (_hitSceneIdx + 1) % HitScenes.Length, Width: HitWinW - 40f),
        };
        // The mechanic list scrolls inside a fixed-height viewport (framework ScrollElement) — the raid alone has ~30.
        var rows = new HudElement[HitSlots];
        for (int s = 0; s < HitSlots; s++) rows[s] = BuildHitRow(s);
        items.Add(new ScrollElement(new ColumnElement(rows, Gap: 4f), HitListH));
        items.Add(new TextElement(() => _loc.T("rm.mech.hit.footer"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted));
        return new ColumnElement(items.ToArray(), Gap: 4f);
    }

    // Group heading shown on the first mechanic of each group.
    private bool HitGroupStart(int i) => HitMechAt(i) is { } m && (i == 0 || HitMechAt(i - 1)?.Group != m.Group);

    private string HitStatus(HitMech m)
    {
        float v = _mechTracker.HitOffset(m.Key), d = MechanicCalloutTracker.DefaultHitOffset(m.Key);
        string now = v > 0f ? _loc.TFormat("rm.mech.hit.seconds", v.ToString("0.0")) : _loc.T("rm.mech.hit.none");
        return _mechTracker.HitOffsetIsDefault(m.Key)
            ? _loc.TFormat("rm.mech.hit.isDefault", now)
            : _loc.TFormat("rm.mech.hit.default", now, d.ToString("0.0"));
    }

    private HudElement BuildHitRow(int i) => new ConditionalElement(() => HitMechAt(i) != null,
        new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => HitGroupStart(i),
                new TextElement(() => HitMechAt(i) is { } m ? McText.T(m.Group) : "",
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted, NoWrap: true)),
            new RowElement(new HudElement[]
            {
                // Fixed 300 px label cell; long names WRAP inside it (never into the slider/status columns).
                new CellElement(new TextElement(() => HitMechAt(i) is { } m ? HitLabel(m) : "", Width: 300f), Width: 300f),
                new CellElement(new SliderElement(
                    Get: () => HitMechAt(i) is { } m ? _mechTracker.HitOffset(m.Key) : 0f,
                    Set: v => { if (HitMechAt(i) is { } m) _mechTracker.SetManualHitOffset(m.Key, MathF.Round(Math.Clamp(v, 0f, 4f) * 10f) / 10f); },
                    Min: 0f, Max: 4f), Weight: 1f),          // takes the remaining width (MechSliderRow pattern)
                new CellElement(new TextElement(() => HitMechAt(i) is { } m ? HitStatus(m) : "", NoWrap: true), Width: 190f),
                new CellElement(new ButtonElement(() => _loc.T("rm.mech.hit.reset"),
                    OnClick: () => { if (HitMechAt(i) is { } m) _mechTracker.ResetHitOffset(m.Key); }, Width: 70f), Width: 70f),
            }, Gap: 10f),
        }, Gap: 1f));
}
