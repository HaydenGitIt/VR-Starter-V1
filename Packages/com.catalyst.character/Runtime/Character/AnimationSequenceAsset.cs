using System.Collections.Generic;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Saved AnimationSequencePlayer setup — the steps (clips, holds, blends,
    /// speeds, start offsets) plus loop blend and total duration — so a
    /// sequence can be authored once on a preview character, saved, and
    /// loaded onto other characters or into the recorder. Create via the
    /// component's Save To Asset button or Assets > Create > xr67 >
    /// Animation Sequence.
    /// </summary>
    [CreateAssetMenu(fileName = "AnimationSequence", menuName = "xr67/Animation Sequence")]
    public class AnimationSequenceAsset : ScriptableObject
    {
        public List<AnimationSequencePlayer.Step> steps = new List<AnimationSequencePlayer.Step>();
        public float loopBlend = 0.5f;
        public float totalDuration;

        /// <summary>Copies the player's sequence settings into this asset.</summary>
        public void CopyFrom(AnimationSequencePlayer player)
        {
            steps = CloneSteps(player.steps);
            loopBlend = player.loopBlend;
            totalDuration = player.totalDuration;
        }

        /// <summary>Copies this asset's sequence settings onto the player.</summary>
        public void ApplyTo(AnimationSequencePlayer player)
        {
            player.steps = CloneSteps(steps);
            player.loopBlend = loopBlend;
            player.totalDuration = totalDuration;
        }

        private static List<AnimationSequencePlayer.Step> CloneSteps(List<AnimationSequencePlayer.Step> source)
        {
            var clones = new List<AnimationSequencePlayer.Step>();
            if (source == null) return clones;
            foreach (AnimationSequencePlayer.Step step in source)
                clones.Add(new AnimationSequencePlayer.Step
                {
                    clip = step.clip,
                    hold = step.hold,
                    blendIn = step.blendIn,
                    speed = step.speed,
                    startOffset = step.startOffset,
                });
            return clones;
        }
    }
}
