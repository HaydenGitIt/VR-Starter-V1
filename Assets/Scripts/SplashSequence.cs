using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Plays a sequence of logo images on a world-space canvas, then advances
/// to the Introduction scene.
///
/// Put this on the Splash scene's canvas. Each logo fades in, holds, then
/// fades out, one after another.
///
/// NOTE ON THE "MADE WITH UNITY" LOGO:
/// Unity's own splash (Project Settings > Player > Splash Image) is
/// mandatory on a Personal licence and plays BEFORE any scene loads. If you
/// are on Personal, you already get the Unity logo automatically and you may
/// only need your institute logo here. If you are on Pro and have disabled
/// the built-in splash, you can include an official "Made with Unity" logo
/// as the first entry below - but you must use the official artwork from
/// Unity's brand guidelines, not a recreation.
/// </summary>
public class SplashSequence : MonoBehaviour
{
    /// <summary>
    /// One logo in the sequence. Marked [System.Serializable] so it shows
    /// up as an editable entry in the Inspector.
    /// </summary>
    [System.Serializable]
    public class SplashLogo
    {
        [Tooltip("Friendly name - purely for your own reference in the Inspector.")]
        public string label = "Logo";

        [Tooltip("The UI Image component displaying this logo.")]
        public Image image;

        [Tooltip("Seconds to fade this logo in.")]
        public float fadeInDuration = 0.6f;

        [Tooltip("Seconds to hold at full opacity.")]
        public float holdDuration = 2.0f;

        [Tooltip("Seconds to fade this logo out.")]
        public float fadeOutDuration = 0.6f;
    }

    [Header("Logos")]
    [Tooltip("Played top to bottom, in order.")]
    [SerializeField] private SplashLogo[] logos;

    [Header("Timing")]
    [Tooltip("Pause before the first logo appears. Gives the player a moment to settle after putting the headset on.")]
    [SerializeField] private float initialDelay = 1.0f;

    [Tooltip("Gap between one logo disappearing and the next appearing.")]
    [SerializeField] private float gapBetweenLogos = 0.4f;

    [Tooltip("Pause after the last logo before moving to the Introduction scene.")]
    [SerializeField] private float delayBeforeAdvancing = 0.5f;

    [Header("Behaviour")]
    [Tooltip("If true, the sequence starts automatically when this scene loads.")]
    [SerializeField] private bool playOnStart = true;

    [Tooltip("If true, the player can skip by looking at a skip button (wire that button's OnClick to Skip()).")]
    [SerializeField] private bool allowSkip = false;

    private bool hasAdvanced = false;

    private void Start()
    {
        // Make sure every logo begins invisible, regardless of how it was
        // left in the editor.
        foreach (SplashLogo logo in logos)
        {
            if (logo.image != null)
            {
                SetImageAlpha(logo.image, 0f);
            }
        }

        if (playOnStart)
        {
            StartCoroutine(PlaySequence());
        }
    }

    private IEnumerator PlaySequence()
    {
        yield return new WaitForSeconds(initialDelay);

        for (int i = 0; i < logos.Length; i++)
        {
            SplashLogo logo = logos[i];

            if (logo.image == null)
            {
                Debug.LogWarning($"[SplashSequence] Logo '{logo.label}' has no Image assigned - skipping it.");
                continue;
            }

            yield return FadeImage(logo.image, 0f, 1f, logo.fadeInDuration);
            yield return new WaitForSeconds(logo.holdDuration);
            yield return FadeImage(logo.image, 1f, 0f, logo.fadeOutDuration);

            // Don't add a gap after the final logo - the advance delay covers it.
            if (i < logos.Length - 1)
            {
                yield return new WaitForSeconds(gapBetweenLogos);
            }
        }

        yield return new WaitForSeconds(delayBeforeAdvancing);

        Advance();
    }

    /// <summary>
    /// Called by a skip button, if you have one. Does nothing unless
    /// allowSkip is ticked.
    /// </summary>
    public void Skip()
    {
        if (!allowSkip) return;

        StopAllCoroutines();
        Advance();
    }

    private void Advance()
    {
        // Guard so we can never trigger two scene loads.
        if (hasAdvanced) return;
        hasAdvanced = true;

        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.LoadIntroduction();
        }
        else
        {
            Debug.LogError("[SplashSequence] No GameFlowManager found. " +
                           "Make sure the Bootstrap scene loaded first, or that a " +
                           "GameFlowManager exists in this scene.");
        }
    }

    private IEnumerator FadeImage(Image image, float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            SetImageAlpha(image, to);
            yield break;
        }

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            SetImageAlpha(image, Mathf.SmoothStep(from, to, t));
            yield return null;
        }

        SetImageAlpha(image, to);
    }

    private void SetImageAlpha(Image image, float alpha)
    {
        Color c = image.color;
        c.a = alpha;
        image.color = c;
    }
}
