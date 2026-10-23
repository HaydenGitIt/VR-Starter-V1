using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Runs the introduction scene: information cards, then the tutorial video,
/// then a final panel that sends the trainee into the scenario.
///
///     cards (x7)  ->  tutorial video  ->  final panel  ->  Deescalation
///
/// WHY THIS IS WORTH ITS OWN SCRIPT:
/// The sequence has one hard rule - the trainee must not reach the kindergarten
/// before the server is ready - and one easy mistake, which is that
/// VideoPlayer.loopPointReached never fires on a looping clip, so "advance when
/// the video ends" silently never happens. Both are handled here rather than
/// being left as things to remember.
///
/// SUPPORTS TWO CARD ARRANGEMENTS:
/// If the UI pack already pages through its own cards, leave the Cards array
/// empty and simply wire the LAST card's Continue button to ShowVideo().
/// If the cards are separate objects, put them in the array in order and wire
/// EVERY Continue button to Advance() - this script will show one at a time.
/// </summary>
public class IntroSequence : MonoBehaviour
{
    public enum Stage
    {
        Cards,
        Video,
        FinalPanel,
        Leaving
    }

    [Header("Stage 1 - Information cards")]
    [Tooltip("OPTIONAL. Only fill this if the cards are separate objects you want " +
             "THIS script to show one at a time. Leave empty when the UI pack does " +
             "its own paging - which is the usual case.")]
    [SerializeField] private GameObject[] cards;

    [Tooltip("How many cards there are, when the UI pack pages them itself.\n\n" +
             "Use this when ONE shared Continue button advances every card: wire that " +
             "button to BOTH the pack's own next-step method AND this script's " +
             "Advance(). This script counts the presses and opens the video after the " +
             "last one.\n\n" +
             "Set to 0 if a single press should go straight to the video.")]
    [SerializeField] private int externalCardCount = 7;

    [Tooltip("The whole card UI. Hidden when the video starts.")]
    [SerializeField] private GameObject cardPanel;

    [Header("Stage 2 - Tutorial video")]
    [SerializeField] private GameObject tutorialPlayerUI;
    [SerializeField] private VideoPlayer videoPlayer;

    [Tooltip("Seconds to wait after the video ends before the final panel appears.")]
    [SerializeField] private float pauseAfterVideo = 0.5f;

    [Header("Stage 3 - Final panel")]
    [SerializeField] private GameObject finalPanel;

    [Header("Transition")]
    [Tooltip("Seconds between pressing the final button and the scene changing. " +
             "The fade handles the rest.")]
    [SerializeField] private float delayBeforeLeaving = 0.25f;

    [Header("Server Readiness")]
    [Tooltip("The client connects during this scene so the server's ~20s warm-up is " +
             "hidden behind the introduction. Leave empty to find it automatically.")]
    [SerializeField] private ConversationClient client;

    [Tooltip("Block the final button until the server is ready. Strongly recommended - " +
             "otherwise the trainee walks into the room and stands in silence.")]
    [SerializeField] private bool requireServerBeforeLeaving = true;

    [Tooltip("Shown on the final panel while the server is still warming.")]
    [SerializeField] private GameObject waitingForServerNotice;

    [Tooltip("Shown if the server cannot be reached at all.")]
    [SerializeField] private GameObject serverUnavailableNotice;

    [Header("Cleanup")]
    [Tooltip("Destroy finished UI rather than just deactivating it. Deactivating is " +
             "usually better - it costs almost nothing and leaves the objects " +
             "inspectable while you are building.")]
    [SerializeField] private bool destroyWhenDone = false;

    // ---------------------------------------------------------------
    // STATE
    // ---------------------------------------------------------------

    public Stage CurrentStage { get; private set; } = Stage.Cards;
    public event Action<Stage> OnStageChanged;

    private int cardIndex;
    private bool serverReady;
    private bool videoFinished;

    private void Awake()
    {
        if (client == null) client = FindInScene<ConversationClient>();
    }

    private void OnEnable()
    {
        if (client != null)
        {
            client.OnReady += HandleServerReady;
            client.OnError += HandleServerError;
        }
    }

    private void OnDisable()
    {
        if (client != null)
        {
            client.OnReady -= HandleServerReady;
            client.OnError -= HandleServerError;
        }
    }

    private void Start()
    {
        // Everything except the first stage starts hidden, regardless of how
        // the scene was left in the editor.
        SetActive(tutorialPlayerUI, false);
        SetActive(finalPanel, false);
        SetActive(waitingForServerNotice, false);
        SetActive(serverUnavailableNotice, false);

        if (cardPanel != null) cardPanel.SetActive(true);

        if (cards != null && cards.Length > 0)
        {
            ShowCard(0);
        }

        if (client != null && client.IsReady)
        {
            serverReady = true;
        }

        SetStage(Stage.Cards);
    }

    // ---------------------------------------------------------------
    // STAGE 1 - CARDS
    // ---------------------------------------------------------------

    /// <summary>
    /// Moves to the next card. Wire every Continue button to this when the
    /// cards are separate objects in the Cards array.
    ///
    /// On the last card this hands over to the video automatically, so the
    /// final Continue button needs no special wiring.
    /// </summary>
    public void Advance()
    {
        if (CurrentStage != Stage.Cards) return;

        // --- Case 1: this script owns the cards ---
        if (cards != null && cards.Length > 0)
        {
            cardIndex++;

            if (cardIndex >= cards.Length)
            {
                ShowVideo();
                return;
            }

            ShowCard(cardIndex);
            return;
        }

        // --- Case 2: the UI pack owns the paging, one shared button ---
        //
        // The button calls the pack's own next-step method AND this. The pack
        // moves the card; we count. On the last press the pack has nothing
        // left to show, so we take over and open the video.
        if (externalCardCount > 0)
        {
            cardIndex++;

            if (cardIndex >= externalCardCount)
            {
                ShowVideo();
            }
            else
            {
                Debug.Log($"[IntroSequence] Card {cardIndex + 1} of {externalCardCount}");
            }

            return;
        }

        // --- Case 3: nothing to page, go straight on ---
        ShowVideo();
    }

    private void ShowCard(int index)
    {
        for (int i = 0; i < cards.Length; i++)
        {
            if (cards[i] != null)
            {
                cards[i].SetActive(i == index);
            }
        }

        cardIndex = index;
    }

    // ---------------------------------------------------------------
    // STAGE 2 - VIDEO
    // ---------------------------------------------------------------

    /// <summary>
    /// Hides the cards and plays the tutorial video.
    ///
    /// Wire the LAST card's Continue button here if the UI pack pages itself.
    /// </summary>
    public void ShowVideo()
    {
        if (CurrentStage == Stage.Video) return;

        Dismiss(cardPanel);

        SetActive(tutorialPlayerUI, true);
        SetStage(Stage.Video);

        if (videoPlayer == null)
        {
            Debug.LogError("[IntroSequence] No VideoPlayer assigned. Skipping to the final panel.");
            StartCoroutine(ShowFinalPanelAfter(0f));
            return;
        }

        // loopPointReached NEVER fires on a looping clip, so "advance when the
        // video finishes" would silently never happen and the trainee would be
        // stuck watching it forever with no way on.
        if (videoPlayer.isLooping)
        {
            Debug.LogWarning("[IntroSequence] VideoPlayer was set to loop. Turning that off, " +
                             "or the end-of-video event would never fire.");
            videoPlayer.isLooping = false;
        }

        videoFinished = false;
        videoPlayer.loopPointReached += HandleVideoFinished;

        videoPlayer.Play();
    }

    private void HandleVideoFinished(VideoPlayer source)
    {
        if (videoFinished) return;
        videoFinished = true;

        source.loopPointReached -= HandleVideoFinished;

        StartCoroutine(ShowFinalPanelAfter(pauseAfterVideo));
    }

    // ---------------------------------------------------------------
    // STAGE 3 - FINAL PANEL
    // ---------------------------------------------------------------

    private IEnumerator ShowFinalPanelAfter(float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        Dismiss(tutorialPlayerUI);

        SetActive(finalPanel, true);
        SetStage(Stage.FinalPanel);

        RefreshServerNotices();
    }

    /// <summary>
    /// Leaves for the scenario. Wire the final panel's button here.
    /// </summary>
    public void BeginScenario()
    {
        if (CurrentStage != Stage.FinalPanel) return;

        // No client in the scene at all means this scene is being run on its
        // own for testing, rather than through Bootstrap. There is nothing to
        // wait for, so blocking here would just be a dead end with no way
        // forward and no explanation.
        bool clientExists = client != null;

        if (requireServerBeforeLeaving && clientExists && !serverReady)
        {
            // Refusing is kinder than letting them through. Walking into the
            // kindergarten and standing in silence in front of a parent who
            // never speaks is a far worse experience than waiting here with an
            // explanation on screen.
            Debug.LogWarning("[IntroSequence] Server not ready - holding the trainee here.");
            RefreshServerNotices();
            return;
        }

        if (!clientExists)
        {
            Debug.LogWarning("[IntroSequence] No ConversationClient found - continuing anyway. " +
                             "Run from the Bootstrap scene for the real flow.");
        }

        SetStage(Stage.Leaving);
        StartCoroutine(LeaveRoutine());
    }

    private IEnumerator LeaveRoutine()
    {
        if (delayBeforeLeaving > 0f) yield return new WaitForSeconds(delayBeforeLeaving);

        Dismiss(finalPanel);

        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.LoadDeescalation();
        }
        else
        {
            Debug.LogError("[IntroSequence] No GameFlowManager. Is the Bootstrap scene " +
                           "loading first? Cannot change scene.");
        }
    }

    // ---------------------------------------------------------------
    // SERVER READINESS
    // ---------------------------------------------------------------

    private void HandleServerReady()
    {
        serverReady = true;
        Debug.Log("[IntroSequence] Server ready - the trainee can go through whenever they like.");

        RefreshServerNotices();
    }

    private void HandleServerError(string message)
    {
        serverReady = false;
        Debug.LogError($"[IntroSequence] Server problem: {message}");

        RefreshServerNotices();
    }

    private void RefreshServerNotices()
    {
        if (CurrentStage != Stage.FinalPanel) return;

        bool connecting = !serverReady && client != null;

        SetActive(waitingForServerNotice, connecting);
        SetActive(serverUnavailableNotice, false);
    }

    // ---------------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------------

    private void Dismiss(GameObject target)
    {
        if (target == null) return;

        if (destroyWhenDone)
        {
            Destroy(target);
        }
        else
        {
            target.SetActive(false);
        }
    }

    private static void SetActive(GameObject target, bool value)
    {
        if (target != null) target.SetActive(value);
    }

    private void SetStage(Stage next)
    {
        if (CurrentStage == next) return;

        CurrentStage = next;
        Debug.Log($"[IntroSequence] {next}");

        OnStageChanged?.Invoke(next);
    }

    private static T FindInScene<T>() where T : UnityEngine.Object
    {
#if UNITY_6000_0_OR_NEWER
        // FindAnyObjectByType does not guarantee WHICH instance it returns when
        // several exist - that is fine here, because every type looked up this
        // way is meant to be unique in the scene, and it is faster than the
        // ordered alternatives.
        return FindAnyObjectByType<T>();
#elif UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<T>();
#else
        return FindObjectOfType<T>();
#endif
    }
}
