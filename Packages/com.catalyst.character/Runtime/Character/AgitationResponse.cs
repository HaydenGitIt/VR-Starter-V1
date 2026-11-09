using System;
using System.Collections.Generic;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// One agitation band of a character's speaking behaviour: while the
    /// character speaks a reply whose self-reported agitation (0 = happy and
    /// calm, 100 = as worked up as they get) falls inside [min, max], one of
    /// the band's loops plays instead of the ambient sequence. Bands are
    /// checked in list order and the first match wins; a band with no loops —
    /// or an agitation no band covers — leaves the ambient sequence running.
    /// </summary>
    [Serializable]
    public class AgitationResponse
    {
        [Tooltip("Lowest agitation this band covers, inclusive (0 = happy and calm).")]
        [Range(0, 100)]
        public int minAgitation = 0;

        [Tooltip("Highest agitation this band covers, inclusive (100 = as agitated as they get). Bands are checked in list order; the first one containing the value wins.")]
        [Range(0, 100)]
        public int maxAgitation = 100;

        [Tooltip("Loops to choose from while the character speaks at this agitation — one is picked at random per reply and repeats until the reply ends. Loop-enabled clips wrap cleanly. Empty = the ambient sequence keeps playing.")]
        public List<AnimationClip> speakingLoops = new List<AnimationClip>();

        [Tooltip("Crossfade from the ambient sequence into the chosen loop when the reply starts playing, seconds.")]
        [Min(0f)]
        public float blendIn = 0.5f;

        [Tooltip("Crossfade back into the ambient sequence when the reply ends, seconds.")]
        [Min(0f)]
        public float blendOut = 0.8f;

        [Tooltip("Playback speed of the chosen loop — a random value in this range per reply, so repeats don't look identical. 1 = authored speed.")]
        public Vector2 speedRange = new Vector2(0.95f, 1.05f);

        public bool Contains(int agitation) => agitation >= minAgitation && agitation <= maxAgitation;

        public bool HasLoops
        {
            get
            {
                if (speakingLoops == null) return false;
                foreach (AnimationClip clip in speakingLoops)
                    if (clip != null) return true;
                return false;
            }
        }
    }
}
