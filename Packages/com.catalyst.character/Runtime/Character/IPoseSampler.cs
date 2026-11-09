using System;

namespace catalyst.xr67
{
    /// <summary>
    /// A component that poses the character's skeleton itself (a PlayableGraph
    /// player, an edit-mode preview) and says so after every evaluation, so
    /// layers that sit on top of the pose — ProceduralIdleMotion,
    /// BonePoseSmoother — can run right after it, in Edit Mode as well as in
    /// Play Mode. The layers find every sampler on their GameObject with
    /// GetComponents&lt;IPoseSampler&gt;(); no sampler type is known by name.
    /// </summary>
    public interface IPoseSampler
    {
        /// <summary>Raised right after the pose was evaluated.</summary>
        event Action PoseSampled;

        /// <summary>
        /// True while this sampler advances the pose on its own Edit Mode
        /// ticks — the layers then follow <see cref="PoseSampled"/> instead
        /// of stepping on their own editor tick.
        /// </summary>
        bool IsPreviewing { get; }
    }

    /// <summary>
    /// Supplies the idle-motion tuning in effect for the character (for
    /// example a student profile's per-mode settings). ProceduralIdleMotion
    /// prefers a sibling source over its own local settings.
    /// </summary>
    public interface IIdleMotionSettingsSource
    {
        /// <summary>Settings to use right now, or null to fall back to the local ones.</summary>
        IdleMotionSettings ActiveIdleSettings { get; }

        /// <summary>The asset those settings live in (for editor dirtying / undo), or null.</summary>
        UnityEngine.Object IdleSettingsAsset { get; }
    }
}
