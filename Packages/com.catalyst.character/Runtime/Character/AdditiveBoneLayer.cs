using System.Collections.Generic;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Bookkeeping for a layer that writes bones additively (postural sway,
    /// a head turn) so it never stacks on its own previous frame. Before a
    /// bone is touched, <see cref="Begin"/> checks whether it still holds
    /// exactly what this layer wrote last time — meaning nothing re-posed the
    /// skeleton since: Edit Mode without a pose sampler, an Animator culled
    /// off-screen, a clip with no curve for that bone — and if so puts the
    /// clean base pose back first. <see cref="Commit"/> remembers what was
    /// written; <see cref="RestoreAll"/> undoes the layer's last write
    /// wherever it still stands (for OnDisable).
    /// </summary>
    public sealed class AdditiveBoneLayer
    {
        private struct Entry
        {
            public Quaternion baseRot, lastRot;
            public Vector3 basePos, lastPos;
            public bool written;
        }

        private readonly Dictionary<Transform, Entry> _entries = new Dictionary<Transform, Entry>();
        private readonly List<Transform> _touched = new List<Transform>();

        private const float RotationTolerance = 1f - 1e-6f;   // |dot| above this = the same rotation
        private const float PositionToleranceSqr = 1e-12f;

        /// <summary>Call before modifying the bone this frame. Restores the base when the bone still carries last frame's write, then records the base.</summary>
        public void Begin(Transform bone)
        {
            if (bone == null) return;
            if (_entries.TryGetValue(bone, out Entry e) && e.written && Holds(bone, e))
            {
                bone.localRotation = e.baseRot;
                bone.localPosition = e.basePos;
            }
            e.baseRot = bone.localRotation;
            e.basePos = bone.localPosition;
            e.written = false;
            _entries[bone] = e;
            _touched.Add(bone);
        }

        /// <summary>Call after the frame's writes: remembers what each touched bone ended up with.</summary>
        public void Commit()
        {
            foreach (Transform bone in _touched)
            {
                if (bone == null || !_entries.TryGetValue(bone, out Entry e)) continue;
                e.lastRot = bone.localRotation;
                e.lastPos = bone.localPosition;
                e.written = true;
                _entries[bone] = e;
            }
            _touched.Clear();
        }

        /// <summary>Puts every bone that still carries this layer's last write back to its base pose.</summary>
        public void RestoreAll()
        {
            foreach (KeyValuePair<Transform, Entry> kv in _entries)
            {
                Transform bone = kv.Key;
                Entry e = kv.Value;
                if (bone == null || !e.written || !Holds(bone, e)) continue;
                bone.localRotation = e.baseRot;
                bone.localPosition = e.basePos;
            }
            _touched.Clear();
        }

        /// <summary>Forgets everything (after RestoreAll, or when the rig changes).</summary>
        public void Reset()
        {
            _entries.Clear();
            _touched.Clear();
        }

        private static bool Holds(Transform bone, in Entry e)
        {
            return Mathf.Abs(Quaternion.Dot(bone.localRotation, e.lastRot)) >= RotationTolerance
                && (bone.localPosition - e.lastPos).sqrMagnitude <= PositionToleranceSqr;
        }
    }
}
