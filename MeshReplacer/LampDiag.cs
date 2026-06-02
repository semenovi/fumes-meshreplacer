using System;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

// Comprehensive lamp-system diagnostics.
// Call LampDiag.Dump(vehicle, "phase") from any patch; it filters to our custom vehicles.
//
// Offsets from dump.cs:
//   Vehicle.lamps (VehicleLampsController) = 0x70
//   Vehicle.body  (VehicleBody)            = 0xF0
//   VehicleLampsController.automaticController = 0x38
//   VehicleLampsController.powersBuffer        = 0x40
//   VehicleLampsController.frontPower          = 0x48
//   VehicleLampsController.rearPower           = 0x4C
//   VehicleBody.parts                  = 0x80
//   VehicleBody.lamps                  = 0x108
//   VehicleBody.frontLampsLight        = 0x110
//   VehicleBody.frontLampsShafts       = 0x118
//   VehicleBody.frontLampsMaterials    = 0x150
//   VehicleBody.rearlampsMaterials     = 0x158
//   VehicleBodyPart.meshRenderer       = 0x48
//   VehicleBodyPart.bodyMaterial       = 0x80
//   VehicleBodyPart.frontLampsMaterial = 0x90
//   VehicleBodyPart.rearlampsMaterial  = 0x98
//   VehicleLamp.isFront        = 0x10
//   VehicleLamp.part           = 0x18
//   VehicleLamp.bulbPosition   = 0x20
//   VehicleLamp.shaft          = 0x30
//   VehicleLamp.lensFlare      = 0x38
//   VehicleLamp.Power (backing)= 0x58
static class LampDiag
{
    public static void Dump(Game.Vehicle vehicle, string phase)
    {
        try
        {
            if (VehicleFactory.GetDefForVehicle(vehicle) == null) return;
            string tag = $"[LD/{phase}]";
            string bodyId = "(?)";
            try { bodyId = vehicle.config?.body?.Type?.id ?? "(null)"; } catch { }
            Plugin.L.LogInfo($"{tag} ========= vehicle body='{bodyId}' frame={Time.frameCount} =========");

            DumpAllRenderers(vehicle.transform, tag);
            DumpVehicleBody(vehicle, tag);
            DumpLampsController(vehicle, tag);
            DumpBodyParts(vehicle.transform, tag);

            Plugin.L.LogInfo($"{tag} ========= END =========");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LD/ERR] Dump({phase}): {e.Message}"); }
    }

    static void DumpAllRenderers(Transform root, string tag)
    {
        try
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null) { Plugin.L.LogInfo($"{tag} [MR] renderers=null"); return; }
            Plugin.L.LogInfo($"{tag} [MR] total={renderers.Length}");
            foreach (var r in renderers)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append($"{tag} [MR] '{r.gameObject.name}' act={r.gameObject.activeSelf} en={r.enabled} mats=[");
                    var mats = r.sharedMaterials;
                    if (mats != null)
                        for (int i = 0; i < mats.Length; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append($"'{mats[i]?.name ?? "null"}'{(mats[i] != null ? $"(0x{mats[i].Pointer:X})" : "")}");
                        }
                    sb.Append("]");
                    Plugin.L.LogInfo(sb.ToString());
                }
                catch { }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"{tag} [MR] ERR: {e.Message}"); }
    }

    static void DumpVehicleBody(Game.Vehicle vehicle, string tag)
    {
        try
        {
            var vb = vehicle.body;
            if (vb == null) { Plugin.L.LogInfo($"{tag} [VB] vehicle.body=NULL"); return; }
            Plugin.L.LogInfo($"{tag} [VB] ptr=0x{vb.Pointer:X}");

            try
            {
                IntPtr viewPtr = Marshal.ReadIntPtr(vb.Pointer + 0x78);
                Plugin.L.LogInfo($"{tag} [VB] view={(viewPtr == IntPtr.Zero ? "null" : $"0x{viewPtr:X}")}");
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB] view ERR: {e.Message}"); }

            try
            {
                var l = vb.frontLampsLight;
                Plugin.L.LogInfo($"{tag} [VB] frontLampsLight={(l == null ? "null" : $"pos={l.transform.localPosition} int={l.intensity:F3} en={l.enabled}")}");
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB] frontLampsLight ERR: {e.Message}"); }

            try
            {
                var shafts = vb.frontLampsShafts;
                Plugin.L.LogInfo($"{tag} [VB] frontLampsShafts={(shafts == null ? "null" : shafts.Length.ToString())}");
                if (shafts != null)
                    for (int i = 0; i < shafts.Length; i++)
                        try { Plugin.L.LogInfo($"{tag} [VB]   shaft[{i}]={(shafts[i] == null ? "null" : $"'{shafts[i].gameObject.name}' act={shafts[i].gameObject.activeSelf} en={shafts[i].enabled}")}"); } catch { }
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB] frontLampsShafts ERR: {e.Message}"); }

            try
            {
                var fmats = vb.frontLampsMaterials;
                if (fmats == null) Plugin.L.LogInfo($"{tag} [VB] frontLampsMaterials=null");
                else
                {
                    Plugin.L.LogInfo($"{tag} [VB] frontLampsMaterials count={fmats.Count}");
                    for (int i = 0; i < fmats.Count; i++)
                        try { Plugin.L.LogInfo($"{tag} [VB]   fmat[{i}]='{fmats[i]?.name}'(0x{(fmats[i] != null ? fmats[i].Pointer.ToString("X") : "0")})"); } catch { }
                }
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB] frontLampsMaterials ERR: {e.Message}"); }

            try
            {
                var rmats = vb.rearlampsMaterials;
                if (rmats == null) Plugin.L.LogInfo($"{tag} [VB] rearlampsMaterials=null");
                else
                {
                    Plugin.L.LogInfo($"{tag} [VB] rearlampsMaterials count={rmats.Count}");
                    for (int i = 0; i < rmats.Count; i++)
                        try { Plugin.L.LogInfo($"{tag} [VB]   rmat[{i}]='{rmats[i]?.name}'(0x{(rmats[i] != null ? rmats[i].Pointer.ToString("X") : "0")})"); } catch { }
                }
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB] rearlampsMaterials ERR: {e.Message}"); }

            try
            {
                var lamps = vb.lamps;
                if (lamps == null) Plugin.L.LogInfo($"{tag} [VB] vb.lamps=null");
                else
                {
                    Plugin.L.LogInfo($"{tag} [VB] vb.lamps count={lamps.Length}");
                    for (int i = 0; i < lamps.Length; i++)
                    {
                        try
                        {
                            var lamp = lamps[i];
                            string partStr = "null";
                            try
                            {
                                var p = lamp.part;
                                partStr = p == null ? "null" : $"ptr=0x{p.Pointer:X} id='{p.ID}'";
                            }
                            catch { }
                            string shaftStr = "null";
                            try { var s = lamp.shaft; shaftStr = s == null ? "null" : $"'{s.gameObject.name}'"; } catch { }
                            string lfStr = "null";
                            try { var lf = lamp.lensFlare; lfStr = lf == null ? "null" : $"'{lf.gameObject.name}' act={lf.gameObject.activeSelf} en={lf.enabled}"; } catch { }
                            float power = 0f;
                            try { power = lamp.Power; } catch { }
                            var bulb = Vector3.zero;
                            try { bulb = lamp.bulbPosition.position; } catch { }
                            Plugin.L.LogInfo($"{tag} [VB]   lamp[{i}] isFront={lamp.isFront} power={power:F3} bulb=({bulb.x:F3},{bulb.y:F3},{bulb.z:F3})");
                            Plugin.L.LogInfo($"{tag} [VB]         part={partStr} shaft={shaftStr} lf={lfStr}");
                        }
                        catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB]   lamp[{i}] ERR: {e.Message}"); }
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [VB] lamps ERR: {e.Message}"); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"{tag} [VB] ERR: {e.Message}"); }
    }

    static void DumpLampsController(Game.Vehicle vehicle, string tag)
    {
        try
        {
            var lc = vehicle.lamps;
            if (lc == null) { Plugin.L.LogInfo($"{tag} [LC] vehicle.lamps(controller)=null"); return; }
            Plugin.L.LogInfo($"{tag} [LC] ptr=0x{lc.Pointer:X}");

            try
            {
                unsafe
                {
                    float fp = *(float*)(lc.Pointer + 0x48);
                    float rp = *(float*)(lc.Pointer + 0x4C);
                    Plugin.L.LogInfo($"{tag} [LC] frontPower={fp:F3} rearPower={rp:F3}");
                }
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [LC] power ERR: {e.Message}"); }

            try
            {
                IntPtr autoPtr = Marshal.ReadIntPtr(lc.Pointer + 0x38);
                Plugin.L.LogInfo($"{tag} [LC] automaticController={(autoPtr == IntPtr.Zero ? "null" : $"0x{autoPtr:X}")}");
            }
            catch { }

            try
            {
                IntPtr bufPtr = Marshal.ReadIntPtr(lc.Pointer + 0x40);
                Plugin.L.LogInfo($"{tag} [LC] powersBuffer={(bufPtr == IntPtr.Zero ? "null" : $"0x{bufPtr:X}")}");
                if (bufPtr != IntPtr.Zero)
                {
                    // GPUBuffer<float>.materials at [bufPtr+0x28] per CLAUDE.md — count only (safe)
                    try
                    {
                        IntPtr matsListPtr = Marshal.ReadIntPtr(bufPtr + 0x28);
                        Plugin.L.LogInfo($"{tag} [LC] powersBuffer.materials ptr=0x{matsListPtr:X}");
                        if (matsListPtr != IntPtr.Zero)
                        {
                            int listSize = Marshal.ReadInt32(matsListPtr + 0x18);
                            Plugin.L.LogInfo($"{tag} [LC] powersBuffer.materials.Count={listSize}");
                        }
                    }
                    catch (Exception e) { Plugin.L.LogInfo($"{tag} [LC] bufMats ERR: {e.Message}"); }
                }
            }
            catch (Exception e) { Plugin.L.LogInfo($"{tag} [LC] powersBuffer ERR: {e.Message}"); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"{tag} [LC] ERR: {e.Message}"); }
    }

    static void DumpBodyParts(Transform root, string tag)
    {
        try
        {
            var parts = root.GetComponentsInChildren<Game.VehicleBodyPart>(true);
            if (parts == null) { Plugin.L.LogInfo($"{tag} [VBP] GetComponentsInChildren=null"); return; }
            Plugin.L.LogInfo($"{tag} [VBP] count={parts.Length}");
            foreach (var p in parts)
            {
                try
                {
                    string id = "?";
                    try { id = p.ID ?? "null"; } catch { }
                    bool canHide = false;
                    try { canHide = p.canBeHidden; } catch { }
                    string mrMats = "mr=null";
                    try
                    {
                        var mr = p.meshRenderer;
                        if (mr != null)
                        {
                            var sb = new StringBuilder("mr.mats=[");
                            var mats = mr.sharedMaterials;
                            if (mats != null)
                                for (int i = 0; i < mats.Length; i++)
                                {
                                    if (i > 0) sb.Append(", ");
                                    sb.Append($"'{mats[i]?.name ?? "null"}'{(mats[i] != null ? $"(0x{mats[i].Pointer:X})" : "")}");
                                }
                            sb.Append("]");
                            mrMats = sb.ToString();
                        }
                    }
                    catch { }

                    // Read native field pointers at dump.cs offsets
                    string bodyMat  = ReadNativeMatName(p.Pointer + 0x80);
                    string frontMat = ReadNativeMatName(p.Pointer + 0x90);
                    string rearMat  = ReadNativeMatName(p.Pointer + 0x98);
                    IntPtr bodyMatPtr  = SafeReadPtr(p.Pointer + 0x80);
                    IntPtr frontMatPtr = SafeReadPtr(p.Pointer + 0x90);
                    IntPtr rearMatPtr  = SafeReadPtr(p.Pointer + 0x98);

                    Plugin.L.LogInfo($"{tag} [VBP] id='{id}' canHide={canHide} {mrMats}");
                    Plugin.L.LogInfo($"{tag} [VBP]   bodyMat='{bodyMat}'(0x{bodyMatPtr:X}) frontLampMat='{frontMat}'(0x{frontMatPtr:X}) rearLampMat='{rearMat}'(0x{rearMatPtr:X})");
                }
                catch (Exception e) { Plugin.L.LogInfo($"{tag} [VBP] part ERR: {e.Message}"); }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"{tag} [VBP] ERR: {e.Message}"); }
    }

    // Reads a Material name from the native field pointer (address of a Material* field).
    public static string ReadNativeMatName(IntPtr fieldAddr)
    {
        try
        {
            IntPtr matPtr = Marshal.ReadIntPtr(fieldAddr);
            if (matPtr == IntPtr.Zero) return "(null)";
            return new Material(matPtr).name ?? "(unnamed)";
        }
        catch (Exception e) { return $"ERR:{e.Message}"; }
    }

    // Reads a native pointer at the given address, or IntPtr.Zero on failure.
    public static IntPtr SafeReadPtr(IntPtr addr)
    {
        try { return Marshal.ReadIntPtr(addr); }
        catch { return IntPtr.Zero; }
    }

    // Gets the name of any native IL2CPP object (via il2cpp_class_get_name on the object name field).
    // Falls back to raw pointer if name cannot be read.
    static string ReadNativeObjectName(IntPtr objPtr)
    {
        try { return new Material(objPtr).name ?? "(unnamed)"; }
        catch
        {
            // Generic fallback: try to invoke get_name
            try
            {
                IntPtr klass  = IL2CPP.il2cpp_object_get_class(objPtr);
                IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, "get_name", 0);
                if (method == IntPtr.Zero) return $"(no get_name 0x{objPtr:X})";
                unsafe
                {
                    IntPtr exc = IntPtr.Zero;
                    IntPtr nameStr = IL2CPP.il2cpp_runtime_invoke(method, objPtr, null, ref exc);
                    if (exc != IntPtr.Zero || nameStr == IntPtr.Zero) return $"(invoke-err 0x{objPtr:X})";
                    return IL2CPP.Il2CppStringToManaged(nameStr) ?? "(null str)";
                }
            }
            catch { return $"(err 0x{objPtr:X})"; }
        }
    }
}
