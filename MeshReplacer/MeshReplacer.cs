using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

static class MeshReplacer
{
    const bool ExperimentSkipPickupBodyMeshSwap = false;
    const bool ExperimentHybridPickupBodyMesh = false;
    const bool ExperimentForcePickupRearBulbRadius = false;
    const float ForcedPickupRearBulbRadius = 0.80f;
    const bool ExperimentPickupOverlayHybrid = false;
    const bool ExperimentForcePickupRearClearCoatColor = false;
    static readonly Color ForcedPickupRearClearCoatColor = new Color(0f, 1f, 0f, 1f);
    const bool ExperimentOverridePickupRearBufferPositions = false;
    static readonly Vector3 ExperimentPickupRearBufLamp6 = new Vector3(-1.90f, 1.55f, -3.75f);
    static readonly Vector3 ExperimentPickupRearBufLamp7 = new Vector3( 1.90f, 2.15f, -4.55f);
    const bool ExperimentForcePickupRearBulbDepth = false;
    const float ForcedPickupRearBulbDepth = 10.05f;
    const bool ExperimentForcePickupRearBulbRadiusRear = false;
    const float ForcedPickupRearBulbRadiusRear = 1.90f;
    const bool ExperimentOverridePickupBodyMeshBounds = false;
    static readonly Vector3 ExperimentPickupBoundsCenter = new Vector3(0.0f, 0.45f, -1.65f);
    static readonly Vector3 ExperimentPickupBoundsSize = new Vector3(1.9f, 1.7f, 1.2f);

    // Bundle path -> asset name -> Mesh (also "game|<name>" for Resources meshes)
    static readonly Dictionary<string, Mesh?>     _cache     = new();
    static readonly Dictionary<string, Mesh?>     _hybridCache = new();
    // Material name -> Material (for materialSlots lookups)
    static readonly Dictionary<string, Material?> _matCache  = new();
    static readonly Dictionary<Transform, ComputeBuffer>                                _globalLampBuffers = new();
    static readonly List<(MeshRenderer mr, int slots)>                              _fixers     = new();
    static readonly List<(MeshRenderer mr, string[] names)>                         _matSlots   = new();
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

    public static void Apply(Transform vehicleRoot)
    {
        var def = GetDefForVehicle(vehicleRoot);
        if (def == null) return;

        var markerGo = FindInHierarchy(vehicleRoot, def.VehicleMarker);
        if (markerGo == null) return;

        foreach (var entry in def.MeshReplacements)
        {
            if (ShouldSkipMeshReplacement(def, entry))
            {
                Plugin.L.LogInfo($"[EXP] Skip mesh replacement for '{def.Id}' target='{entry.Target}' isBody={entry.IsBody}");
                continue;
            }

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

            if (TryApplyPickupOverlayHybrid(def, entry, targetGo, mesh))
                continue;

            var mf = targetGo.GetComponent<MeshFilter>();
            if (mf != null)
                mesh = MaybeBuildHybridBodyMesh(def, entry, targetGo, mf.sharedMesh, mesh) ?? mesh;

            if (mf != null && mf.sharedMesh != mesh)
            {
                Plugin.L.LogInfo($"[MESH] '{entry.Target}': '{mf.sharedMesh?.name}' -> '{mesh.name}'");
                mf.sharedMesh = mesh;
            }
            if (mf != null)
                ApplyBodyMeshExperiments(def, entry, targetGo, mf.sharedMesh);

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
        ApplyLampShaderExperiments(vehicleRoot, def);
        LogVehicleState(vehicleRoot, def);
        VehiclePatcher.Apply(vehicleRoot, def);

        if (def.KeepGameLampMesh)
        {
            // Re-run the bake AFTER VehiclePatcher so the lamp index channel is computed
            // from the PATCHED lamp positions (the first bake above used pre-patch ones).
            RebuildVehicleBodyLampMeshData(vehicleRoot);
            DumpBodyLampUVs(def, markerGo, "[UV2/REBAKED]");
        }

        // lampPositionsBuffer (VehicleBody+0x180) is a private ComputeBuffer that is NOT copied by
        // Instantiate. It was created in RuntimeInit→InitPrefab→InitLamps on the runtimePrefab, so
        // it's null on every spawned instance.  Calling InitLamps() here recreates it from the
        // current vb.lamps[] (already patched by VehiclePatcher) and presumably calls
        // SetBuffer("_LampsPositions", ...) on the front/rear lamp materials — enabling mesh emission.
        ReinitLampPositionsBuffer(vehicleRoot);

        if (def.VehicleBody?.Lamps != null)
        {
            TrackLampVehicle(vehicleRoot);
            UploadLampPositionsBuffer(vehicleRoot, fullBind: true);
        }
    }

    static bool ShouldSkipMeshReplacement(CustomVehicleDef def, MeshEntry entry)
    {
        if (!ExperimentSkipPickupBodyMeshSwap) return false;
        if (def.Id != "body-caro-pickup") return false;
        if (!entry.IsBody) return false;
        return entry.Target == def.VehicleMarker;
    }

    static bool TryApplyPickupOverlayHybrid(CustomVehicleDef def, MeshEntry entry, GameObject targetGo, Mesh replacementMesh)
    {
        if (!ExperimentPickupOverlayHybrid) return false;
        if (def.Id != "body-caro-pickup") return false;
        if (!entry.IsBody) return false;
        if (entry.Target != def.VehicleMarker) return false;

        try
        {
            var baseMr = targetGo.GetComponent<MeshRenderer>();
            var baseMf = targetGo.GetComponent<MeshFilter>();
            if (baseMr == null || baseMf == null || baseMf.sharedMesh == null) return false;

            var baseMats = baseMr.sharedMaterials;
            if (baseMats == null || baseMats.Length < 5) return false;

            var voidMat = baseMats[2];
            if (voidMat == null) return false;

            // Keep the original CaroModel as the lamp carrier and hide body/window geometry on it.
            var carrierMats = new Material[baseMats.Length];
            for (int i = 0; i < baseMats.Length; i++)
                carrierMats[i] = i <= 2 ? voidMat : baseMats[i];
            baseMr.sharedMaterials = carrierMats;

            var parent = targetGo.transform.parent;
            if (parent == null) return false;

            const string overlayName = "MR_PickupBodyOverlay";
            var overlayGo = FindDirectChild(parent, overlayName);
            if (overlayGo == null)
            {
                overlayGo = new GameObject(overlayName);
                overlayGo.transform.SetParent(parent, false);
                Plugin.L.LogInfo($"[EXP] Created overlay GO '{overlayName}'");
            }

            overlayGo.transform.localPosition = targetGo.transform.localPosition;
            overlayGo.transform.localRotation = targetGo.transform.localRotation;
            overlayGo.transform.localScale = targetGo.transform.localScale;

            var overlayMf = overlayGo.GetComponent<MeshFilter>() ?? overlayGo.AddComponent<MeshFilter>();
            var overlayMr = overlayGo.GetComponent<MeshRenderer>() ?? overlayGo.AddComponent<MeshRenderer>();
            overlayMf.sharedMesh = replacementMesh;

            var overlayMats = new Material[baseMats.Length];
            for (int i = 0; i < baseMats.Length; i++)
            {
                if (i == 3 || i == 4) overlayMats[i] = voidMat;
                else overlayMats[i] = baseMats[i];
            }
            overlayMr.sharedMaterials = overlayMats;
            overlayMr.enabled = true;
            overlayGo.SetActive(true);

            if (def.PaintMaskTextureName != null)
                RegisterPaintMask(overlayMr, def.PaintMaskTextureName);
            if (def.AlbedoTextureName != null)
                RegisterAlbedo(overlayMr, def.AlbedoTextureName, isBody: true);

            Plugin.L.LogInfo($"[EXP] Overlay hybrid active for '{def.Id}': original mesh keeps lamp slots, overlay mesh='{replacementMesh.name}' hides slots 3/4");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[EXP] Overlay hybrid failed for '{def.Id}': {ex.Message}");
            return false;
        }
    }

    static GameObject? FindDirectChild(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.gameObject.name == name) return child.gameObject;
        }
        return null;
    }

    static void ApplyLampShaderExperiments(Transform vehicleRoot, CustomVehicleDef def)
    {
        try
        {
            if (!ExperimentForcePickupRearBulbRadius &&
                !ExperimentForcePickupRearBulbDepth &&
                !ExperimentForcePickupRearBulbRadiusRear &&
                !ExperimentForcePickupRearClearCoatColor) return;
            if (def.Id != "body-caro-pickup") return;

            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            var rmats = vb?.rearlampsMaterials;
            if (rmats == null) return;

            for (int i = 0; i < rmats.Count; i++)
            {
                var mat = rmats[i];
                if (mat == null) continue;

                if (ExperimentForcePickupRearBulbRadius && mat.HasProperty("_BulbRadius"))
                {
                    mat.SetFloat("_BulbRadius", ForcedPickupRearBulbRadius);
                    Plugin.L.LogInfo($"[EXP] rear lamp material[{i}] _BulbRadius -> {ForcedPickupRearBulbRadius:F3}");
                }

                if (ExperimentForcePickupRearBulbDepth && mat.HasProperty("_BulbDepth"))
                {
                    mat.SetFloat("_BulbDepth", ForcedPickupRearBulbDepth);
                    Plugin.L.LogInfo($"[EXP] rear lamp material[{i}] _BulbDepth -> {ForcedPickupRearBulbDepth:F3}");
                }

                if (ExperimentForcePickupRearBulbRadiusRear && mat.HasProperty("_BulbRadiusRear"))
                {
                    mat.SetFloat("_BulbRadiusRear", ForcedPickupRearBulbRadiusRear);
                    Plugin.L.LogInfo($"[EXP] rear lamp material[{i}] _BulbRadiusRear -> {ForcedPickupRearBulbRadiusRear:F3}");
                }

                if (ExperimentForcePickupRearClearCoatColor && mat.HasProperty("_ClearCoatColor"))
                {
                    mat.SetColor("_ClearCoatColor", ForcedPickupRearClearCoatColor);
                    Plugin.L.LogInfo($"[EXP] rear lamp material[{i}] _ClearCoatColor -> {ForcedPickupRearClearCoatColor}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[EXP] ApplyLampShaderExperiments: {ex.Message}");
        }
    }

    static void ApplyBodyMeshExperiments(CustomVehicleDef def, MeshEntry entry, GameObject targetGo, Mesh? mesh)
    {
        try
        {
            if (!ExperimentOverridePickupBodyMeshBounds) return;
            if (def.Id != "body-caro-pickup") return;
            if (!entry.IsBody) return;
            if (entry.Target != def.VehicleMarker) return;
            if (mesh == null) return;

            var b = mesh.bounds;
            b.center = ExperimentPickupBoundsCenter;
            b.size = ExperimentPickupBoundsSize;
            mesh.bounds = b;
            Plugin.L.LogInfo($"[EXP] body mesh bounds -> center=({b.center.x:F3},{b.center.y:F3},{b.center.z:F3}) size=({b.size.x:F3},{b.size.y:F3},{b.size.z:F3}) on '{targetGo.name}' mesh='{mesh.name}'");
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[EXP] ApplyBodyMeshExperiments: {ex.Message}");
        }
    }

    static Vector3 GetLampBufferPositionOverride(CustomVehicleDef? def, int lampIndex, bool isFront, Vector3 fallback)
    {
        if (!ExperimentOverridePickupRearBufferPositions) return fallback;
        if (def == null || def.Id != "body-caro-pickup") return fallback;
        if (isFront) return fallback;

        if (lampIndex == 6) return ExperimentPickupRearBufLamp6;
        if (lampIndex == 7) return ExperimentPickupRearBufLamp7;
        return fallback;
    }

    static Mesh? MaybeBuildHybridBodyMesh(CustomVehicleDef def, MeshEntry entry, GameObject targetGo, Mesh? originalMesh, Mesh replacementMesh)
    {
        if (!ExperimentHybridPickupBodyMesh) return null;
        if (def.Id != "body-caro-pickup") return null;
        if (!entry.IsBody) return null;
        if (entry.Target != def.VehicleMarker) return null;
        if (originalMesh == null) return null;
        if (replacementMesh == null) return null;
        if (originalMesh.subMeshCount < 5 || replacementMesh.subMeshCount < 5) return null;

        string key = $"hybrid|{def.Id}|{entry.Target}";
        if (_hybridCache.TryGetValue(key, out var cached) && cached != null) return cached;

        if (originalMesh.name != null && originalMesh.name.Contains("_HybridLampCarrier"))
            return null;

        try
        {
            var combined = new CombineInstance[5];
            for (int i = 0; i < 5; i++)
            {
                bool keepOriginalLampSubmesh = i == 3 || i == 4;
                combined[i] = new CombineInstance
                {
                    mesh = keepOriginalLampSubmesh ? originalMesh : replacementMesh,
                    subMeshIndex = i,
                    transform = Matrix4x4.identity
                };
            }

            var hybrid = new Mesh();
            hybrid.name = $"{replacementMesh.name}_HybridLampCarrier";
            hybrid.indexFormat = replacementMesh.indexFormat;
            hybrid.CombineMeshes(combined, mergeSubMeshes: false, useMatrices: true, hasLightmapData: false);
            hybrid.RecalculateBounds();
            _hybridCache[key] = hybrid;

            Plugin.L.LogInfo($"[EXP] Built hybrid mesh for '{def.Id}' on '{targetGo.name}': body subs 0-2 from '{replacementMesh.name}', lamp subs 3-4 from '{originalMesh.name}'");
            return hybrid;
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[EXP] Hybrid mesh build failed for '{def.Id}': {ex.Message}");
            _hybridCache[key] = null;
            return null;
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

            IntPtr cbPtr = Marshal.ReadIntPtr(vb.Pointer + 0x180);
            Plugin.L.LogInfo($"[LPOS] InitLamps done: lampPositionsBuffer={(cbPtr == IntPtr.Zero ? "null" : $"0x{cbPtr:X}")}");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[LPOS] ReinitLampPositionsBuffer: {ex.Message}"); }
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
    // InitLamps (called in ReinitLampPositionsBuffer before this) fills VB+0x180 with
    // the correct local-space positions.  We also call GetData once (fullBind only) to
    // log exactly what the buffer contains so we can verify positions are correct.
    static unsafe void UploadLampPositionsBuffer(Transform vehicleRoot, bool fullBind)
    {
        try
        {
            var vb = vehicleRoot.GetComponentInChildren<Game.VehicleBody>(true);
            if (vb == null) return;

            IntPtr cbObjPtr = Marshal.ReadIntPtr(vb.Pointer + 0x180);
            if (cbObjPtr == IntPtr.Zero)
            {
                if (fullBind) Plugin.L.LogWarning("[LPOS] lampPositionsBuffer is null");
                return;
            }

            var lamps = vb.lamps;
            int n = lamps?.Count ?? 0;

            if (fullBind)
            {
                // Read back buffer contents via GetData to verify InitLamps filled it correctly.
                try
                {
                    IntPtr klass   = IL2CPP.il2cpp_object_get_class(cbObjPtr);
                    IntPtr getData = IL2CPP.il2cpp_class_get_method_from_name(klass, "GetData", 1);
                    if (getData != IntPtr.Zero)
                    {
                        // GetData into a float array (n*3 floats = n Vector3s with stride 12)
                        var readback = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(n * 3);
                        IntPtr exc2 = IntPtr.Zero;
                        void** args2 = stackalloc void*[1];
                        args2[0] = (void*)readback.Pointer;
                        IL2CPP.il2cpp_runtime_invoke(getData, cbObjPtr, args2, ref exc2);
                        if (exc2 == IntPtr.Zero)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                float x = readback[i * 3];
                                float y = readback[i * 3 + 1];
                                float z = readback[i * 3 + 2];
                                bool isFront = lamps?[i]?.isFront ?? true;
                                if (!isFront || i < 2)
                                    Plugin.L.LogInfo($"[LPOS/RB] buf[{i}] isFront={isFront} pos=({x:F3},{y:F3},{z:F3})");
                            }
                        }
                        else Plugin.L.LogWarning("[LPOS/RB] GetData threw");
                    }
                }
                catch (Exception ex) { Plugin.L.LogWarning($"[LPOS/RB] {ex.Message}"); }
            }

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

    static void UpdateGlobalLampPositions(Transform vehicleRoot, CustomVehicleDef? def, Game.VehicleLamp[] lamps, int n)
    {
        try
        {
            if (!_globalLampBuffers.TryGetValue(vehicleRoot, out var cb) || cb == null || cb.count != n || cb.stride != 12)
            {
                try { cb?.Release(); } catch { }
                cb = new ComputeBuffer(n, 12);
                _globalLampBuffers[vehicleRoot] = cb;
                Plugin.L.LogInfo($"[LPOS] Created global world-space lamp buffer count={n}");
            }

            var world = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                var lamp = lamps[i];
                if (lamp == null) continue;
                var local = GetLampBufferPositionOverride(def, i, lamp.isFront, lamp.bulbPosition.position);
                world[i] = vehicleRoot.TransformPoint(local);
                try
                {
                    if (!lamp.isFront)
                        Plugin.L.LogInfo($"[LPOS/G] rear lamp[{i}] world=({world[i].x:F3},{world[i].y:F3},{world[i].z:F3})");
                }
                catch { }
            }

            IntPtr klass  = IL2CPP.il2cpp_object_get_class(cb.Pointer);
            IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SetData", 1);
            if (method == IntPtr.Zero)
            {
                Plugin.L.LogWarning("[LPOS] Global ComputeBuffer.SetData(1) not found");
                return;
            }

            unsafe
            {
                IntPtr exc = IntPtr.Zero;
                void** args = stackalloc void*[1];
                args[0] = (void*)world.Pointer;
                IL2CPP.il2cpp_runtime_invoke(method, cb.Pointer, args, ref exc);
                if (exc != IntPtr.Zero)
                {
                    Plugin.L.LogWarning("[LPOS] Global ComputeBuffer.SetData threw exception");
                    return;
                }
            }

            Shader.SetGlobalBuffer(Shader.PropertyToID("_LampsPositions"), cb);
        }
        catch (Exception ex)
        {
            Plugin.L.LogWarning($"[LPOS] UpdateGlobalLampPositions: {ex.Message}");
        }
    }

    // Reads vlc.powersBuffer (GPUBuffer<float>) and calls material.SetBuffer("_LampsPowers", cb)
    // on every material currently in vb.frontLampsMaterials and vb.rearlampsMaterials.
    // Safe to call multiple times — SetBuffer is idempotent.
    // Call from VehicleBody.InitMaterials postfix (covers skin re-init) and from
    // VLC.InitPowerBuffer postfix (covers initial registration).
    public static unsafe void RebindLampPowersBuffer(Game.Vehicle vehicle)
    {
        try
        {
            var vlc = vehicle.lamps;
            if (vlc == null) return;

            // GPUBuffer<float> at vlc+0x40; null before VLC.Start → not yet ready
            IntPtr gpuBufPtr = Marshal.ReadIntPtr(vlc.Pointer + 0x40);
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
                Plugin.L.LogInfo($"[REBIND] _LampsPowers → {count} lamp material(s)");
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
