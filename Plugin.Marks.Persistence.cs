using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — persistence (split out of Plugin.Marks.cs to keep it under the 500-line cap; logic
// unchanged). Presets JSON + the active preset's name, both in the 'marks' config section.
public sealed partial class Plugin
{
    private void LoadPresetsFromConfig()
    {
        _presets.Clear();
        _activeIndex = -1;
        _currentStep = -1;
        try
        {
            string json = _marksCfg.Get<string>("presets", "") ?? "";
            if (json.Length > 0)
            {
                var list = JsonSerializer.Deserialize<List<MarkPreset>>(json);
                if (list != null) _presets.AddRange(list);
            }

            // Migration: every preset must own a blank Step 0 anchor. Old presets (saved before this feature) have
            // real layouts starting at Steps[0] — prepend an empty step so their first layout isn't mistaken for it.
            foreach (var p in _presets)
                if (p.Steps.Count == 0 || p.Steps[0].Marks.Count > 0)
                    p.Steps.Insert(0, new MarkStep());

            // A step with an explicit JSON null comment would otherwise carry null past the initializer.
            foreach (var p in _presets)
                foreach (var st in p.Steps)
                    st.Comment ??= "";

            // Resolve the persisted active name back to an index; the cursor lands on Step 0 (the blank anchor).
            string active = _marksCfg.Get<string>("active", "") ?? "";
            if (active.Length > 0)
                _activeIndex = _presets.FindIndex(p => string.Equals(p.Name, active, StringComparison.OrdinalIgnoreCase));
            _currentStep = _activeIndex >= 0 ? 0 : -1;
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] preset load failed: {ex.Message}"); }
    }

    private void SavePresetsToConfig()
    {
        try
        {
            _marksCfg.Set<string>("presets", JsonSerializer.Serialize(_presets));
            _marksCfg.Set<string>("active", ActivePreset()?.Name ?? "");
            _marksCfg.Save();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] preset save failed: {ex.Message}"); }
    }
}
