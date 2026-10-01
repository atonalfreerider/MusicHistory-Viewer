#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// One song (fork of Unity-FDG's NodeSphere). The root object sits at the song's position at
    /// scale 1 and carries a sphere collider for picking; a child quad is the bubble, drawn by the
    /// camera-facing SongBubble shader with a shared material (fill = key, ring = decade). Bubble
    /// area is proportional to descendants + 1. The label is a separate world label, so its size
    /// never depends on the bubble's size.
    /// While a tour highlights the song (the playing song, both singers of a duet, the vocal and the
    /// instrumental of a changeover: <see cref="SetHighlighted"/>) the whole node grows to
    /// <see cref="HighlightScale"/> times its size, easing over <see cref="ScaleSeconds"/>: the
    /// bubble, its photo and glow (children), its picking collider (on the root), its label's anchor
    /// radius and the edges' insets follow (<see cref="DisplayRadius"/>), and the camera frames the
    /// size it is heading to (<see cref="TargetRadius"/>).
    /// </summary>
    public sealed class SongNode : MonoBehaviour
    {
        static Mesh? quadMesh;
        /// <summary>Size of a highlighted bubble (times its own).</summary>
        public const float HighlightScale = 3f;
        /// <summary>How long a bubble takes to grow or shrink back (smooth in and out).</summary>
        public const float ScaleSeconds = .4f;
        static readonly HashSet<SongNode> scaling = new();
        float scale = 1f, scaleFrom = 1f, scaleTo = 1f, scaleT = 1f;

        [NonSerialized] public SongRecord Song = null!;
        public float Radius;
        public Color KeyColor;
        public Color DecadeColor;
        public SongNode? TreeParent;
        public InfluenceEdge? TreeEdge;
        public readonly List<SongNode> TreeChildren = new();
        public readonly List<InfluenceEdge> Incoming = new();
        public readonly List<InfluenceEdge> Outgoing = new();
        /// <summary>Always labelled (one of the most influential songs).</summary>
        public bool LabelPinned;
        [NonSerialized] public WorldLabel? Label;

        MeshRenderer bubbleRenderer = null!;
        MeshRenderer? photoRenderer;
        string photoId = "";
        BubbleState state = BubbleState.Normal;
        bool labelRequested;

        public BubbleState State => state;
        /// <summary>The node's current size factor (1, easing to <see cref="HighlightScale"/> while highlighted).</summary>
        public float Scale => scale;
        /// <summary>A tour highlights this song: it grows (or has grown) to <see cref="HighlightScale"/>.</summary>
        public bool Highlighted => scaleTo > 1f;
        /// <summary>The radius drawn now (world units).</summary>
        public float DisplayRadius => Radius * scale;
        /// <summary>The radius it is heading to (what the camera frames).</summary>
        public float TargetRadius => Radius * scaleTo;
        public MeshRenderer BubbleRenderer => bubbleRenderer;
        /// <summary>The artist photo on the bubble (<see cref="BubblePhotos"/>; null when the song has none).</summary>
        public MeshRenderer? PhotoRenderer => photoRenderer;
        /// <summary>The photo's image id ("artist-&lt;QID&gt;"; empty when none).</summary>
        public string PhotoId => photoId;
        public int NodeId => Song.NodeId;
        public string LabelText => $"<b><noparse>{Song.Title}</noparse></b>\n<size=78%><color=#c3c8d0><noparse>{Song.Artist}</noparse> · {Song.Year}</color></size>" +
                                   (Song.IsValidationExtra ? "\n<size=64%><color=#ffcf4a><i>validation control, outside the ranked list</i></color></size>" : "");

        public static SongNode Create(SongRecord song, Vector3 position, float radius, Transform parent)
        {
            GameObject go = new($"{song.NodeId:0000} {song.Title}");
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            SongNode node = go.AddComponent<SongNode>();
            node.Song = song;
            node.Radius = radius;
            node.KeyColor = SongPalette.KeyColor(song.TonicPc, song.Minor);
            node.DecadeColor = SongPalette.DecadeColor(song.Year);

            SphereCollider picker = go.AddComponent<SphereCollider>();
            picker.radius = radius;

            GameObject bubble = new("Bubble");
            bubble.transform.SetParent(go.transform, false);
            bubble.transform.localScale = Vector3.one * (radius * 2f);
            bubble.AddComponent<MeshFilter>().sharedMesh = QuadMesh();
            node.bubbleRenderer = bubble.AddComponent<MeshRenderer>();
            node.bubbleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            node.bubbleRenderer.receiveShadows = false;
            node.bubbleRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            node.bubbleRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            node.bubbleRenderer.sharedMaterial = GraphMaterials.Bubble(node.KeyColor, node.DecadeColor, BubbleState.Normal);
            return node;
        }

        /// <summary>Unit quad in the XY plane; bounds are a cube so camera-facing discs are never culled early.</summary>
        public static Mesh QuadMesh()
        {
            if (quadMesh != null) return quadMesh;
            quadMesh = new Mesh
            {
                name = "Song Bubble Quad",
                hideFlags = HideFlags.DontSave,
                vertices = new[]
                {
                    new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
                    new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0)
                },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 }
            };
            quadMesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return quadMesh;
        }

        public void SetState(BubbleState newState)
        {
            if (newState == state) return;
            state = newState;
            // Teardown: the bubble may already be destroyed (no fixed destruction order).
            if (bubbleRenderer != null) bubbleRenderer.sharedMaterial = GraphMaterials.Bubble(KeyColor, DecadeColor, newState);
            if (photoRenderer != null) photoRenderer.sharedMaterial = BubblePhotos.MaterialFor(photoId, newState);
        }

        /// <summary>
        /// Grows the node to <see cref="HighlightScale"/> (true) or back to its own size (false),
        /// easing over <see cref="ScaleSeconds"/> from wherever it is (<see cref="TickScales"/> runs it);
        /// <paramref name="immediate"/> jumps there.
        /// </summary>
        public void SetHighlighted(bool on, bool immediate = false)
        {
            float target = on ? HighlightScale : 1f;
            if (Mathf.Approximately(target, scaleTo) && !immediate) return;
            scaleFrom = scale;
            scaleTo = target;
            scaleT = 0f;
            if (immediate || ScaleSeconds <= 0f)
            {
                scaleT = 1f;
                ApplyScale(target);
                scaling.Remove(this);
                return;
            }
            scaling.Add(this);
        }

        /// <summary>Advances every growing or shrinking node by <paramref name="dt"/> seconds.</summary>
        public static void TickScales(float dt)
        {
            if (scaling.Count == 0) return;
            List<SongNode>? done = null;
            foreach (SongNode n in scaling)
            {
                if (n == null)
                {
                    (done ??= new List<SongNode>()).Add(n!);
                    continue;
                }
                n.scaleT = Mathf.Min(1f, n.scaleT + Mathf.Max(0f, dt) / ScaleSeconds);
                float u = n.scaleT * n.scaleT * (3f - 2f * n.scaleT);
                n.ApplyScale(Mathf.Lerp(n.scaleFrom, n.scaleTo, u));
                if (n.scaleT >= 1f) (done ??= new List<SongNode>()).Add(n);
            }
            if (done != null) foreach (SongNode n in done) scaling.Remove(n);
        }

        /// <summary>Every growing or shrinking node jumps to its end size (edit-mode captures).</summary>
        public static void SettleScales() => TickScales(ScaleSeconds * 2f);

        /// <summary>Nodes still easing.</summary>
        public static int ScalingCount => scaling.Count;

        void ApplyScale(float s)
        {
            scale = s;
            if (this == null) return;
            transform.localScale = Vector3.one * s;
            if (Label != null && LabelLayer.IsAlive(Label)) Label.AnchorRadius = Radius * s;
            OnMoved();
        }

        /// <summary>Puts photo <paramref name="id"/>'s renderer on this bubble (its material follows the bubble's state).</summary>
        public void AttachPhoto(MeshRenderer renderer, string id)
        {
            photoRenderer = renderer;
            photoId = id;
            renderer.sharedMaterial = BubblePhotos.MaterialFor(id, state);
        }

        /// <summary>Removes the photo (its object is destroyed by the caller).</summary>
        public void DetachPhoto()
        {
            photoRenderer = null;
            photoId = "";
        }

        /// <summary>Called when the live simulation moved this node.</summary>
        public void OnMoved()
        {
            foreach (InfluenceEdge e in Incoming) e.UpdateGeometry();
            foreach (InfluenceEdge e in Outgoing) e.UpdateGeometry();
        }

        /// <summary>Requests a label beyond the pinned set (hover, walkthrough); created on first use.</summary>
        public void RequestLabel(LabelLayer layer, bool requested)
        {
            labelRequested = requested;
            RefreshLabel(layer);
        }

        public void RefreshLabel(LabelLayer layer)
        {
            // Teardown (scene unload, play-mode exit, a reload): the layer or this label's text box
            // may already be destroyed. Leave the label alone then; never create one mid-teardown.
            if (layer == null || this == null) return;
            if (Label != null && !LabelLayer.IsAlive(Label)) return;
            bool want = LabelPinned || labelRequested;
            if (want && Label == null)
                Label = layer.Create(LabelText, transform, transform.position, DisplayRadius, LabelPlacement.Right);
            if (Label != null)
            {
                layer.SetVisible(Label, want);
                layer.SetDimmed(Label, state == BubbleState.Dimmed);
                // Focus first, then related or requested songs, then the most influential.
                float influence = Song.Descendants + Song.RefCount * .01f;
                Label.Priority = state switch
                {
                    BubbleState.Focus => 1e8f,
                    BubbleState.Related => 1e7f + influence,
                    _ when labelRequested => 1e7f + influence,
                    BubbleState.Dimmed => influence,
                    _ => 1e6f + influence
                };
            }
        }

        public IEnumerable<SongNode> Influencers()
        {
            foreach (InfluenceEdge e in Incoming) yield return e.Source;
        }

        public IEnumerable<SongNode> Influenced()
        {
            foreach (InfluenceEdge e in Outgoing) yield return e.Target;
        }

        public static void ReleaseSharedMesh()
        {
            if (quadMesh == null) return;
            if (Application.isPlaying) Destroy(quadMesh);
            else DestroyImmediate(quadMesh);
            quadMesh = null;
        }
    }
}
