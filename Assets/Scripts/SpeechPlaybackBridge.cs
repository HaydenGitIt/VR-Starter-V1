using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Feeds audio from the server into the streaming player.
///
/// Deliberately a separate, tiny script rather than wiring the client
/// directly to the player. The client knows about the network and nothing
/// else; the player knows about audio and nothing else. This is the only
/// piece that knows they belong together, which keeps either one replaceable.
/// </summary>
public class SpeechPlaybackBridge : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ConversationClient client;
    [SerializeField] private StreamingAudioPlayer player;

    [Header("Behaviour")]
    [Tooltip("Discard queued audio when the scenario ends. Leave ON - audio from the " +
             "final turn would otherwise play over the ending Timeline.")]
    [SerializeField] private bool flushOnEnding = true;

    private void Awake()
    {
        if (client == null) client = FindInScene<ConversationClient>();
        if (player == null) player = FindInScene<StreamingAudioPlayer>();

        if (client == null || player == null)
        {
            Debug.LogError("[SpeechPlaybackBridge] Needs both a ConversationClient and a " +
                           "StreamingAudioPlayer in the scene.");
            enabled = false;
        }
    }

    private void OnEnable()
    {
        if (client == null || player == null) return;

        client.OnAudio += player.Enqueue;
        client.OnEnding += HandleEnding;
    }

    private void OnDisable()
    {
        if (client == null || player == null) return;

        client.OnAudio -= player.Enqueue;
        client.OnEnding -= HandleEnding;
    }

    private void HandleEnding(bool resolved)
    {
        if (!flushOnEnding) return;

        // On failure the server sends no audio at all for the final turn - the
        // verdict is known from the tags before anything is synthesised, so
        // there is nothing to interrupt. This flush covers the success ending,
        // where Mel does speak a closing line, and any audio still in flight.
        player.Flush();
    }

    /// <summary>
    /// Finds a component in the scene, using whichever API this Unity version
    /// provides. FindObjectOfType was deprecated in Unity 2023/6 in favour of
    /// FindFirstObjectByType, and using the old name produces warnings there
    /// while the new name does not exist on older versions.
    /// </summary>
    private static T FindInScene<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<T>();
#else
        return FindObjectOfType<T>();
#endif
    }
}
