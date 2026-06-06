using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

[BepInPlugin("me.ivan.meshreplacer", "MeshReplacer", "3.0.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource L   = null!;
    public static ModConfig       Cfg = null!;
    public static string          Dir = "";

    public override void Load()
    {
        L   = Log;
        Dir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        Cfg = ModConfig.Load(Dir);
        new Harmony("me.ivan.meshreplacer").PatchAll();
        L.LogInfo("MeshReplacer loaded");
    }
}

// F3: cycle submesh->material mapping (diagnostic)
static class MaterialCycler
{
    public static int           Offset   = 0;
    public static MeshRenderer? Renderer;
    public static Material[]?   Saved;
    static int _lastFrame = -1;

    public static void Cycle()
    {
        int frame = Time.frameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;

        if (Renderer == null || Saved == null || Saved.Length == 0) return;

        int n = Saved.Length;
        Offset = (Offset + 1) % n;
        var rotated = new Material[n];
        for (int i = 0; i < n; i++)
            rotated[i] = Saved[(Offset + i) % n];
        Renderer.sharedMaterials = rotated;

        Plugin.L.LogInfo($"[CYCLE] offset={Offset}/{n - 1}");
        for (int i = 0; i < n; i++)
            Plugin.L.LogInfo($"  [{i}] <- saved[{(Offset + i) % n}] '{rotated[i]?.name}'");
    }
}

// Inject cloned BodyTypes after ItemDatabase loads so they appear in Body list.
[HarmonyPatch(typeof(Game.ItemDatabase), "RuntimeLoad")]
static class ItemDatabasePatch
{
    static void Postfix() => VehicleFactory.InjectBodies();
}

// Append custom clones to PlayableBodies so they appear in the garage Body list UI.
// PopulateBodies() uses ItemDatabase.PlayableBodies (backed by ItemDatabaseConfig.playableBodies
// — a serialized asset array that never includes our runtime clones).
[HarmonyPatch(typeof(Game.ItemDatabase), "get_PlayableBodies")]
static class PlayableBodiesPatch
{
    static void Postfix(ref Il2CppReferenceArray<Game.BodyType> __result)
    {
        var clones = VehicleFactory.GetClones();
        if (clones.Count == 0) return;
        int origLen = __result?.Length ?? 0;
        var combined = new Il2CppReferenceArray<Game.BodyType>(origLen + clones.Count);
        for (int i = 0; i < origLen; i++) combined[i] = __result[i];
        for (int i = 0; i < clones.Count; i++) combined[origLen + i] = clones[i];
        __result = combined;
        Plugin.L.LogInfo($"[VF] PlayableBodies: appended {clones.Count} clone(s), total={combined.Length}");
    }
}

// UIItemPicker.PopulateSuspensions filters by suspension.body == config.body.Type (pointer
// comparison in native code). For Body-list vehicles, config.body.Type = our cloneBodyType,
// so original suspensions (which have body = originalBodyType) would be filtered out.
//
// Fix: temporarily set suspension.body = cloneBodyType for the listed suspensions while
// PopulateSuspensions runs, then restore. No SuspensionType cloning needed — cloning is
// fundamentally broken because SuspensionType.RuntimeInit() modifies the shared axes[]
// array objects (zeroing axis.track at offset 0x48), corrupting the original.
[HarmonyPatch(typeof(Game.UIItemPicker), "PopulateSuspensions")]
static class PopulateSuspensionsPatch
{
    // Suspensions temporarily patched this frame, stored for Postfix restoration.
    static readonly List<(Game.SuspensionType susp, Game.BodyType orig)> _swapped = new();

    static void Prefix(Game.UIItemPicker __instance)
    {
        _swapped.Clear();
        try
        {
            // Get current VehicleConfig via private get_Config() — must use native invoke.
            IntPtr klass     = IL2CPP.il2cpp_object_get_class(__instance.Pointer);
            IntPtr getConfig = IL2CPP.il2cpp_class_get_method_from_name(klass, "get_Config", 0);
            if (getConfig == IntPtr.Zero) return;

            unsafe
            {
                IntPtr exc       = IntPtr.Zero;
                IntPtr configPtr = IL2CPP.il2cpp_runtime_invoke(getConfig, __instance.Pointer, null, ref exc);
                if (configPtr == IntPtr.Zero || exc != IntPtr.Zero) return;

                // VehicleConfig.body (BodyItem) at offset 0x38; BodyItem.Type at offset 0x10.
                IntPtr bodyItemPtr = Marshal.ReadIntPtr(configPtr + 0x38);
                if (bodyItemPtr == IntPtr.Zero) return;
                IntPtr bodyTypePtr = Marshal.ReadIntPtr(bodyItemPtr + 0x10);
                if (bodyTypePtr == IntPtr.Zero) return;

                var bodyType = new Game.BodyType(bodyTypePtr);
                var bodyId   = bodyType.id;
                if (bodyId == null) return;

                var bodyMap = VehicleFactory.GetBodyMap();
                if (!bodyMap.TryGetValue(bodyId, out var entry)) return;
                var (def, cloneBody, origBody) = entry;

                var suspensions = Game.ItemDatabase.Suspensions;

                if (def.AvailableSuspensions != null)
                {
                    // Explicit list: find each suspension by ID and swap regardless of its base body.
                    // This allows mixing suspensions from different vehicle types.
                    var byId = new Dictionary<string, Game.SuspensionType>();
                    for (int i = 0; i < suspensions.Count; i++)
                        try { var s = suspensions[i]; if (s?.id != null) byId[s.id] = s; } catch { }

                    foreach (var suspId in def.AvailableSuspensions)
                    {
                        try
                        {
                            if (!byId.TryGetValue(suspId, out var susp)) continue;
                            var suspBody = susp.body;
                            _swapped.Add((susp, suspBody));
                            susp.body = cloneBody;
                        }
                        catch { }
                    }
                }
                else
                {
                    // No list configured: show all suspensions for the base body type.
                    for (int i = 0; i < suspensions.Count; i++)
                    {
                        try
                        {
                            var susp = suspensions[i];
                            if (susp == null) continue;
                            var suspBody = susp.body;
                            if (suspBody == null || suspBody.Pointer != origBody.Pointer) continue;
                            _swapped.Add((susp, suspBody));
                            susp.body = cloneBody;
                        }
                        catch { }
                    }
                }
                Plugin.L.LogInfo($"[VF] PopulateSusp: swapped {_swapped.Count} suspension(s) for '{bodyId}'");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[VF] PopulateSusp Prefix: {e.Message}"); }
    }

    static void Postfix()
    {
        foreach (var (susp, orig) in _swapped)
            try { susp.body = orig; } catch { }
        _swapped.Clear();
    }
}

// Inject garage slots for custom vehicles before save is converted to runtime objects.
[HarmonyPatch(typeof(Save.PlayerSaveData), "ToPlayer")]
static class PlayerSaveDataPatch
{
    static void Prefix(Save.PlayerSaveData __instance)
        => VehicleFactory.InjectSaveConfigs(__instance);
}

[HarmonyPatch(typeof(Game.Vehicle), "LateUpdate")]
static class VehicleUpdatePatch
{
    static int _lastRunFrame  = -1;
    static int _lastF3Frame   = -1;
    static int _lastScanFrame = -1;
    static int _lastLampDump  = -9999;

    static void Postfix(Game.Vehicle __instance)
    {
        if (Input.GetKeyDown(KeyCode.F3))
        {
            int f = Time.frameCount;
            if (f != _lastF3Frame) { _lastF3Frame = f; MaterialCycler.Cycle(); }
        }

        int frame = Time.frameCount;
        if (frame == _lastRunFrame) return;
        _lastRunFrame = frame;

        MeshReplacer.FixMaterialSlots();
        MeshReplacer.FixMatSlots();
        MeshReplacer.FixPaintMasks();
        MeshReplacer.FixAlbedos();
        MeshReplacer.UpdateAllLampPositions();

        // Periodic LensFlare scan — every 300 frames, only for our custom vehicle
        if (frame - _lastScanFrame > 300)
        {
            try
            {
                if (LampDiag.ShouldTrace(__instance))
                {
                    _lastScanFrame = frame;
                    var root = __instance.transform;
                    var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                    if (renderers != null)
                        foreach (var r in renderers)
                        {
                            try
                            {
                                if (!r.gameObject.name.StartsWith("LensFlare")) continue;
                                var wp = r.transform.position;
                                var lp = r.transform.localPosition;
                                Plugin.L.LogInfo($"[LF-SCAN] GO='{r.gameObject.name}' world=({wp.x:F3},{wp.y:F3},{wp.z:F3}) local=({lp.x:F3},{lp.y:F3},{lp.z:F3}) active={r.gameObject.activeSelf} en={r.enabled}");
                            }
                            catch { }
                        }
                }
            }
            catch { }
        }

        // Periodic full lamp dump — every 900 frames for our vehicle; also once at frame 120 (after Start settled)
        if (frame - _lastLampDump > 900 || (frame == 120 && _lastLampDump < 0))
        {
            try
            {
                if (LampDiag.ShouldTrace(__instance))
                {
                    _lastLampDump = frame;
                    LampDiag.Dump(__instance, "LATE");
                }
            }
            catch { }
        }
    }
}

[HarmonyPatch(typeof(Game.Vehicle), "Awake")]
static class VehicleAwakePatch
{
    // Hardpoints must be patched before Awake runs — weapon slots are placed during Awake.
    static void Prefix(Game.Vehicle __instance)
    {
        try
        {
            var def = VehicleFactory.GetDefForVehicle(__instance);
            if (def == null) return;
            VehicleFactory.FixNullSkin(__instance);
            var bt = __instance.config?.body?.Type?.TryCast<Game.BodyType>();
            if (bt == null) return;
            VehicleFactory.PatchHardpoints(bt, def);
        }
        catch { }
    }

    static void Postfix(Game.Vehicle __instance)
    {
        MeshReplacer.Apply(__instance.transform);
        MeshReplacer.DiagnoseVehicle(__instance);
        if (LampDiag.ShouldTrace(__instance))
            LampDiag.Dump(__instance, "AWAKE");
    }
}

[HarmonyPatch(typeof(Game.Vehicle), "Start")]
static class VehicleStartPatch
{
    static void Prefix(Game.Vehicle __instance)
    {
        MeshReplacer.DiagnoseVehicle(__instance);
        DiagnoseForBodyList(__instance);
    }
    static void Postfix(Game.Vehicle __instance)
    {
        MeshReplacer.Apply(__instance.transform);
        if (LampDiag.ShouldTrace(__instance))
            LampDiag.Dump(__instance, "START");
    }

    static void DiagnoseForBodyList(Game.Vehicle v)
    {
        try
        {
            var cfg     = v.config;
            var bodyId  = cfg?.body?.Type?.id ?? "(null)";
            if (!bodyId.Contains("pickup")) return; // only log for our custom vehicle

            Plugin.L.LogInfo($"[BL-DIAG] --- Vehicle.Start for body='{bodyId}' ---");
            Plugin.L.LogInfo($"[BL-DIAG] simulation={v.simulation != null}");
            if (v.simulation != null)
            {
                Plugin.L.LogInfo($"[BL-DIAG] simulation.suspension={v.simulation.suspension != null}");
                if (v.simulation.suspension != null)
                {
                    var w = v.simulation.suspension.wheels;
                    Plugin.L.LogInfo($"[BL-DIAG] suspension.wheels={w != null}, count={w?.Length ?? -1}");
                    if (w != null)
                        for (int i = 0; i < w.Length; i++)
                            Plugin.L.LogInfo($"[BL-DIAG]   wheel[{i}]={w[i] != null}");
                }
            }
            Plugin.L.LogInfo($"[BL-DIAG] config={cfg != null}");
            Plugin.L.LogInfo($"[BL-DIAG] config.body.Type={cfg?.body?.Type != null}");
            var bt = cfg?.body?.Type?.TryCast<Game.BodyType>();
            Plugin.L.LogInfo($"[BL-DIAG] body.Type as BodyType={bt != null}, runtimePrefab={bt?.runtimePrefab != null}");
            Plugin.L.LogInfo($"[BL-DIAG] config.skin={cfg?.skin?.id ?? "(null)"}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[BL-DIAG] exception: {e.Message}"); }
    }
}

// ─── Lamp init chain patches ───────────────────────────────────────────────
// These fire for ALL vehicles but are cheap (GetDefForVehicle returns null fast for non-custom).

// VehicleBodyPart.InitMaterials() scans MeshRenderer.sharedMaterials and assigns
// bodyMaterial/frontLampsMaterial/rearlampsMaterial.  We log what's in the renderer
// BEFORE and the field values AFTER.
[HarmonyPatch(typeof(Game.VehicleBodyPart), "InitMaterials")]
static class VehicleBodyPartInitMaterialsPatch
{
    static void Prefix(Game.VehicleBodyPart __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            string id = "?";
            try { id = __instance.ID ?? "null"; } catch { }
            Plugin.L.LogInfo($"[VBP-INIT/PRE] id='{id}'");
            try
            {
                var mr = __instance.meshRenderer;
                if (mr == null) { Plugin.L.LogInfo($"[VBP-INIT/PRE]   meshRenderer=null"); return; }
                var mats = mr.sharedMaterials;
                if (mats == null) { Plugin.L.LogInfo($"[VBP-INIT/PRE]   sharedMaterials=null"); return; }
                Plugin.L.LogInfo($"[VBP-INIT/PRE]   sharedMaterials.Length={mats.Length}");
                for (int i = 0; i < mats.Length; i++)
                    Plugin.L.LogInfo($"[VBP-INIT/PRE]     slot[{i}]='{mats[i]?.name ?? "null"}'(0x{(mats[i] != null ? mats[i].Pointer.ToString("X") : "0")})");
            }
            catch (Exception e) { Plugin.L.LogInfo($"[VBP-INIT/PRE]   mr ERR: {e.Message}"); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[VBP-INIT] prefix ERR: {e.Message}"); }
    }

    static void Postfix(Game.VehicleBodyPart __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            string id = "?";
            try { id = __instance.ID ?? "null"; } catch { }
            string bodyMat  = LampDiag.ReadNativeMatName(__instance.Pointer + 0x80);
            string frontMat = LampDiag.ReadNativeMatName(__instance.Pointer + 0x90);
            string rearMat  = LampDiag.ReadNativeMatName(__instance.Pointer + 0x98);
            IntPtr bodyPtr  = LampDiag.SafeReadPtr(__instance.Pointer + 0x80);
            IntPtr frontPtr = LampDiag.SafeReadPtr(__instance.Pointer + 0x90);
            IntPtr rearPtr  = LampDiag.SafeReadPtr(__instance.Pointer + 0x98);
            Plugin.L.LogInfo($"[VBP-INIT/POST] id='{id}' body='{bodyMat}'(0x{bodyPtr:X}) front='{frontMat}'(0x{frontPtr:X}) rear='{rearMat}'(0x{rearPtr:X})");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[VBP-INIT] postfix ERR: {e.Message}"); }
    }
}

// VehicleBody.InitMaterials() (private) aggregates part material lists.
[HarmonyPatch(typeof(Game.VehicleBody), "InitMaterials")]
static class VehicleBodyInitMaterialsPatch
{
    static void Prefix(Game.VehicleBody __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            // Read vb.parts ptr via raw (typed access returns null per CLAUDE.md)
            IntPtr partsPtr = LampDiag.SafeReadPtr(__instance.Pointer + 0x80);
            Plugin.L.LogInfo($"[VB-INITMAT/PRE] vb.parts raw ptr=0x{partsPtr:X}");
        }
        catch { }
    }

    static void Postfix(Game.VehicleBody __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            // frontLampsMaterials is now freshly populated — apply lamp slot sync
            // before VehicleLampsController.Init() runs so panels are included in the GPUBuffer.
            MeshReplacer.SyncLampMaterialsForVehicle(v.transform);
            // If skin system re-ran InitMaterials after InitPowerBuffer already registered
            // old material instances, the new instances need _LampsPowers rebound.
            MeshReplacer.RebindLampPowersBuffer(v);
        }
        catch { }
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            try
            {
                var fmats = __instance.frontLampsMaterials;
                var rmats = __instance.rearlampsMaterials;
                Plugin.L.LogInfo($"[VB-INITMAT/POST] frontLampsMaterials={(fmats == null ? "null" : fmats.Count.ToString())} rearlampsMaterials={(rmats == null ? "null" : rmats.Count.ToString())}");
                if (fmats != null)
                    for (int i = 0; i < fmats.Count; i++)
                        try { Plugin.L.LogInfo($"[VB-INITMAT/POST]   fmat[{i}]='{fmats[i]?.name}'(0x{(fmats[i] != null ? fmats[i].Pointer.ToString("X") : "0")})"); } catch { }
                if (rmats != null)
                    for (int i = 0; i < rmats.Count; i++)
                        try { Plugin.L.LogInfo($"[VB-INITMAT/POST]   rmat[{i}]='{rmats[i]?.name}'(0x{(rmats[i] != null ? rmats[i].Pointer.ToString("X") : "0")})"); } catch { }
            }
            catch (Exception e) { Plugin.L.LogInfo($"[VB-INITMAT/POST] ERR: {e.Message}"); }
        }
        catch { }
    }
}

// VehicleBody.InitLamps() (private) assigns shaft/lensFlare references to vb.lamps[].
[HarmonyPatch(typeof(Game.VehicleBody), "InitLamps")]
static class VehicleBodyInitLampsPatch
{
    static void Prefix(Game.VehicleBody __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            Plugin.L.LogInfo($"[VB-INITLAMPS/PRE] called frame={Time.frameCount}");
        }
        catch { }
    }

    static void Postfix(Game.VehicleBody __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            try
            {
                var lamps = __instance.lamps;
                Plugin.L.LogInfo($"[VB-INITLAMPS/POST] lamps={(lamps == null ? "null" : lamps.Length.ToString())}");
                if (lamps != null)
                    for (int i = 0; i < lamps.Length; i++)
                    {
                        try
                        {
                            var lamp = lamps[i];
                            string shaftStr = "null";
                            try { var s = lamp.shaft; shaftStr = s == null ? "null" : $"'{s.gameObject.name}'"; } catch { }
                            string lfStr = "null";
                            try { var lf = lamp.lensFlare; lfStr = lf == null ? "null" : $"'{lf.gameObject.name}'"; } catch { }
                            Plugin.L.LogInfo($"[VB-INITLAMPS/POST]   lamp[{i}] isFront={lamp.isFront} shaft={shaftStr} lf={lfStr}");
                        }
                        catch { }
                    }
            }
            catch (Exception e) { Plugin.L.LogInfo($"[VB-INITLAMPS/POST] ERR: {e.Message}"); }
        }
        catch { }
    }
}

// VehicleLampsController.Init() (public) — the main lamp system init called from Start().
[HarmonyPatch(typeof(Game.VehicleLampsController), "Init")]
static class VehicleLampsControllerInitPatch
{
    static void Prefix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            Plugin.L.LogInfo($"[LC-INIT/PRE] === VehicleLampsController.Init() ===");
            LampDiag.Dump(v, "LC-PRE");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LC-INIT] prefix ERR: {e.Message}"); }
    }

    static void Postfix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            Plugin.L.LogInfo($"[LC-INIT/POST] === Init() complete ===");
            LampDiag.Dump(v, "LC-POST");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LC-INIT] postfix ERR: {e.Message}"); }
    }
}

// VehicleLampsController.InitPowerBuffer() (private) — creates GPUBuffer and registers materials.
[HarmonyPatch(typeof(Game.VehicleLampsController), "InitPowerBuffer")]
static class VehicleLampsControllerInitPowerBufferPatch
{
    static void Prefix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            try
            {
                var vb = v.body;
                var fmats = vb?.frontLampsMaterials;
                var rmats = vb?.rearlampsMaterials;
                Plugin.L.LogInfo($"[LC-POWBUF/PRE] frontLampsMaterials={(fmats == null ? "null" : fmats.Count.ToString())} rearlampsMaterials={(rmats == null ? "null" : rmats.Count.ToString())}");
                if (fmats != null)
                    for (int i = 0; i < fmats.Count; i++)
                        try { Plugin.L.LogInfo($"[LC-POWBUF/PRE]   fmat[{i}]='{fmats[i]?.name}'(0x{(fmats[i] != null ? fmats[i].Pointer.ToString("X") : "0")})"); } catch { }
                if (rmats != null)
                    for (int i = 0; i < rmats.Count; i++)
                        try { Plugin.L.LogInfo($"[LC-POWBUF/PRE]   rmat[{i}]='{rmats[i]?.name}'(0x{(rmats[i] != null ? rmats[i].Pointer.ToString("X") : "0")})"); } catch { }
            }
            catch (Exception e) { Plugin.L.LogInfo($"[LC-POWBUF/PRE] body access ERR: {e.Message}"); }
        }
        catch { }
    }

    static void Postfix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || VehicleFactory.GetDefForVehicle(v) == null) return;
            try
            {
                IntPtr bufPtr = Marshal.ReadIntPtr(__instance.Pointer + 0x40);
                Plugin.L.LogInfo($"[LC-POWBUF/POST] powersBuffer=0x{bufPtr:X}");
                if (bufPtr != IntPtr.Zero)
                {
                    IntPtr matsPtr = Marshal.ReadIntPtr(bufPtr + 0x28);
                    if (matsPtr != IntPtr.Zero)
                    {
                        int count = Marshal.ReadInt32(matsPtr + 0x18);
                        Plugin.L.LogInfo($"[LC-POWBUF/POST] materials.Count={count}");
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogInfo($"[LC-POWBUF/POST] ERR: {e.Message}"); }

            // Explicitly bind _LampsPowers to all current lamp materials.
            // InitPowerBuffer registers materials via GPUBuffer.Register, but if
            // rearlampsMaterials was populated with instances that differ from what
            // the renderer currently uses, those won't light up.  Force-rebind here.
            MeshReplacer.RebindLampPowersBuffer(v);
        }
        catch { }
    }
}

// VehicleLampsController.InitLamps() (private) — creates LensFlare instances.
[HarmonyPatch(typeof(Game.VehicleLampsController), "InitLamps")]
static class VehicleLampsControllerInitLampsPatch
{
    static void Prefix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            Plugin.L.LogInfo($"[LC-INITLAMPS/PRE] called");
        }
        catch { }
    }

    static void Postfix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            try
            {
                var vb = v.body;
                var lamps = vb?.lamps;
                Plugin.L.LogInfo($"[LC-INITLAMPS/POST] vb.lamps={(lamps == null ? "null" : lamps.Length.ToString())}");
                if (lamps != null)
                    for (int i = 0; i < lamps.Length; i++)
                    {
                        try
                        {
                            var lamp = lamps[i];
                            string lfStr = "null";
                            try { var lf = lamp.lensFlare; lfStr = lf == null ? "null" : $"'{lf.gameObject.name}' active={lf.gameObject.activeSelf}"; } catch { }
                            string shaftStr = "null";
                            try { var s = lamp.shaft; shaftStr = s == null ? "null" : $"'{s.gameObject.name}'"; } catch { }
                            Plugin.L.LogInfo($"[LC-INITLAMPS/POST]   lamp[{i}] isFront={lamp.isFront} lf={lfStr} shaft={shaftStr}");
                        }
                        catch { }
                    }
            }
            catch (Exception e) { Plugin.L.LogInfo($"[LC-INITLAMPS/POST] ERR: {e.Message}"); }
        }
        catch { }
    }
}

[HarmonyPatch(typeof(Game.VehicleLampsController), "UpdateLight")]
static class VehicleLampsControllerUpdateLightPatch
{
    static void Prefix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            LampDiag.LogLive(v, "UL-PRE");
        }
        catch { }
    }

    static void Postfix(Game.VehicleLampsController __instance)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            LampDiag.LogLive(v, "UL-POST");
        }
        catch { }
    }
}

[HarmonyPatch(typeof(Game.VehicleLampsController), "UpdateLamps")]
[HarmonyPatch(new Type[] { typeof(float) })]
static class VehicleLampsControllerUpdateLampsPatch
{
    static void Prefix(Game.VehicleLampsController __instance, float deltaTime)
    {
        try
        {
            var v = __instance.vehicle;
            if (v == null || !LampDiag.ShouldTrace(v)) return;
            if (Time.frameCount % 180 != 0) return;
            Plugin.L.LogInfo($"[LC-STEP] UpdateLamps dt={deltaTime:F4} frame={Time.frameCount}");
            LampDiag.LogLive(v, "ULAMPS", force: true);
        }
        catch { }
    }
}
