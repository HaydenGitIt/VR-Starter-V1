using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Records what happened during a scenario, and survives the scene change
/// into the debrief.
///
/// WHY THIS ASSEMBLES THE LOG RATHER THAN ASKING THE SERVER FOR IT:
/// The server keeps its own log and can send it on request - but it arrives
/// as JSON containing an array of objects, and Unity's built-in JsonUtility
/// cannot deserialise that without adding a third-party JSON library.
///
/// Unity already receives every piece of that log as it happens: the
/// transcript, the tags, the reply, the turn completion. Assembling it here
/// costs nothing, adds no dependency, and has a real advantage - if the
/// connection drops mid-scenario, the trainee still gets a debrief covering
/// everything up to that point.
/// </summary>
public class SessionLog : MonoBehaviour
{
    // ---------------------------------------------------------------
    // DATA
    // ---------------------------------------------------------------

    [Serializable]
    public class Turn
    {
        public int number;
        public string traineeSaid;
        public string melSaid;
        public DeescalationVerdict verdict;
        public ParentEmotion emotion;
        public float latencySeconds;

        /// <summary>True if this turn made progress toward reaching her.</summary>
        public bool ReachedHer => verdict == DeescalationVerdict.Deescalated;
    }

    [Serializable]
    public class Session
    {
        public string startedAt;
        public string openingLine;
        public List<Turn> turns = new List<Turn>();

        public bool ended;
        public bool resolved;

        public int Progress
        {
            get
            {
                int count = 0;
                foreach (Turn t in turns) if (t.ReachedHer) count++;
                return count;
            }
        }

        public int Strikes
        {
            get
            {
                int count = 0;
                foreach (Turn t in turns)
                {
                    // Unknown is a generation fault, not trainee performance,
                    // and must never count against them.
                    if (t.verdict == DeescalationVerdict.Neutral) count++;
                }
                return count;
            }
        }

        public float AverageLatency
        {
            get
            {
                if (turns.Count == 0) return 0f;

                float total = 0f;
                foreach (Turn t in turns) total += t.latencySeconds;
                return total / turns.Count;
            }
        }
    }

    // ---------------------------------------------------------------
    // SINGLETON
    // ---------------------------------------------------------------

    public static SessionLog Instance { get; private set; }

    [Header("Recording")]
    [Tooltip("Write the session to disk when it ends. Recommended for study data - " +
             "the debrief screen is transient, the file is not.")]
    [SerializeField] private bool saveToFile = true;

    [Tooltip("Subfolder inside the persistent data path.")]
    [SerializeField] private string folderName = "Sessions";

    public Session Current { get; private set; } = new Session();

    /// <summary>Raised whenever a turn is added, for live UI.</summary>
    public event Action<Turn> OnTurnRecorded;

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    private ConversationClient client;

    // A turn is assembled across several messages. The server sends them in a
    // fixed order - transcript, tags, reply, turn_complete - so we accumulate
    // here and commit when the turn completes.
    private string pendingTranscript;
    private string pendingReply;
    private DeescalationVerdict pendingVerdict = DeescalationVerdict.Unknown;
    private ParentEmotion pendingEmotion = ParentEmotion.Unknown;

    private bool scenarioStarted;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        Unsubscribe();
    }

    private void Start()
    {
        Subscribe();
    }

    /// <summary>
    /// The client lives in whichever scene is running the conversation, while
    /// this object persists across scenes. So we re-find it after every load
    /// rather than holding a reference that goes stale.
    /// </summary>
    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Unsubscribe();
        Subscribe();
    }

    private void Subscribe()
    {
        client = FindInScene<ConversationClient>();

        if (client == null) return;

        client.OnTranscript += HandleTranscript;
        client.OnTags += HandleTags;
        client.OnReply += HandleReply;
        client.OnTurnComplete += HandleTurnComplete;
        client.OnDiscarded += HandleDiscarded;
        client.OnEnding += HandleEnding;
    }

    private void Unsubscribe()
    {
        if (client == null) return;

        client.OnTranscript -= HandleTranscript;
        client.OnTags -= HandleTags;
        client.OnReply -= HandleReply;
        client.OnTurnComplete -= HandleTurnComplete;
        client.OnDiscarded -= HandleDiscarded;
        client.OnEnding -= HandleEnding;

        client = null;
    }

    // ---------------------------------------------------------------
    // RECORDING
    // ---------------------------------------------------------------

    /// <summary>Clears everything, ready for a new attempt.</summary>
    public void BeginSession()
    {
        Current = new Session
        {
            startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        ClearPending();
        scenarioStarted = true;

        Debug.Log("[SessionLog] Recording a new session.");
    }

    private void HandleTranscript(string text)
    {
        pendingTranscript = text;
    }

    private void HandleTags(DeescalationVerdict verdict, ParentEmotion emotion)
    {
        pendingVerdict = verdict;
        pendingEmotion = emotion;
    }

    private void HandleReply(string text)
    {
        // The opening line arrives with tags and a reply but no transcript,
        // because the trainee has not spoken yet. Store it separately - it is
        // context for the debrief, not a turn they can be judged on.
        if (string.IsNullOrEmpty(pendingTranscript) && Current.turns.Count == 0)
        {
            Current.openingLine = text;
            ClearPending();
            return;
        }

        pendingReply = text;
    }

    private void HandleTurnComplete(int turnNumber, DeescalationVerdict verdict)
    {
        Turn turn = new Turn
        {
            number = turnNumber,
            traineeSaid = pendingTranscript ?? "",
            melSaid = pendingReply ?? "",

            // Prefer the verdict on the completion message - it is the one the
            // server actually scored with, and cannot disagree with the tags.
            verdict = verdict != DeescalationVerdict.Unknown ? verdict : pendingVerdict,
            emotion = pendingEmotion,
        };

        Current.turns.Add(turn);
        ClearPending();

        OnTurnRecorded?.Invoke(turn);
    }

    private void HandleDiscarded(string reason)
    {
        // Nothing intelligible was said, so no turn happened. Drop whatever
        // was accumulating rather than letting it attach to the next turn.
        ClearPending();
    }

    private void HandleEnding(bool resolved)
    {
        Current.ended = true;
        Current.resolved = resolved;

        if (saveToFile && scenarioStarted)
        {
            SaveToDisk();
        }
    }

    private void ClearPending()
    {
        pendingTranscript = null;
        pendingReply = null;
        pendingVerdict = DeescalationVerdict.Unknown;
        pendingEmotion = ParentEmotion.Unknown;
    }

    // ---------------------------------------------------------------
    // EXPORT
    // ---------------------------------------------------------------

    /// <summary>
    /// Writes the session as JSON.
    ///
    /// The debrief screen is transient; a file is not. For a study this is the
    /// actual data, so it is written the moment the scenario ends rather than
    /// when the trainee finishes reading the debrief - they might take the
    /// headset off and close the application first.
    /// </summary>
    public string SaveToDisk()
    {
        try
        {
            string directory = Path.Combine(Application.persistentDataPath, folderName);
            Directory.CreateDirectory(directory);

            string fileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string path = Path.Combine(directory, fileName);

            string json = JsonUtility.ToJson(Current, prettyPrint: true);
            File.WriteAllText(path, json);

            Debug.Log($"[SessionLog] Saved to {path}");
            return path;
        }
        catch (Exception error)
        {
            Debug.LogError($"[SessionLog] Could not save: {error.Message}");
            return null;
        }
    }

    public string SessionFolder => Path.Combine(Application.persistentDataPath, folderName);

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
