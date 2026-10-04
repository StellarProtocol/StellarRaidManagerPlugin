using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — share code (export / import) ──────────────────────────────────────────────────────
//
// Players share a preset by copying a short "RMP1.…" code and pasting it on another machine. The wire format and its
// strict validation live in MarkPresetCode.cs (pure, no Unity); this partial maps the plugin's private
// MarkPreset/MarkStep/MarkPos to the codec DTOs, owns the export box state, and talks to the clipboard. The two UI
// pieces (Import button under the create row, Export button + code box in the step panel) are built here and slotted
// into BuildMarksRoot (Plugin.Marks.Ui.cs) so that file stays small.
//
// Export is ON DEMAND and the code is a snapshot: anything that changes what it would contain (switching / deleting /
// renaming the preset, saving or deleting a step, editing a comment) calls ClearExportCode(), so the box never shows
// a stale code. The UI additionally gates the box on _exportPresetIdx == _activeIndex as a belt-and-braces check.
//
// Clipboard: UnityEngine.GUIUtility.systemCopyBuffer — the IL2CPP-safe clipboard path already used by CombatMeter's
// CopyUploadLink. Both directions are wrapped in try/catch; a clipboard failure only sets a status line.
public sealed partial class Plugin
{
    private string _exportCode = "";
    private int _exportPresetIdx = -1;

    private bool HasExportCode() => _exportCode.Length > 0 && _exportPresetIdx == _activeIndex && _activeIndex >= 0;

    private void ClearExportCode()
    {
        if (_exportCode.Length == 0) return;
        _exportCode = "";
        _exportPresetIdx = -1;
        _marksWindow?.MarkDirty();
    }

    private void ExportActivePreset()
    {
        var p = ActivePreset();
        if (p == null) return;

        if (!MarkPresetCode.TryEncode(ToCodePreset(p), out string code, out var err))
        {
            ClearExportCode();
            _marksStatus = _loc.T("rm.marks.exportFailed");
            _marksWindow?.MarkDirty();
            _services.Log.Warning($"[MarkPresets] export of '{p.Name}' failed: {err}");
            return;
        }

        _exportCode = code;
        _exportPresetIdx = _activeIndex;
        _marksStatus = _loc.TFormat("rm.marks.exported", p.Name);
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] exported '{p.Name}' ({p.Steps.Count - 1} steps, {code.Length} chars)");
    }

    private void CopyExportCode()
    {
        if (!HasExportCode()) return;
        try
        {
            UnityEngine.GUIUtility.systemCopyBuffer = _exportCode;   // plugins may reference UnityEngine; IL2CPP-safe clipboard
            _marksStatus = _loc.T("rm.marks.copied");
            ShowMarksNotice(_loc.T("rm.marks.copied"));
        }
        catch (Exception ex)
        {
            _marksStatus = _loc.T("rm.marks.clipboardFailed");
            _services.Log.Warning($"[MarkPresets] clipboard write failed: {ex.Message}");
        }
        _marksWindow?.MarkDirty();
    }

    private void ImportFromClipboard()
    {
        string text;
        try { text = UnityEngine.GUIUtility.systemCopyBuffer ?? ""; }
        catch (Exception ex)
        {
            _marksStatus = _loc.T("rm.marks.clipboardFailed");
            _marksWindow?.MarkDirty();
            _services.Log.Warning($"[MarkPresets] clipboard read failed: {ex.Message}");
            return;
        }

        // Invalid code → status only; the preset list is untouched.
        if (!MarkPresetCode.TryDecode(text, out var dto, out var err))
        {
            _marksStatus = _loc.T("rm.marks.importInvalid");
            _marksWindow?.MarkDirty();
            _services.Log.Info($"[MarkPresets] import rejected: {err}");
            return;
        }
        if (_presets.Count >= MaxPresets)
        {
            _marksStatus = _loc.TFormat("rm.marks.limit", MaxPresets);
            _marksWindow?.MarkDirty();
            return;
        }

        // Same case-insensitive uniqueness rule as CreatePreset/CommitRename, but resolved instead of refused:
        // importing your own export (or a friend's same-named preset) yields "Name (2)", "Name (3)", …
        string name = dto.Name.Trim();
        if (NameTaken(name))
        {
            int n = 2;
            while (NameTaken($"{name} ({n})")) n++;
            name = $"{name} ({n})";
        }

        // Re-create the permanent blank Start anchor at Steps[0]; the code carries only the real steps (1..N).
        var preset = new MarkPreset { Name = name, Steps = { new MarkStep() } };
        foreach (var st in dto.Steps)
        {
            var step = new MarkStep { Comment = st.Comment ?? "" };
            foreach (var m in st.Marks) step.Marks.Add(new MarkPos { Slot = m.Slot, X = m.X, Y = m.Y, Z = m.Z });
            preset.Steps.Add(step);
        }

        _presets.Add(preset);   // appended, NOT activated — the user picks it like any other preset
        _editingIdx = -1;       // list changed — drop any in-progress rename (it is keyed by index)
        CancelStepCommentEdit();
        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.imported", name, dto.Steps.Count);
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] imported '{name}' ({dto.Steps.Count} steps)");
    }

    private bool NameTaken(string name)
        => _presets.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // Steps[0] is the blank Start anchor — never exported; the importer re-creates it.
    private static MarkCodePreset ToCodePreset(MarkPreset p)
    {
        var dto = new MarkCodePreset { Name = p.Name ?? "" };
        for (int i = 1; i < p.Steps.Count; i++)
        {
            var st = p.Steps[i];
            var s = new MarkCodeStep { Comment = st.Comment ?? "" };
            foreach (var m in st.Marks) s.Marks.Add(new MarkCodeMark(m.Slot, m.X, m.Y, m.Z));
            dto.Steps.Add(s);
        }
        return dto;
    }

    // ── UI pieces (slotted into BuildMarksRoot) ──────────────────────────────────────────────────────────────────
    // Full-width button under the create row.
    private HudElement BuildMarksImportButton()
        => new ButtonElement(() => _loc.T("rm.marks.import"), OnClick: ImportFromClipboard);

    // Own row in the step panel: Save Step / Delete Step already fill the 440 window in the longer locales
    // (e.g. fil "Tanggalin ang Hakbang"), so a third button there would squeeze/clip. The code box below only shows
    // once a code has been generated for the CURRENT active preset.
    private HudElement BuildMarksExportPanel()
        => new ColumnElement(new HudElement[]
        {
            new ButtonElement(() => _loc.T("rm.marks.export"), OnClick: ExportActivePreset),
            new ConditionalElement(HasExportCode,
                new ColumnElement(new HudElement[]
                {
                    // Preview only — the full code can be a few hundred chars; Copy puts the whole thing on the clipboard.
                    new TextElement(() => ExportPreview(),
                        Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                    new RowElement(new HudElement[]
                    {
                        new CellElement(new TextElement(
                            () => _loc.TFormat("rm.marks.codeLength", _exportCode.Length),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Weight: 1f),
                        new CellElement(new ButtonElement(() => _loc.T("rm.marks.copy"), OnClick: CopyExportCode),
                            Width: 90f),
                    }, Gap: 4f),
                }, Gap: 4f)),
        }, Gap: 6f);

    private string ExportPreview()
        => _exportCode.Length > 32 ? _exportCode.Substring(0, 32) + "…" : _exportCode;
}
