using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Skill-cast sampling for the cast-driven rules. Upstream's "skill cast" is NOT a hook: it is the caster's
// AttrSkillId (EAttrType 100) changing to a non-zero skill id (decoder.rs ATTR_SKILL_ID → SkillLifecycleChanged
// Observed). In-process we read the same state by POLLING the scene's relevant monsters every 100 ms tick:
//   primary  — ZStateSkillComp.curSkillId_ + curSkillUuid_ (dump.cs 230454; per-cast uuid, so the SAME skill cast
//              twice in a row is still two casts), via ZEntity.GetComponent<ZStateSkillComp>() (the generic
//              resolution TargetLens CastPatch uses);
//   fallback — AttrSkillId via GetAttr (then a back-to-back repeat of the same skill id is indistinguishable).
// No Harmony patch: SetSingGuide (Boss-Cast-Bar-Tracking.md) only fires for channelled skills, and the
// ZStateSkillComp begin hooks are AOT-inlined. A cast shorter than one tick between two samples can be missed.
internal sealed partial class MechanicCalloutTracker
{
    // Caster position / facing are captured AT CAST TIME (upstream MinimapSkillCast x, z, facing) — the Cursed Tomb
    // charge half-plane is anchored there. Only filled while positions are read (minimap on / reef / raid).
    private readonly struct McCast
    {
        public readonly long  Caster, Tick;
        public readonly int   SkillId;
        public readonly bool  HasPos;
        public readonly float X, Z, Facing;
        public readonly int   CastUuid;        // ZStateSkillComp.curSkillUuid_ (0 = unavailable → AttrSkillId fallback)
        public readonly string FacingSrc;      // "rot" = caster model rotation read at detection; "rot-scan"/"attr" = scan; "" = none
        public McCast(long caster, int skillId, long tick, bool hasPos = false, float x = 0f, float z = 0f,
                      float facing = float.NaN, int castUuid = 0, string facingSrc = "")
        {
            Caster = caster; SkillId = skillId; Tick = tick; HasPos = hasPos; X = x; Z = z; Facing = facing;
            CastUuid = castUuid; FacingSrc = facingSrc;
        }
    }

    private const int MaxCastLog = 64;   // upstream MAX_SKILL_CAST_LOG
    private readonly List<McCast> _casts = new();
    private readonly Dictionary<long, (int Id, int Uuid)> _castState = new();

    private void SampleCasts()
    {
        long now = Environment.TickCount64;
        foreach (var e in _ents.Values)
        {
            if (e.IsPlayer) continue;
            var obj = GetEntity(e.Uuid);
            if (obj == null) continue;
            var (id, uuid) = ReadCurrentSkill(obj);
            _castState.TryGetValue(e.Uuid, out var last);
            _castState[e.Uuid] = (id, uuid);
            if (id <= 0 || (id == last.Id && uuid == last.Uuid)) continue;
            // Capture position + facing AT DETECTION from the caster's live model (fresh, not the ≤200 ms-old scan
            // snapshot, and never a default AttrDir when the rotation is readable).
            bool hasPos = e.HasPos; float x = e.Pos.x, z = e.Pos.z, facing = e.Facing;
            string src = float.IsNaN(e.Facing) ? "" : e.FacingFromRot ? "rot-scan" : "attr";
            if (e.GoComp != null)
            {
                if (TryGoPos(e.GoComp, out var lp) && !(lp.x == 0f && lp.z == 0f)) { hasPos = true; x = lp.x; z = lp.z; }
                if (TryGoYaw(e.GoComp, out float yaw)) { facing = yaw; src = "rot"; }
            }
            _casts.Add(new McCast(e.Uuid, id, now, hasPos, x, z, facing, uuid, src));
            if (_casts.Count > MaxCastLog) _casts.RemoveAt(0);
        }
    }

    // Casts of `skillId` (any caster, or `caster` when non-zero) whose age is within [-500 ms, maxAgeMs].
    private IEnumerable<McCast> RecentCasts(int skillId, long maxAgeMs)
    {
        long now = Environment.TickCount64;
        foreach (var c in _casts)
        {
            long age = now - c.Tick;
            if (c.SkillId == skillId && age >= -500 && age <= maxAgeMs) yield return c;
        }
    }

    // ── Reading the current skill off an entity ──────────────────────────────────────────────────────────────
    private bool          _skillCompResolved;
    private MethodInfo?   _miGetSkillComp;              // ZEntity.GetComponent<ZStateSkillComp>()
    private PropertyInfo? _piCurSkillId, _piCurSkillUuid;
    private bool          _skillCompFailed;

    private (int Id, int Uuid) ReadCurrentSkill(object ent)
    {
        if (!_skillCompResolved) ResolveSkillComp(ent);
        if (_miGetSkillComp != null && !_skillCompFailed)
        {
            try
            {
                var comp = _miGetSkillComp.Invoke(ent, null);
                if (comp != null)
                {
                    if (_piCurSkillId == null)
                    {
                        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                        _piCurSkillId   = comp.GetType().GetProperty("curSkillId_", any);
                        _piCurSkillUuid = comp.GetType().GetProperty("curSkillUuid_", any);
                        if (_piCurSkillId == null) _skillCompFailed = true;
                    }
                    if (_piCurSkillId != null)
                        return (ReadInt(_piCurSkillId, comp), _piCurSkillUuid != null ? ReadInt(_piCurSkillUuid, comp) : 0);
                }
            }
            catch { _skillCompFailed = true; }   // → AttrSkillId fallback from now on
        }
        return ((int)ReadAttrLong(ent, _boxAttrSkillId), 0);
    }

    // GetComponent<T>() lives on the ZEntity base — walk up for the open generic def, then close it over
    // ZStateSkillComp (FlattenHierarchy doesn't surface generic method defs reliably) — TargetLens CastPatch.
    private void ResolveSkillComp(object ent)
    {
        _skillCompResolved = true;
        try
        {
            var skComp = StellarInterop.FindType("Panda.ZGame.ZStateSkillComp");
            MethodInfo? gen = null;
            for (var cur = ent.GetType(); cur != null && gen == null; cur = cur.BaseType)
                foreach (var mi in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (mi.Name == "GetComponent" && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 0)
                    { gen = mi; break; }
            if (skComp != null && gen != null) _miGetSkillComp = gen.MakeGenericMethod(skComp);
        }
        catch { }
    }
}
