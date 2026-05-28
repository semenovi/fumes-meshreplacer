using System;
using System.Collections.Generic;
using UnityEngine;

static class MeshReplacer
{
    // Bundle path -> asset name -> Mesh
    static readonly Dictionary<string, Mesh?> _cache  = new();
    static readonly List<(MeshRenderer mr, int slots)> _fixers = new();

    public static void Apply(Transform vehicleRoot)
    {
        var def = GetDefForVehicle(vehicleRoot);
        if (def == null) return;

        var markerGo = FindInHierarchy(vehicleRoot, def.VehicleMarker);
        if (markerGo == null) return;

        foreach (var entry in def.MeshReplacements)
        {
            var mesh = GetMesh(entry, def.FolderPath);
            if (mesh == null) continue;

            var targetGo = entry.Target == def.VehicleMarker
                ? markerGo
                : FindInHierarchy(markerGo.transform, entry.Target);
            if (targetGo == null)
            {
                Plugin.L.LogWarning($"[MESH] Target '{entry.Target}' not found under '{def.VehicleMarker}'");
                continue;
            }

            var mf = targetGo.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != mesh)
            {
                Plugin.L.LogInfo($"[MESH] '{entry.Target}': '{mf.sharedMesh?.name}' -> '{mesh.name}'");
                mf.sharedMesh = mesh;
            }

            if (entry.IsBody)
            {
                var smr = targetGo.GetComponent<SkinnedMeshRenderer>();
                if (smr != null) smr.sharedMesh = mesh;

                var mr = targetGo.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    if (MaterialCycler.Saved == null)
                        MaterialCycler.Saved = mr.sharedMaterials;
                    MaterialCycler.Renderer = mr;
                }
            }

            if (entry.FixMaterialSlots)
            {
                var mr = targetGo.GetComponent<MeshRenderer>();
                if (mr != null) RegisterFixer(mr, mesh.subMeshCount);
            }
        }

        LogVehicleState(vehicleRoot, def);
        VehiclePatcher.Apply(vehicleRoot, def);
    }

    // Returns the CustomVehicleDef for this vehicle.
    // Checks license plate (Cars list) and body type id (assembled from Body list).
    static CustomVehicleDef? GetDefForVehicle(Transform vehicleRoot)
    {
        try
        {
            var vehicle = vehicleRoot.GetComponent<Game.Vehicle>();
            if (vehicle == null) return null;
            return VehicleFactory.GetDefForVehicle(vehicle);
        }
        catch { return null; }
    }

    public static void FixMaterialSlots()
    {
        for (int i = _fixers.Count - 1; i >= 0; i--)
        {
            var (mr, need) = _fixers[i];
            try
            {
                var mats = mr.sharedMaterials;
                if (mats.Length <= need) continue;
                var trimmed = new Material[need];
                for (int j = 0; j < need; j++) trimmed[j] = mats[j];
                mr.sharedMaterials = trimmed;
            }
            catch { _fixers.RemoveAt(i); }
        }
    }

    static void RegisterFixer(MeshRenderer mr, int slots)
    {
        for (int i = 0; i < _fixers.Count; i++)
        {
            try
            {
                if (_fixers[i].mr == mr) { _fixers[i] = (mr, slots); return; }
            }
            catch { _fixers.RemoveAt(i--); }
        }
        _fixers.Add((mr, slots));
    }

    static Mesh? GetMesh(MeshEntry entry, string folderPath)
    {
        string cacheKey = $"{folderPath}|{entry.Bundle}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var path   = System.IO.Path.Combine(folderPath, entry.Bundle);
        var bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null)
        {
            Plugin.L.LogError($"[MESH] Bundle not found: {path}");
            _cache[cacheKey] = null;
            return null;
        }

        Mesh? mesh = null;
        foreach (var name in entry.Candidates)
        {
            if (!bundle.Contains(name)) continue;
            mesh = bundle.LoadAsset<Mesh>(name);
            Plugin.L.LogInfo($"[MESH] Loaded '{mesh?.name}' from '{entry.Bundle}' verts={mesh?.vertexCount}");
            break;
        }
        if (mesh == null)
            Plugin.L.LogError($"[MESH] No mesh in '{entry.Bundle}'. Candidates: {string.Join(", ", entry.Candidates)}");

        bundle.Unload(false);
        _cache[cacheKey] = mesh;
        return mesh;
    }

    // Logs state for ALL vehicles right after Awake, to catch the crasher.
    public static void DiagnoseVehicle(Game.Vehicle vehicle)
    {
        try
        {
            var cfg = vehicle?.config;
            if (cfg == null) return;
            string? bodyId = null;
            try { bodyId = cfg.body?.Type?.id; } catch { }
            if (bodyId == null) { Plugin.L.LogInfo("[DBG-A] body=UNRESOLVABLE"); return; }
            string? plate = null;
            try { plate = cfg.licensePlate; } catch { }
            Plugin.L.LogInfo($"[DBG-A] body={bodyId} plate={plate ?? "null"}");
            try { Plugin.L.LogInfo($"[DBG-A] skin={(cfg.skin == null ? "NULL" : cfg.skin.id)}"); } catch (Exception e) { Plugin.L.LogInfo($"[DBG-A] skin ERR: {e.Message}"); }
            try { Plugin.L.LogInfo($"[DBG-A] engine={(cfg.engine == null ? "NULL" : "ok")}"); } catch { }
            try { Plugin.L.LogInfo($"[DBG-A] suspension={(cfg.suspension == null ? "NULL" : "ok")}"); } catch { }
            try { Plugin.L.LogInfo($"[DBG-A] wheels={(cfg.wheels == null ? "NULL" : "ok")}"); } catch (Exception e) { Plugin.L.LogInfo($"[DBG-A] wheels ERR: {e.Message}"); }
            try { Plugin.L.LogInfo($"[DBG-A] modules={(cfg.modules == null ? "NULL" : "ok")}"); } catch (Exception e) { Plugin.L.LogInfo($"[DBG-A] modules ERR: {e.Message}"); }
        }
        catch (Exception e) { Plugin.L.LogInfo($"[DBG-A] outer: {e.Message}"); }
    }

    static void LogVehicleState(Transform vehicleRoot, CustomVehicleDef def)
    {
        try
        {
            var v = vehicleRoot.GetComponent<Game.Vehicle>();
            if (v == null) { Plugin.L.LogInfo("[DBG] vehicle=NULL"); return; }
            var cfg = v.config;
            Plugin.L.LogInfo($"[DBG] config={(cfg == null ? "NULL" : "ok")}");
            if (cfg == null) return;
            try { Plugin.L.LogInfo($"[DBG] skin={(cfg.skin == null ? "NULL" : cfg.skin.id)}"); } catch (Exception e) { Plugin.L.LogInfo($"[DBG] skin ERR: {e.Message}"); }
            try { Plugin.L.LogInfo($"[DBG] body={(cfg.body == null ? "NULL" : cfg.body.Type?.id ?? "type=null")}"); } catch (Exception e) { Plugin.L.LogInfo($"[DBG] body ERR: {e.Message}"); }
            try { Plugin.L.LogInfo($"[DBG] engine={(cfg.engine == null ? "NULL" : "ok")}"); } catch { }
            try { Plugin.L.LogInfo($"[DBG] suspension={(cfg.suspension == null ? "NULL" : "ok")}"); } catch { }
            try { Plugin.L.LogInfo($"[DBG] wheels={(cfg.wheels == null ? "NULL" : "ok")}"); } catch { }
        }
        catch (Exception e) { Plugin.L.LogInfo($"[DBG] LogVehicleState: {e.Message}"); }
    }

    public static GameObject? FindInHierarchy(Transform t, string name)
    {
        if (t.gameObject.name == name) return t.gameObject;
        for (int i = 0; i < t.childCount; i++)
        {
            var found = FindInHierarchy(t.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
