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
    static readonly List<(MeshRenderer mr, string[] names, MeshRenderer? bodyMr)>   _matSlots   = new();
    // mr + cached resolved Texture (null = not yet found); applied once via MPB
    static readonly List<(MeshRenderer mr, Texture? tex, string name)>              _paintMasks = new();
    static readonly List<(MeshRenderer mr, Texture? tex, string name, bool isBody)> _albedos    = new();
    // Replaced meshes to re-assert every frame: the game re-clones part meshes at runtime
    // (deformation/lamp bake), losing our UV channels. (mf, our mesh, target name for logs)
    static readonly List<(MeshFilter mf, Mesh mesh, string tag)>                    _meshGuards = new();
    // Renderers with a lamp material slot that are NOT a VehicleBodyPart lamp renderer
    // (e.g. fender panels). The game's per-frame _LampsPositions dispatch covers only the
    // renderers it knows about, so these get a world-space buffer via per-renderer MPB.
    static readonly Dictionary<Transform, List<MeshRenderer>> _lampMpbRenderers = new();
    static readonly HashSet<Transform> _lampMpbLogged = new();
    // frame of last FindObjectsOfTypeAll retry (throttled to avoid per-frame stall)
    static int _textureRetryFrame = -1000;
    static readonly List<Transform>                                                 _lampVehicles = new();
    // pivot GOs spun each frame; vehiclePtr reads live shaft speed, IntPtr.Zero = fixed rpm
    static readonly List<(Transform pivot, float maxRPM, Vector3 axis, IntPtr vehiclePtr)> _spinners = new();
    // motion-blur ghosts trailing behind a spinner; blur[0] = lerped intensity [0..1]
    static readonly List<(List<Transform> ghostPivots, List<Material> ghostMats,
        float peakAlpha, float trailAngle, IntPtr vehiclePtr, float[] blur,
        Transform pivot, Vector3 axis)> _spinBlurs = new();
    // wheel-track overrides applied every LateUpdate.
    // axleCache stays null until physics has initialised Wheel.axisLocalPosition.
    static readonly List<(Transform root, SuspensionAxisPatch[] patches,
        IntPtr[] axisPtrs, Transform[] axisTfs,
        (IntPtr axis, IntPtr right, IntPtr left, float origY, float origZ, float origRadius)[]? axleCache,
        List<Transform> wheelGos)> _wheelAxes = new();
    // base localScale of each visual wheel, so a radius patch stays idempotent per frame
    static readonly Dictionary<Transform, Vector3> _wheelBaseScale = new();
    // default shock mount per wheel, so mount offsets are applied from the stock pose
    static readonly Dictionary<IntPtr, (float y, float z)> _mountBase = new();

    static void WriteBoth(IntPtr rightPtr, IntPtr leftPtr, int offset, float? value)
    {
        if (!value.HasValue) return;
        int bits = BitConverter.SingleToInt32Bits(value.Value);
        Marshal.WriteInt32(rightPtr + offset, bits);
        Marshal.WriteInt32(leftPtr  + offset, bits);
    }

    public static void Apply(Transform vehicleRoot)
    {
        var def = GetDefForVehicle(vehicleRoot);
        if (def == null) return;

        var markerGo = FindInHierarchy(vehicleRoot, def.VehicleMarker);
        if (markerGo == null) return;

        var vehicleComp = vehicleRoot.GetComponent<Game.Vehicle>();
        IntPtr vehiclePtr = vehicleComp != null ? vehicleComp.Pointer : IntPtr.Zero;

        foreach (var entry in def.MeshReplacements)
        {
            if (entry.Disable)
            {
                // deactivate every GO with this name (parts can be duplicated, e.g. AxisShaft)
                int disabledCount = 0;
                var allTf = vehicleRoot.GetComponentsInChildren<Transform>(true);
                if (allTf != null)
                    foreach (var t in allTf)
                        try { if (t.gameObject.name == entry.Target) { t.gameObject.SetActive(false); disabledCount++; } } catch { }
                if (disabledCount > 0)
                    Plugin.L.LogInfo($"[MESH] Disabled {disabledCount}x GO '{entry.Target}'");
                else
                    Plugin.L.LogWarning($"[MESH] Disable: GO '{entry.Target}' not found");
                continue;
            }

            var mesh = GetMesh(entry, def.FolderPath);
            if (mesh == null) continue;

            if (entry.SpinRPM != 0)
            {
                ApplySpinner(markerGo, entry, mesh, def, vehiclePtr);
                continue;
            }

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

                    if (PaintMaskRef(def) != null)
                        RegisterPaintMask(mr, PaintMaskRef(def)!);
                    if (AlbedoRef(def) != null)
                        RegisterAlbedo(mr, AlbedoRef(def)!, isBody: true);
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
                if (mr != null) RegisterMatSlots(mr, entry.MaterialSlots, markerGo.GetComponent<MeshRenderer>());
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
                    if (!entry.SkipPaintMask && PaintMaskRef(def) != null) RegisterPaintMask(mr, PaintMaskRef(def)!);
                    if (AlbedoRef(def) != null) RegisterAlbedo(mr, AlbedoRef(def)!, isBody: false);
                }
            }
        }

        // InitLampsMeshes was called by RuntimeInit with the ORIGINAL mesh -> bake data is stale.
        // Must call InitMeshData first (updates part.mesh = mf.sharedMesh = our new mesh),
        // then InitLampsMeshes (reads part.mesh -> rebuilds bake buffer from our new mesh).
        // Without InitMeshData first, InitLampsMeshes would reset mf.sharedMesh back to the old mesh!
        RebuildVehicleBodyLampMeshData(vehicleRoot);
        DumpBodyLampUVs(def, markerGo, "[UV2/NATIVE]");

        if (!def.KeepGameLampMesh)
        {
            // InitLampsMeshes may replace mf.sharedMesh with a native clone that lacks UV1 (TEXCOORD1).
            // Re-apply our AssetBundle body mesh so the renderer always uses the UV1-containing mesh.
            // The physics bake buffer (built by InitLampsMeshes) is for deformation only, not rendering.
            ReapplyBodyMesh(vehicleRoot, def, markerGo);
        }
        else
            Plugin.L.LogInfo("[MESH] keepGameLampMesh: keeping InitLampsMeshes-generated body mesh");

        SyncLampMaterials(vehicleRoot, def);
        LogVehicleState(vehicleRoot, def);
        VehiclePatcher.Apply(vehicleRoot, def);

        if (def.KeepGameLampMesh)
        {
            // Re-run the bake AFTER VehiclePatcher so the lamp index channel is computed
            // from the PATCHED lamp positions (the first bake above used pre-patch ones).
            RebuildVehicleBodyLampMeshData(vehicleRoot);
            DumpBodyLampUVs(def, markerGo, "[UV2/REBAKED]");
        }

        // lampPositionsBuffer (VehicleBody+0x180) is null on every spawned instance (not copied by
        // Instantiate). InitLamps() recreates it from vb.lamps[] (already patched by VehiclePatcher).
        // ForceWriteLampPositions then overwrites its contents from the managed side, because native
        // InitLamps cannot read bulbPosition from il2cpp_object_new-allocated lamp objects.
        ReinitLampPositionsBuffer(vehicleRoot);

        if (def.VehicleBody?.Lamps != null)
        {
            ForceWriteLampPositions(vehicleRoot);
            TrackLampVehicle(vehicleRoot);
            UploadLampPositionsBuffer(vehicleRoot, fullBind: true);
        }
    }

    static void TrackLampVehicle(Transform vehicleRoot)
    {
        for (int i = _lampVehicles.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_lampVehicles[i] == null) _lampVehicles.RemoveAt(i);
                else if (_lampVehicles[i] == vehicleRoot) return;
            }
            catch { _lampVehicles.RemoveAt(i); }
        }
        _lampVehicles.Add(vehicleRoot);
        Plugin.L.LogInfo($"[LPOS] Tracking '{vehicleRoot.gameObject.name}' for LateUpdate uploads");
    }

    public static void UpdateAllLampPositions()
    {
        for (int i = _lampVehicles.Count - 1; i >= 0; i--)
        {
            try
            {
                var root = _lampVehicles[i];
                if (root == null) { _lampVehicles.RemoveAt(i); continue; }
                UploadLampPositionsBuffer(root, fullBind: false);
                UpdatePanelLampMPBs(root);
                ApplyDebugRearPowers(root);
            }
            catch
            {
                _lampVehicles.RemoveAt(i);
            }
        }
    }

    static readonly Dictionary<Transform, ComputeBuffer> _testPowerBuffers = new();
    static int _debugRearLogFrame = -1000;

    // DEBUG (DebugForceRearPowers): bind an all-ones _LampsPowers buffer to the rear lamp
    // materials every frame. Isolates "power binding broken" from "index/shader broken".
    static unsafe void ApplyDebugRearPowers(Transform vehicleRoot)
    {
        var def = GetDefForVehicle(vehicleRoot);
        if (def == null || !def.DebugForceRearPowers) return;

        var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
        var lamps = vb?.lamps;
        int n = lamps?.Length ?? 0;
        if (n == 0) return;

        if (!_testPowerBuffers.TryGetValue(vehicleRoot, out var cb) || cb == null)
        {
            cb = new ComputeBuffer(n, 4);
            var ones = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(n);
            for (int i = 0; i < n; i++) ones[i] = 1f;
            IntPtr klass  = IL2CPP.il2cpp_object_get_class(cb.Pointer);
            IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SetData", 1);
            if (method != IntPtr.Zero)
            {
                IntPtr exc = IntPtr.Zero;
                void** args = stackalloc void*[1];
                args[0] = (void*)ones.Pointer;
                IL2CPP.il2cpp_runtime_invoke(method, cb.Pointer, args, ref exc);
            }
            _testPowerBuffers[vehicleRoot] = cb;
            Plugin.L.LogInfo($"[DBGREAR] Created all-ones powers buffer n={n}");
        }

        int bound = 0;
        var rmats = vb.rearlampsMaterials;
        if (rmats != null)
            foreach (var mat in rmats)
                if (mat != null) { mat.SetBuffer("_LampsPowers", cb); bound++; }

        if (Time.frameCount - _debugRearLogFrame > 600)
        {
            _debugRearLogFrame = Time.frameCount;
            Plugin.L.LogInfo($"[DBGREAR] all-ones _LampsPowers bound to {bound} rear material(s)");
        }
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

            IntPtr cbPtr = Marshal.ReadIntPtr(vb.Pointer + 0x198);
            int nativeCbCount = -1;
            if (cbPtr != IntPtr.Zero)
            {
                try
                {
                    IntPtr cbKlass = IL2CPP.il2cpp_object_get_class(cbPtr);
                    IntPtr countProp = IL2CPP.il2cpp_class_get_method_from_name(cbKlass, "get_count", 0);
                    if (countProp != IntPtr.Zero)
                    {
                        unsafe
                        {
                            IntPtr exc2 = IntPtr.Zero;
                            IntPtr r = IL2CPP.il2cpp_runtime_invoke(countProp, cbPtr, null, ref exc2);
                            if (exc2 == IntPtr.Zero && r != IntPtr.Zero)
                                nativeCbCount = Marshal.ReadInt32(r + 0x10);
                        }
                    }
                }
                catch { }
            }
            int lampCount = vb.lamps?.Length ?? -1;
            Plugin.L.LogInfo($"[LPOS] InitLamps done: buf={(cbPtr == IntPtr.Zero ? "null" : $"0x{cbPtr:X}")} cb.count={nativeCbCount} vb.lamps.Length={lampCount}");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[LPOS] ReinitLampPositionsBuffer: {ex.Message}"); }
    }

    // Write bulbPosition.position (local coords) from managed vb.lamps directly into the
    // native lampPositionsBuffer (VB+0x180) via SetData. Needed because il2cpp_object_new-
    // allocated lamp objects have positions that native InitLamps can't read correctly.
    static unsafe void ForceWriteLampPositions(Transform vehicleRoot)
    {
        try
        {
            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            IntPtr cbPtr = Marshal.ReadIntPtr(vb.Pointer + 0x198);
            if (cbPtr == IntPtr.Zero) { Plugin.L.LogWarning("[LPOS/FW] lampPositionsBuffer null"); return; }

            var lamps = vb.lamps;
            int n = lamps?.Length ?? 0;
            if (n == 0) return;

            var positions = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(n);
            for (int i = 0; i < n; i++)
                positions[i] = lamps?[i]?.bulbPosition.position ?? Vector3.zero;

            IntPtr klass  = IL2CPP.il2cpp_object_get_class(cbPtr);
            IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SetData", 1);
            if (method == IntPtr.Zero) { Plugin.L.LogWarning("[LPOS/FW] SetData not found"); return; }

            IntPtr exc = IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)positions.Pointer;
            IL2CPP.il2cpp_runtime_invoke(method, cbPtr, args, ref exc);
            if (exc != IntPtr.Zero) { Plugin.L.LogWarning("[LPOS/FW] SetData threw"); return; }

            var sb = new System.Text.StringBuilder($"[LPOS/FW] Wrote {n} positions:");
            for (int i = 0; i < n; i++)
                sb.Append($" [{i}]=({positions[i].x:F2},{positions[i].y:F2},{positions[i].z:F2})");
            Plugin.L.LogInfo(sb.ToString());
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[LPOS/FW] {ex.Message}"); }
    }

    // Dumps the lamp index channel (UV2 = TEXCOORD1) of the body mesh currently on the
    // renderer. Used to inspect what InitLampsMeshes generated before we revert it.
    static void DumpBodyLampUVs(CustomVehicleDef def, GameObject markerGo, string tag)
    {
        foreach (var entry in def.MeshReplacements)
        {
            if (!entry.IsBody) continue;
            var targetGo = entry.Target == def.VehicleMarker
                ? markerGo
                : FindInHierarchy(markerGo.transform, entry.Target);
            var mf = targetGo?.GetComponent<MeshFilter>();
            if (mf != null) DumpLampUVs(mf.sharedMesh, tag);
        }
    }

    public static void DumpLampUVs(Mesh? mesh, string tag)
    {
        try
        {
            if (mesh == null) { Plugin.L.LogInfo($"{tag} mesh=null"); return; }
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2>? uv2 = null;
            try { var a = mesh.uv2; if (a != null && a.Length > 0) uv2 = a; } catch { }
            Plugin.L.LogInfo($"{tag} mesh='{mesh.name}' verts={mesh.vertexCount} subs={mesh.subMeshCount} uv2.len={(uv2 == null ? "none" : uv2.Length.ToString())}");
            if (uv2 == null) return;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>? tris = null;
                try { tris = mesh.GetTriangles(s); } catch { }
                if (tris == null || tris.Length == 0)
                {
                    Plugin.L.LogInfo($"{tag}   sub[{s}] tris unreadable");
                    continue;
                }
                var xs = new SortedSet<float>();
                var ys = new SortedSet<float>();
                foreach (var vi in tris)
                {
                    if (vi < 0 || vi >= uv2.Length) continue;
                    var u = uv2[vi];
                    xs.Add((float)Math.Round(u.x, 2));
                    ys.Add((float)Math.Round(u.y, 2));
                }
                string xstr = xs.Count <= 12 ? string.Join(",", xs) : $"{xs.Min}..{xs.Max} ({xs.Count} vals)";
                string ystr = ys.Count <= 12 ? string.Join(",", ys) : $"{ys.Min}..{ys.Max} ({ys.Count} vals)";
                Plugin.L.LogInfo($"{tag}   sub[{s}] uv2.x=[{xstr}] uv2.y=[{ystr}]");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"{tag} ERR: {e.Message}"); }
    }

    // InitLampsMeshes iterates ALL VehicleBodyParts (body, panels, doors...) and swaps each
    // part's mf.sharedMesh for a native clone. For our isReadable=false bundle meshes the
    // clone has no uv2 (lamp indices) — so every replaced mesh must be re-applied, not just isBody.
    static void ReapplyBodyMesh(Transform vehicleRoot, CustomVehicleDef def, GameObject markerGo)
    {
        foreach (var entry in def.MeshReplacements)
        {
            if (entry.Disable || entry.SpinRPM != 0) continue;
            var mesh = GetMesh(entry, def.FolderPath);
            if (mesh == null) continue;
            var targetGo = entry.Target == def.VehicleMarker
                ? markerGo
                : FindInHierarchy(markerGo.transform, entry.Target);
            if (targetGo == null) continue;
            var mf = targetGo.GetComponent<MeshFilter>();
            if (mf == null) continue;
            RegisterMeshGuard(mf, mesh, entry.Target);
            var current = mf.sharedMesh;
            if (current == mesh) continue;
            Plugin.L.LogInfo($"[MESH] ReapplyBody '{entry.Target}': '{current?.name}' -> '{mesh.name}' (UV1 restore after InitLampsMeshes)");
            mf.sharedMesh = mesh;
            // Also update SkinnedMeshRenderer if present.
            var smr = targetGo.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) smr.sharedMesh = mesh;
        }
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

    // Restore the working local-coords pattern: bind per-material + global to VB+0x180.
    static unsafe void UploadLampPositionsBuffer(Transform vehicleRoot, bool fullBind)
    {
        try
        {
            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            IntPtr cbObjPtr = Marshal.ReadIntPtr(vb.Pointer + 0x198);
            if (cbObjPtr == IntPtr.Zero)
            {
                if (fullBind) Plugin.L.LogWarning("[LPOS] lampPositionsBuffer is null");
                return;
            }

            IntPtr cbKlass = IL2CPP.il2cpp_object_get_class(cbObjPtr);
            string cbClassName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cbKlass)) ?? "";
            if (cbClassName != "ComputeBuffer")
            {
                if (fullBind) Plugin.L.LogWarning($"[LPOS] VB+0x180 resolves to '{cbClassName}', not ComputeBuffer - offset changed in this game version, skipping lamp-position bind");
                return;
            }

            var lamps = vb.lamps;
            int n = lamps?.Count ?? 0;

            var cb = new ComputeBuffer(cbObjPtr);

            // Set global (game's standard vehicles use SetGlobalBuffer with world; we use local
            // since world makes balls disappear — shader must interpret these as object-space).
            Shader.SetGlobalBuffer(Shader.PropertyToID("_LampsPositions"), cb);

            var frontMats = vb.frontLampsMaterials;
            var rearMats  = vb.rearlampsMaterials;
            int setCount  = 0;
            if (frontMats != null)
                foreach (var mat in frontMats)
                    if (mat != null) { mat.SetBuffer("_LampsPositions", cb); setCount++; }
            if (rearMats != null)
                foreach (var mat in rearMats)
                    if (mat != null) { mat.SetBuffer("_LampsPositions", cb); setCount++; }

            if (fullBind)
                Plugin.L.LogInfo($"[LPOS] Bound n={n} mats={setCount}");
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[LPOS] UploadLampPositionsBuffer: {ex.Message}");
        }
    }

    public static unsafe void RebindLampPowersBuffer(Game.Vehicle vehicle)
    {
        try
        {
            var vlc = vehicle.lamps;
            if (vlc == null) return;

            // GPUBuffer<float> at vlc+0x50; null before VLC.Start -> not yet ready
            IntPtr gpuBufPtr = Marshal.ReadIntPtr(vlc.Pointer + 0x50);
            if (gpuBufPtr == IntPtr.Zero) return;

            // ComputeBuffer is at GPUBuffer+0x10 (confirmed via BindBuffer(Material) disasm)
            IntPtr cbPtr = Marshal.ReadIntPtr(gpuBufPtr + 0x10);
            if (cbPtr == IntPtr.Zero) return;

            var cb    = new ComputeBuffer(cbPtr);
            int propId = Shader.PropertyToID("_LampsPowers");

            var vb = vehicle.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            var fmats = vb.frontLampsMaterials;
            var rmats = vb.rearlampsMaterials;
            int count = 0;
            if (fmats != null)
                foreach (var mat in fmats)
                    if (mat != null) { mat.SetBuffer(propId, cb); count++; }
            if (rmats != null)
                foreach (var mat in rmats)
                    if (mat != null) { mat.SetBuffer(propId, cb); count++; }

            if (count > 0)
                Plugin.L.LogInfo($"[REBIND] _LampsPowers -> {count} lamp material(s)");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[REBIND] {ex.Message}"); }
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

        var mpbList = new List<MeshRenderer>();

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
            mpbList.Add(mr);
        }

        _lampMpbRenderers[vehicleRoot] = mpbList;
    }

    // Ground truth (disasm 2026-06-11): there is NO global _LampsPositions. The game binds
    // VehicleBody.lampPositionsBuffer per-material in VehicleBody.InitInstance (two
    // Material.SetBuffer calls at 0x1664151/0x1664291), and the Car Lamp shaders do ALL the
    // bulb math in the RENDERER's OBJECT SPACE (the PS transforms the camera by cb2
    // unity_WorldToObject of the current draw). The buffer holds body-local coords, so any
    // lamp renderer whose GO sits at a non-identity local offset (our fender panels at
    // (±0.6288, 0.537, 1.367)) sees the bulb sphere displaced by exactly that offset.
    // Stock light bars work only because their GOs sit at localPosition (0,0,0).
    // Fix: per-renderer MPB buffer with positions converted into THAT renderer's local space.
    static readonly Dictionary<MeshRenderer, ComputeBuffer> _rendererLampBuffers = new();

    static unsafe void UpdatePanelLampMPBs(Transform vehicleRoot)
    {
        try
        {
            if (!_lampMpbRenderers.TryGetValue(vehicleRoot, out var list) || list == null || list.Count == 0)
                return;

            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            var lamps = vb?.lamps;
            int n = lamps?.Count ?? 0;
            if (n == 0) return;
            var bodyTf = vb!.transform;

            int bound = 0;
            for (int r = list.Count - 1; r >= 0; r--)
            {
                try
                {
                    var mr = list[r];
                    if (mr == null) { list.RemoveAt(r); continue; }

                    if (!_rendererLampBuffers.TryGetValue(mr, out var cb) || cb == null || cb.count != n)
                    {
                        try { cb?.Release(); } catch { }
                        cb = new ComputeBuffer(n, 12);
                        _rendererLampBuffers[mr] = cb;
                    }

                    // bulbPosition coords live in body space; the shader needs them in the
                    // renderer's own object space (also keeps the ball glued to a torn-off panel).
                    var local = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(n);
                    var rtf = mr.transform;
                    for (int i = 0; i < n; i++)
                    {
                        var lamp = lamps![i];
                        if (lamp == null) continue;
                        local[i] = rtf.InverseTransformPoint(bodyTf.TransformPoint(lamp.bulbPosition.position));
                    }

                    IntPtr klass  = IL2CPP.il2cpp_object_get_class(cb.Pointer);
                    IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SetData", 1);
                    if (method == IntPtr.Zero) continue;
                    IntPtr exc = IntPtr.Zero;
                    void** args = stackalloc void*[1];
                    args[0] = (void*)local.Pointer;
                    IL2CPP.il2cpp_runtime_invoke(method, cb.Pointer, args, ref exc);
                    if (exc != IntPtr.Zero) continue;

                    var block = new MaterialPropertyBlock();
                    mr.GetPropertyBlock(block);
                    block.SetBuffer("_LampsPositions", cb);
                    mr.SetPropertyBlock(block);
                    bound++;
                }
                catch { list.RemoveAt(r); }
            }

            if (bound > 0 && _lampMpbLogged.Add(vehicleRoot))
                Plugin.L.LogInfo($"[LMPB] renderer-local _LampsPositions bound via MPB to {bound} renderer(s) on '{vehicleRoot.gameObject.name}'");
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[LMPB] {ex.Message}");
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

    // Texture reference: either a game-texture name or "file:<abs path>" for a PNG
    // shipped in the vehicle folder (patched albedo/paint mask from uv_dedup.py).
    static string? PaintMaskRef(CustomVehicleDef def)
        => def.PaintMaskTextureFile != null
            ? "file:" + System.IO.Path.Combine(def.FolderPath, def.PaintMaskTextureFile)
            : def.PaintMaskTextureName;

    static string? AlbedoRef(CustomVehicleDef def)
        => def.AlbedoTextureFile != null
            ? "file:" + System.IO.Path.Combine(def.FolderPath, def.AlbedoTextureFile)
            : def.AlbedoTextureName;

    static readonly Dictionary<string, Texture2D?> _fileTextures = new();

    static Texture? FindTexture(string name)
    {
        if (name.StartsWith("file:"))
            return LoadTextureFile(name.Substring(5));
        var all = Resources.FindObjectsOfTypeAll<Texture2D>();
        if (all == null) return null;
        foreach (var t in all)
            try { if (t.name == name) return t; } catch { }
        return null;
    }

    static Texture2D? LoadTextureFile(string path)
    {
        if (_fileTextures.TryGetValue(path, out var cached) && cached != null)
            return cached;
        try
        {
            if (!System.IO.File.Exists(path))
            {
                Plugin.L.LogWarning($"[TEX] file not found: {path}");
                _fileTextures[path] = null;
                return null;
            }
            var bytes = System.IO.File.ReadAllBytes(path);
            // mipChain: true — стоковые текстуры с мипами; Point — пиксель-арт стиль игры
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!ImageConversion.LoadImage(tex, bytes))
            {
                Plugin.L.LogWarning($"[TEX] LoadImage failed: {path}");
                _fileTextures[path] = null;
                return null;
            }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.name = System.IO.Path.GetFileNameWithoutExtension(path);
            tex.hideFlags = HideFlags.HideAndDontSave;   // survive scene loads
            _fileTextures[path] = tex;
            Plugin.L.LogInfo($"[TEX] loaded '{tex.name}' {tex.width}x{tex.height} from {path}");
            return tex;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TEX] {path}: {e.Message}");
            _fileTextures[path] = null;
            return null;
        }
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
        // Renderer-level _MainTex: set every frame to override skin system's material.SetTexture().
        var block = new MaterialPropertyBlock();
        mr.GetPropertyBlock(block);
        block.SetTexture("_MainTex", tex);
        mr.SetPropertyBlock(block);

        if (!isBody) return;

        // Rear lamp _AlbedoTexture: set directly on the material instance. The rear lamp
        // material is a per-vehicle clone (CricketLampRearMaterial(Clone)(Clone)), so this
        // does not leak to other vehicles. A per-slot MPB is NOT used here: it was the only
        // beetle-specific difference on the rear lamp slot while rear bulbs didn't render,
        // and per-material SetBuffer(_LampsPowers/_LampsPositions) interaction with per-slot
        // MPBs is unverified. Re-applied every frame from FixAlbedos (cheap, idempotent) so
        // skin-system material re-inits stay covered.
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
                if (mat.GetTexture("_AlbedoTexture")?.Pointer == tex.Pointer) continue;
                mat.SetTexture("_AlbedoTexture", tex);
                Plugin.L.LogInfo($"[ALB] rear-mat '{mname}' _AlbedoTexture -> '{tex.name}' (direct, slot {slot})");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ALB] rear-mat: {e.Message}"); }
    }

    public static void FixAlbedos()
    {
        bool doRetry = Time.frameCount - _textureRetryFrame > 300;
        if (doRetry) _textureRetryFrame = Time.frameCount;

        for (int i = _albedos.Count - 1; i >= 0; i--)
        {
            try
            {
                var (mr, tex, name, isBody) = _albedos[i];
                if (tex == null) { if (doRetry) TryApplyAlbedo(i); }
                else DoSetAlbedo(mr, tex, isBody);
            }
            catch { _albedos.RemoveAt(i); }
        }
    }

    public static void FixPaintMasks()
    {
        bool doRetry = Time.frameCount - _textureRetryFrame > 300;

        for (int i = _paintMasks.Count - 1; i >= 0; i--)
        {
            try
            {
                var (mr, tex, name) = _paintMasks[i];
                if (tex == null) { if (doRetry) TryApplyPaintMask(i); }
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

    static void RegisterMeshGuard(MeshFilter mf, Mesh mesh, string tag)
    {
        for (int i = 0; i < _meshGuards.Count; i++)
        {
            try { if (_meshGuards[i].mf == mf) { _meshGuards[i] = (mf, mesh, tag); return; } }
            catch { _meshGuards.RemoveAt(i--); }
        }
        _meshGuards.Add((mf, mesh, tag));
    }

    public static void FixMeshes()
    {
        for (int i = _meshGuards.Count - 1; i >= 0; i--)
        {
            try
            {
                var (mf, mesh, tag) = _meshGuards[i];
                var cur = mf.sharedMesh;
                if (cur == mesh) continue;
                Plugin.L.LogInfo($"[MGUARD] '{tag}': '{cur?.name}' -> '{mesh.name}' (runtime mesh swap reverted, frame {Time.frameCount})");
                mf.sharedMesh = mesh;
            }
            catch { _meshGuards.RemoveAt(i); }
        }
    }

    static void RegisterMatSlots(MeshRenderer mr, string[] names, MeshRenderer? bodyMr = null)
    {
        for (int i = 0; i < _matSlots.Count; i++)
        {
            try { if (_matSlots[i].mr == mr) { _matSlots[i] = (mr, names, bodyMr); TryApplyMatSlots(i); return; } }
            catch { _matSlots.RemoveAt(i--); }
        }
        _matSlots.Add((mr, names, bodyMr));
        TryApplyMatSlots(_matSlots.Count - 1);
    }

    static void TryApplyMatSlots(int i)
    {
        var (mr, names, bodyMr) = _matSlots[i];
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
            // "@body:N" takes the live instance from the body renderer's slot N, so a part
            // painted like the hull shares the vehicle's own clone (skin/colour keep working).
            if (names[j].StartsWith("@body:"))
            {
                if (bodyMr == null) return;
                var bodyMats = bodyMr.sharedMaterials;
                if (bodyMats == null) return;
                if (!int.TryParse(names[j].Substring(6), out int bodySlot)) bodySlot = 0;
                if (bodySlot < 0 || bodySlot >= bodyMats.Length) return;
                mats[j] = bodyMats[bodySlot];
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

    // ─── Spinner system ───────────────────────────────────────────────────────

    // Transparent material for one blur ghost copy.
    // Uses Sprites/Default + albedo texture so blade detail shows through at partial opacity.
    static Material? CreateGhostMaterial(Texture? albedoTex, Color tint)
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Transparent");
        if (shader == null) { Plugin.L.LogWarning("[SPIN] No transparent shader for blur ghost"); return null; }
        var mat = new Material(shader) { name = "SpinBlurGhost", hideFlags = HideFlags.HideAndDontSave };
        if (albedoTex != null) mat.mainTexture = albedoTex;
        tint.a = 0f; // start fully transparent; alpha driven per-frame
        mat.color = tint;
        return mat;
    }

    static void ApplySpinner(GameObject markerGo, MeshEntry entry, Mesh mesh, CustomVehicleDef def, IntPtr vehiclePtr)
    {
        // Pivot GO lives at mesh.bounds.center in vehicleMarker-local space.
        // Mesh child GO sits at -bounds.center so vertices appear at their original positions.
        var center = mesh.bounds.center;

        var pivotGo = FindInHierarchy(markerGo.transform, entry.Target);
        bool isNew = pivotGo == null;
        if (isNew)
        {
            var go = new GameObject(entry.Target);
            go.transform.SetParent(markerGo.transform, false);
            pivotGo = go;
            Plugin.L.LogInfo($"[SPIN] Created pivot '{entry.Target}' under '{markerGo.name}'");
        }
        pivotGo.transform.localPosition = center;

        const string childName = "mesh";
        var childTf = pivotGo.transform.Find(childName);
        GameObject meshChild = childTf != null ? childTf.gameObject : new GameObject(childName);
        if (childTf == null) meshChild.transform.SetParent(pivotGo.transform, false);
        meshChild.transform.localPosition = new Vector3(-center.x, -center.y, -center.z);

        var mf = meshChild.GetComponent<MeshFilter>() ?? meshChild.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr = meshChild.GetComponent<MeshRenderer>() ?? meshChild.AddComponent<MeshRenderer>();
        if (entry.MaterialSlots != null)
        {
            RegisterMatSlots(mr, entry.MaterialSlots, markerGo.GetComponent<MeshRenderer>());
        }
        else if (isNew)
        {
            // Inherit body material so the spinner uses the correct Game/Car Paint shader.
            var bodyMr = markerGo.GetComponent<MeshRenderer>();
            if (bodyMr?.sharedMaterials != null && bodyMr.sharedMaterials.Length > 0)
                mr.sharedMaterials = new[] { bodyMr.sharedMaterials[0] };
        }

        if (!entry.SkipTextures)
        {
            if (!entry.SkipPaintMask && PaintMaskRef(def) != null) RegisterPaintMask(mr, PaintMaskRef(def)!);
            if (AlbedoRef(def) != null) RegisterAlbedo(mr, AlbedoRef(def)!, isBody: false);
        }

        var axis = entry.SpinAxis != null && entry.SpinAxis.Length == 3
            ? new Vector3(entry.SpinAxis[0], entry.SpinAxis[1], entry.SpinAxis[2])
            : Vector3.up;
        RegisterSpinner(pivotGo.transform, entry.SpinRPM, axis, vehiclePtr);
        Plugin.L.LogInfo($"[SPIN] '{entry.Target}': center=({center.x:F3},{center.y:F3},{center.z:F3}) maxRPM={entry.SpinRPM}");

        if (entry.SpinBlur)
        {
            var tint = entry.SpinBlurColor != null && entry.SpinBlurColor.Length >= 3
                ? new Color(entry.SpinBlurColor[0], entry.SpinBlurColor[1], entry.SpinBlurColor[2], 1f)
                : Color.white;

            var albedoRef = AlbedoRef(def);
            Texture? albedoTex = albedoRef != null ? FindTexture(albedoRef) : null;

            int ghostCount = Mathf.Max(1, entry.SpinBlurGhosts);

            var ghostPivots = new List<Transform>(ghostCount);
            var ghostMats   = new List<Material>(ghostCount);
            bool anyFailed  = false;

            for (int gi = 0; gi < ghostCount; gi++)
            {
                var mat = CreateGhostMaterial(albedoTex, tint);
                if (mat == null) { anyFailed = true; break; }
                ghostMats.Add(mat);

                var ghostPivotName = entry.Target + "_ghost" + gi;
                var existing = FindInHierarchy(markerGo.transform, ghostPivotName);
                var ghostPivotGo = existing != null ? existing : new GameObject(ghostPivotName);
                ghostPivotGo.transform.SetParent(markerGo.transform, false);
                ghostPivotGo.transform.localPosition = center;
                ghostPivots.Add(ghostPivotGo.transform);

                const string ghostChildName = "mesh";
                var gChildTf = ghostPivotGo.transform.Find(ghostChildName);
                var ghostChild = gChildTf != null ? gChildTf.gameObject : new GameObject(ghostChildName);
                if (gChildTf == null) ghostChild.transform.SetParent(ghostPivotGo.transform, false);
                ghostChild.transform.localPosition = new Vector3(-center.x, -center.y, -center.z);

                var gmf = ghostChild.GetComponent<MeshFilter>() ?? ghostChild.AddComponent<MeshFilter>();
                gmf.sharedMesh = mesh;

                var gmr = ghostChild.GetComponent<MeshRenderer>() ?? ghostChild.AddComponent<MeshRenderer>();
                gmr.sharedMaterials   = new[] { mat };
                gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                gmr.receiveShadows    = false;
            }

            if (!anyFailed)
            {
                for (int k = _spinBlurs.Count - 1; k >= 0; k--)
                    if (_spinBlurs[k].pivot == pivotGo.transform) _spinBlurs.RemoveAt(k);
                _spinBlurs.Add((ghostPivots, ghostMats, entry.SpinBlurAlpha,
                                entry.SpinBlurTrailAngle, vehiclePtr, new float[1],
                                pivotGo.transform, axis));
                Plugin.L.LogInfo($"[SPIN] Trail blur x{ghostCount} ghosts, {entry.SpinBlurTrailAngle}deg for '{entry.Target}'");
            }
        }
    }

    static void RegisterSpinner(Transform pivot, float maxRPM, Vector3 axis, IntPtr vehiclePtr)
    {
        for (int i = 0; i < _spinners.Count; i++)
        {
            try { if (_spinners[i].pivot == pivot) { _spinners[i] = (pivot, maxRPM, axis, vehiclePtr); return; } }
            catch { _spinners.RemoveAt(i--); }
        }
        _spinners.Add((pivot, maxRPM, axis, vehiclePtr));
    }

    // Vehicle+0x68 -> CarSimulation; +0x30 -> CarEngine; +0x38 -> Shaft; Shaft+0x18 = speed (rad/s)
    // RevLimiter at CarEngine+0x28; RevLimiter+0x18 = maxShaftSpeed (rad/s).
    static float GetEngineShaftFraction(IntPtr vehiclePtr)
    {
        try
        {
            if (vehiclePtr == IntPtr.Zero) return 1f;
            IntPtr simPtr = Marshal.ReadIntPtr(vehiclePtr + 0x68);
            if (simPtr == IntPtr.Zero) return 0f;
            IntPtr engPtr = Marshal.ReadIntPtr(simPtr + 0x30);
            if (engPtr == IntPtr.Zero) return 0f;
            IntPtr shaftPtr = Marshal.ReadIntPtr(engPtr + 0x38);
            if (shaftPtr == IntPtr.Zero) return 0f;
            float speed    = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(shaftPtr + 0x18));
            IntPtr revPtr  = Marshal.ReadIntPtr(engPtr + 0x28);
            if (revPtr == IntPtr.Zero) return 0f;
            float maxSpeed = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(revPtr + 0x18));
            if (maxSpeed <= 0f) return 0f;
            return Math.Abs(speed) / maxSpeed;
        }
        catch { return 0f; }
    }

    public static void UpdateSpinners()
    {
        float dt = Time.deltaTime;
        for (int i = _spinners.Count - 1; i >= 0; i--)
        {
            try
            {
                var (pivot, maxRPM, axis, vehiclePtr) = _spinners[i];
                if (pivot == null) { _spinners.RemoveAt(i); continue; }
                float fraction = GetEngineShaftFraction(vehiclePtr);
                pivot.Rotate(axis, fraction * maxRPM * 6f * dt, Space.Self);
            }
            catch { _spinners.RemoveAt(i); }
        }

        const float BlurLerpSpeed = 4f;
        const float AlphaDecay    = 0.60f; // each successive ghost is 60% as opaque as the previous
        for (int i = _spinBlurs.Count - 1; i >= 0; i--)
        {
            try
            {
                var (ghostPivots, ghostMats, peakAlpha, trailAngle, vehiclePtr, blur, pivot, axis) = _spinBlurs[i];
                if (pivot == null) { _spinBlurs.RemoveAt(i); continue; }

                float fraction = GetEngineShaftFraction(vehiclePtr);
                // Blur fades in above 15% RPM, reaches full at 50%+.
                float target = Mathf.Clamp01((fraction - 0.15f) / 0.35f);
                blur[0] = Mathf.Lerp(blur[0], target, Mathf.Clamp01(BlurLerpSpeed * dt));

                int n = ghostPivots.Count;
                float step = trailAngle / n; // angular gap between consecutive ghosts

                for (int gi = 0; gi < n; gi++)
                {
                    var gp = ghostPivots[gi];
                    if (gp == null) continue;

                    // Ghost lags behind the live blade rotation (negative = trailing).
                    float offsetDeg = -(gi + 1) * step;
                    gp.localRotation = pivot.localRotation * Quaternion.AngleAxis(offsetDeg, axis);

                    // Closest ghost = peakAlpha, each further ghost decays exponentially.
                    if (gi < ghostMats.Count && ghostMats[gi] != null)
                    {
                        float falloff = Mathf.Pow(AlphaDecay, gi);
                        var c = ghostMats[gi].color;
                        c.a = blur[0] * peakAlpha * falloff;
                        ghostMats[gi].color = c;
                    }
                }
            }
            catch { _spinBlurs.RemoveAt(i); }
        }
    }

    public static void RegisterWheelAxes(Transform vehicleRoot, SuspensionAxisPatch[] patches,
        IntPtr[] sortedAxisPtrs, Transform[] sortedAxisTfs, List<Transform> wheelGos)
    {
        for (int i = _wheelAxes.Count - 1; i >= 0; i--)
            if (_wheelAxes[i].root == vehicleRoot) _wheelAxes.RemoveAt(i);
        _wheelAxes.Add((vehicleRoot, patches, sortedAxisPtrs, sortedAxisTfs, null, wheelGos));
        Plugin.L.LogInfo($"[WHL] Registered {sortedAxisPtrs.Length} axle(s), {wheelGos.Count} visual wheel(s)");
    }

    // dump.cs offsets: AxisAnimator.axis 0x58; Axis.right/left 0x20/0x28;
    // Wheel.localPosition 0x40 (read by WheelRepresentation); Wheel.axisLocalPosition 0xB0 (read by AxisAnimator.Step)
    // A visual wheel belongs to the axle whose cached local Z it sits closest to.
    // Scaling is written from the cached base scale so re-running per frame is idempotent.
    static void ScaleAxleWheels(Transform root, List<Transform> wheelGos,
        (IntPtr axis, IntPtr right, IntPtr left, float origY, float origZ, float origRadius)[] axleCache,
        int axleIndex, float factor)
    {
        if (wheelGos == null) return;
        foreach (var wheelTf in wheelGos)
        {
            try
            {
                if (wheelTf == null) continue;
                float z = root.InverseTransformPoint(wheelTf.position).z;
                int nearest = 0;
                float best = float.MaxValue;
                for (int ai = 0; ai < axleCache.Length; ai++)
                {
                    float d = Math.Abs(z - axleCache[ai].origZ);
                    if (d < best) { best = d; nearest = ai; }
                }
                if (nearest != axleIndex) continue;
                if (!_wheelBaseScale.TryGetValue(wheelTf, out var baseScale))
                {
                    baseScale = wheelTf.localScale;
                    _wheelBaseScale[wheelTf] = baseScale;
                }
                var want = baseScale * factor;
                if ((wheelTf.localScale - want).sqrMagnitude > 1e-6f) wheelTf.localScale = want;
            }
            catch { }
        }
    }

    const int WHL_RADIUS       = 0x1B8;
    const int WHL_SHOCKMOUNT   = 0x80;   // shockMountBaseLocalPosition
    const int WHL_SPRINGDIST   = 0x130;  // maxSpringDistance
    const int WHL_SPRINGFORCE  = 0x134;  // maxSpringForce
    const int WHL_DAMPMIN      = 0x138;  // minSpringDamping
    const int WHL_DAMPMAX      = 0x13C;  // maxSpringDamping
    const int WHL_SPRINGLIMIT  = 0x140;  // springDistanceLimit
    const int WHL_AXIS_PTR     = 0x58;
    const int WHL_RIGHT        = 0x20;
    const int WHL_LEFT         = 0x28;
    const int WHL_LOCALPOS     = 0x40;
    const int WHL_AXISLOCALPOS = 0xB0;

    public static void FixWheelAxes()
    {
        for (int vi = _wheelAxes.Count - 1; vi >= 0; vi--)
        {
            try
            {
                var (root, patches, axisPtrs, axisTfs, axleCache, wheelGos) = _wheelAxes[vi];
                if (root == null) { _wheelAxes.RemoveAt(vi); continue; }

                if (axleCache == null)
                {
                    bool ready = true;
                    var cache = new (IntPtr axis, IntPtr right, IntPtr left, float origY, float origZ, float origRadius)[axisPtrs.Length];
                    for (int ai = 0; ai < axisPtrs.Length; ai++)
                    {
                        IntPtr axisPtr  = Marshal.ReadIntPtr(axisPtrs[ai] + WHL_AXIS_PTR);
                        if (axisPtr == IntPtr.Zero) { ready = false; break; }
                        IntPtr rightPtr = Marshal.ReadIntPtr(axisPtr + WHL_RIGHT);
                        IntPtr leftPtr  = Marshal.ReadIntPtr(axisPtr + WHL_LEFT);
                        if (rightPtr == IntPtr.Zero || leftPtr == IntPtr.Zero) { ready = false; break; }
                        float rx = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(rightPtr + WHL_AXISLOCALPOS));
                        if (Math.Abs(rx) < 0.001f) { ready = false; break; }
                        float origY = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(rightPtr + WHL_LOCALPOS + 4));
                        float origZ = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(rightPtr + WHL_LOCALPOS + 8));
                        float origR = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(rightPtr + WHL_RADIUS));
                        if (origR <= 0.001f) { ready = false; break; }
                        cache[ai] = (axisPtr, rightPtr, leftPtr, origY, origZ, origR);
                    }
                    if (!ready) continue;

                    axleCache = cache;
                    for (int ai = 0; ai < cache.Length; ai++)
                    {
                        float rx = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(cache[ai].right + WHL_AXISLOCALPOS));
                        Plugin.L.LogInfo($"[WHL] Cached axle[{ai}]: axisLocX={rx:F3} origY={cache[ai].origY:F3} origZ={cache[ai].origZ:F3} origRadius={cache[ai].origRadius:F3}");
                    }
                    _wheelAxes[vi] = (root, patches, axisPtrs, axisTfs, axleCache, wheelGos);
                }

                foreach (var p in patches)
                {
                    if (p.Index < 0 || p.Index >= axleCache.Length) continue;
                    var (axisPtr, rightPtr, leftPtr, origY, origZ, origRadius) = axleCache[p.Index];

                    if (p.Track.HasValue)
                    {
                        float half = p.Track.Value * 0.5f;
                        Marshal.WriteInt32(rightPtr + WHL_AXISLOCALPOS,     BitConverter.SingleToInt32Bits(+half));
                        Marshal.WriteInt32(leftPtr  + WHL_AXISLOCALPOS,     BitConverter.SingleToInt32Bits(-half));
                        Marshal.WriteInt32(rightPtr + WHL_LOCALPOS,         BitConverter.SingleToInt32Bits(+half));
                        Marshal.WriteInt32(leftPtr  + WHL_LOCALPOS,         BitConverter.SingleToInt32Bits(-half));
                    }

                    if (p.PositionY.HasValue)
                    {
                        float newY = origY + p.PositionY.Value;
                        Marshal.WriteInt32(rightPtr + WHL_LOCALPOS     + 4, BitConverter.SingleToInt32Bits(newY));
                        Marshal.WriteInt32(leftPtr  + WHL_LOCALPOS     + 4, BitConverter.SingleToInt32Bits(newY));
                        Marshal.WriteInt32(rightPtr + WHL_AXISLOCALPOS + 4, BitConverter.SingleToInt32Bits(newY));
                        Marshal.WriteInt32(leftPtr  + WHL_AXISLOCALPOS + 4, BitConverter.SingleToInt32Bits(newY));
                    }

                    if (p.PositionZ.HasValue)
                    {
                        float newZ = origZ + p.PositionZ.Value;
                        Marshal.WriteInt32(rightPtr + WHL_LOCALPOS     + 8, BitConverter.SingleToInt32Bits(newZ));
                        Marshal.WriteInt32(leftPtr  + WHL_LOCALPOS     + 8, BitConverter.SingleToInt32Bits(newZ));
                        Marshal.WriteInt32(rightPtr + WHL_AXISLOCALPOS + 8, BitConverter.SingleToInt32Bits(newZ));
                        Marshal.WriteInt32(leftPtr  + WHL_AXISLOCALPOS + 8, BitConverter.SingleToInt32Bits(newZ));
                    }

                    if (p.Radius.HasValue && p.Radius.Value > 0.001f)
                    {
                        Marshal.WriteInt32(rightPtr + WHL_RADIUS, BitConverter.SingleToInt32Bits(p.Radius.Value));
                        Marshal.WriteInt32(leftPtr  + WHL_RADIUS, BitConverter.SingleToInt32Bits(p.Radius.Value));
                        ScaleAxleWheels(root, wheelGos, axleCache, p.Index, p.Radius.Value / origRadius);
                    }

                    WriteBoth(rightPtr, leftPtr, WHL_SPRINGDIST,  p.SpringDistance);
                    WriteBoth(rightPtr, leftPtr, WHL_SPRINGLIMIT, p.SpringLimit);
                    WriteBoth(rightPtr, leftPtr, WHL_SPRINGFORCE, p.SpringForce);
                    WriteBoth(rightPtr, leftPtr, WHL_DAMPMIN,     p.DampingMin);
                    WriteBoth(rightPtr, leftPtr, WHL_DAMPMAX,     p.DampingMax);

                    if (p.MountY.HasValue || p.MountZ.HasValue)
                    {
                        if (!_mountBase.TryGetValue(rightPtr, out var baseMount))
                        {
                            baseMount = (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(rightPtr + WHL_SHOCKMOUNT + 4)),
                                         BitConverter.Int32BitsToSingle(Marshal.ReadInt32(rightPtr + WHL_SHOCKMOUNT + 8)));
                            _mountBase[rightPtr] = baseMount;
                        }
                        if (p.MountY.HasValue)
                        {
                            float y = baseMount.y + p.MountY.Value;
                            Marshal.WriteInt32(rightPtr + WHL_SHOCKMOUNT + 4, BitConverter.SingleToInt32Bits(y));
                            Marshal.WriteInt32(leftPtr  + WHL_SHOCKMOUNT + 4, BitConverter.SingleToInt32Bits(y));
                        }
                        if (p.MountZ.HasValue)
                        {
                            float z = baseMount.z + p.MountZ.Value;
                            Marshal.WriteInt32(rightPtr + WHL_SHOCKMOUNT + 8, BitConverter.SingleToInt32Bits(z));
                            Marshal.WriteInt32(leftPtr  + WHL_SHOCKMOUNT + 8, BitConverter.SingleToInt32Bits(z));
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[WHL] FixWheelAxes: {e.Message}"); _wheelAxes.RemoveAt(vi); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

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
