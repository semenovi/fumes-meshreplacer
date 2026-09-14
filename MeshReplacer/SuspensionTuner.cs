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

    const int AXIS_FIELDS_START = 0x10;
    const int AXIS_FIELDS_END   = 0x50;

    public static void DeepCloneAxes(IntPtr susp)
    {
        IntPtr oldAxes = Marshal.ReadIntPtr(susp + SUSP_AXES);
        if (oldAxes == IntPtr.Zero) return;
        long count = Marshal.ReadInt64(oldAxes + ARRAY_LENGTH);
        if (count <= 0) return;

        IntPtr firstAxis = Marshal.ReadIntPtr(oldAxes + ARRAY_DATA);
        if (firstAxis == IntPtr.Zero) return;
        IntPtr axisClass = IL2CPP.il2cpp_object_get_class(firstAxis);

        IntPtr newAxes = IL2CPP.il2cpp_array_new(axisClass, (ulong)count);
        for (int i = 0; i < count; i++)
        {
            IntPtr src = Marshal.ReadIntPtr(oldAxes + ARRAY_DATA + i * 8);
            if (src == IntPtr.Zero) continue;
            IntPtr fresh = IL2CPP.il2cpp_object_new(axisClass);
            for (int off = AXIS_FIELDS_START; off < AXIS_FIELDS_END; off += 8)
                Marshal.WriteInt64(fresh + off, Marshal.ReadInt64(src + off));
            Marshal.WriteIntPtr(newAxes + ARRAY_DATA + i * 8, fresh);
        }
        Marshal.WriteIntPtr(susp + SUSP_AXES, newAxes);
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

    public static void ApplyMass(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        float? want = def.Mass;
        if (!want.HasValue) return;
        try
        {
            var rb = vehicle.GetComponent<Rigidbody>();
            if (rb == null) { Plugin.L.LogWarning("[PERF] Rigidbody is null"); return; }
            float stock = rb.mass;
            rb.mass = want.Value;
            Plugin.L.LogInfo($"[PERF] Rigidbody.mass {stock:F1} -> {want.Value:F1} kg");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[PERF] ApplyMass: {e.Message}"); }
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

    const int SIM_SUSPENSION  = 0x68;   // CarSimulation.suspension
    const int SUSP_WHEELS     = 0x28;   // CarSuspension.wheels
    const int WHEEL_LOCALPOS  = 0x40;
    const int WHEEL_SHOCKMOUNT = 0x80;
    const int WHEEL_AXISLOCAL = 0xB0;
    const int WHEEL_RADIUS    = 0x1B8;
    const int WHEEL_SPRINGDIST  = 0x130;  // maxSpringDistance
    const int WHEEL_SPRINGLIMIT = 0x140;  // springDistanceLimit

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
                // foreach (var w in live)
                // {
                //     if (Math.Abs(w.z - axleZ) > 0.2f) continue;
                //     if (cfg.SpringDistance.HasValue) WriteFloat(w.ptr + WHEEL_SPRINGDIST,  cfg.SpringDistance.Value);
                //     if (cfg.SpringLimit.HasValue)    WriteFloat(w.ptr + WHEEL_SPRINGLIMIT, cfg.SpringLimit.Value);
                // }
                Plugin.L.LogInfo($"[SUSP/live] axle[{cfg.Index}] at z={axleZ:F3} travel limits " +
                                 $"(NOT applied, disabled) springDistance={cfg.SpringDistance} springLimit={cfg.SpringLimit}");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[SUSP/live] {e.Message}"); }
    }

    const int WHEEL_SPRINGDIST_CUR = 0x144; // springDistance (current, recomputed live)
    const int WHEEL_SPRINGCOMP     = 0x14C; // springCompression
    const int WHEEL_ATTACHED       = 0x1B0; // bool

    class JitterWheel
    {
        public IntPtr ptr;
        public Transform? repTf;
        public float y, z, springDist, springComp;
        public bool attached;
        public bool seeded;
    }

    class JitterState
    {
        public List<JitterWheel> wheels = new();
        public int lastLogFrame = -1;
        public int linesThisSecond;
        public float lastSecondMark;
        public Vector3 rootPos, rootEuler;
        public bool rootSeeded;
        public Rigidbody? rb;
        public bool rbSearched;
    }

    static readonly Dictionary<IntPtr, JitterState> _jitterStates = new();
    const float EPS_POS = 0.0015f;    // ~1.5 mm
    const float EPS_SPRING = 0.0015f;
    const int MAX_LINES_PER_SEC = 40;

    const int SETTLE_GRACE_FRAMES = 120;
    static readonly Dictionary<IntPtr, int> _spawnFrame = new();

    public static void FixPreviewRigidbody(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        if (def.SuspensionTuning == null) return;
        try
        {
            string? name = vehicle.name;
            if (string.IsNullOrEmpty(name) || !name.Contains("(Clone)")) return; // never the driven 'PlayerVehicle'
            var rb = vehicle.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) return;

            if (!_spawnFrame.TryGetValue(vehicle.Pointer, out int spawnFrame))
            {
                spawnFrame = Time.frameCount;
                _spawnFrame[vehicle.Pointer] = spawnFrame;
            }
            if (Time.frameCount - spawnFrame < SETTLE_GRACE_FRAMES) return; // let it fall and settle first

            if (rb.velocity != Vector3.zero || rb.angularVelocity != Vector3.zero)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        catch { }
    }

    public static void DiagnoseJitter(Game.Vehicle vehicle, CustomVehicleDef def)
    {
        if (def.SuspensionTuning == null) return;
        try
        {
            if (!_jitterStates.TryGetValue(vehicle.Pointer, out var state))
            {
                state = BuildJitterState(vehicle);
                if (state == null) return; // not ready yet (wheels not built), retry next frame
                _jitterStates[vehicle.Pointer] = state;
            }

            float now = Time.unscaledTime;
            if (now - state.lastSecondMark >= 1f) { state.lastSecondMark = now; state.linesThisSecond = 0; }

            var root = vehicle.transform;
            Vector3 pos = root.position;
            Vector3 euler = root.eulerAngles;
            if (!state.rootSeeded) { state.rootPos = pos; state.rootEuler = euler; state.rootSeeded = true; }
            else
            {
                float dPos = (pos - state.rootPos).magnitude;
                float dRotX = Mathf.DeltaAngle(state.rootEuler.x, euler.x);
                float dRotY = Mathf.DeltaAngle(state.rootEuler.y, euler.y);
                float dRotZ = Mathf.DeltaAngle(state.rootEuler.z, euler.z);
                float dRot = Mathf.Abs(dRotX) + Mathf.Abs(dRotY) + Mathf.Abs(dRotZ);
                if ((dPos > EPS_POS || dRot > 0.05f) && state.linesThisSecond < MAX_LINES_PER_SEC)
                {
                    state.linesThisSecond++;

                    if (!state.rbSearched) { state.rb = vehicle.GetComponent<Rigidbody>(); state.rbSearched = true; }
                    string rbInfo = "no-rigidbody";
                    if (state.rb != null)
                    {
                        var rb = state.rb;
                        rbInfo = $"rb.isKinematic={rb.isKinematic} rb.vel={rb.velocity.ToString("F4")} " +
                                 $"rb.angVel={rb.angularVelocity.ToString("F4")} |angVel|={rb.angularVelocity.magnitude:F4}";
                    }

                    Plugin.L.LogInfo($"[JITTER/ROOT] frame={Time.frameCount} dt={Time.deltaTime * 1000f:F1}ms " +
                        $"pos {state.rootPos.ToString("F4")}->{pos.ToString("F4")} (d={dPos * 1000f:F2}mm) " +
                        $"euler {state.rootEuler.ToString("F3")}->{euler.ToString("F3")} (dRot={dRot:F3}deg) {rbInfo}");
                }
                state.rootPos = pos; state.rootEuler = euler;
            }

            foreach (var w in state.wheels)
            {
                if (w.ptr == IntPtr.Zero) continue;
                float y = ReadFloat(w.ptr + WHEEL_LOCALPOS + 4);
                float z = ReadFloat(w.ptr + WHEEL_LOCALPOS + 8);
                float sd = ReadFloat(w.ptr + WHEEL_SPRINGDIST_CUR);
                float sc = ReadFloat(w.ptr + WHEEL_SPRINGCOMP);
                bool attached = Marshal.ReadByte(w.ptr + WHEEL_ATTACHED) != 0;
                float tfY = w.repTf != null ? w.repTf.localPosition.y : float.NaN;

                if (!w.seeded)
                {
                    w.y = y; w.z = z; w.springDist = sd; w.springComp = sc; w.attached = attached;
                    w.seeded = true;
                    continue;
                }

                bool moved = Math.Abs(y - w.y) > EPS_POS || Math.Abs(z - w.z) > EPS_POS ||
                             Math.Abs(sd - w.springDist) > EPS_SPRING || Math.Abs(sc - w.springComp) > EPS_SPRING ||
                             attached != w.attached;

                if (moved && state.linesThisSecond < MAX_LINES_PER_SEC)
                {
                    state.linesThisSecond++;
                    Plugin.L.LogInfo($"[JITTER] frame={Time.frameCount} dt={Time.deltaTime * 1000f:F1}ms " +
                        $"wheel@{w.ptr.ToInt64():X} y={w.y:F4}->{y:F4} z={w.z:F4}->{z:F4} " +
                        $"springDist={w.springDist:F4}->{sd:F4} springComp={w.springComp:F4}->{sc:F4} " +
                        $"attached={w.attached}->{attached} repTf.y={tfY:F4}");
                }

                w.y = y; w.z = z; w.springDist = sd; w.springComp = sc; w.attached = attached;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[JITTER] {e.Message}"); }
    }

    static JitterState? BuildJitterState(Game.Vehicle vehicle)
    {
        IntPtr sim = Marshal.ReadIntPtr(vehicle.Pointer + VEH_SIMULATION);
        if (sim == IntPtr.Zero) return null;
        IntPtr susp = Marshal.ReadIntPtr(sim + SIM_SUSPENSION);
        if (susp == IntPtr.Zero) return null;
        IntPtr wheels = Marshal.ReadIntPtr(susp + SUSP_WHEELS);
        if (wheels == IntPtr.Zero) return null;
        long n = Marshal.ReadInt64(wheels + ARRAY_LENGTH);
        if (n <= 0) return null;

        var repTfs = new List<Transform>();
        var allComps = vehicle.transform.GetComponentsInChildren<Component>(true);
        if (allComps != null)
        {
            foreach (var c in allComps)
            {
                try
                {
                    if (c == null) continue;
                    IntPtr klass = IL2CPP.il2cpp_object_get_class(c.Pointer);
                    string typeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "";
                    if (typeName == "WheelRepresentation") repTfs.Add(c.transform);
                }
                catch { }
            }
        }

        var state = new JitterState();
        for (int i = 0; i < n; i++)
        {
            IntPtr w = Marshal.ReadIntPtr(wheels + ARRAY_DATA + i * 8);
            if (w == IntPtr.Zero) continue;
            float wz = ReadFloat(w + WHEEL_LOCALPOS + 8);

            Transform? best = null;
            float bestD = float.MaxValue;
            foreach (var tf in repTfs)
            {
                float tz = vehicle.transform.InverseTransformPoint(tf.position).z;
                float d = Math.Abs(tz - wz);
                if (d < bestD) { bestD = d; best = tf; }
            }

            state.wheels.Add(new JitterWheel { ptr = w, repTf = best });
        }
        Plugin.L.LogInfo($"[JITTER] tracking {state.wheels.Count} wheel(s) on '{vehicle.name}'");
        return state;
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
