using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

static class MeshReplacer
{
    // Bundle path -> asset name -> Mesh (also "game|<name>" for Resources meshes)
    static readonly Dictionary<string, Mesh?>     _cache     = new();
    // Material name -> Material (for materialSlots lookups)
    static readonly Dictionary<string, Material?> _matCache  = new();
    static readonly List<(MeshRenderer mr, int slots)>                              _fixers     = new();
    static readonly List<(MeshRenderer mr, string[] names)>                         _matSlots   = new();
    // mr + cached resolved Texture (null = not yet found); applied once via MPB
    static readonly List<(MeshRenderer mr, Texture? tex, string name)>              _paintMasks = new();
    static readonly List<(MeshRenderer mr, Texture? tex, string name, bool isBody)> _albedos    = new();

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

                    if (def.PaintMaskTextureName != null)
                        RegisterPaintMask(mr, def.PaintMaskTextureName);
                    if (def.AlbedoTextureName != null)
                        RegisterAlbedo(mr, def.AlbedoTextureName, isBody: true);
                }
            }

            if (entry.FixMaterialSlots)
            {
                var mr = targetGo.GetComponent<MeshRenderer>();
                if (mr != null) RegisterFixer(mr, mesh.subMeshCount);
            }

            if (entry.MaterialSlots != null)
            {
                var mr = targetGo.GetComponent<MeshRenderer>();
                if (mr != null) RegisterMatSlots(mr, entry.MaterialSlots);
            }

            if (entry.TargetRotation != null)
                ApplyWithWrapper(targetGo, entry.TargetPosition, entry.TargetRotation);
            else if (entry.TargetPosition != null)
                targetGo.transform.localPosition = new UnityEngine.Vector3(
                    entry.TargetPosition[0], entry.TargetPosition[1], entry.TargetPosition[2]);
            if (entry.TargetScale != null)
                targetGo.transform.localScale = new UnityEngine.Vector3(
                    entry.TargetScale[0], entry.TargetScale[1], entry.TargetScale[2]);

            if (!entry.IsBody && !entry.SkipTextures)
            {
                var mr = targetGo.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    if (!entry.SkipPaintMask && def.PaintMaskTextureName != null) RegisterPaintMask(mr, def.PaintMaskTextureName);
                    if (def.AlbedoTextureName != null) RegisterAlbedo(mr, def.AlbedoTextureName, isBody: false);
                }
            }
        }

        // InitLampsMeshes was called by RuntimeInit with the ORIGINAL mesh → bake data is stale.
        // Must call InitMeshData first (updates part.mesh = mf.sharedMesh = our new mesh),
        // then InitLampsMeshes (reads part.mesh → rebuilds bake buffer from our new mesh).
        // Without InitMeshData first, InitLampsMeshes would reset mf.sharedMesh back to the old mesh!
        RebuildVehicleBodyLampMeshData(vehicleRoot);

        SyncLampMaterials(vehicleRoot, def);
        LogVehicleState(vehicleRoot, def);
        VehiclePatcher.Apply(vehicleRoot, def);

        // lampPositionsBuffer (VehicleBody+0x180) is a private ComputeBuffer that is NOT copied by
        // Instantiate. It was created in RuntimeInit→InitPrefab→InitLamps on the runtimePrefab, so
        // it's null on every spawned instance.  Calling InitLamps() here recreates it from the
        // current vb.lamps[] (already patched by VehiclePatcher) and presumably calls
        // SetBuffer("_LampsPositions", ...) on the front/rear lamp materials — enabling mesh emission.
        ReinitLampPositionsBuffer(vehicleRoot);

        if (def.VehicleBody?.Lamps != null)
            UploadLampPositionsBuffer(vehicleRoot);
    }

    static unsafe void ReinitLampPositionsBuffer(Transform vehicleRoot)
    {
        try
        {
            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            IntPtr klass  = IL2CPP.il2cpp_object_get_class(vb.Pointer);
            IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "InitLamps", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.L.LogWarning("[LPOS] InitLamps not found on VehicleBody");
                return;
            }

            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, vb.Pointer, null, ref exc);
            if (exc != IntPtr.Zero) { Plugin.L.LogWarning("[LPOS] InitLamps threw exception"); return; }

            IntPtr cbPtr = Marshal.ReadIntPtr(vb.Pointer + 0x180);
            Plugin.L.LogInfo($"[LPOS] InitLamps done: lampPositionsBuffer={(cbPtr == IntPtr.Zero ? "null" : $"0x{cbPtr:X}")}");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[LPOS] ReinitLampPositionsBuffer: {ex.Message}"); }
    }

    static unsafe void RebuildVehicleBodyLampMeshData(Transform vehicleRoot)
    {
        try
        {
            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            IntPtr klass = IL2CPP.il2cpp_object_get_class(vb.Pointer);
            foreach (var methodName in new[] { "InitMeshData", "InitLampsMeshes" })
            {
                IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, methodName, 0);
                if (method == IntPtr.Zero)
                {
                    Plugin.L.LogWarning($"[LMESH] {methodName} not found on VehicleBody");
                    continue;
                }
                IntPtr exc = IntPtr.Zero;
                IL2CPP.il2cpp_runtime_invoke(method, vb.Pointer, null, ref exc);
                if (exc != IntPtr.Zero)
                    Plugin.L.LogWarning($"[LMESH] {methodName} threw exception");
                else
                    Plugin.L.LogInfo($"[LMESH] {methodName} OK");
            }
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[LMESH] RebuildVehicleBodyLampMeshData: {ex.Message}");
        }
    }

    static unsafe void UploadLampPositionsBuffer(Transform vehicleRoot)
    {
        try
        {
            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            // lampPositionsBuffer is a ComputeBuffer stored at vb+0x180
            IntPtr cbObjPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(vb.Pointer + 0x180);
            if (cbObjPtr == IntPtr.Zero)
            {
                Plugin.L.LogWarning("[LPOS] lampPositionsBuffer is null at Awake time");
                return;
            }

            var lamps = vb.lamps;
            if (lamps == null) return;
            int n = lamps.Count;

            // Buffer stores lamps.Length * Vector3 (3 floats, stride=12)
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                var lamp = lamps[i];
                if (lamp == null) continue;
                arr[i] = lamp.bulbPosition.position; // current (patched) position
            }

            IntPtr klass  = IL2CPP.il2cpp_object_get_class(cbObjPtr);
            IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SetData", 1);
            if (method == IntPtr.Zero)
            {
                Plugin.L.LogWarning("[LPOS] ComputeBuffer.SetData(1) not found");
                return;
            }

            IntPtr exc = IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)arr.Pointer;
            IL2CPP.il2cpp_runtime_invoke(method, cbObjPtr, args, ref exc);

            if (exc != IntPtr.Zero)
            {
                Plugin.L.LogWarning("[LPOS] ComputeBuffer.SetData threw exception");
                return;
            }
            Plugin.L.LogInfo($"[LPOS] Uploaded {n} lamp positions to GPU buffer");

            // Bind the positions buffer globally so the Game/Lamp shader sees it.
            // The game's normal path (VehicleBody init chain) does this, but for custom
            // vehicles we must do it explicitly after creating our own lampPositionsBuffer.
            var cb = new ComputeBuffer(cbObjPtr);
            int propId = Shader.PropertyToID("_LampsPositions");
            Shader.SetGlobalBuffer(propId, cb);
            Plugin.L.LogInfo($"[LPOS] Shader.SetGlobalBuffer('_LampsPositions', 0x{cbObjPtr:X}) done");

            // Also set per-material on every registered lamp material, in case the
            // shader reads it per-material rather than globally.
            var frontMats = vb.frontLampsMaterials;
            var rearMats  = vb.rearlampsMaterials;
            int setCount  = 0;
            if (frontMats != null)
                foreach (var mat in frontMats)
                    if (mat != null) { mat.SetBuffer("_LampsPositions", cb); setCount++; }
            if (rearMats != null)
                foreach (var mat in rearMats)
                    if (mat != null) { mat.SetBuffer("_LampsPositions", cb); setCount++; }
            Plugin.L.LogInfo($"[LPOS] SetBuffer('_LampsPositions') on {setCount} lamp materials");
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[LPOS] UploadLampPositionsBuffer: {ex.Message}");
        }
    }

    public static void SyncLampMaterialsForVehicle(Transform vehicleRoot)
    {
        var def = GetDefForVehicle(vehicleRoot);
        if (def == null) return;
        SyncLampMaterials(vehicleRoot, def);
    }

    static void SyncLampMaterials(Transform vehicleRoot, CustomVehicleDef def)
    {
        bool needSync = false;
        foreach (var e in def.MeshReplacements)
            if (e.SyncFrontLampSlot) { needSync = true; break; }
        if (!needSync) return;

        var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
        if (vb == null) { Plugin.L.LogWarning("[LSYNC] VehicleBody not found"); return; }

        var fmats = vb.frontLampsMaterials;
        if (fmats == null || fmats.Count == 0) { Plugin.L.LogWarning("[LSYNC] frontLampsMaterials empty"); return; }
        var registeredFrontMat = fmats[0];
        if (registeredFrontMat == null) { Plugin.L.LogWarning("[LSYNC] frontLampsMaterials[0] null"); return; }

        var markerGo = FindInHierarchy(vehicleRoot, def.VehicleMarker);
        if (markerGo == null) return;

        foreach (var entry in def.MeshReplacements)
        {
            if (!entry.SyncFrontLampSlot || entry.MaterialSlots == null) continue;
            var targetGo = entry.Target == def.VehicleMarker
                ? markerGo
                : FindInHierarchy(markerGo.transform, entry.Target);
            if (targetGo == null) continue;
            var mr = targetGo.GetComponent<MeshRenderer>();
            if (mr == null) continue;

            var mats = mr.sharedMaterials;
            if (mats == null) continue;
            bool changed = false;
            for (int i = 0; i < System.Math.Min(mats.Length, entry.MaterialSlots.Length); i++)
            {
                var slotName = entry.MaterialSlots[i];
                if (string.IsNullOrEmpty(slotName)) continue;
                if (!slotName.Contains("LampFront") && !slotName.Contains("FrontLamp")) continue;
                if (mats[i]?.Pointer == registeredFrontMat.Pointer) continue;
                string oldPtr = mats[i] != null ? $"0x{mats[i].Pointer:X}" : "null";
                Plugin.L.LogInfo($"[LSYNC] '{targetGo.name}' slot[{i}]: '{mats[i]?.name}'({oldPtr}) -> '{registeredFrontMat.name}'(0x{registeredFrontMat.Pointer:X})");
                mats[i] = registeredFrontMat;
                changed = true;
            }
            if (changed) mr.sharedMaterials = mats;
        }
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

    static void RegisterPaintMask(MeshRenderer mr, string name)
    {
        for (int i = 0; i < _paintMasks.Count; i++)
        {
            try { if (_paintMasks[i].mr == mr) { TryApplyPaintMask(i); return; } }
            catch { _paintMasks.RemoveAt(i--); }
        }
        var tex = FindTexture(name);
        _paintMasks.Add((mr, tex, name));
        if (tex != null) { DoSetPaintMask(mr, tex); Plugin.L.LogInfo($"[PM] '{tex.name}' -> '{mr.gameObject.name}'"); }
        else Plugin.L.LogWarning($"[PM] '{name}' not found yet, will retry");
    }

    static void TryApplyPaintMask(int i)
    {
        var (mr, tex, name) = _paintMasks[i];
        if (tex != null) return;
        tex = FindTexture(name);
        if (tex == null) return;
        _paintMasks[i] = (mr, tex, name);
        DoSetPaintMask(mr, tex);
        Plugin.L.LogInfo($"[PM] (retry) '{tex.name}' -> '{mr.gameObject.name}'");
    }

    static void DoSetPaintMask(MeshRenderer mr, Texture tex)
    {
        var block = new MaterialPropertyBlock();
        mr.GetPropertyBlock(block);
        block.SetTexture("_PaintMaskTexture", tex);
        mr.SetPropertyBlock(block);
    }

    static Texture? FindTexture(string name)
    {
        var all = Resources.FindObjectsOfTypeAll<Texture2D>();
        if (all == null) return null;
        foreach (var t in all)
            try { if (t.name == name) return t; } catch { }
        return null;
    }

    static void RegisterAlbedo(MeshRenderer mr, string name, bool isBody)
    {
        for (int i = 0; i < _albedos.Count; i++)
        {
            try { if (_albedos[i].mr == mr) { TryApplyAlbedo(i); return; } }
            catch { _albedos.RemoveAt(i--); }
        }
        var tex = FindTexture(name);
        _albedos.Add((mr, tex, name, isBody));
        if (tex != null) { DoSetAlbedo(mr, tex, isBody); Plugin.L.LogInfo($"[ALB] '{tex.name}' -> '{mr.gameObject.name}'"); }
        else Plugin.L.LogWarning($"[ALB] '{name}' not found yet, will retry");
    }

    static void TryApplyAlbedo(int i)
    {
        var (mr, tex, name, isBody) = _albedos[i];
        if (tex != null) return;
        tex = FindTexture(name);
        if (tex == null) return;
        _albedos[i] = (mr, tex, name, isBody);
        DoSetAlbedo(mr, tex, isBody);
        Plugin.L.LogInfo($"[ALB] (retry) '{tex.name}' -> '{mr.gameObject.name}'");
    }

    // Game/Car Paint shader reads _MainTex.
    // Game/Lamp shader reads _AlbedoTexture.
    // Renderer-level MPB overrides ALL submesh slots, so we must NOT set _AlbedoTexture
    // at renderer level — that would break front lamp glass appearance.
    // Instead, apply _AlbedoTexture via per-material MPB for rear lamp slots only.
    static void DoSetAlbedo(MeshRenderer mr, Texture tex, bool isBody)
    {
        var block = new MaterialPropertyBlock();
        mr.GetPropertyBlock(block);
        block.SetTexture("_MainTex", tex);
        mr.SetPropertyBlock(block);

        if (!isBody) return;

        // Rear lamp material stores albedo in _AlbedoTexture (Game/Lamp shader).
        // The skin system applies a material-level albedo that may look wrong for our vehicle.
        // Use per-material MPB to override only the rear lamp slot.
        try
        {
            var mats = mr.sharedMaterials;
            if (mats == null) return;
            for (int slot = 0; slot < mats.Length; slot++)
            {
                var mat = mats[slot];
                if (mat == null) continue;
                var mname = mat.name ?? "";
                if (!mname.Contains("Lamp") || !mname.Contains("Rear")) continue;
                var slotBlock = new MaterialPropertyBlock();
                mr.GetPropertyBlock(slotBlock, slot);
                slotBlock.SetTexture("_AlbedoTexture", tex);
                mr.SetPropertyBlock(slotBlock, slot);
                Plugin.L.LogInfo($"[ALB]   rear lamp slot[{slot}] '{mname}' _AlbedoTexture -> '{tex.name}'");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ALB] per-slot: {e.Message}"); }
    }

    public static void FixAlbedos()
    {
        for (int i = _albedos.Count - 1; i >= 0; i--)
        {
            try
            {
                var (mr, tex, name, isBody) = _albedos[i];
                if (tex == null) TryApplyAlbedo(i);
                else DoSetAlbedo(mr, tex, isBody);
            }
            catch { _albedos.RemoveAt(i); }
        }
    }

    public static void FixPaintMasks()
    {
        for (int i = _paintMasks.Count - 1; i >= 0; i--)
        {
            try
            {
                var (mr, tex, name) = _paintMasks[i];
                if (tex == null) TryApplyPaintMask(i);
                else DoSetPaintMask(mr, tex);
            }
            catch { _paintMasks.RemoveAt(i); }
        }
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

    static void RegisterMatSlots(MeshRenderer mr, string[] names)
    {
        for (int i = 0; i < _matSlots.Count; i++)
        {
            try { if (_matSlots[i].mr == mr) { _matSlots[i] = (mr, names); TryApplyMatSlots(i); return; } }
            catch { _matSlots.RemoveAt(i--); }
        }
        _matSlots.Add((mr, names));
        TryApplyMatSlots(_matSlots.Count - 1);
    }

    static void TryApplyMatSlots(int i)
    {
        var (mr, names) = _matSlots[i];
        var existing = mr.sharedMaterials;
        var mats = new Material[names.Length];
        for (int j = 0; j < names.Length; j++)
        {
            if (string.IsNullOrEmpty(names[j]))
            {
                // Keep the original material instance in this slot (preserves skin-system references).
                mats[j] = existing != null && j < existing.Length ? existing[j] : null;
                continue;
            }
            var m = FindMaterial(names[j]);
            if (m == null) return; // not all resolved yet
            mats[j] = m;
        }
        mr.sharedMaterials = mats;
        Plugin.L.LogInfo($"[MSLOT] Set {names.Length} slot(s) on '{mr.gameObject.name}'");
        _matSlots.RemoveAt(i);
    }

    public static void FixMatSlots()
    {
        for (int i = _matSlots.Count - 1; i >= 0; i--)
        {
            try { TryApplyMatSlots(i); }
            catch { _matSlots.RemoveAt(i); }
        }
    }

    static Material? FindMaterial(string name)
    {
        if (_matCache.TryGetValue(name, out var cached)) return cached;
        var all = Resources.FindObjectsOfTypeAll<Material>();
        Material? found = null;
        int matchCount = 0;
        if (all != null)
            foreach (var m in all)
            {
                try
                {
                    if (m == null) continue;
                    // Log all materials whose name contains the search term (catches Clone variants)
                    if (m.name != null && m.name.Contains(name))
                        Plugin.L.LogInfo($"[MSLOT] candidate for '{name}': '{m.name}'(0x{m.Pointer:X})");
                    if (m.name == name) { if (found == null) found = m; matchCount++; }
                }
                catch { }
            }
        if (matchCount > 1)
            Plugin.L.LogWarning($"[MSLOT] '{name}' matched {matchCount} times in Resources — using first");
        if (found != null) _matCache[name] = found;
        else Plugin.L.LogWarning($"[MSLOT] Material '{name}' not found in Resources");
        return found;
    }

    // Inserts a wrapper GO between the target and its parent so EngineAnimator (or any other
    // per-frame animator on the target GO) can freely set localRotation on the child while our
    // orientation lives on the parent wrapper and is never overwritten.
    static void ApplyWithWrapper(GameObject go, float[]? posArr, float[]? rotArr)
    {
        const string prefix = "MR_Wrap_";
        var rot = rotArr != null
            ? Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2])
            : go.transform.localRotation;
        var pos = posArr != null
            ? new Vector3(posArr[0], posArr[1], posArr[2])
            : go.transform.localPosition;

        var parent = go.transform.parent;

        // Already wrapped by a previous Apply call?
        if (parent != null && parent.name == prefix + go.name)
        {
            parent.localPosition = pos;
            parent.localRotation = rot;
            Plugin.L.LogInfo($"[MESH] Wrapper updated: '{go.name}' pos=({pos.x:F3},{pos.y:F3},{pos.z:F3}) rot=({rotArr?[0]},{rotArr?[1]},{rotArr?[2]})");
            return;
        }

        var wrapper = new GameObject(prefix + go.name);
        wrapper.transform.SetParent(parent, false);
        wrapper.transform.localPosition = pos;
        wrapper.transform.localRotation = rot;
        wrapper.transform.localScale    = Vector3.one;
        go.transform.SetParent(wrapper.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one;
        Plugin.L.LogInfo($"[MESH] Wrapper created for '{go.name}' pos=({pos.x:F3},{pos.y:F3},{pos.z:F3}) rot=({rotArr?[0]},{rotArr?[1]},{rotArr?[2]})");
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
        if (entry.GameMesh != null)
        {
            string gKey = $"game|{entry.GameMesh}";
            if (_cache.TryGetValue(gKey, out var gm) && gm != null) return gm;
            var all = Resources.FindObjectsOfTypeAll<Mesh>();
            if (all != null)
                foreach (var m in all)
                    try { if (m?.name == entry.GameMesh) { _cache[gKey] = m; Plugin.L.LogInfo($"[MESH] Game mesh '{m.name}' verts={m.vertexCount}"); return m; } } catch { }
            Plugin.L.LogWarning($"[MESH] Game mesh '{entry.GameMesh}' not found in Resources");
            return null;
        }

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
