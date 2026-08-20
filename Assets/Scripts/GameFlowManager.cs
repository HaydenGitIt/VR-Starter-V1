using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Controls which scene the experience is in, and handles all transitions
/// between them. This object survives scene loads (DontDestroyOnLoad), so
/// there is exactly ONE of these alive for the whole session.
///
/// Scene order: Splash (0) -> Introduction (1) -> Deescalation (2)
/// </summary>
public class GameFlowManager : MonoBehaviour
{
    // ---------------------------------------------------------------
    // SINGLETON
    // "Instance" lets any other script reach this manager by writing
    // GameFlowManager.Instance.LoadIntroduction() without needing a
    // direct reference wired up in the Inspector.
    // ---------------------------------------------------------------
    public static GameFlowManager Instance { get; private set; }

    [Header("Scene Names")]
    [Tooltip("Must match the scene file names EXACTLY, and each scene must be added to Build Settings.")]
    [SerializeField] private string splashSceneName = "Splash";
    [SerializeField] private string introductionSceneName = "Introduction";
    [SerializeField] private string deescalationSceneName = "Deescalation";

    [Header("Transition")]
    [Tooltip("How long the fade to black takes, in seconds. 0.5-1.0 is comfortable in VR.")]
    [SerializeField] private float fadeDuration = 0.75f;

    [Tooltip("Optional pause on the black screen before the new scene fades in.")]
    [SerializeField] private float holdOnBlackDuration = 0.25f;

    // Reference to the fader that draws the black screen. Assigned in the Inspector.
    [Header("References")]
    [SerializeField] private ScreenFader screenFader;

    [Header("Startup")]
    [Tooltip("If true, this manager loads the Splash scene as soon as it wakes up. Leave ON for the Bootstrap scene.")]
    [SerializeField] private bool loadSplashOnStart = true;

    // Guard flag: stops a second transition starting while one is already running.
    private bool isTransitioning = false;

    private void Awake()
    {
        // If a GameFlowManager already exists, this one is a duplicate
        // (which happens if you re-enter the Bootstrap scene). Destroy it.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        // Bootstrap holds nothing but this manager and the fader, so it
        // hands straight over to the Splash scene.
        if (loadSplashOnStart)
        {
            TransitionTo(splashSceneName);
        }
    }

    // ---------------------------------------------------------------
    // PUBLIC ENTRY POINTS
    // Call these from other scripts, or from a UI button's OnClick.
    // ---------------------------------------------------------------

    /// <summary>Move from the splash into the introduction scene.</summary>
    public void LoadIntroduction()
    {
        TransitionTo(introductionSceneName);
    }

    /// <summary>Move from the introduction into the de-escalation scenario.</summary>
    public void LoadDeescalation()
    {
        TransitionTo(deescalationSceneName);
    }

    /// <summary>Return to the very start (useful for "try again" after a debrief).</summary>
    public void LoadSplash()
    {
        TransitionTo(splashSceneName);
    }

    /// <summary>Reload whichever scene is currently active.</summary>
    public void ReloadCurrentScene()
    {
        TransitionTo(SceneManager.GetActiveScene().name);
    }

    // ---------------------------------------------------------------
    // TRANSITION LOGIC
    // ---------------------------------------------------------------

    /// <summary>
    /// Fades to black, loads the requested scene in the background,
    /// then fades back in. Safe to call from anywhere.
    /// </summary>
    public void TransitionTo(string sceneName)
    {
        if (isTransitioning)
        {
            Debug.LogWarning($"[GameFlowManager] Ignored request to load '{sceneName}' - a transition is already in progress.");
            return;
        }

        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("[GameFlowManager] Scene name is empty. Check the names in the Inspector.");
            return;
        }

        StartCoroutine(TransitionRoutine(sceneName));
    }

    private IEnumerator TransitionRoutine(string sceneName)
    {
        isTransitioning = true;

        // 1. Fade the player's view to black BEFORE anything changes.
        //    In VR an instant cut is disorienting, so this is not just polish.
        if (screenFader != null)
        {
            yield return screenFader.FadeOut(fadeDuration);
        }

        // 2. Load asynchronously so the frame rate does not stall.
        //    A frozen frame in a headset is far more unpleasant than on a monitor.
        AsyncOperation load = SceneManager.LoadSceneAsync(sceneName);

        // allowSceneActivation = false lets us wait until loading is fully
        // finished before swapping, rather than swapping mid-load.
        load.allowSceneActivation = false;

        // Unity reports 0.9 as "loaded but not yet activated".
        while (load.progress < 0.9f)
        {
            yield return null; // wait one frame, then check again
        }

        // Optional beat of stillness on black.
        if (holdOnBlackDuration > 0f)
        {
            yield return new WaitForSeconds(holdOnBlackDuration);
        }

        // 3. Actually swap to the new scene.
        load.allowSceneActivation = true;

        // Wait until Unity confirms the swap is complete.
        while (!load.isDone)
        {
            yield return null;
        }

        // 4. Give the new scene one frame to set itself up (cameras, XR rig)
        //    before we reveal it, so the player never sees a half-built scene.
        yield return null;

        // 5. Fade back in.
        if (screenFader != null)
        {
            yield return screenFader.FadeIn(fadeDuration);
        }

        isTransitioning = false;
    }
}
