using System;
using System.IO;
using System.Text.Json;

public class MeshEntry
{
    public string    Bundle           { get; set; } = "";
    public string[]  Candidates       { get; set; } = Array.Empty<string>();
    public string    Target           { get; set; } = "";
    public bool      IsBody           { get; set; }
    public bool      FixMaterialSlots { get; set; }
    // Load mesh by name from game Resources instead of a bundle file.
    public string?   GameMesh         { get; set; }
    // Assign exact material slots by name (looked up from game Resources).
    public string[]? MaterialSlots    { get; set; }
    // Override the target GO's localPosition / localRotation (Euler XYZ) / localScale after mesh replacement.
    public float[]?  TargetPosition   { get; set; }
    public float[]?  TargetRotation   { get; set; }
    public float[]?  TargetScale      { get; set; }
    // Skip registering paint mask / albedo overrides for this entry.
    public bool      SkipTextures     { get; set; }
    // Skip only the paint mask override; albedo (_MainTex) is still applied.
    public bool      SkipPaintMask    { get; set; }
    // After materialSlots are applied, replace any front-lamp slot with VehicleBody.frontLampsMaterials[0]
    // so the panel shares the same material pointer registered in VehicleLampsController's GPUBuffer.
    public bool      SyncFrontLampSlot { get; set; }
}

public class EngineSwapDef
{
    public string  Id        { get; set; } = "";
    // Overrides physics: maxTorque written to CarEngine.maxTorque (+0x34)
    public float?  MaxTorque { get; set; }
    // Overrides physics: converted to rad/s, written to CarEngine.idleShaftSpeed (+0x30)
    public float?  IdleRPM   { get; set; }
    // Overrides physics: converted to rad/s, written to CarEngine.revLimiter.maxShaftSpeed (+0x28→+0x18)
    public float?  MaxRPM    { get; set; }
}

public class LampConfig
{
    public bool    Front { get; set; }
    public float[] Bulb  { get; set; } = new float[3];
}

public class ShaftConfig
{
    public float[]? Position { get; set; }
    public float[]? Scale    { get; set; }
}

public class HardpointPatch
{
    public int     Index    { get; set; }
    public float[] Position { get; set; } = new float[3];
}

public class VehicleBodyConfig
{
    public float[]?     GrillPosition         { get; set; }
    public float[]?     InteriorCameraPosition { get; set; }
    public float[]?     EnginePosition         { get; set; }
    public float[]?      FrontLampsColor         { get; set; }
    public float[][]?    CargoStrapsHooks        { get; set; }
    public LampConfig[]? Lamps                   { get; set; }
    public float[]?      FrontLampsLightPosition { get; set; }
    public ShaftConfig[]? FrontLampsShafts       { get; set; }
    public string?       RoofLampPartGo          { get; set; }
    public int[]?        RoofLampIndices         { get; set; }
    public string?       FrontLampBulbGoName     { get; set; }
    public float[]?      FrontLampBulbPosition   { get; set; }
    public float[]?      FrontLampBulbScale      { get; set; }
    public float[]?      LicensePlatePosition    { get; set; }
    public float[]?      LicensePlateRotation    { get; set; }
    public float[]?      LicensePlateScale       { get; set; }
}

public class CustomVehicleDef
{
    public string             Id               { get; set; } = "";
    public string             BaseBodyId       { get; set; } = "";
    public string             DisplayName      { get; set; } = "";
    public string             VehicleMarker    { get; set; } = "CaroModel";
    public MeshEntry[]        MeshReplacements { get; set; } = Array.Empty<MeshEntry>();
    public VehicleBodyConfig? VehicleBody      { get; set; }
    public float[]?           AntennaPosition  { get; set; }
    public HardpointPatch[]?  Hardpoints       { get; set; }

    public string? PaintMaskTextureName { get; set; }
    public string? AlbedoTextureName    { get; set; }

    // PNG files in the vehicle folder, used instead of the *TextureName lookups.
    // Needed when the albedo/paint mask are patched copies of a stock texture
    // (UV de-duplication moves islands and copies their pixels — see uv_dedup.py).
    public string? PaintMaskTextureFile { get; set; }
    public string? AlbedoTextureFile    { get; set; }

    // Keep the game's InitLampsMeshes-generated body mesh (with runtime-computed lamp
    // index channel) instead of reverting to the bundle mesh with hand-authored UV1.
    // Original game meshes ship WITHOUT UV1 — the lamp index data is generated at
    // runtime, so the generated mesh is the authoritative source of lamp indexing.
    public bool KeepGameLampMesh { get; set; }

    // DEBUG: every frame bind a constant all-ones _LampsPowers buffer to the rear lamp
    // materials. If the rear glass lights up, the power buffer binding is the problem;
    // if it stays dark, the lamp index path (TEXCOORD1) or shader inputs are at fault.
    public bool DebugForceRearPowers { get; set; }

    // Suspension type IDs (from ItemDatabase) that should be available in the garage for this body.
    // Each listed suspension is cloned and bound to this custom body type so the garage shows them.
    public string[]? AvailableSuspensions { get; set; }

    // Engine configurations to cycle through with F6.
    // Each entry specifies an engine ID (from ItemDatabase) and optional physics overrides.
    public EngineSwapDef[]? AvailableEngines { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FolderPath { get; set; } = "";
}

public class ModConfig
{
    public CustomVehicleDef[] CustomVehicles { get; set; } = Array.Empty<CustomVehicleDef>();

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    public static ModConfig Load(string pluginDir)
    {
        var modDir = Path.Combine(pluginDir, "meshreplacer");
        if (!Directory.Exists(modDir))
        {
            Plugin.L.LogWarning($"[CFG] Mod directory not found: {modDir}");
            return new ModConfig();
        }

        var defs = new System.Collections.Generic.List<CustomVehicleDef>();
        foreach (var subdir in Directory.GetDirectories(modDir))
        {
            var vehicleJson = Path.Combine(subdir, "vehicle.json");
            if (!File.Exists(vehicleJson)) continue;
            try
            {
                var def = JsonSerializer.Deserialize<CustomVehicleDef>(File.ReadAllText(vehicleJson), Opts);
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                def.FolderPath = subdir;
                defs.Add(def);
                Plugin.L.LogInfo($"[CFG] Loaded vehicle '{def.Id}' from '{Path.GetFileName(subdir)}'");
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"[CFG] Failed to load '{vehicleJson}': {e.Message}");
            }
        }

        var cfg = new ModConfig { CustomVehicles = defs.ToArray() };
        Plugin.L.LogInfo($"[CFG] Loaded {cfg.CustomVehicles.Length} custom vehicle(s)");
        return cfg;
    }
}
