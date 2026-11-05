using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// TEMPORARY SCAFFOLDING for the "audio plays, picture is frozen" problem.
///
/// Delete this component once the video works.
///
/// WHAT IT SETTLES:
/// A frozen picture with working audio has three quite different causes,
/// needing quite different fixes. This compares how far the playhead moved
/// against how far it should have moved in the same wall-clock time, which
/// separates them in one reading.
///
///   FRAME NOT ADVANCING     -> playback is not running at all, whatever the
///                              audio is doing. Something is holding the
///                              playhead, or the player never started.
///
///   ADVANCING SLOWLY        -> the decoder cannot keep up. The clip is too
///                              demanding: reduce resolution and bitrate.
///
///   ADVANCING AT FULL RATE  -> frames are being decoded perfectly well and
///                              something downstream is not showing them.
///                              The clip is not the problem; the render
///                              target, a transform, or the UI is.
///
/// Derived rather than read: VideoPlayer exposes no dropped-frame counter.
/// </summary>
[RequireComponent(typeof(VideoPlayer))]
public class VideoDiagnostics : MonoBehaviour
{
    [Tooltip("Leave empty to use the VideoPlayer on this same GameObject.")]
    [SerializeField] private VideoPlayer videoPlayer;

    [Tooltip("Seconds between Console reports. One a second is enough to see " +
             "a trend without burying everything else in the Console.")]
    [SerializeField] private float intervalSeconds = 1f;

    private float nextReport;
    private long lastFrame = -1;
    private float lastReportTime = -1f;
    private bool describedOnce;

    private void Awake()
    {
        if (videoPlayer == null) videoPlayer = GetComponent<VideoPlayer>();
    }

    private void OnEnable()
    {
        videoPlayer.errorReceived += HandleError;
        videoPlayer.prepareCompleted += HandlePrepared;
    }

    private void OnDisable()
    {
        videoPlayer.errorReceived -= HandleError;
        videoPlayer.prepareCompleted -= HandlePrepared;
    }

    private void HandleError(VideoPlayer source, string message)
    {
        Debug.LogError($"[VideoDiagnostics] VideoPlayer error: {message}", this);
    }

    private void HandlePrepared(VideoPlayer source)
    {
        Debug.Log("[VideoDiagnostics] Prepared.", this);
        DescribeOnce();
    }

    private void Update()
    {
        if (Time.unscaledTime < nextReport) return;
        nextReport = Time.unscaledTime + Mathf.Max(0.25f, intervalSeconds);

        if (!videoPlayer.isPlaying)
        {
            // Worth saying out loud. A player that is not playing explains a
            // frozen picture completely, and is a different hunt entirely
            // from one that is playing but showing nothing.
            Debug.Log($"[VideoDiagnostics] Not playing. " +
                      $"prepared: {videoPlayer.isPrepared}  " +
                      $"paused: {videoPlayer.isPaused}  " +
                      $"frame: {videoPlayer.frame}", this);
            return;
        }

        DescribeOnce();

        long frame = videoPlayer.frame;
        float now = Time.unscaledTime;

        long advanced = lastFrame < 0 ? 0 : frame - lastFrame;
        float elapsed = lastReportTime < 0f ? 0f : now - lastReportTime;

        lastFrame = frame;
        lastReportTime = now;

        // DERIVED RATHER THAN READ.
        //
        // VideoPlayer exposes no dropped-frame counter, so compare how far
        // the playhead moved against how far it SHOULD have moved in the
        // wall-clock time that passed. A decoder keeping up advances one
        // frame per frame-interval; one that cannot falls behind in exactly
        // this ratio, which is the same information a drop count would give.
        float expected = 0f;

        if (videoPlayer.clip != null && elapsed > 0f)
        {
            expected = elapsed * (float)videoPlayer.clip.frameRate;
        }

        float keepingUp = expected > 0.5f ? advanced / expected : -1f;

        string verdict;

        if (advanced <= 0)
        {
            verdict = "FRAME IS NOT ADVANCING - playback is stalled, not merely slow. " +
                      "Something is holding the playhead, whatever the audio is doing.";
        }
        else if (keepingUp < 0f)
        {
            verdict = "advancing (no frame rate to compare against yet).";
        }
        else if (keepingUp < 0.5f)
        {
            verdict = $"ONLY {keepingUp:P0} OF REAL TIME - the decoder cannot keep up.";
        }
        else
        {
            verdict = $"DECODING CLEANLY ({keepingUp:P0} of real time) - if the picture is " +
                      "frozen, the problem is the render target or the UI, not the clip.";
        }

        Debug.Log(
            $"[VideoDiagnostics] frame {frame}/{videoPlayer.frameCount}  " +
            $"(+{advanced} in {elapsed:F2}s, expected +{expected:F0})  -  {verdict}",
            this);
    }

    /// <summary>
    /// Prints the static facts once, rather than on every report. These are
    /// the settings worth checking before reading anything into the numbers:
    /// a null target texture with Render Texture mode means the frames have
    /// nowhere to go, however well they decode.
    /// </summary>
    private void DescribeOnce()
    {
        if (describedOnce) return;
        describedOnce = true;

        string size = videoPlayer.clip != null
            ? $"{videoPlayer.clip.width}x{videoPlayer.clip.height} @ {videoPlayer.clip.frameRate:F2}fps"
            : "no clip assigned";

        Debug.Log($"[VideoDiagnostics] Clip: {size}", this);
        Debug.Log($"[VideoDiagnostics] Render mode: {videoPlayer.renderMode}", this);

        if (videoPlayer.renderMode == VideoRenderMode.RenderTexture)
        {
            if (videoPlayer.targetTexture == null)
            {
                Debug.LogError("[VideoDiagnostics] Render mode is RenderTexture but NO target " +
                               "texture is assigned, so decoded frames have nowhere to go. " +
                               "This alone would produce a frozen picture.", this);
            }
            else
            {
                RenderTexture rt = videoPlayer.targetTexture;

                Debug.Log($"[VideoDiagnostics] Target texture: {rt.name} {rt.width}x{rt.height}", this);

                // A render texture sized differently from the clip is not
                // fatal - it scales - but a mismatch is worth seeing when the
                // clip has just been swapped for one of another shape.
                if (videoPlayer.clip != null &&
                    (rt.width != videoPlayer.clip.width || rt.height != videoPlayer.clip.height))
                {
                    Debug.LogWarning($"[VideoDiagnostics] Target texture is {rt.width}x{rt.height} " +
                                     $"but the clip is {videoPlayer.clip.width}x{videoPlayer.clip.height}. " +
                                     "If this texture was built for the previous clip, it may need " +
                                     "recreating.", this);
                }
            }
        }
    }
}
