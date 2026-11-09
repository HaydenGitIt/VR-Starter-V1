using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.HighDefinition;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

/// <summary>
/// URP -> HDRP migration for the PC VR copy of the project.
///
/// Tools > HDRP Migration:
///   1. Set Up Pipeline      - HDRP asset (APV on) for every quality level.
///   2. Convert Materials    - URP Lit / Simple Lit / Unlit / Particles -> HDRP Lit / Unlit.
///                             Metallic + occlusion maps are packed into HDRP mask maps.
///   3. Set Up Scenes        - for each classroom scene: physical light units,
///                             sky + exposure + SSAO volume, Adaptive Probe Volume,
///                             reflection probe, camera AA.
///   4. Bake Scenes          - lightmaps + APV.
///   5. Render Previews      - PNGs in Logs/ to check the look without a headset.
///
/// Everything can also run headless:
///   Unity.exe -batchmode -quit -projectPath ... -executeMethod HdrpMigration.RunAll
///   Unity.exe -batchmode -quit -projectPath ... -executeMethod HdrpMigration.BakeScenes
///
/// Safe to run again: converted materials are skipped, scene objects are reused.
/// </summary>
public static class HdrpMigration
{
    private const string Log = "[HdrpMigration]";
    private const string SettingsFolder = "Assets/Settings/HDRP";
    private const string HdrpAssetPath = SettingsFolder + "/HDRP VR.asset";
    private const string OldVolumeProfilePath = SettingsFolder + "/ParentDemo Volume.asset";
    private const string VolumeProfilePath = SettingsFolder + "/Classroom Volume.asset";
    private const string MaskMapFolder = "Assets/Materials/HDRP Mask Maps";

    // All three are the same Kindergarten classroom, so they share one look.
    private static readonly string[] ClassroomScenes =
    {
        "Assets/Scenes/ParentDemo.unity",
        "Assets/Scenes/Introduction.unity",
        "Assets/Scenes/Deescalation.unity",
    };

    // The original rig lit the room with four wide (151 deg) ceiling spots.
    // In ParentDemo these were swapped for area-light panels matching the
    // ceiling fittings; the other scenes get the same panels.
    private const float CeilingSpotMinAngle = 120f;
    private static readonly Vector2 CeilingPanelSize = new Vector2(0.47f, 0.09f);
    private const float CeilingPanelDrop = 0.08f;   // below the top of the room bounds

    // ---------------------------------------------------------------
    // LIGHT LEVELS
    // Physical units. Exposure is fixed (auto exposure "breathing" is
    // uncomfortable in a headset), set for a lamp-lit classroom.
    // Raise FixedExposure to darken everything, lower it to brighten.
    // Re-run "3. Set Up Scenes" after changing any of these.
    // ---------------------------------------------------------------
    private const float FixedExposureEV100 = 7.5f;
    private const float SunLux = 20000f;            // hazy daylight through the windows
    private const float CeilingPanelLumen = 5000f;  // the 0.47 x 0.09 m area-light panels
    private const float CeilingSpotCandela = 1200f; // ~4000 lm ceiling spot
    private const float PointLightLumen = 800f;     // a 60 W-equivalent lamp

    // Anything bigger than this (terrain, ground planes, sky domes) is ignored
    // when working out how big the room is.
    private const float MaxRoomPieceSize = 60f;

    [MenuItem("Tools/HDRP Migration/Run All (1-3)")]
    public static void RunAll()
    {
        SetUpPipeline();
        ConvertMaterials();
        SetUpScenes();
    }

    // ===============================================================
    // 1. PIPELINE
    // ===============================================================
    [MenuItem("Tools/HDRP Migration/1. Set Up Pipeline")]
    public static void SetUpPipeline()
    {
        Directory.CreateDirectory(SettingsFolder);

        var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(HdrpAssetPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            AssetDatabase.CreateAsset(asset, HdrpAssetPath);
        }

        var settings = asset.currentPlatformRenderPipelineSettings;
        settings.lightProbeSystem = RenderPipelineSettings.LightProbeSystem.AdaptiveProbeVolumes;
        settings.supportSSAO = true;
        settings.supportSSR = true;
        settings.supportSSGI = false;         // too expensive at VR resolution
        settings.supportRayTracing = false;
        settings.xrSettings.singlePass = true;
        asset.currentPlatformRenderPipelineSettings = settings;
        EditorUtility.SetDirty(asset);

        GraphicsSettings.defaultRenderPipeline = asset;

        // Point every quality level at the HDRP asset (they all held URP assets).
        var quality = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        var levels = quality.FindProperty("m_QualitySettings");
        for (int i = 0; i < levels.arraySize; i++)
            levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline").objectReferenceValue = asset;
        quality.ApplyModifiedProperties();

        PlayerSettings.colorSpace = ColorSpace.Linear;
        AssetDatabase.SaveAssets();
        Debug.Log($"{Log} HDRP asset '{HdrpAssetPath}' assigned to Graphics + {levels.arraySize} quality levels");
    }

    // ===============================================================
    // 2. MATERIALS
    // ===============================================================
    [MenuItem("Tools/HDRP Migration/2. Convert Materials")]
    public static void ConvertMaterials()
    {
        Shader hdLit = Shader.Find("HDRP/Lit");
        Shader hdUnlit = Shader.Find("HDRP/Unlit");
        Shader hdTerrain = Shader.Find("HDRP/TerrainLit");
        if (hdLit == null || hdUnlit == null)
        {
            Debug.LogError($"{Log} HDRP shaders not found - is the HDRP package installed?");
            return;
        }

        int lit = 0, unlit = 0, fallback = 0, skipped = 0;
        var notes = new List<string>();

        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".mat")) continue;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null) continue;

            string shaderName = mat.shader.name;
            if (shaderName.StartsWith("HDRP/") || shaderName.StartsWith("Shader Graphs/HDRP")) { skipped++; continue; }

            switch (shaderName)
            {
                case "Universal Render Pipeline/Lit":
                case "Universal Render Pipeline/Complex Lit":
                case "Universal Render Pipeline/Simple Lit":
                case "Universal Render Pipeline/Baked Lit":
                    ConvertLit(mat, hdLit, notes);
                    lit++;
                    break;

                case "Universal Render Pipeline/Unlit":
                case "Universal Render Pipeline/Particles/Unlit":
                case "Universal Render Pipeline/Particles/Simple Lit":
                case "Universal Render Pipeline/Particles/Lit":
                    ConvertUnlit(mat, hdUnlit);
                    if (shaderName.Contains("Particles")) notes.Add($"{path}: particle material -> HDRP/Unlit (vertex colour is not applied)");
                    unlit++;
                    break;

                case "Universal Render Pipeline/Terrain/Lit":
                    if (hdTerrain != null) { mat.shader = hdTerrain; HDShaderUtils.ResetMaterialKeywords(mat); lit++; }
                    break;

                default:
                    if (IsUrpOnlyShader(mat.shader))
                    {
                        ConvertFallback(mat, hdLit, hdUnlit);
                        notes.Add($"{path}: '{shaderName}' has no HDRP pass -> replaced with a plain HDRP material, check it by eye");
                        fallback++;
                    }
                    else skipped++;   // TMP, UI, skybox, untagged shaders: HDRP draws these as-is
                    break;
            }

            EditorUtility.SetDirty(mat);
        }

        AssetDatabase.SaveAssets();
        foreach (var n in notes) Debug.LogWarning($"{Log} {n}");
        Debug.Log($"{Log} Materials: {lit} lit, {unlit} unlit, {fallback} fallback, {skipped} left as-is");
    }

    private static void ConvertLit(Material mat, Shader hdLit, List<string> notes)
    {
        // Read everything before the shader swap - URP and HDRP names differ.
        Color baseColor = GetColor(mat, "_BaseColor", Color.white);
        Texture baseMap = GetTex(mat, "_BaseMap");
        Vector2 tiling = mat.HasProperty("_BaseMap") ? mat.GetTextureScale("_BaseMap") : Vector2.one;
        Vector2 offset = mat.HasProperty("_BaseMap") ? mat.GetTextureOffset("_BaseMap") : Vector2.zero;
        Texture normal = GetTex(mat, "_BumpMap");
        float normalScale = GetFloat(mat, "_BumpScale", 1f);
        float metallic = GetFloat(mat, "_Metallic", 0f);
        float smoothness = GetFloat(mat, "_Smoothness", 0.5f);
        Texture metalMap = GetTex(mat, "_MetallicGlossMap");
        Texture occlusion = GetTex(mat, "_OcclusionMap");
        float occlusionStrength = GetFloat(mat, "_OcclusionStrength", 1f);
        bool smoothFromAlbedo = GetFloat(mat, "_SmoothnessTextureChannel", 0f) > 0.5f;
        bool emissive = mat.IsKeywordEnabled("_EMISSION");
        Color emission = GetColor(mat, "_EmissionColor", Color.black);
        Texture emissionMap = GetTex(mat, "_EmissionMap");
        bool transparent = GetFloat(mat, "_Surface", 0f) > 0.5f;
        int urpBlend = Mathf.RoundToInt(GetFloat(mat, "_Blend", 0f));
        bool alphaClip = GetFloat(mat, "_AlphaClip", 0f) > 0.5f;
        float cutoff = GetFloat(mat, "_Cutoff", 0.5f);
        bool doubleSided = Mathf.RoundToInt(GetFloat(mat, "_Cull", 2f)) == 0;
        bool hasHeight = GetTex(mat, "_ParallaxMap") != null;
        bool specularWorkflow = mat.HasProperty("_WorkflowMode") && GetFloat(mat, "_WorkflowMode", 1f) < 0.5f;

        mat.shader = hdLit;

        mat.SetColor("_BaseColor", baseColor);
        mat.SetTexture("_BaseColorMap", baseMap);
        mat.SetTextureScale("_BaseColorMap", tiling);
        mat.SetTextureOffset("_BaseColorMap", offset);
        mat.SetTexture("_NormalMap", normal);
        mat.SetFloat("_NormalScale", normalScale);

        if (metalMap != null || occlusion != null || (smoothFromAlbedo && baseMap != null))
        {
            // URP keeps metal (R) + smoothness (A) and occlusion in separate maps;
            // HDRP wants one mask map: R metal, G occlusion, B detail, A smoothness.
            // The URP sliders are baked into the map so the remaps stay 0..1.
            mat.SetTexture("_MaskMap", PackMaskMap(mat, metalMap, metallic, occlusion, occlusionStrength,
                smoothFromAlbedo ? baseMap : metalMap, smoothness));
            mat.SetFloat("_MetallicRemapMin", 0f); mat.SetFloat("_MetallicRemapMax", 1f);
            mat.SetFloat("_SmoothnessRemapMin", 0f); mat.SetFloat("_SmoothnessRemapMax", 1f);
            mat.SetFloat("_AORemapMin", 0f); mat.SetFloat("_AORemapMax", 1f);
        }
        else
        {
            mat.SetTexture("_MaskMap", null);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
        }

        // Emission: exposure weight 0 = not scaled by exposure, so an emission
        // colour looks the same on screen as it did in URP.
        bool hasEmission = emissive && (emission.maxColorComponent > 0.001f || emissionMap != null);
        mat.SetColor("_EmissiveColor", hasEmission ? emission : Color.black);
        mat.SetColor("_EmissiveColorLDR", hasEmission ? emission : Color.black);
        mat.SetTexture("_EmissiveColorMap", hasEmission ? emissionMap : null);
        mat.SetFloat("_UseEmissiveIntensity", 0f);
        mat.SetFloat("_EmissiveExposureWeight", 0f);

        SetSurface(mat, transparent, urpBlend, alphaClip, cutoff, doubleSided);
        HDShaderUtils.ResetMaterialKeywords(mat);

        if (hasHeight) notes.Add($"{AssetDatabase.GetAssetPath(mat)}: height/parallax map not carried over");
        if (specularWorkflow) notes.Add($"{AssetDatabase.GetAssetPath(mat)}: URP specular workflow -> converted as metallic, check it by eye");
    }

    private static void ConvertUnlit(Material mat, Shader hdUnlit)
    {
        Color color = GetColor(mat, "_BaseColor", Color.white);
        Texture map = GetTex(mat, "_BaseMap");
        Vector2 tiling = mat.HasProperty("_BaseMap") ? mat.GetTextureScale("_BaseMap") : Vector2.one;
        Vector2 offset = mat.HasProperty("_BaseMap") ? mat.GetTextureOffset("_BaseMap") : Vector2.zero;
        bool transparent = GetFloat(mat, "_Surface", 0f) > 0.5f;
        int urpBlend = Mathf.RoundToInt(GetFloat(mat, "_Blend", 0f));
        bool alphaClip = GetFloat(mat, "_AlphaClip", 0f) > 0.5f;
        float cutoff = GetFloat(mat, "_Cutoff", 0.5f);
        bool doubleSided = Mathf.RoundToInt(GetFloat(mat, "_Cull", 2f)) == 0;

        mat.shader = hdUnlit;
        mat.SetColor("_UnlitColor", color);
        mat.SetTexture("_UnlitColorMap", map);
        mat.SetTextureScale("_UnlitColorMap", tiling);
        mat.SetTextureOffset("_UnlitColorMap", offset);
        mat.SetFloat("_EmissiveExposureWeight", 0f);
        SetSurface(mat, transparent, urpBlend, alphaClip, cutoff, doubleSided);
        HDShaderUtils.ResetMaterialKeywords(mat);
    }

    // URP-only shader graphs / hand-written shaders (XRI samples, VR template):
    // swap to a plain HDRP material carrying the main colour and texture.
    private static void ConvertFallback(Material mat, Shader hdLit, Shader hdUnlit)
    {
        Color color = FirstColor(mat, "_BaseColor", "_Color", "_MainColor", "_Tint");
        Texture tex = GetTex(mat, "_BaseMap") ?? GetTex(mat, "_MainTex");
        bool unlit = mat.shader.name.ToLowerInvariant().Contains("unlit")
                     || AssetDatabase.GetAssetPath(mat.shader).ToLowerInvariant().Contains("unlit");
        bool transparent = color.a < 0.999f || GetFloat(mat, "_Surface", 0f) > 0.5f;

        mat.shader = unlit ? hdUnlit : hdLit;
        mat.SetColor(unlit ? "_UnlitColor" : "_BaseColor", color);
        mat.SetTexture(unlit ? "_UnlitColorMap" : "_BaseColorMap", tex);
        SetSurface(mat, transparent, 0, false, 0.5f, false);
        HDShaderUtils.ResetMaterialKeywords(mat);
    }

    private static void SetSurface(Material mat, bool transparent, int urpBlend, bool alphaClip, float cutoff, bool doubleSided)
    {
        mat.SetFloat("_SurfaceType", transparent ? 1f : 0f);
        // URP blend: 0 Alpha, 1 Premultiply, 2 Additive, 3 Multiply.
        // HDRP blend: 0 Alpha, 1 Additive, 4 Premultiply (no Multiply).
        mat.SetFloat("_BlendMode", urpBlend == 1 ? 4f : urpBlend == 2 ? 1f : 0f);
        mat.SetFloat("_AlphaCutoffEnable", alphaClip ? 1f : 0f);
        mat.SetFloat("_AlphaCutoff", cutoff);
        mat.SetFloat("_DoubleSidedEnable", doubleSided ? 1f : 0f);
        mat.renderQueue = -1;   // let HDRP pick the queue from the surface type
    }

    private static bool IsUrpOnlyShader(Shader shader)
    {
        string path = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return shader.name.StartsWith("Universal Render Pipeline/");

        string text = File.ReadAllText(path);
        if (path.EndsWith(".shadergraph"))
            return !text.Contains("HighDefinition.ShaderGraph.HDTarget");
        return text.Contains("UniversalForward") || text.Contains("\"UniversalPipeline\"") || text.Contains("\"UniversalRenderPipeline\"");
    }

    // ---------------------------------------------------------------
    // MASK MAP PACKING
    // Textures are read back through the GPU so any import format works.
    // ---------------------------------------------------------------
    private static Texture2D PackMaskMap(Material mat, Texture metalMap, float metallic, Texture occlusion,
        float occlusionStrength, Texture smoothnessSource, float smoothness)
    {
        int size = new[] { metalMap, occlusion, smoothnessSource }
            .Where(t => t != null).Select(t => Mathf.Max(t.width, t.height)).DefaultIfEmpty(512).Max();
        size = Mathf.Clamp(size, 4, 2048);

        Color[] metal = metalMap != null ? ReadBack(metalMap, size) : null;
        Color[] ao = occlusion != null ? ReadBack(occlusion, size) : null;
        Color[] smooth = smoothnessSource != null ? ReadBack(smoothnessSource, size) : null;

        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            float m = metal != null ? metal[i].r : metallic;
            float o = ao != null ? Mathf.Lerp(1f, ao[i].g, occlusionStrength) : 1f;
            float s = smooth != null ? smooth[i].a * smoothness : smoothness;
            pixels[i] = new Color(m, o, 0f, s);
        }

        Directory.CreateDirectory(MaskMapFolder);
        string path = $"{MaskMapFolder}/{mat.name}_MaskMap.png";
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        tex.SetPixels(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.sRGBTexture = false;   // mask maps are data, not colour
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Color[] ReadBack(Texture source, int size)
    {
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(source, rt);
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        var pixels = tex.GetPixels();
        Object.DestroyImmediate(tex);
        return pixels;
    }

    // ===============================================================
    // 3-5. CLASSROOM SCENES
    // ===============================================================
    [MenuItem("Tools/HDRP Migration/3. Set Up Scenes")]
    public static void SetUpScenes()
    {
        foreach (var path in ClassroomScenes) SetUpScene(path);
    }

    [MenuItem("Tools/HDRP Migration/4. Bake Scenes")]
    public static void BakeScenes()
    {
        foreach (var path in ClassroomScenes) BakeScene(path);
    }

    // Batch mode: bake one scene per process (-bakeScene <path>).
    public static void BakeSceneFromCommandLine()
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-bakeScene");
        BakeScene(args[i + 1]);
    }

    // Batch mode: list the big room shells (walls / ceiling / floor) and whether they take part in the bake.
    public static void DiagnoseSceneFromCommandLine()
    {
        var args = System.Environment.GetCommandLineArgs();
        var scene = EditorSceneManager.OpenScene(args[System.Array.IndexOf(args, "-bakeScene") + 1], OpenSceneMode.Single);
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            var b = r.bounds;
            if (Mathf.Max(b.size.x, b.size.z) < 3f || b.center.y < -1f || b.center.y > 4f) continue;
            bool gi = GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.ContributeGI);
            Debug.Log($"{Log} DIAG {scene.name} | {GetPath(r.transform)} | static {gi} | lm {r.lightmapIndex} | rgi {r.receiveGI} | active {r.gameObject.activeInHierarchy} | " +
                      $"center {b.center} size {b.size} | {string.Join(",", r.sharedMaterials.Select(m => m ? m.name + "/" + m.shader.name : "null"))}");
        }
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            Debug.Log($"{Log} DIAG {scene.name} | LIGHT {GetPath(l.transform)} | {l.type} {l.lightmapBakeType} | pos {l.transform.position} | fwd {l.transform.forward} | " +
                      $"intensity {l.intensity} | indirect {l.bounceIntensity} | colour {l.color} | temp {l.useColorTemperature}/{l.colorTemperature} | enabled {l.enabled && l.gameObject.activeInHierarchy}");
    }

    private static string GetPath(Transform t) => t.parent ? GetPath(t.parent) + "/" + t.name : t.name;

    private static void BakeScene(string path)
    {
        // A Single-mode OpenScene can leave the active scene unloaded mid-open; HDBakedReflectionSystem.Tick
        // then throws on it every tick and Lightmapping.Bake waits forever. Keep a loaded scene active throughout.
        var placeholder = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        EditorSceneManager.CloseScene(placeholder, true);
        bool ok = Lightmapping.Bake();
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"{Log} {scene.name}: bake {(ok ? "finished" : "FAILED")}");
    }

    [MenuItem("Tools/HDRP Migration/5. Render Previews")]
    public static void RenderPreviews()
    {
        foreach (var path in ClassroomScenes) RenderPreview(path);
    }

    private static void SetUpScene(string path)
    {
        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

        Bounds room = FindRoomBounds();
        Debug.Log($"{Log} {scene.name}: room bounds center {room.center}, size {room.size}");

        AssignLightingSettings(scene.name);
        ConfigureLights(room);
        ConfigureCameras();

        Transform root = FindOrCreate("HDRP Lighting", null).transform;
        SetUpVolume(root);
        SetUpProbeVolume(root, room);
        SetUpReflectionProbe(root, room);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"{Log} {scene.name} set up and saved - bake next");
    }

    // Two views across the classroom, written to Logs/<Scene>_preview_a/b.png.
    // Framed from the ceiling lights, which sit inside the classroom (the
    // static-mesh bounds also take in the yard outside).
    private static void RenderPreview(string path)
    {
        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        Bounds room = FindRoomBounds();
        var ceiling = Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
            .Where(l => l.type == LightType.Rectangle || l.type == LightType.Spot)
            .Select(l => l.transform.position).ToList();
        Bounds inside = ceiling.Count > 0 ? new Bounds(ceiling[0], Vector3.zero) : room;
        foreach (var p in ceiling) inside.Encapsulate(p);
        inside.Expand(new Vector3(2f, 0f, 2f));   // lights sit ~1 m in from the walls

        float eye = room.min.y + 1.6f;
        Vector3 half = inside.extents;
        var views = new[]
        {
            (name: "a", from: new Vector3(inside.center.x - half.x, eye, inside.center.z - half.z), to: new Vector3(inside.center.x + half.x, eye - 0.6f, inside.center.z + half.z)),
            (name: "b", from: new Vector3(inside.center.x + half.x, eye, inside.center.z + half.z), to: new Vector3(inside.center.x - half.x, eye - 0.6f, inside.center.z - half.z)),
        };

        var go = new GameObject("Preview Camera");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 80f;
        cam.nearClipPlane = 0.05f;
        GetOrAdd<HDAdditionalCameraData>(go).antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;

        var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        cam.targetTexture = rt;
        Directory.CreateDirectory("Logs");

        foreach (var v in views)
        {
            go.transform.position = v.from;
            go.transform.LookAt(v.to);
            for (int i = 0; i < 8; i++) cam.Render();   // a few frames so SSAO / history settle

            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes($"Logs/{scene.name}_preview_{v.name}.png", tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(go);
        Debug.Log($"{Log} Preview written to Logs/{scene.name}_preview_a.png and _b.png");
    }

    private static void AssignLightingSettings(string sceneName)
    {
        // One asset per scene (a scene's lighting settings are its own), all
        // with the same values.
        string path = $"{SettingsFolder}/{sceneName}.lighting";
        var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
        if (settings == null)
        {
            settings = new LightingSettings { name = sceneName };
            AssetDatabase.CreateAsset(settings, path);
        }

        settings.bakedGI = true;
        settings.realtimeGI = false;
        settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
        // Baked Indirect: all direct light + shadows stay realtime (HDRP is
        // good at those), only the bounce is baked into lightmaps + APV.
        settings.mixedBakeMode = MixedLightingMode.IndirectOnly;

        settings.lightmapResolution = 40;
        settings.lightmapMaxSize = 2048;
        settings.lightmapPadding = 4;
        settings.lightmapCompression = LightmapCompression.HighQuality;
        settings.directionalityMode = LightmapsMode.CombinedDirectional;

        settings.maxBounces = 3;
        settings.directSampleCount = 32;
        settings.indirectSampleCount = 512;
        settings.environmentSampleCount = 512;

        settings.ao = true;
        settings.aoMaxDistance = 0.6f;
        settings.aoExponentIndirect = 1f;
        settings.aoExponentDirect = 0f;

        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        Lightmapping.lightingSettings = settings;
    }

    private static void ConfigureLights(Bounds room)
    {
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var hd = light.GetComponent<HDAdditionalLightData>();
            if (hd == null)
            {
                hd = light.gameObject.AddComponent<HDAdditionalLightData>();
                HDAdditionalLightData.InitDefaultHDAdditionalLightData(hd);
            }

            if (light.type == LightType.Spot && light.spotAngle >= CeilingSpotMinAngle)
            {
                // Ceiling fill spot -> area panel, as already done by hand in ParentDemo.
                // The spots point straight down, so the panel faces the floor.
                light.type = LightType.Rectangle;
                light.areaSize = CeilingPanelSize;

                // The spots sat above the ceiling mesh (fine for a 151 deg spot,
                // but a panel there lights the roof void). Hang it just under the
                // ceiling, where ParentDemo's hand-placed panels are.
                var p = light.transform.position;
                light.transform.position = new Vector3(p.x, room.max.y - CeilingPanelDrop, p.z);
            }

            light.lightmapBakeType = LightmapBakeType.Mixed;
            light.shadows = LightShadows.Soft;
            hd.useContactShadow.useOverride = true;
            hd.useContactShadow.@override = true;

            // HDRP stores directional intensity in lux, punctual in candela and
            // area in nits; lightUnit only picks what the Inspector shows.
            switch (light.type)
            {
                case LightType.Directional:
                    light.lightUnit = LightUnit.Lux;
                    light.intensity = SunLux;
                    break;
                case LightType.Rectangle:
                    light.lightUnit = LightUnit.Lumen;
                    light.intensity = LightUnitUtils.ConvertIntensity(light, CeilingPanelLumen, LightUnit.Lumen, LightUnit.Nits);
                    // Realtime area-light shadows are expensive at VR resolution;
                    // soft panels read fine with SSAO + contact shadows + baked bounce.
                    light.shadows = LightShadows.None;
                    break;
                case LightType.Spot:
                    light.lightUnit = LightUnit.Candela;
                    light.intensity = CeilingSpotCandela;
                    break;
                case LightType.Point:
                    light.lightUnit = LightUnit.Lumen;
                    light.intensity = PointLightLumen / (4f * Mathf.PI);
                    break;
            }

            EditorUtility.SetDirty(light);
            EditorUtility.SetDirty(hd);
            string unit = light.type == LightType.Directional ? "lux" : light.type == LightType.Rectangle ? "nits" : "cd";
            Debug.Log($"{Log} {light.name}: {light.type} {light.intensity:0.#} {unit} at y {light.transform.position.y:0.00}");
        }
    }

    private static void ConfigureCameras()
    {
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var hd = GetOrAdd<HDAdditionalCameraData>(cam.gameObject);
            // SMAA: sharp, no temporal ghosting on hands - TAA is the alternative
            // if specular shimmer bothers you.
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            EditorUtility.SetDirty(hd);
        }
    }

    private static void SetUpVolume(Transform root)
    {
        // The profile started life as ParentDemo-only; it is shared now.
        if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath) == null
            && AssetDatabase.LoadAssetAtPath<VolumeProfile>(OldVolumeProfilePath) != null)
            AssetDatabase.MoveAsset(OldVolumeProfilePath, VolumeProfilePath);

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, VolumeProfilePath);
        }

        // Re-applied on every run so changing the constants above takes effect.
        Override<VisualEnvironment>(profile).skyType.Override((int)SkyType.PhysicallyBased);
        Override<PhysicallyBasedSky>(profile);

        var exposure = Override<Exposure>(profile);
        exposure.mode.Override(ExposureMode.Fixed);
        exposure.fixedExposure.Override(FixedExposureEV100);

        Override<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);

        var ssao = Override<ScreenSpaceAmbientOcclusion>(profile);
        ssao.intensity.Override(1f);
        ssao.radius.Override(1f);

        var contact = Override<ContactShadows>(profile);
        contact.enable.Override(true);
        contact.length.Override(0.1f);

        Override<HDShadowSettings>(profile).maxShadowDistance.Override(30f);
        Override<ScreenSpaceReflection>(profile).enabled.Override(true);
        Override<Bloom>(profile).intensity.Override(0.15f);

        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        var go = FindOrCreate("Global Volume", root);
        var volume = GetOrAdd<Volume>(go);
        volume.isGlobal = true;
        volume.priority = 1f;
        volume.sharedProfile = profile;
        EditorUtility.SetDirty(volume);
    }

    private static Bounds FindRoomBounds()
    {
        var pieces = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
            .Where(r => GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.ContributeGI))
            .Select(r => r.bounds)
            .Where(b => b.size.x < MaxRoomPieceSize && b.size.y < MaxRoomPieceSize && b.size.z < MaxRoomPieceSize)
            .ToList();

        if (pieces.Count == 0)
        {
            Debug.LogWarning($"{Log} No static meshes found - using a 20 m box at the origin.");
            return new Bounds(Vector3.zero, Vector3.one * 20f);
        }

        Bounds room = pieces[0];
        foreach (var b in pieces) room.Encapsulate(b);
        return room;
    }

    private static void SetUpProbeVolume(Transform root, Bounds room)
    {
        var go = FindOrCreate("Adaptive Probe Volume", root);
        go.transform.SetPositionAndRotation(room.center, Quaternion.identity);
        var volume = GetOrAdd<ProbeVolume>(go);
        volume.mode = ProbeVolume.Mode.Local;
        volume.size = room.size + Vector3.one;   // half a metre of margin all round
        EditorUtility.SetDirty(volume);
    }

    private static void SetUpReflectionProbe(Transform root, Bounds room)
    {
        var go = FindOrCreate("Room Reflection Probe", root);
        go.transform.SetPositionAndRotation(room.center, Quaternion.identity);

        var probe = GetOrAdd<ReflectionProbe>(go);
        probe.mode = ReflectionProbeMode.Baked;
        var hd = GetOrAdd<HDAdditionalReflectionData>(go);
        hd.mode = ProbeSettings.Mode.Baked;
        hd.influenceVolume.shape = InfluenceShape.Box;
        hd.influenceVolume.boxSize = room.size;
        hd.influenceVolume.boxBlendDistancePositive = Vector3.one * 0.5f;
        hd.influenceVolume.boxBlendDistanceNegative = Vector3.one * 0.5f;
        EditorUtility.SetDirty(probe);
        EditorUtility.SetDirty(hd);
    }

    // ===============================================================
    // HELPERS
    // ===============================================================
    private static GameObject FindOrCreate(string name, Transform parent)
    {
        Transform existing = parent != null
            ? parent.Find(name)
            : EditorSceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == name)?.transform;
        if (existing != null) return existing.gameObject;

        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    private static T Override<T>(VolumeProfile profile) where T : VolumeComponent
    {
        return profile.TryGet(out T existing) ? existing : VolumeProfileFactory.CreateVolumeComponent<T>(profile);
    }

    // Not "GetComponent() ?? AddComponent()": in the editor a missing component
    // comes back as a fake-null object that ?? treats as real.
    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static Texture GetTex(Material m, string p) => m.HasProperty(p) ? m.GetTexture(p) : null;
    private static float GetFloat(Material m, string p, float fallback) => m.HasProperty(p) ? m.GetFloat(p) : fallback;
    private static Color GetColor(Material m, string p, Color fallback) => m.HasProperty(p) ? m.GetColor(p) : fallback;

    private static Color FirstColor(Material m, params string[] names)
    {
        foreach (var n in names)
            if (m.HasProperty(n)) return m.GetColor(n);
        return Color.white;
    }
}
