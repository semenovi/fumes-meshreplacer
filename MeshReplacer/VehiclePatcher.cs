using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Nothke.Utils;
using UnityEngine;

static class VehiclePatcher
{
    public static void Apply(Transform vehicleRoot, CustomVehicleDef def)
    {
        PatchVehicleBody(vehicleRoot, def.VehicleBody);
        PatchAntenna(vehicleRoot, def.AntennaPosition);
    }

    static void PatchVehicleBody(Transform vehicleRoot, VehicleBodyConfig? cfg)
    {
        if (cfg == null) return;

        var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
        if (vb == null) return;

        if (cfg.GrillPosition          != null) Set(() => vb.grillPosition         = PP(cfg.GrillPosition),          "grillPosition");
        if (cfg.InteriorCameraPosition != null) Set(() => vb.interiorCameraPosition = PP(cfg.InteriorCameraPosition), "interiorCameraPosition");
        if (cfg.EnginePosition         != null) Set(() => vb.enginePosition         = PP(cfg.EnginePosition),         "enginePosition");
        if (cfg.FrontLampsColor        != null) Set(() => vb.frontLampsColor        = Col(cfg.FrontLampsColor),       "frontLampsColor");

        if (cfg.CargoStrapsHooks != null)
            Set(() =>
            {
                var arr = new PositionPivot[cfg.CargoStrapsHooks.Length];
                for (int i = 0; i < arr.Length; i++) arr[i] = PP(cfg.CargoStrapsHooks[i]);
                vb.cargoStrapsHooks = arr;
            }, "cargoStrapsHooks");

        if (cfg.Lamps != null)
            Set(() =>
            {
                var lamps = vb.lamps;
                if (lamps == null) return;

                int existingCount = lamps.Length;
                int targetCount   = cfg.Lamps.Length;

                Game.VehicleLamp[] workLamps;
                if (targetCount > existingCount)
                {
                    // Extend the lamps array with new il2cpp-allocated VehicleLamp instances
                    workLamps = new Game.VehicleLamp[targetCount];
                    for (int i = 0; i < existingCount; i++) workLamps[i] = lamps[i];

                    IntPtr lampClass = IL2CPP.il2cpp_object_get_class(lamps[0].Pointer);
                    for (int i = existingCount; i < targetCount; i++)
                    {
                        IntPtr ptr = IL2CPP.il2cpp_object_new(lampClass);
                        if (ptr == IntPtr.Zero)
                        {
                            Plugin.L.LogWarning($"[MB]   lamps[{i}] il2cpp_object_new failed");
                            workLamps[i] = lamps[0];
                            continue;
                        }
                        // Write isFront (bool, offset 0x10) and bulbPosition.position
                        // (Vector3, offset 0x20) directly — property setters don't work
                        // on il2cpp_object_new-allocated objects before GC registration.
                        Marshal.WriteByte(IntPtr.Add(ptr, 0x10),
                            cfg.Lamps[i].Front ? (byte)1 : (byte)0);
                        float[] b = cfg.Lamps[i].Bulb;
                        Marshal.Copy(new float[] { b[0], b[1], b[2] },
                            0, IntPtr.Add(ptr, 0x20), 3);
                        workLamps[i] = new Game.VehicleLamp(ptr);
                        Plugin.L.LogInfo($"[MB]   Created VehicleLamp[{i}] ptr=0x{ptr:X} isFront={cfg.Lamps[i].Front} bulb=({b[0]},{b[1]},{b[2]})");
                    }
                }
                else
                {
                    workLamps = new Game.VehicleLamp[existingCount];
                    for (int i = 0; i < existingCount; i++) workLamps[i] = lamps[i];
                }

                // Patch existing lamps via property setters (known to work)
                int n = Math.Min(Math.Min(workLamps.Length, cfg.Lamps.Length), existingCount);
                for (int i = 0; i < n; i++)
                {
                    var l = workLamps[i];
                    if (l == null) continue;
                    l.isFront      = cfg.Lamps[i].Front;
                    l.bulbPosition = PP(cfg.Lamps[i].Bulb);
                    workLamps[i]   = l;
                }
                vb.lamps = workLamps;

                // Readback: verify bulbPosition was written
                var rb = vb.lamps;
                if (rb != null)
                    for (int i = 0; i < Math.Min(rb.Length, cfg.Lamps.Length); i++)
                    {
                        var p = rb[i]?.bulbPosition.position ?? Vector3.zero;
                        var e = cfg.Lamps[i].Bulb;
                        Plugin.L.LogInfo($"[MB]   lamps[{i}] bulb readback=({p.x:F3},{p.y:F3},{p.z:F3}) expected=({e[0]},{e[1]},{e[2]})");
                    }
            }, "lamps");

        if (cfg.FrontLampsLightPosition != null)
            Set(() =>
            {
                var light = vb.frontLampsLight;
                if (light != null) light.transform.localPosition = V3(cfg.FrontLampsLightPosition);
            }, "frontLampsLight.position");

        if (cfg.FrontLampsShafts != null)
            Set(() =>
            {
                var shafts = vb.frontLampsShafts;
                if (shafts == null) return;
                int n = Math.Min(shafts.Length, cfg.FrontLampsShafts.Length);
                for (int i = 0; i < n; i++)
                {
                    var s = shafts[i];
                    if (s == null) continue;
                    var sc = cfg.FrontLampsShafts[i];
                    if (sc.Position != null) s.transform.localPosition = V3(sc.Position);
                    if (sc.Scale    != null) s.transform.localScale    = V3(sc.Scale);
                }
            }, "frontLampsShafts");

        DumpCaroModelMaterials(vehicleRoot);

        if (cfg.FrontLampBulbGoName != null)
            Set(() =>
            {
                var go = FindByBodyPartId(vehicleRoot, "VehicleParts/PartFrontLamps")
                      ?? MeshReplacer.FindInHierarchy(vehicleRoot, cfg.FrontLampBulbGoName);
                if (go == null) { Plugin.L.LogWarning($"[MB]   FrontLampBulbGo '{cfg.FrontLampBulbGoName}' not found"); return; }
                Plugin.L.LogInfo($"[MB]   FrontLampBulbGo='{go.name}' pos={go.transform.localPosition} scale={go.transform.localScale}");
                if (cfg.FrontLampBulbPosition != null) go.transform.localPosition = V3(cfg.FrontLampBulbPosition);
                if (cfg.FrontLampBulbScale    != null) go.transform.localScale    = V3(cfg.FrontLampBulbScale);
            }, "frontLampBulb");

        if (cfg.LicensePlatePosition != null || cfg.LicensePlateRotation != null || cfg.LicensePlateScale != null)
            Set(() =>
            {
                var lp = vb.licensePlate;
                if (lp == null) { Plugin.L.LogWarning("[MB]   licensePlate component not found"); return; }
                var t = lp.transform;
                if (cfg.LicensePlatePosition != null) t.localPosition = V3(cfg.LicensePlatePosition);
                if (cfg.LicensePlateRotation != null) t.localRotation = Quaternion.Euler(V3(cfg.LicensePlateRotation));
                if (cfg.LicensePlateScale    != null) t.localScale    = V3(cfg.LicensePlateScale);
                Plugin.L.LogInfo($"[MB]   licensePlate pos={t.localPosition} rot={t.localEulerAngles}");
            }, "licensePlate");

        if (cfg.RoofLampPartGo != null && cfg.RoofLampIndices != null)
            Set(() =>
            {
                var go = MeshReplacer.FindInHierarchy(vehicleRoot, cfg.RoofLampPartGo);
                if (go == null) { Plugin.L.LogWarning($"[MB]   roofLampPart GO '{cfg.RoofLampPartGo}' not found"); return; }

                var roofPart = go.GetComponent<Game.VehicleBodyPart>();
                if (roofPart == null) { Plugin.L.LogWarning($"[MB]   roofLampPart: no VehicleBodyPart on '{cfg.RoofLampPartGo}'"); return; }

                // Diagnostics
                try { Plugin.L.LogInfo($"[MB]   roofPart.ID='{roofPart.ID}' canBeHidden={roofPart.canBeHidden}"); } catch (Exception ex) { Plugin.L.LogWarning($"[MB]   roofPart diag err: {ex.Message}"); }

                // Add to vb.parts if not already present
                var parts = vb.parts;
                Plugin.L.LogInfo($"[MB]   vb.parts={(parts == null ? "null" : parts.Length.ToString())}");
                if (parts != null)
                {
                    bool found = false;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        try { if (parts[i]?.Pointer == roofPart.Pointer) { found = true; break; } } catch { }
                    }
                    if (!found)
                    {
                        var newParts = new Game.VehicleBodyPart[parts.Length + 1];
                        for (int i = 0; i < parts.Length; i++) newParts[i] = parts[i];
                        newParts[parts.Length] = roofPart;
                        vb.parts = newParts;
                        Plugin.L.LogInfo($"[MB]   roofLampPart added to vb.parts (total={newParts.Length})");
                    }
                    else Plugin.L.LogInfo($"[MB]   roofLampPart already in vb.parts");
                }

                // Redirect lamp[i].part references to the roof lamp part
                var lamps = vb.lamps;
                Plugin.L.LogInfo($"[MB]   vb.lamps={(lamps == null ? "null" : lamps.Length.ToString())}");
                if (lamps != null)
                {
                    foreach (int idx in cfg.RoofLampIndices)
                    {
                        if (idx < 0 || idx >= lamps.Length) continue;
                        var l = lamps[idx];
                        var prevPtr = l.part?.Pointer ?? IntPtr.Zero;
                        l.part = roofPart;
                        lamps[idx] = l;
                        Plugin.L.LogInfo($"[MB]   lamps[{idx}].part: {prevPtr:X} -> {roofPart.Pointer:X}");
                    }
                    vb.lamps = lamps;
                    // Readback check
                    var readback = vb.lamps;
                    if (readback != null && cfg.RoofLampIndices.Length > 0)
                    {
                        int ri = cfg.RoofLampIndices[0];
                        var rp = readback[ri].part?.Pointer ?? IntPtr.Zero;
                        Plugin.L.LogInfo($"[MB]   readback lamps[{ri}].part={rp:X} (expected {roofPart.Pointer:X}, match={rp == roofPart.Pointer})");
                    }
                }
            }, "roofLampPart");
    }

    static void PatchAntenna(Transform vehicleRoot, float[]? pos)
    {
        if (pos == null || pos.Length < 3) return;
        try
        {
            var ant = vehicleRoot.GetComponentInChildren<Game.AntennaAnimator>(true);
            if (ant == null) return;
            var target = V3(pos);
            if ((ant.transform.localPosition - target).sqrMagnitude < 0.0001f) return;
            ant.transform.localPosition = target;
            Plugin.L.LogInfo($"[MB] Antenna -> ({target.x:F3}, {target.y:F3}, {target.z:F3})");
        }
        catch (Exception e) { Plugin.L.LogError($"[MB] PatchAntenna: {e.Message}"); }
    }

    static void DumpCaroModelMaterials(Transform root)
    {
        try
        {
            var caroModel = MeshReplacer.FindInHierarchy(root, "CaroModel");
            if (caroModel == null) return;
            var mr = caroModel.GetComponent<MeshRenderer>();
            var mf = caroModel.GetComponent<MeshFilter>();
            if (mr == null || mf == null) return;
            var mats = mr.sharedMaterials;
            var mesh = mf.sharedMesh;
            Plugin.L.LogInfo($"[MAT-DIAG] CaroModel mesh='{mesh?.name}' subs={mesh?.subMeshCount} mats={mats?.Length}");
            if (mats != null)
                for (int i = 0; i < mats.Length; i++)
                    Plugin.L.LogInfo($"[MAT-DIAG]   slot[{i}] = '{mats[i]?.name}'");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[MAT-DIAG] {e.Message}"); }
    }

    static void DumpVehicleBodyParts(Transform root)
    {
        try
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null) return;
            foreach (var r in renderers)
            {
                try
                {
                    var name = r.gameObject.name;
                    if (!name.StartsWith("LensFlare")) continue;
                    var wp = r.transform.position;
                    var lp = r.transform.localPosition;
                    Plugin.L.LogInfo($"[MB-DIAG] LensFlare GO='{name}' world=({wp.x:F3},{wp.y:F3},{wp.z:F3}) local=({lp.x:F3},{lp.y:F3},{lp.z:F3}) active={r.gameObject.activeSelf} enabled={r.enabled}");
                }
                catch { }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[MB-DIAG] Dump: {e.Message}"); }
    }

    static GameObject? FindByBodyPartId(Transform root, string partId)
    {
        try
        {
            var parts = root.GetComponentsInChildren<Game.VehicleBodyPart>(true);
            if (parts == null) return null;
            foreach (var p in parts)
            {
                try { if (p.ID == partId) return p.gameObject; }
                catch { }
            }
        }
        catch { }
        return null;
    }

    static PositionPivot PP(float[] v)  { var p = new PositionPivot(); p.position = V3(v); return p; }
    static Vector3 V3(float[] v)        => v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
    static Color   Col(float[] v)       => v.Length >= 4 ? new Color(v[0], v[1], v[2], v[3]) : Color.white;

    static void Set(Action a, string label)
    {
        try   { a(); Plugin.L.LogInfo($"[MB]   + {label}"); }
        catch (Exception e) { Plugin.L.LogWarning($"[MB]   ! {label}: {e.Message}"); }
    }
}
