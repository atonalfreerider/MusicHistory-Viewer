#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// A featured path drawn on the graph (docs/DESIGN.md §11): its songs in play order and the
    /// graph edges between consecutive songs.
    /// </summary>
    public sealed class GraphRoute
    {
        /// <summary>What the route draws (the featured path).</summary>
        public readonly object Key;
        public readonly List<SongNode> Nodes = new();
        /// <summary>Edges between consecutive songs that exist in the graph.</summary>
        public readonly List<InfluenceEdge> Edges = new();
        /// <summary>Per step: the edge from the song before (null for the first step, or when the graph has none).</summary>
        public readonly List<InfluenceEdge?> StepEdges = new();
        /// <summary>Colour of the route's main shared identity (the channel colour of its edges).</summary>
        public Color Color = Color.white;

        public GraphRoute(object key) => Key = key;

        /// <summary>The graph edge between two songs, either direction (null when there is none).</summary>
        public static InfluenceEdge? Between(SongNode a, SongNode b)
        {
            foreach (InfluenceEdge e in b.Incoming) if (e.Source == a) return e;
            foreach (InfluenceEdge e in a.Incoming) if (e.Source == b) return e;
            return null;
        }
    }

    /// <summary>
    /// Hover and click selection (fork of Unity-FDG's MouseBubbleHighlighter). The focus song
    /// glows, its influencers and influenced songs glow faintly, everything else dims; the focus'
    /// secondary edges appear (they are hidden otherwise) and its edges light up. A click selects
    /// a song (sticky focus, the walkthrough's target); a click on no bubble but within
    /// <see cref="EdgePickPixels"/> of a drawn edge selects the edge (its card, and the family a
    /// family tour plays); clicking empty space clears both.
    /// The walkthrough takes over with <see cref="ShowTourStep"/> and gives it back with
    /// <see cref="EndTour"/>. While the featured-paths panel is open, the hovered or selected
    /// path's route rests on the graph (<see cref="SetRoutePreview"/>): its songs and edges glow,
    /// everything else dims; hovering a song still shows that song.
    /// The tour steps (<see cref="ShowTourStep"/>, <see cref="ShowPathStep"/>, <see cref="ShowDuetStep"/>, <see cref="ShowMosaicStep"/>)
    /// grow the songs they light to three times their size (<see cref="SongNode.SetHighlighted"/>);
    /// hover, click, edge cards and route previews never resize a bubble.
    /// </summary>
    public sealed class HoverHighlighter : MonoBehaviour
    {
        public SongGraphLoader Loader = null!;
        [Tooltip("Extra labels shown for the most influential songs a focus influenced.")]
        public int InfluencedLabelLimit = 12;

        [Tooltip("A click this close (pixels) to a drawn edge, and on no bubble, selects the edge.")]
        public float EdgePickPixels = 7f;

        public SongNode? Hovered { get; private set; }
        public SongNode? Selected { get; private set; }
        public SongNode? Focus { get; private set; }
        /// <summary>A clicked edge (its card replaces the song info; family tours play its identity).</summary>
        public InfluenceEdge? SelectedEdge { get; private set; }
        /// <summary>The edge whose card is showing (null while a song is in focus).</summary>
        public InfluenceEdge? FocusEdge { get; private set; }
        /// <summary>True while the walkthrough owns highlighting.</summary>
        public bool Suspended { get; private set; }
        /// <summary>The route shown when nothing is hovered (the paths panel's hovered or selected path).</summary>
        public GraphRoute? RoutePreview { get; private set; }
        /// <summary>The route drawn right now (null while a song or an edge is in focus).</summary>
        public GraphRoute? FocusRoute { get; private set; }
        public event Action<SongNode?>? FocusChanged;
        public event Action<InfluenceEdge>? EdgeFocusChanged;

        readonly HashSet<SongNode> labelled = new();
        Vector2 pressPosition;
        bool pressed;
        bool applied;

        void Update()
        {
            if (Suspended || Loader == null || Loader.Nodes.Count == 0) return;
            Mouse? mouse = Mouse.current;
            if (mouse == null) return;
            Camera? cam = Camera.main;
            if (cam == null) return;

            if (CameraControl.UserIsNavigating())
            {
                Hovered = null;
                pressed = false;
                ApplyResting();
                return;
            }

            Vector2 pointer = mouse.position.ReadValue();
            if (Loader.Paths != null && Loader.Paths.ContainsScreenPoint(pointer))
            {
                // Over the paths panel: no picking through it, and its clicks select nothing here.
                Hovered = null;
                pressed = false;
                ApplyResting();
                return;
            }
            Hovered = Pick(cam, pointer);

            if (mouse.leftButton.wasPressedThisFrame)
            {
                pressed = true;
                pressPosition = pointer;
            }
            if (pressed && mouse.leftButton.wasReleasedThisFrame)
            {
                pressed = false;
                if ((pointer - pressPosition).sqrMagnitude < 36f)
                {
                    InfluenceEdge? edge = Hovered == null ? PickEdge(cam, pointer, EdgePickPixels, out _) : null;
                    if (edge != null) SelectEdge(edge);
                    else Select(Hovered);
                }
            }

            if (Hovered != null) ApplyFocus(Hovered);
            else ApplyResting();
        }

        /// <summary>Without a hover: the previewed route, else the selected edge's card, else the selected song (or nothing).</summary>
        void ApplyResting(bool force = false)
        {
            if (RoutePreview != null) ApplyRoute(RoutePreview, force);
            else if (SelectedEdge != null) ApplyEdgeFocus(SelectedEdge, force);
            else ApplyFocus(Selected, force);
        }

        /// <summary>
        /// The route that rests on the graph while nothing is hovered (null = none): the paths
        /// panel's hovered row, else its selected row.
        /// </summary>
        public void SetRoutePreview(GraphRoute? route)
        {
            if (ReferenceEquals(route, RoutePreview)) return;
            RoutePreview = route;
            if (Suspended || Hovered != null) return;
            ApplyResting(force: true);
        }

        /// <summary>A route: its songs glow and its edges light up (secondary ones appear); the rest dims.</summary>
        public void ApplyRoute(GraphRoute route, bool force = false)
        {
            if (!force && applied && FocusRoute == route) return;
            applied = true;
            Focus = null;
            FocusEdge = null;
            FocusRoute = route;
            HashSet<SongNode> onRoute = new(route.Nodes);
            HashSet<InfluenceEdge> routeEdges = new(route.Edges);
            foreach (SongNode n in Loader.Nodes)
            {
                n.SetState(onRoute.Contains(n) ? BubbleState.Focus : BubbleState.Dimmed);
                n.SetHighlighted(false);
            }
            foreach (InfluenceEdge e in Loader.Edges)
            {
                bool on = routeEdges.Contains(e);
                e.SetState(on ? EdgeState.Highlight : EdgeState.Dimmed);
                e.SetShown(e.IsTree || on || Loader.ShowAllSecondaryEdges);
            }
            // Only the route's songs keep labels (the rest dims away), and a glowing line joins them in
            // play order, so the path reads at overview scale.
            Loader.Labels.HideDimmed = true;
            SetExtraLabels(route.Nodes);
            if (Loader.RouteLine != null) Loader.RouteLine.Show(route);
            FocusChanged?.Invoke(null);
        }

        /// <summary>
        /// The drawn edge nearest <paramref name="screenPoint"/> on screen, within
        /// <paramref name="maxPixels"/> of its centre line (null when none is that close).
        /// </summary>
        public InfluenceEdge? PickEdge(Camera cam, Vector2 screenPoint, float maxPixels, out float pixels)
        {
            InfluenceEdge? best = null;
            pixels = float.MaxValue;
            foreach (InfluenceEdge e in Loader.Edges)
            {
                if (!e.Shown || !e.Renderer.enabled) continue;
                Vector3 a = cam.WorldToScreenPoint(e.Source.transform.position);
                Vector3 b = cam.WorldToScreenPoint(e.Target.transform.position);
                if (a.z <= cam.nearClipPlane || b.z <= cam.nearClipPlane) continue;
                float d = DistanceToSegment(screenPoint, a, b);
                if (d < pixels)
                {
                    pixels = d;
                    best = e;
                }
            }
            return pixels <= maxPixels ? best : null;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        public static SongNode? Pick(Camera cam, Vector2 screenPoint)
        {
            Ray ray = cam.ScreenPointToRay(screenPoint);
            return Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore)
                ? hit.collider.GetComponentInParent<SongNode>()
                : null;
        }

        public void Select(SongNode? node)
        {
            Selected = node;
            SelectedEdge = null;
            if (!Suspended)
            {
                if (Hovered != null) ApplyFocus(Hovered);
                else ApplyResting();
            }
        }

        /// <summary>Selects an edge (clears the song selection); its card shows until something else is selected.</summary>
        public void SelectEdge(InfluenceEdge? edge)
        {
            if (edge == null)
            {
                Select(null);
                return;
            }
            Selected = null;
            SelectedEdge = edge;
            if (!Suspended)
            {
                if (Hovered != null) ApplyFocus(Hovered);
                else ApplyEdgeFocus(edge);
            }
        }

        /// <summary>
        /// An edge card: the edge lights up and its two songs glow; for identity lineages the other
        /// songs and edges of the edge's family stay undimmed, so the whole shared identity shows.
        /// </summary>
        public void ApplyEdgeFocus(InfluenceEdge edge, bool force = false)
        {
            if (!force && applied && FocusEdge == edge) return;
            applied = true;
            Focus = null;
            FocusEdge = edge;
            FocusRoute = null;
            Loader.Labels.HideDimmed = Suspended;
            if (Loader.RouteLine != null) Loader.RouteLine.Show(null);
            SongGraphData? data = Loader.Data;
            int? family = edge.Record.FamilyId;
            HashSet<int> members = new();
            if (data != null && data.Family(family) is IdentityFamily f)
                foreach (FamilyMember m in f.Members) members.Add(m.NodeId);
            foreach (SongNode n in Loader.Nodes)
            {
                n.SetState(n == edge.Target ? BubbleState.Focus : n == edge.Source ? BubbleState.Related
                    : members.Contains(n.NodeId) ? BubbleState.Normal : BubbleState.Dimmed);
                n.SetHighlighted(false);
            }
            foreach (InfluenceEdge e in Loader.Edges)
            {
                bool same = family != null && e.Record.FamilyId == family;
                e.SetState(e == edge ? EdgeState.Highlight : same ? EdgeState.Normal : EdgeState.Dimmed);
                e.SetShown(e.IsTree || e == edge || same || Loader.ShowAllSecondaryEdges);
            }
            SetExtraLabels(new[] { edge.Source, edge.Target });
            EdgeFocusChanged?.Invoke(edge);
        }

        /// <summary>Highlights <paramref name="focus"/> with its influencers and influenced songs (null = none).</summary>
        public void ApplyFocus(SongNode? focus, bool force = false)
        {
            if (!force && applied && focus == Focus && FocusEdge == null && FocusRoute == null) return;
            applied = true;
            Focus = focus;
            FocusEdge = null;
            FocusRoute = null;
            if (Loader.Labels != null) Loader.Labels.HideDimmed = Suspended;
            if (Loader.RouteLine != null) Loader.RouteLine.Show(null);
            IReadOnlyList<SongNode> nodes = Loader.Nodes;
            IReadOnlyList<InfluenceEdge> edges = Loader.Edges;

            if (focus == null)
            {
                foreach (SongNode n in nodes)
                {
                    n.SetState(BubbleState.Normal);
                    n.SetHighlighted(false);
                }
                foreach (InfluenceEdge e in edges)
                {
                    e.SetState(EdgeState.Normal);
                    e.SetShown(e.IsTree || Loader.ShowAllSecondaryEdges);
                }
                SetExtraLabels(Array.Empty<SongNode>());
                FocusChanged?.Invoke(null);
                return;
            }

            HashSet<SongNode> related = new();
            foreach (SongNode n in focus.Influencers()) related.Add(n);
            foreach (SongNode n in focus.Influenced()) related.Add(n);
            foreach (SongNode n in nodes)
            {
                n.SetState(n == focus ? BubbleState.Focus : related.Contains(n) ? BubbleState.Related : BubbleState.Dimmed);
                // Hover and click never resize (the bubble under the pointer would jump).
                n.SetHighlighted(false);
            }
            foreach (InfluenceEdge e in edges)
            {
                bool incident = e.Source == focus || e.Target == focus;
                e.SetState(incident ? EdgeState.Highlight : EdgeState.Dimmed);
                e.SetShown(e.IsTree || incident || Loader.ShowAllSecondaryEdges);
            }

            List<SongNode> extra = new() { focus };
            extra.AddRange(focus.Influencers());
            extra.AddRange(focus.Influenced()
                .OrderByDescending(n => n.Song.Descendants)
                .ThenBy(n => n.NodeId)
                .Take(InfluencedLabelLimit));
            SetExtraLabels(extra);
            FocusChanged?.Invoke(focus);
        }

        /// <summary>Walkthrough step: the child glows, its tree parent glows faintly, the tree edge lights up.</summary>
        public void ShowTourStep(SongNode child, SongNode? parent) => ShowTourStep(child, parent, child.TreeEdge, null);

        /// <summary>
        /// Walkthrough step: the child glows, <paramref name="parent"/> (the tree parent, or the
        /// previous song of a family tour) glows faintly and <paramref name="stepEdge"/> lights up.
        /// With <paramref name="familyId"/> (a family tour) the family's other songs and edges stay
        /// undimmed instead of fading.
        /// </summary>
        public void ShowTourStep(SongNode child, SongNode? parent, InfluenceEdge? stepEdge, int? familyId)
        {
            Suspended = true;
            Loader.Labels.HideDimmed = true;
            Hovered = null;
            Focus = child;
            FocusEdge = null;
            FocusRoute = null;
            applied = true;
            if (Loader.RouteLine != null) Loader.RouteLine.Show(null);
            HashSet<int> members = new();
            if (Loader.Data?.Family(familyId) is IdentityFamily f)
                foreach (FamilyMember m in f.Members) members.Add(m.NodeId);
            foreach (SongNode n in Loader.Nodes)
            {
                n.SetState(n == child ? BubbleState.Focus : n == parent ? BubbleState.Related
                    : members.Contains(n.NodeId) ? BubbleState.Normal : BubbleState.Dimmed);
                n.SetHighlighted(n.State == BubbleState.Focus);
            }
            foreach (InfluenceEdge e in Loader.Edges)
            {
                bool step = e == stepEdge;
                bool intoChild = e.Target == child;
                bool same = familyId != null && e.Record.FamilyId == familyId;
                e.SetState(step ? EdgeState.Highlight : same ? EdgeState.Normal : EdgeState.Dimmed);
                e.SetShown(e.IsTree || intoChild || step || same || Loader.ShowAllSecondaryEdges);
            }
            List<SongNode> extra = new() { child };
            if (parent != null) extra.Add(parent);
            SetExtraLabels(extra);
            FocusChanged?.Invoke(child);
        }

        /// <summary>
        /// Path tour step: the playing song glows, the song heard before glows faintly, the rest of
        /// the route stays undimmed with its edges lit, everything else fades; every song of the
        /// route keeps its label. With <paramref name="bothLit"/> (a mashup changeover: one song's
        /// vocal over the other's instrumental) <paramref name="previous"/> glows as brightly too.
        /// </summary>
        public void ShowPathStep(SongNode child, SongNode? previous, InfluenceEdge? stepEdge, GraphRoute route, bool bothLit = false)
        {
            Suspended = true;
            Loader.Labels.HideDimmed = true;
            Hovered = null;
            Focus = child;
            FocusEdge = null;
            FocusRoute = null;
            applied = true;
            if (Loader.RouteLine != null) Loader.RouteLine.Show(null);
            HashSet<SongNode> onRoute = new(route.Nodes);
            HashSet<InfluenceEdge> routeEdges = new(route.Edges);
            foreach (SongNode n in Loader.Nodes)
            {
                n.SetState(n == child ? BubbleState.Focus : n == previous ? (bothLit ? BubbleState.Focus : BubbleState.Related)
                    : onRoute.Contains(n) ? BubbleState.Normal : BubbleState.Dimmed);
                n.SetHighlighted(n.State == BubbleState.Focus);
            }
            foreach (InfluenceEdge e in Loader.Edges)
            {
                bool on = e == stepEdge || routeEdges.Contains(e);
                e.SetState(on ? EdgeState.Highlight : EdgeState.Dimmed);
                e.SetShown(e.IsTree || on || Loader.ShowAllSecondaryEdges);
            }
            SetExtraLabels(route.Nodes);
            FocusChanged?.Invoke(child);
        }

        /// <summary>
        /// Duet loop: both singing songs glow, the edge between them lights up (the route's other
        /// edges rest undimmed), a handoff's borrowed instrumental's song <paramref name="marked"/>
        /// glows faintly, the rest of the route stays undimmed with its labels, everything else fades.
        /// </summary>
        public void ShowDuetStep(SongNode a, SongNode b, SongNode? marked, InfluenceEdge? pairEdge, GraphRoute route)
        {
            Suspended = true;
            Loader.Labels.HideDimmed = true;
            Hovered = null;
            Focus = b;
            FocusEdge = null;
            FocusRoute = null;
            applied = true;
            if (Loader.RouteLine != null) Loader.RouteLine.Show(null);
            HashSet<SongNode> onRoute = new(route.Nodes);
            HashSet<InfluenceEdge> routeEdges = new(route.Edges);
            foreach (SongNode n in Loader.Nodes)
            {
                n.SetState(n == a || n == b ? BubbleState.Focus : n == marked ? BubbleState.Related
                    : onRoute.Contains(n) ? BubbleState.Normal : BubbleState.Dimmed);
                n.SetHighlighted(n.State == BubbleState.Focus);
            }
            foreach (InfluenceEdge e in Loader.Edges)
            {
                bool lit = e == pairEdge;
                bool rest = !lit && routeEdges.Contains(e);
                e.SetState(lit ? EdgeState.Highlight : rest ? EdgeState.Normal : EdgeState.Dimmed);
                e.SetShown(e.IsTree || lit || rest || Loader.ShowAllSecondaryEdges);
            }
            SetExtraLabels(route.Nodes);
            FocusChanged?.Invoke(b);
        }

        /// <summary>
        /// Melody mosaic: the songs sounding now (<paramref name="lit"/>: the target, the playing piece's
        /// song, the harmony voices) glow at three times their size, <paramref name="related"/> (the
        /// target under a piece) glows faintly, the mosaic's other songs (<paramref name="members"/>) rest
        /// undimmed with their labels, everything else fades; edges dim (a mosaic is no lineage).
        /// <paramref name="focus"/> is the song the wheel and the HUD follow.
        /// </summary>
        public void ShowMosaicStep(IReadOnlyCollection<SongNode> lit, SongNode? related, IReadOnlyCollection<SongNode> members, SongNode focus)
        {
            Suspended = true;
            Loader.Labels.HideDimmed = true;
            Hovered = null;
            Focus = focus;
            FocusEdge = null;
            FocusRoute = null;
            applied = true;
            if (Loader.RouteLine != null) Loader.RouteLine.Show(null);
            HashSet<SongNode> on = new(lit), rest = new(members);
            foreach (SongNode n in Loader.Nodes)
            {
                n.SetState(on.Contains(n) ? BubbleState.Focus : n == related ? BubbleState.Related
                    : rest.Contains(n) ? BubbleState.Normal : BubbleState.Dimmed);
                n.SetHighlighted(n.State == BubbleState.Focus);
            }
            foreach (InfluenceEdge e in Loader.Edges)
            {
                e.SetState(EdgeState.Dimmed);
                e.SetShown(e.IsTree || Loader.ShowAllSecondaryEdges);
            }
            List<SongNode> labels = new(members);
            foreach (SongNode n in lit) if (!rest.Contains(n)) labels.Add(n);
            SetExtraLabels(labels);
            FocusChanged?.Invoke(focus);
        }

        public void EndTour()
        {
            Suspended = false;
            Loader.Labels.HideDimmed = false;
            Reapply();
        }

        /// <summary>Re-applies the hover, else the selected edge's card, else the selected song (after V or a tour).</summary>
        public void Reapply()
        {
            if (Hovered != null) ApplyFocus(Hovered, force: true);
            else ApplyResting(force: true);
        }

        void SetExtraLabels(IEnumerable<SongNode> wanted)
        {
            HashSet<SongNode> next = new(wanted);
            foreach (SongNode n in labelled)
                if (!next.Contains(n)) n.RequestLabel(Loader.Labels, false);
            foreach (SongNode n in next) n.RequestLabel(Loader.Labels, true);
            labelled.Clear();
            labelled.UnionWith(next);
            foreach (SongNode n in next) n.RefreshLabel(Loader.Labels);
            // Pinned labels grey out with their dimmed bubbles.
            foreach (SongNode n in Loader.PinnedLabelNodes) n.RefreshLabel(Loader.Labels);
        }

        void OnDisable()
        {
            // Only when this component alone is switched off. Scene unload, play-mode exit and
            // Destroy leave 'enabled' true, and by then the songs and their label boxes may
            // already be destroyed (no fixed order), so nothing is re-applied.
            if (enabled || !gameObject.activeInHierarchy) return;
            if (Loader == null || Loader.Nodes.Count == 0 || Suspended) return;
            ApplyFocus(null, force: true);
        }
    }
}
