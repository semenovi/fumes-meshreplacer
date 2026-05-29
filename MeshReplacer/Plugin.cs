using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
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
        MeshReplacer.FixPaintMasks();
        MeshReplacer.FixAlbedos();

        // Periodic LensFlare scan — every 300 frames, only for our custom vehicle
        if (frame - _lastScanFrame > 300)
        {
            try
            {
                var def = VehicleFactory.GetDefForVehicle(__instance);
                if (def != null)
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
    static void Postfix(Game.Vehicle __instance) => MeshReplacer.Apply(__instance.transform);

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
