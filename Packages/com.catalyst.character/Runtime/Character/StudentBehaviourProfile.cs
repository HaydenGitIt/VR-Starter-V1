using System;
using System.Collections.Generic;
using UnityEngine;

namespace catalyst.xr67
{
    public enum StudentMode { Reading, Listening }

    /// <summary>
    /// Data-driven behaviour authoring for a foreground student: per mode
    /// (Reading / Listening) a base loop, a pool of occasional one-shot
    /// clips, and gaze-lock tuning. One shared asset can drive every student
    /// — per-seat variation (excluded clips, random speeds and phases) lives
    /// on <see cref="StudentAmbientPlayer"/>, not in asset copies. New clips
    /// are added here, never wired into a state machine.
    /// </summary>
    [CreateAssetMenu(fileName = "StudentBehaviourProfile", menuName = "xr67/Student Behaviour Profile")]
    public class StudentBehaviourProfile : ScriptableObject
    {
        [Serializable]
        public class OneShotEntry
        {
            [Tooltip("Optional name for logs and the inspector, e.g. 'frown' or 'head shake'.")]
            public string label;

            [Tooltip("Clip to play as an occasional one-shot — any clip: a full-body motion, or a face-only clip together with a Mask below.")]
            public AnimationClip clip;

            [Tooltip("Optional. Empty = the clip replaces the whole pose (body motions). With a mask (e.g. a face-only mask: no humanoid body parts, transform CC_Base_Body only), the clip plays on a layer over the base loop and only the masked part changes — use it for face-only clips like a frown.")]
            public AvatarMask mask;

            [Tooltip("Selection probability weight within the pool.")]
            public float weight = 1f;

            [Tooltip("Random playback speed range, rolled per play.")]
            public Vector2 speedRange = new Vector2(0.95f, 1.05f);

            [Tooltip("Seconds into the clip where playback starts.")]
            public float startOffset;

            [Tooltip("Seconds at full weight. 0 = play once to the clip end; > 0 slices a long take.")]
            public float holdSeconds;

            [Tooltip("Crossfade from the base loop into this clip.")]
            public float blendIn = 0.6f;

            [Tooltip("Crossfade back to the base loop.")]
            public float blendOut = 0.6f;

            [Tooltip("Release any gaze lock while this plays — for clips that move the head themselves (e.g. mocap_lookLeft).")]
            public bool suppressGaze;

            [Tooltip("Conversation phases: list this one-shot in the LLM's reply format, so the LLM can pick it for a reply that fits. Its id is the Label (lower case, spaces → _), else the clip name.")]
            public bool offerToLlm = true;

            [Tooltip("Tells the LLM when this one-shot fits, e.g. 'shakes her head — disagreeing, doubting or refusing what she hears'. Empty = the label alone.")]
            [TextArea(1, 3)]
            public string llmDescription;
        }

        /// <summary>
        /// One of a set's idle loops. The player rotates between a set's loops
        /// at random (weighted), holding each for a random time and
        /// crossfading to the next — the character never freezes on one loop.
        /// </summary>
        [Serializable]
        public class LoopEntry
        {
            [Tooltip("Optional name for logs and the inspector.")]
            public string label;

            [Tooltip("A looping clip (Loop Time on in the import settings).")]
            public AnimationClip clip;

            [Tooltip("Selection probability weight among this set's loops.")]
            public float weight = 1f;

            [Tooltip("Random seconds this loop plays before the player crossfades to another loop of the set. Only matters with 2+ loops.")]
            public Vector2 holdSeconds = new Vector2(10f, 20f);

            [Tooltip("Random playback speed range, rolled each time the loop starts.")]
            public Vector2 speedRange = new Vector2(0.95f, 1.05f);

            [Tooltip("Crossfade into this loop when the rotation switches to it.")]
            public float blendSeconds = 1.2f;
        }

        /// <summary>A one-shot the LLM may pick: its id, what it means, and the phase sets that list it.</summary>
        public sealed class LlmGesture
        {
            public string id;
            public string description;
            public readonly List<ConversationPhase> phases = new List<ConversationPhase>();
        }

        /// <summary>The id a one-shot is known by in the LLM's reply format: label (lower case, non-alphanumerics → _), else the clip name.</summary>
        public static string GestureId(OneShotEntry entry)
        {
            if (entry == null) return null;
            string source = !string.IsNullOrWhiteSpace(entry.label) ? entry.label
                : entry.clip != null ? entry.clip.name : null;
            if (string.IsNullOrWhiteSpace(source)) return null;
            var sb = new System.Text.StringBuilder(source.Length);
            bool underscore = false;
            foreach (char c in source.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); underscore = false; }
                else if (!underscore && sb.Length > 0) { sb.Append('_'); underscore = true; }
            }
            string id = sb.ToString().TrimEnd('_');
            return id.Length > 0 ? id : null;
        }

        [Serializable]
        public class GazeSettings
        {
            [Tooltip("Master switch for gaze locking in this mode.")]
            public bool enabled = true;

            [Tooltip("Max angle (deg) between the animated head direction and the target for a lock to engage.")]
            public float engageAngle = 15f;

            [Tooltip("Force release beyond this angle (deg) — hysteresis above Engage Angle.")]
            public float releaseAngle = 35f;

            [Tooltip("Random seconds a lock holds before releasing.")]
            public Vector2 lockDuration = new Vector2(2.5f, 6f);

            [Tooltip("Random seconds after a release before the next lock may engage.")]
            public Vector2 cooldown = new Vector2(5f, 12f);

            [Tooltip("Response rate of the lock blend-in.")]
            public float lockInRate = 4f;

            [Tooltip("Response rate of the lock blend-out.")]
            public float lockOutRate = 2f;

            [Tooltip("Response rate of head movement while locked.")]
            public float headRate = 3.5f;

            [Tooltip("Fraction of the gaze angle the head turns (eyes do the rest).")]
            [Range(0f, 1f)] public float headFollow = 0.6f;

            [Tooltip("Head turn limit, degrees.")]
            public float maxHeadYaw = 25f;

            [Tooltip("Head pitch limit, degrees.")]
            public float maxHeadPitch = 15f;

            [Tooltip("Drive the CC eye-look blendshapes so the eyes converge on the target.")]
            public bool driveEyes = true;
        }

        [Serializable]
        public class ModeSet
        {
            [Tooltip("Looping clip that plays whenever no one-shot is active. Ignored when Loops below has at least one clip.")]
            public AnimationClip baseLoop;

            [Tooltip("Idle loops of this set, rotated at random (weighted), each held for its random time and crossfaded into the next. Empty = Base Loop alone.")]
            public List<LoopEntry> loops = new List<LoopEntry>();

            [Tooltip("Play the one-shots below at random on the idle timer (One Shot Interval). Off = they play only when picked — by the LLM for a reply, or from code / the inspector.")]
            public bool timerOneShots = true;

            [Tooltip("Random playback speed range for the base loop, rolled per student. (0,0) freezes the base — the anchor-pose workflow.")]
            public Vector2 baseSpeedRange = new Vector2(0.9f, 1.05f);

            [Tooltip("Seconds into the base loop where playback starts. -1 = random phase per student (right for playing loops). For a frozen base, set the exact time of the anchor frame found by scrubbing EditorAnimationPreview.")]
            public float baseStartOffset = -1f;

            [Tooltip("Random ± seconds per student around Base Start Offset — each kid freezes on a slightly different micro-pose of the same anchor.")]
            public float basePhaseJitter;

            [Tooltip("Crossfade duration into this mode's base loop on a mode switch.")]
            public float modeBlendSeconds = 0.8f;

            [Tooltip("Random seconds between one-shots.")]
            public Vector2 oneShotInterval = new Vector2(15f, 45f);

            public List<OneShotEntry> oneShots = new List<OneShotEntry>();

            [Tooltip("Conversation phases only: the face's mood (AgitationResponder, 0..100) never sits below this while the phase is active — e.g. 55 keeps a visible frown in Transition even if the reported mood is lower. -1 = off.")]
            [Range(-1, 100)]
            public int faceAgitationFloor = -1;

            public GazeSettings gaze = new GazeSettings();

            public IdleMotionSettings idle = new IdleMotionSettings();

            /// <summary>True when the set has something to loop: a Loops entry with a clip, or a Base Loop.</summary>
            public bool HasLoop
            {
                get
                {
                    if (baseLoop != null) return true;
                    if (loops == null) return false;
                    foreach (LoopEntry loop in loops)
                        if (loop != null && loop.clip != null && loop.weight > 0f) return true;
                    return false;
                }
            }

            /// <summary>True when Loops has at least one usable clip (then Base Loop is ignored).</summary>
            public bool UsesLoopList
            {
                get
                {
                    if (loops == null) return false;
                    foreach (LoopEntry loop in loops)
                        if (loop != null && loop.clip != null && loop.weight > 0f) return true;
                    return false;
                }
            }
        }

        public ModeSet reading = new ModeSet();
        public ModeSet listening = new ModeSet();

        [Header("Conversation phases (talking characters)")]
        [Tooltip("Played while the LLM reports the character calm. Empty base loop = falls back to Listening.")]
        public ModeSet calm = new ModeSet();

        [Tooltip("Played while the LLM reports the character in transition (tense but considering). Empty base loop = falls back to Listening.")]
        public ModeSet transition = new ModeSet();

        [Tooltip("Played while the LLM reports the character agitated. Empty base loop = falls back to Listening.")]
        public ModeSet agitated = new ModeSet();

        [Header("Face")]
        [Tooltip("How much of the facial expression baked into the body clips is kept (brows, eyes, nose, cheeks, mouth, jaw — not blinks). 1 = as authored. 0 = the clips' faces are removed, so the face is only mood + lip sync + procedural life (use it when clips carry a fixed expression, e.g. a frown baked into an idle). Play Mode only.")]
        [Range(0f, 1f)]
        public float clipFaceWeight = 1f;

        [Header("Mood blending (phases)")]
        [Tooltip("Pick idle loops from all three phase sets, weighted by a slowly eased mood, instead of only the reported phase's set: as she calms down her calm loops grow likelier and her agitated ones fade out, one loop rotation at a time. A phase change never cuts the loop she is in, it only shortens it when it no longer fits. Off = hard switch to the phase's set.")]
        public bool blendLoopsByMood = true;

        [Tooltip("Mood (0..100, the LLM's number) at and below which only the Calm loops are picked.")]
        [Range(0f, 100f)]
        public float calmMood = 20f;

        [Tooltip("Mood at and above which only the Agitated loops are picked. Halfway between Calm Mood and this, only Transition loops; in between they mix.")]
        [Range(0f, 100f)]
        public float agitatedMood = 75f;

        [Tooltip("How slowly the body follows the LLM's mood: seconds for the eased mood to cover about two thirds of a jump (60 → 20 takes ~2× this to settle). The face still follows the mood directly.")]
        [Min(0f)]
        public float moodEaseSeconds = 20f;

        /// <summary>How much each phase's loops count at this mood (sums to 1).</summary>
        public void PhaseWeights(float mood, out float calmW, out float transitionW, out float agitatedW)
        {
            float lo = Mathf.Min(calmMood, agitatedMood), hi = Mathf.Max(calmMood, agitatedMood);
            float mid = 0.5f * (lo + hi);
            calmW = transitionW = agitatedW = 0f;
            if (mood <= lo) calmW = 1f;
            else if (mood >= hi) agitatedW = 1f;
            else if (mood < mid) { transitionW = Mathf.InverseLerp(lo, mid, mood); calmW = 1f - transitionW; }
            else { agitatedW = Mathf.InverseLerp(mid, hi, mood); transitionW = 1f - agitatedW; }
        }

        /// <summary>A mood that sits squarely in the phase (used before the LLM has reported one).</summary>
        public float MoodFor(ConversationPhase phase) => phase switch
        {
            ConversationPhase.Calm => Mathf.Min(calmMood, agitatedMood) - 5f,
            ConversationPhase.Transition => 0.5f * (calmMood + agitatedMood),
            _ => Mathf.Max(calmMood, agitatedMood) + 10f,
        };

        [Header("Pacing (all sets)")]
        [Tooltip("One-shots never crossfade in faster than this (seconds), whatever their own Blend In says — short blends read as snapping.")]
        [Min(0f)]
        public float minOneShotBlendIn = 0.5f;

        [Tooltip("One-shots never crossfade back out faster than this (seconds).")]
        [Min(0f)]
        public float minOneShotBlendOut = 0.7f;

        [Tooltip("Quiet time (seconds) after a one-shot ends or the phase changes before the loop rotation may switch loops — keeps transitions from piling up.")]
        [Min(0f)]
        public float settleSeconds = 8f;

        [Tooltip("Idle loops drift slowly faster and slower around their rolled speed by up to this fraction (0.06 = ±6%), so a loop never repeats exactly the same. 0 = constant speed.")]
        [Range(0f, 0.3f)]
        public float loopSpeedDrift = 0.06f;

        [Tooltip("Roughly how many seconds one slow-fast swing of the drift takes.")]
        [Min(1f)]
        public float loopSpeedDriftPeriod = 14f;

        [Tooltip("Random seconds between spontaneous blinks (~15-20 per minute), mode-independent.")]
        public Vector2 blinkInterval = new Vector2(2.5f, 5.5f);

        public ModeSet Get(StudentMode mode) => mode == StudentMode.Reading ? reading : listening;

        /// <summary>The set authored for the phase, or Listening when that phase has no loop.</summary>
        public ModeSet Get(ConversationPhase phase)
        {
            ModeSet set = Authored(phase);
            return set != null && set.HasLoop ? set : listening;
        }

        /// <summary>The phase's own set as authored (no Listening fallback).</summary>
        public ModeSet Authored(ConversationPhase phase) => phase switch
        {
            ConversationPhase.Calm => calm,
            ConversationPhase.Transition => transition,
            _ => agitated,
        };

        /// <summary>True when at least one Calm / Transition / Agitated set has a loop.</summary>
        public bool HasPhases =>
            (calm != null && calm.HasLoop) || (transition != null && transition.HasLoop) || (agitated != null && agitated.HasLoop);

        private static readonly ConversationPhase[] AllPhases =
            { ConversationPhase.Calm, ConversationPhase.Transition, ConversationPhase.Agitated };

        /// <summary>
        /// Every one-shot of the phase sets the LLM may pick (Offer To Llm on,
        /// a clip assigned), merged by id across phases — the catalog the reply
        /// format lists.
        /// </summary>
        public List<LlmGesture> LlmGestures()
        {
            var result = new List<LlmGesture>();
            var byId = new Dictionary<string, LlmGesture>();
            foreach (ConversationPhase p in AllPhases)
            {
                ModeSet set = Authored(p);
                if (set == null || !set.HasLoop || set.oneShots == null) continue;
                foreach (OneShotEntry entry in set.oneShots)
                {
                    if (entry == null || entry.clip == null || !entry.offerToLlm) continue;
                    string id = GestureId(entry);
                    if (id == null || id == "none") continue;
                    if (!byId.TryGetValue(id, out LlmGesture gesture))
                    {
                        gesture = new LlmGesture { id = id };
                        byId[id] = gesture;
                        result.Add(gesture);
                    }
                    if (string.IsNullOrWhiteSpace(gesture.description) && !string.IsNullOrWhiteSpace(entry.llmDescription))
                        gesture.description = entry.llmDescription.Trim();
                    if (!gesture.phases.Contains(p)) gesture.phases.Add(p);
                }
            }
            return result;
        }

        /// <summary>
        /// The one-shot with this LLM id: from <paramref name="preferred"/>
        /// first, then any phase set. Null when no set has it.
        /// </summary>
        public OneShotEntry FindGesture(string id, ConversationPhase preferred, out ConversationPhase foundIn)
        {
            foundIn = preferred;
            if (string.IsNullOrWhiteSpace(id)) return null;
            OneShotEntry hit = FindIn(Authored(preferred), id);
            if (hit != null) return hit;
            foreach (ConversationPhase p in AllPhases)
            {
                if (p == preferred) continue;
                hit = FindIn(Authored(p), id);
                if (hit == null) continue;
                foundIn = p;
                return hit;
            }
            return null;
        }

        private static OneShotEntry FindIn(ModeSet set, string id)
        {
            if (set == null || set.oneShots == null) return null;
            foreach (OneShotEntry entry in set.oneShots)
                if (entry != null && entry.clip != null && GestureId(entry) == id) return entry;
            return null;
        }
    }
}
