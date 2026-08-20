using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Draws a black quad directly in front of the player's eyes and fades it
/// in/out. This is how you fade to black in VR.
///
/// IMPORTANT: a normal Screen Space - Overlay UI Canvas does NOT render in
/// VR. The fade surface has to physically exist in the world, positioned in
/// front of the camera so it moves with the player's head.
///
/// This lives on the persistent Bootstrap object, so it survives scene loads.
/// Each scene has its own XR Rig (and therefore its own camera), so the
/// camera reference is refreshed every time a scene loads.
///
/// This script never starts a fade by itself - GameFlowManager drives all
/// fading, so two coroutines can never fight over the same alpha value.
/// </summary>
public class ScreenFader : MonoBehaviour
{
    [Header("Fade Surface")]
    [Tooltip("The Renderer of the quad/sphere used to block the view. Assign in Inspector.")]
    [SerializeField] private Renderer fadeRenderer;

    [Tooltip("How far in front of the camera the fade surface sits. Must be closer than the camera's near clip is far, but not so close it clips. 0.3 is usually safe.")]
    [SerializeField] private float distanceFromCamera = 0.3f;

    [Header("Startup")]
    [Tooltip("If true, the view begins fully black. Leave ON - GameFlowManager fades it back in once the first real scene has loaded.")]
    [SerializeField] private bool startBlack = true;

    // The material instance we animate. Using .material (not .sharedMaterial)
    // gives us a private copy, so we don't permanently edit the project asset.
    private Material fadeMaterial;

    // Current opacity: 0 = fully transparent (can see through), 1 = fully black.
    private float currentAlpha = 1f;

    // The camera we follow. Re-found after every scene load, because each
    // scene has its own XR Rig and therefore its own camera.
    private Camera targetCamera;

    // Which shader property holds the colour. URP shaders use "_BaseColor";
    // the older built-in pipeline uses "_Color". We detect it once at startup
    // rather than assuming, because guessing wrong means the fade silently
    // does nothing at all - no error, just a screen that never darkens.
    private int colorPropertyId;

    private void Awake()
    {
        if (fadeRenderer == null)
        {
            Debug.LogError("[ScreenFader] No fadeRenderer assigned. Fades will not work.");
            enabled = false;
            return;
        }

        fadeMaterial = fadeRenderer.material;

        if (fadeMaterial.HasProperty("_BaseColor"))
        {
            colorPropertyId = Shader.PropertyToID("_BaseColor");
        }
        else if (fadeMaterial.HasProperty("_Color"))
        {
            colorPropertyId = Shader.PropertyToID("_Color");
        }
        else
        {
            Debug.LogError($"[ScreenFader] The material '{fadeMaterial.name}' has no _BaseColor or _Color property. " +
                           "Use a URP/Unlit or Standard shader with Surface Type set to Transparent.");
            enabled = false;
            return;
        }

        // Begin fully black so the first scene never pops in unfaded.
        SetAlpha(startBlack ? 1f : 0f);

        // Re-find the camera whenever a scene finishes loading.
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        // Always unsubscribe from events, or Unity will try to call a
        // destroyed object and throw errors.
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        // Only find the camera. We deliberately do NOT start a fade here.
        // GameFlowManager owns all fading, so that two coroutines can never
        // write to the same alpha value on the same frame.
        AttachToCamera();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        AttachToCamera();
    }

    /// <summary>
    /// Finds the active camera in the current scene and caches it.
    ///
    /// NOTE: we deliberately do NOT parent the quad to the camera. The camera
    /// belongs to the scene, while this fader is persistent (DontDestroyOnLoad).
    /// Parenting a persistent object under a scene object makes it part of that
    /// scene again - so it would be destroyed on the next scene load, silently
    /// breaking every fade after the first. Instead we follow the camera every
    /// frame in LateUpdate.
    /// </summary>
    private void AttachToCamera()
    {
        targetCamera = Camera.main;

        if (targetCamera == null)
        {
            Debug.LogWarning("[ScreenFader] No camera tagged 'MainCamera' found in this scene. The fade cannot position itself.");
            return;
        }

        // Snap into place immediately so there is no visible jump on the
        // first frame of the new scene.
        PositionInFrontOfCamera();
    }

    /// <summary>
    /// Runs after all Update calls, which means the camera has already been
    /// moved by the XR tracking system this frame. Positioning here keeps the
    /// quad locked to the head rather than trailing a frame behind.
    /// </summary>
    private void LateUpdate()
    {
        // Skip the work entirely when fully transparent - nothing to show.
        if (currentAlpha <= 0.001f) return;

        PositionInFrontOfCamera();
    }

    private void PositionInFrontOfCamera()
    {
        if (targetCamera == null) return;

        Transform surface = fadeRenderer.transform;
        Transform camTransform = targetCamera.transform;

        // Sit directly ahead of the camera, facing back toward it.
        surface.position = camTransform.position + (camTransform.forward * distanceFromCamera);
        surface.rotation = camTransform.rotation;
    }

    // ---------------------------------------------------------------
    // PUBLIC FADE CALLS
    // These return IEnumerator so callers can 'yield return' them and
    // wait for the fade to actually finish before continuing.
    // ---------------------------------------------------------------

    /// <summary>Fade the view TO black.</summary>
    public IEnumerator FadeOut(float duration)
    {
        yield return FadeTo(1f, duration);
    }

    /// <summary>Fade the view FROM black back to the scene.</summary>
    public IEnumerator FadeIn(float duration)
    {
        yield return FadeTo(0f, duration);
    }

    /// <summary>Snap instantly to black with no animation.</summary>
    public void SetBlackImmediate()
    {
        SetAlpha(1f);
    }

    /// <summary>Snap instantly to clear with no animation.</summary>
    public void SetClearImmediate()
    {
        SetAlpha(0f);
    }

    private IEnumerator FadeTo(float targetAlpha, float duration)
    {
        float startAlpha = currentAlpha;
        float elapsed = 0f;

        // Guard against a zero/negative duration causing a divide-by-zero.
        if (duration <= 0f)
        {
            SetAlpha(targetAlpha);
            yield break;
        }

        while (elapsed < duration)
        {
            // unscaledDeltaTime means the fade still works if the game is
            // paused via Time.timeScale = 0.
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / duration);

            // SmoothStep eases in and out rather than moving linearly,
            // which reads as noticeably gentler in a headset.
            SetAlpha(Mathf.SmoothStep(startAlpha, targetAlpha, t));

            yield return null; // wait one frame
        }

        SetAlpha(targetAlpha);
    }

    private void SetAlpha(float alpha)
    {
        currentAlpha = Mathf.Clamp01(alpha);

        if (fadeMaterial != null)
        {
            Color c = fadeMaterial.GetColor(colorPropertyId);
            c.a = currentAlpha;
            fadeMaterial.SetColor(colorPropertyId, c);
        }

        // Switch the renderer off entirely when fully transparent.
        // Saves drawing an invisible surface every frame, which matters
        // on a mobile-class GPU.
        if (fadeRenderer != null)
        {
            fadeRenderer.enabled = currentAlpha > 0.001f;
        }
    }
}
