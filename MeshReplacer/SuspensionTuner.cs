using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

// Grip and axle geometry live on SuspensionType, which the game shares between every car
// using that suspension. Patching it in place would retune stock vehicles too, so each
// tuned suspension is cloned once and the clone is bound to this vehicle's config item.
//
// This is also the layer the game reads when it builds the wheels, unlike writing live
// Wheel fields every frame (SuspensionType.axes[i] values are consumed at build time).
static class SuspensionTuner
{
    const int CONFIG_SUSPENSION = 0x48;  // VehicleConfig.suspension (SuspensionItem)
    const int ITEM_TYPE         = 0x10;  // Item<T>.Type

    const int SUSP_GRIP_LN   = 0x78;
    const int SUSP_GRIP_LT   = 0x7C;
    const int SUSP_FORCE_MUL = 0x88;
    const int SUSP_AXES      = 0x98;     // AxisDefinition[]

    const int AXIS_MOUNT_HEIGHT = 0x20;
    const int AXIS_HEIGHT       = 0x24;
    const int AXIS_WIDTH        = 0x28;  // track
    const int AXIS_POSITION     = 0x2C;  // along the car
    const int AXIS_STEERING     = 0x34;
    const int AXIS_WHEELRADIUS  = 0x3C;

    // il2cpp array: object header 0x10, bounds 0x10, length 0x18, data from 0x20
    const int ARRAY_LENGTH = 0x18;
    const int ARRAY_DATA   = 0x20;

    static readonly Dictionary<IntPtr, IntPtr> _clones = new();
    // keeps the cloned ScriptableObjects alive
    static readonly List<UnityEngine.Object> _keepAlive = new();

    public static void Apply(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        var tuning = def.SuspensionTuning;
        if (tuning == null) return;

        try
        {
            var cfg = vehicle.config;
            if (cfg == null) { Plugin.L.LogWarning("[SUSP] config is null, skipping"); return; }

            IntPtr itemPtr = Marshal.ReadIntPtr(cfg.Pointer + CONFIG_SUSPENSION);
            if (itemPtr == IntPtr.Zero) { Plugin.L.LogWarning("[SUSP] config.suspension is null"); return; }
            IntPtr typePtr = Marshal.ReadIntPtr(itemPtr + ITEM_TYPE);
            if (typePtr == IntPtr.Zero) { Plugin.L.LogWarning("[SUSP] suspension Type is null"); return; }

            if (!_clones.TryGetValue(typePtr, out IntPtr clonePtr))
            {
                var original = new Game.SuspensionType(typePtr);
                var clone = UnityEngine.Object.Instantiate(original).Cast<Game.SuspensionType>();
                if (clone == null) { Plugin.L.LogWarning("[SUSP] Instantiate returned null"); return; }
                _keepAlive.Add(clone);
                clonePtr = clone.Pointer;
                PatchType(clonePtr, tuning, original.id ?? "?");
                _clones[typePtr] = clonePtr;
            }

            // NOTE: the clone is deliberately NOT written back into config.suspension.Type.
            // Doing that crashed the game on entering the garage. The UI resolves the fitted
            // item's Type through ItemDatabase, and a clone that was never registered there
            // has no entry. Geometry is applied to the live wheels instead (ApplyLive).
        }
        catch (Exception e) { Plugin.L.LogWarning($"[SUSP] Apply: {e.Message}"); }
    }

    public static void PatchType(IntPtr susp, SuspensionTuning tuning, string id)
    {
        float gripLN = ReadFloat(susp + SUSP_GRIP_LN);
        float gripLT = ReadFloat(susp + SUSP_GRIP_LT);
        Plugin.L.LogInfo($"[SUSP] clone of '{id}': stock gripLN={gripLN:F3} gripLT={gripLT:F3}");

        WriteFloat(susp + SUSP_GRIP_LN,   tuning.GripLN);
        WriteFloat(susp + SUSP_GRIP_LT,   tuning.GripLT);
        WriteFloat(susp + SUSP_FORCE_MUL, tuning.ForceMultiplier);

        IntPtr axes = Marshal.ReadIntPtr(susp + SUSP_AXES);
        if (axes == IntPtr.Zero) { Plugin.L.LogWarning("[SUSP] axes array is null"); return; }
        long count = Marshal.ReadInt64(axes + ARRAY_LENGTH);

        // axle 0 is the front-most axle, matching how the axles are ordered elsewhere
        var order = new List<(int idx, float pos)>();
        for (int i = 0; i < count; i++)
        {
            IntPtr a = Marshal.ReadIntPtr(axes + ARRAY_DATA + i * 8);
            if (a == IntPtr.Zero) continue;
            order.Add((i, ReadFloat(a + AXIS_POSITION)));
        }
        order.Sort((x, y) => y.pos.CompareTo(x.pos));

        for (int i = 0; i < order.Count; i++)
        {
            IntPtr a = Marshal.ReadIntPtr(axes + ARRAY_DATA + order[i].idx * 8);
            if (a == IntPtr.Zero) continue;
            Plugin.L.LogInfo($"[SUSP]   axle[{i}] stock width={ReadFloat(a + AXIS_WIDTH):F3} " +
                             $"position={ReadFloat(a + AXIS_POSITION):F3} height={ReadFloat(a + AXIS_HEIGHT):F3} " +
                             $"mountHeight={ReadFloat(a + AXIS_MOUNT_HEIGHT):F3} radius={ReadFloat(a + AXIS_WHEELRADIUS):F3} " +
                             $"steering={ReadFloat(a + AXIS_STEERING):F1}");
        }

        if (tuning.Axes == null || tuning.Axes.Length == 0) return;

        foreach (var patch in tuning.Axes)
        {
            if (patch.Index < 0 || patch.Index >= order.Count) continue;
            IntPtr a = Marshal.ReadIntPtr(axes + ARRAY_DATA + order[patch.Index].idx * 8);
            if (a == IntPtr.Zero) continue;

            Plugin.L.LogInfo($"[SUSP]   axle[{patch.Index}] stock width={ReadFloat(a + AXIS_WIDTH):F3} " +
                             $"position={ReadFloat(a + AXIS_POSITION):F3} height={ReadFloat(a + AXIS_HEIGHT):F3} " +
                             $"mountHeight={ReadFloat(a + AXIS_MOUNT_HEIGHT):F3} radius={ReadFloat(a + AXIS_WHEELRADIUS):F3}");

            WriteFloat(a + AXIS_WIDTH,        patch.Width);
            WriteFloat(a + AXIS_POSITION,     patch.Position);
            WriteFloat(a + AXIS_HEIGHT,       patch.Height);
            WriteFloat(a + AXIS_MOUNT_HEIGHT, patch.MountHeight);
            WriteFloat(a + AXIS_WHEELRADIUS,  patch.WheelRadius);
            WriteFloat(a + AXIS_STEERING,     patch.SteeringAngle);
        }
    }

    // CarBody.maxSpeed is not just a cap: GearCollection is built from it
    // (ctor(forwardCount, engine, suspension, maxSpeed, maxReverseSpeed)), so it sets the
    // whole gearing spread and therefore the real top speed.
    const int VEH_SIMULATION  = 0x68;   // Vehicle.simulation
    const int SIM_BODY        = 0x28;   // CarSimulation.body
    const int CARBODY_MAXSPEED = 0x5C;  // CarBody.maxSpeed

    public static void ApplyTopSpeed(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        float? want = def.MaxSpeedKph;
        if (!want.HasValue) return;
        try
        {
            IntPtr sim = Marshal.ReadIntPtr(vehicle.Pointer + VEH_SIMULATION);
            if (sim == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] simulation is null"); return; }
            IntPtr body = Marshal.ReadIntPtr(sim + SIM_BODY);
            if (body == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] CarBody is null"); return; }

            float stock = ReadFloat(body + CARBODY_MAXSPEED);
            float target = want.Value / 3.6f;            // km/h -> m/s
            Marshal.WriteInt32(body + CARBODY_MAXSPEED, BitConverter.SingleToInt32Bits(target));
            Plugin.L.LogInfo($"[PERF] CarBody.maxSpeed {stock:F2} -> {target:F2} " +
                             $"(stock reads as {stock * 3.6f:F0} km/h if m/s)");

            RebuildGears(sim, target);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[PERF] ApplyTopSpeed: {e.Message}"); }
    }

    const int SIM_TRANSMISSION = 0x60;  // CarSimulation.transmission
    const int SIM_ENGINE       = 0x30;  // CarSimulation.engine
    const int TRANS_GEARS      = 0x18;  // CarTransmission.gears
    const int GEARS_FWDCOUNT   = 0x38;  // GearCollection.forwardCount

    // The gears were already built in Awake against the stock maxSpeed, which is why the box
    // ran out at 4th. GearCollection takes maxSpeed in its ctor, so build a fresh one.
    static unsafe void RebuildGears(IntPtr sim, float maxSpeed)
    {
        try
        {
            IntPtr trans = Marshal.ReadIntPtr(sim + SIM_TRANSMISSION);
            if (trans == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] transmission is null"); return; }
            IntPtr gears = Marshal.ReadIntPtr(trans + TRANS_GEARS);
            if (gears == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] gears are null"); return; }

            int forwardCount = Marshal.ReadInt32(gears + GEARS_FWDCOUNT);
            IntPtr engine    = Marshal.ReadIntPtr(sim + SIM_ENGINE);
            IntPtr susp      = Marshal.ReadIntPtr(sim + SIM_SUSPENSION);
            if (engine == IntPtr.Zero || susp == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] engine/suspension null"); return; }

            IntPtr klass = IL2CPP.il2cpp_object_get_class(gears);
            IntPtr ctor  = IL2CPP.il2cpp_class_get_method_from_name(klass, ".ctor", 5);
            if (ctor == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] GearCollection ctor(5) not found"); return; }

            IntPtr fresh = IL2CPP.il2cpp_object_new(klass);
            if (fresh == IntPtr.Zero) { Plugin.L.LogWarning("[PERF] object_new failed"); return; }

            float maxReverse = maxSpeed * 0.25f;
            IntPtr* args = stackalloc IntPtr[5];
            args[0] = (IntPtr)(&forwardCount);
            args[1] = engine;
            args[2] = susp;
            args[3] = (IntPtr)(&maxSpeed);
            args[4] = (IntPtr)(&maxReverse);
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(ctor, fresh, (void**)args, ref exc);
            if (exc != IntPtr.Zero) { Plugin.L.LogWarning("[PERF] GearCollection ctor threw"); return; }

            Marshal.WriteIntPtr(trans + TRANS_GEARS, fresh);
            Plugin.L.LogInfo($"[PERF] gears rebuilt: {forwardCount} forward, maxSpeed={maxSpeed:F1} m/s ({maxSpeed * 3.6f:F0} km/h)");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[PERF] RebuildGears: {e.Message}"); }
    }

    // The wheels are already built by the time Vehicle.Start runs, and CarSuspension has no
    // re-init (only a ctor), so the type patch above only takes effect on later spawns.
    // Push the same geometry straight into the live Wheels, derived the way the definition
    // means it: hub sits at mountHeight - height, the strut mount at mountHeight.
    const int SIM_SUSPENSION  = 0x68;   // CarSimulation.suspension
    const int SUSP_WHEELS     = 0x28;   // CarSuspension.wheels
    const int WHEEL_LOCALPOS  = 0x40;
    const int WHEEL_SHOCKMOUNT = 0x80;
    const int WHEEL_AXISLOCAL = 0xB0;
    const int WHEEL_RADIUS    = 0x1B8;
    const int WHEEL_SPRINGDIST  = 0x130;  // maxSpringDistance
    const int WHEEL_SPRINGLIMIT = 0x140;  // springDistanceLimit

    // Wheel.localPosition is still all-zero during Vehicle.Start, so the axles cannot be told
    // apart yet. Grouping there put every wheel on axle 0. Retried from LateUpdate until the
    // positions are real, then applied once.
    static readonly HashSet<IntPtr> _livePatched = new();

    public static void TickLive(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        if (_livePatched.Contains(vehicle.Pointer)) return;
        ApplyLive(vehicle, def);
    }

    public static void ApplyLive(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        var axesCfg = def.SuspensionTuning?.Axes;
        if (axesCfg == null || axesCfg.Length == 0) return;

        try
        {
            IntPtr sim = Marshal.ReadIntPtr(vehicle.Pointer + VEH_SIMULATION);
            if (sim == IntPtr.Zero) return;
            IntPtr susp = Marshal.ReadIntPtr(sim + SIM_SUSPENSION);
            if (susp == IntPtr.Zero) { Plugin.L.LogWarning("[SUSP/live] CarSuspension is null"); return; }
            IntPtr wheels = Marshal.ReadIntPtr(susp + SUSP_WHEELS);
            if (wheels == IntPtr.Zero) { Plugin.L.LogWarning("[SUSP/live] wheels array is null"); return; }
            long n = Marshal.ReadInt64(wheels + ARRAY_LENGTH);

            // group the live wheels by axle, front-most first
            var live = new List<(IntPtr ptr, float x, float z)>();
            for (int i = 0; i < n; i++)
            {
                IntPtr w = Marshal.ReadIntPtr(wheels + ARRAY_DATA + i * 8);
                if (w == IntPtr.Zero) continue;
                live.Add((w, ReadFloat(w + WHEEL_LOCALPOS), ReadFloat(w + WHEEL_LOCALPOS + 8)));
            }
            var byZ = new List<float>();
            foreach (var w in live) if (!byZ.Exists(z => Math.Abs(z - w.z) < 0.2f)) byZ.Add(w.z);
            byZ.Sort((a, b) => b.CompareTo(a));

            // all wheels still report the same position: physics has not placed them yet
            if (byZ.Count < 2) return;
            _livePatched.Add(vehicle.Pointer);

            foreach (var cfg in axesCfg)
            {
                if (cfg.Index < 0 || cfg.Index >= byZ.Count) continue;
                float axleZ = byZ[cfg.Index];
                foreach (var w in live)
                {
                    if (Math.Abs(w.z - axleZ) > 0.2f) continue;
                    float sign = w.x >= 0 ? 1f : -1f;

                    // geometry is NOT written here any more: the solver recomputes wheel
                    // positions every step, so these writes only made the car float. Track,
                    // position and radius come from the registered tuned suspension instead.
                    // Travel limits: without these the spring lets the wheel ride up through
                    // the arch and poke out of the body on bumps.
                    if (cfg.SpringDistance.HasValue) WriteFloat(w.ptr + WHEEL_SPRINGDIST,  cfg.SpringDistance.Value);
                    if (cfg.SpringLimit.HasValue)    WriteFloat(w.ptr + WHEEL_SPRINGLIMIT, cfg.SpringLimit.Value);
                }
                Plugin.L.LogInfo($"[SUSP/live] axle[{cfg.Index}] at z={axleZ:F3} travel limits " +
                                 $"springDistance={cfg.SpringDistance} springLimit={cfg.SpringLimit}");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[SUSP/live] {e.Message}"); }
    }

    static void WriteFloat(IntPtr at, float value)
        => Marshal.WriteInt32(at, BitConverter.SingleToInt32Bits(value));

    static float ReadFloat(IntPtr at) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at));

    static void WriteFloat(IntPtr at, float? value)
    {
        if (!value.HasValue) return;
        Marshal.WriteInt32(at, BitConverter.SingleToInt32Bits(value.Value));
    }
}
