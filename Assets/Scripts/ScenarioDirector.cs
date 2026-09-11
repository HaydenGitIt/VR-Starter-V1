using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Playables;

/// <summary>
/// Runs the scenario from the trainee's point of view.
///
/// The controller handles one turn at a time; this handles the whole session:
/// waiting for the server, giving the trainee a moment to arrive in the room,
/// starting Mel, and handing over to the right ending Timeline.
///
/// WHY THERE IS A DELIBERATE PAUSE BEFORE MEL SPEAKS:
/// The scene fades in, the trainee is still working out where they are, and
/// their headset has only just settled on their face. Having an agitated
/// parent start shouting during that is disorienting, and worse, they will
/// miss the opening line - which is the entire setup for the scenario. A few
/// seconds of quiet room first costs nothing and fixes both.
/// </summary>
public class ScenarioDirector : MonoBehaviour
{
    public enum Phase
    {
        /// <summary>Waiting for the server to answer.</summary>
        Connecting,

        /// <summary>Connected. Letting the trainee arrive in the room.</summary>
        Settling,

        /// <summary>Waiting for a deliberate start signal.</summary>
        ReadyToStart,

        /// <summary>The conversation is happening.</summary>
        Running,

        /// <summary>An ending Timeline is playing.</summary>
        Ending,

        /// <summary>Everything is finished.</summary>
        Complete,

        /// <summary>The server could not be reached, or was lost.</summary>
        Failed
    }

    [Header("References")]
    [SerializeField] private ConversationClient client;
    [SerializeField] private ConversationController controller;

    [Header("Endings")]
    [Tooltip("Plays when the trainee reaches her. Leave empty while building - " +
             "the scenario will simply finish without it.")]
    [SerializeField] private PlayableDirector successTimeline;

    [Tooltip("Plays when she gives up and leaves.")]
    [SerializeField] private PlayableDirector failureTimeline;

    [Header("Timing")]
    [Tooltip("Seconds of quiet after the scene appears before anything happens. " +
             "Lets the trainee look around and settle.")]
    [SerializeField] private float settleSeconds = 4f;

    [Tooltip("Start automatically once settled. Turn OFF to require a gaze button " +
             "or other deliberate trigger.")]
    [SerializeField] private bool autoStart = true;

    [Tooltip("If the ending Timelines are not assigned, wait this long before " +
             "finishing anyway, so the scenario never simply hangs.")]
    [SerializeField] private float endingFallbackSeconds = 3f;

    [Header("Optional")]
    [Tooltip("Shown while connecting, hidden once the scenario starts. A world-space " +
             "panel saying something like 'Preparing...' is enough.")]
    [SerializeField] private GameObject connectingPanel;

    [Tooltip("Shown if the server cannot be reached, so the trainee is not left " +
             "standing in a silent room with no idea what happened.")]
    [SerializeField] private GameObject connectionFailedPanel;

    // ---------------------------------------------------------------
    // PUBLIC STATE
    // ---------------------------------------------------------------

    public Phase CurrentPhase { get; private set; } = Phase.Connecting;

    public event Action<Phase> OnPhaseChanged;

    /// <summary>True if the trainee reached her. Only meaningful once Complete.</summary>
    public bool Resolved { get; private set; }

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    private bool serverReady;
    private bool settled;
    private bool started;

    private void Awake()
    {
        if (client == null) client = FindInScene<ConversationClient>();
        if (controller == null) controller = FindInScene<ConversationController>();

        if (client == null || controller == null)
        {
            Debug.LogError("[ScenarioDirector] Needs a ConversationClient and a " +
                           "ConversationController in the scene.");
            enabled = false;
            return;
        }

        // The controller must NOT start on its own here - this script decides
        // when. Its beginOnReady flag is for the flat test scene only.
        controller.SetAutoBegin(false);
    }

    private void OnEnable()
    {
        client.OnReady += HandleServerReady;
        client.OnEnding += HandleEnding;
        client.OnError += HandleError;
        client.OnDisconnected += HandleDisconnected;
    }

    private void OnDisable()
    {
        client.OnReady -= HandleServerReady;
        client.OnEnding -= HandleEnding;
        client.OnError -= HandleError;
        client.OnDisconnected -= HandleDisconnected;
    }

    private void Start()
    {
        SetPhase(Phase.Connecting);

        if (connectingPanel != null) connectingPanel.SetActive(true);
        if (connectionFailedPanel != null) connectionFailedPanel.SetActive(false);

        StartCoroutine(SettleRoutine());
    }

    /// <summary>
    /// Counts down the settling period in parallel with connecting, so the
    /// two do not add up. A warm server answers in well under a second, and
    /// making the trainee wait four seconds AFTER that for no visible reason
    /// would be time wasted.
    /// </summary>
    private IEnumerator SettleRoutine()
    {
        yield return new WaitForSeconds(settleSeconds);

        settled = true;
        TryStart();
    }

    private void HandleServerReady()
    {
        serverReady = true;

        if (connectingPanel != null) connectingPanel.SetActive(false);

        TryStart();
    }

    private void TryStart()
    {
        if (started || !serverReady || !settled) return;

        if (!autoStart)
        {
            SetPhase(Phase.ReadyToStart);
            return;
        }

        StartScenario();
    }

    /// <summary>
    /// Begins the conversation. Wire this to a gaze button when autoStart is off.
    /// </summary>
    public void StartScenario()
    {
        if (started) return;

        if (!serverReady)
        {
            Debug.LogWarning("[ScenarioDirector] Cannot start - server is not ready.");
            return;
        }

        started = true;

        if (connectingPanel != null) connectingPanel.SetActive(false);

        SetPhase(Phase.Running);
        controller.BeginScenario();
    }

    // ---------------------------------------------------------------
    // ENDINGS
    // ---------------------------------------------------------------

    private void HandleEnding(bool resolved)
    {
        if (CurrentPhase == Phase.Ending || CurrentPhase == Phase.Complete) return;

        Resolved = resolved;
        SetPhase(Phase.Ending);

        StartCoroutine(PlayEnding(resolved));
    }

    private IEnumerator PlayEnding(bool resolved)
    {
        PlayableDirector timeline = resolved ? successTimeline : failureTimeline;

        if (timeline == null)
        {
            Debug.LogWarning(
                $"[ScenarioDirector] No {(resolved ? "success" : "failure")} Timeline " +
                "assigned. Finishing after a short pause instead.");

            yield return new WaitForSeconds(endingFallbackSeconds);

            SetPhase(Phase.Complete);
            yield break;
        }

        bool finished = false;

        void Stopped(PlayableDirector _) => finished = true;

        timeline.stopped += Stopped;
        timeline.Play();

        Debug.Log($"[ScenarioDirector] Playing {(resolved ? "success" : "failure")} Timeline " +
                  $"({timeline.duration:F1}s).");

        // Wait for the Timeline rather than for a fixed duration, so editing
        // the animation later does not silently desynchronise this.
        while (!finished)
        {
            yield return null;
        }

        timeline.stopped -= Stopped;

        SetPhase(Phase.Complete);
    }

    // ---------------------------------------------------------------
    // FAILURE HANDLING
    // ---------------------------------------------------------------

    private void HandleError(string message)
    {
        if (started) return;      // mid-scenario errors are the controller's problem

        Debug.LogError($"[ScenarioDirector] Could not start: {message}");

        if (connectingPanel != null) connectingPanel.SetActive(false);
        if (connectionFailedPanel != null) connectionFailedPanel.SetActive(true);

        SetPhase(Phase.Failed);
    }

    private void HandleDisconnected()
    {
        if (CurrentPhase == Phase.Complete || CurrentPhase == Phase.Ending) return;
        if (!started) return;

        // Losing the server mid-scenario leaves the trainee standing in a room
        // with a person who has stopped responding. Say so rather than letting
        // them wonder whether they broke something.
        Debug.LogError("[ScenarioDirector] Lost the server mid-scenario.");

        if (connectionFailedPanel != null) connectionFailedPanel.SetActive(true);

        SetPhase(Phase.Failed);
    }

    // ---------------------------------------------------------------

    private void SetPhase(Phase next)
    {
        if (CurrentPhase == next) return;

        CurrentPhase = next;
        Debug.Log($"[ScenarioDirector] {next}");

        OnPhaseChanged?.Invoke(next);
    }

    private static T FindInScene<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<T>();
#else
        return FindObjectOfType<T>();
#endif
    }
}
