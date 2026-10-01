#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FDG;
using MusicHistory.Playback;
using MusicHistory.Walkthrough;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// Builds the song influence graph from the graph database (docs/DESIGN.md §10; fork of
    /// Unity-FDG's LoadFromSqliteDb, bound to the scene by the same script GUID).
    ///
    /// One bubble per song at its layout position (time runs left to right by default), tree
    /// edges always visible, secondary edges on hover, a decade timeline, a HUD, hover/click
    /// highlighting and the walkthrough. The live force-directed simulation is off by default
    /// (F toggles it) and axis-locked so it can never move a song off its date.
    ///
    /// DB path: -musicHistoryDb &lt;path&gt; on the command line, else the inspector's DbPath, else
    /// &lt;repo&gt;/data/graph/music_graph.db, else StreamingAssets/music_graph.db (player builds), else
    /// &lt;repo&gt;/data/graph/demo_graph.db. MIDI paths are resolved against the DB's folder.
    ///
    /// Featured paths (recording previews, <see cref="PathCatalog"/>): -musicHistoryPaths &lt;paths.json&gt;,
    /// else the inspector's PathsFile, else &lt;repo&gt;/data/audio/renders/paths.json. A missing file
    /// gives an empty list; the P panel says so.
    ///
    /// Mashup mixes (<see cref="MashupCatalog"/>): -musicHistoryMashups &lt;mashups.json&gt;, else the
    /// inspector's MashupsFile, else &lt;repo&gt;/data/audio/mashups/mashups.json. A path with a mashup
    /// plays it as one continuous mix with the melody graph (<see cref="MelodyGraphPanel"/>); a
    /// missing file leaves every path on its per-step previews.
    ///
    /// Duet loops (<see cref="DuetCatalog"/>, DESIGN.md §16): -musicHistoryDuets &lt;duets.json&gt;, else
    /// the inspector's DuetsFile, else &lt;data&gt;/audio/duets/duets.json. A path with a duet loop offers
    /// it next to its narrated mix (K); a missing file offers none.
    ///
    /// Melody mosaics (<see cref="MosaicCatalog"/>, DESIGN.md §17): -musicHistoryMosaics &lt;mosaics.json&gt;,
    /// else the inspector's MosaicsFile, else &lt;data&gt;/audio/mosaics/mosaics.json. The featured-paths
    /// panel lists them (O); a missing file lists none.
    ///
    /// Artist photos (<see cref="ArtistImages"/>) also go onto the bubbles (<see cref="BubblePhotos"/>).
    /// </summary>
    [RequireComponent(typeof(ForceDirectedGraph))]
    public sealed class SongGraphLoader : MonoBehaviour
    {
        public const string CommandLineFlag = "-musicHistoryDb";

        [Header("Graph database")]
        [Tooltip("Blank: -musicHistoryDb <path>, else <repo>/data/graph/music_graph.db, else StreamingAssets/music_graph.db, else <repo>/data/graph/demo_graph.db.")]
        public string DbPath = "";
        [Tooltip("Build in Start (off for editor tools that build explicitly).")]
        public bool BuildOnStart = true;

        [Header("Layout")]
        public TimeAxisView TimeAxis = TimeAxisView.LeftToRight;

        [Header("Bubbles (area proportional to descendants + 1)")]
        [Tooltip("Radius = scale x sqrt(descendants + 1) when the layout gives no display_radius.")]
        [Min(.01f)] public float BubbleRadiusScale = .2f;
        [Min(.01f)] public float MinBubbleRadius = .22f;
        [Min(.1f)] public float MaxBubbleRadius = 6f;
        public bool UseLayoutDisplayRadius = true;

        [Header("Edges")]
        [Min(.001f)] public float TreeEdgeWidth = .16f;
        [Min(.001f)] public float SecondaryEdgeWidth = .08f;
        [Min(0f)] public float EdgePadding = .05f;
        [Tooltip("A tree edge leading into at least this many descendants is drawn as a trunk.")]
        [Min(1)] public int TrunkDescendants = 10;
        public bool ShowAllSecondaryEdges;

        [Header("Featured paths")]
        [Tooltip("Blank: -musicHistoryPaths <paths.json>, else <repo>/data/audio/renders/paths.json.")]
        public string PathsFile = "";
        [Tooltip("Blank: -musicHistoryMashups <mashups.json>, else <repo>/data/audio/mashups/mashups.json.")]
        public string MashupsFile = "";
        [Tooltip("Blank: -musicHistoryNarration <narration.json>, else <data>/audio/narration/narration.json.")]
        public string NarrationFile = "";
        [Tooltip("Blank: -musicHistoryArtists <artists.json>, else <data>/images/artists.json.")]
        public string ArtistsFile = "";
        [Tooltip("Blank: -musicHistoryDuets <duets.json>, else <data>/audio/duets/duets.json.")]
        public string DuetsFile = "";
        [Tooltip("Blank: -musicHistoryMosaics <mosaics.json>, else <data>/audio/mosaics/mosaics.json.")]
        public string MosaicsFile = "";

        [Header("Labels")]
        [Range(0, 300)] public int AlwaysLabelledSongs = 40;
        [Min(.001f)] public float LabelScreenSize = .1f;

        [Header("Camera")]
        [Tooltip("Camera driven by the viewer; blank = Camera.main.")]
        public Camera? ViewCameraOverride;
        [Tooltip("Screen area (normalized) the overview fills, leaving the HUD panels clear.")]
        public Rect OverviewViewport = new(.01f, .09f, .91f, .71f);

        public SongGraphData? Data { get; private set; }
        public GraphFrame Frame { get; private set; } = new();
        public string ResolvedDbPath { get; private set; } = "";
        public string DbPathReason { get; private set; } = "";
        public double BuildMilliseconds { get; private set; }
        public IReadOnlyList<SongNode> Nodes => nodes;
        public IReadOnlyList<InfluenceEdge> Edges => edges;
        public IReadOnlyList<SongNode> PinnedLabelNodes => pinned;
        public LabelLayer Labels { get; private set; } = null!;
        public HoverHighlighter Highlighter { get; private set; } = null!;
        public GraphHud Hud { get; private set; } = null!;
        public WalkthroughDirector Director { get; private set; } = null!;
        /// <summary>The featured paths, bound to this graph (empty when paths.json is missing).</summary>
        public PathCatalog Catalog { get; private set; } = PathCatalog.Empty("not loaded");
        /// <summary>The featured-paths button, list and now-playing strip (P).</summary>
        public FeaturedPathsPanel? Paths { get; private set; }
        /// <summary>The featured paths' mashup mixes, bound to this graph and the paths (empty when mashups.json is missing).</summary>
        public MashupCatalog Mashups { get; private set; } = MashupCatalog.Empty("not loaded");
        /// <summary>The melody graph shown while a mashup or a duet loop plays (M).</summary>
        public MelodyGraphPanel? MelodyGraph { get; private set; }
        /// <summary>The featured paths' duet loops, bound to this graph and the paths (empty when duets.json is missing).</summary>
        public DuetCatalog Duets { get; private set; } = DuetCatalog.Empty("not loaded");
        /// <summary>The melody mosaics, bound to this graph (empty when mosaics.json is missing).</summary>
        public MosaicCatalog Mosaics { get; private set; } = MosaicCatalog.Empty("not loaded");
        /// <summary>The artist photos on the bubbles.</summary>
        public BubblePhotos? BubblePhotos { get; private set; }
        /// <summary>The narrated walkthroughs (data/audio/narration/narration.json; empty when missing).</summary>
        public NarrationCatalog NarrationCatalog { get; private set; } = NarrationCatalog.Empty("not loaded");
        /// <summary>The artist photos the narration shows (data/images/artists.json; empty when missing).</summary>
        public ArtistImages ArtistImages { get; private set; } = ArtistImages.Empty("not loaded");
        /// <summary>Plays the narration of a narrated mashup and ducks the mix under it (N).</summary>
        public NarrationPlayer? Narration { get; private set; }
        /// <summary>The narration's caption and photo popup.</summary>
        public NarrationOverlay? NarrationOverlay { get; private set; }
        /// <summary>Horizontal / vertical HUD layout from the screen's shape.</summary>
        public ViewerLayout? Layout { get; private set; }
        /// <summary>The glowing line through a previewed featured path.</summary>
        public RouteLine? RouteLine { get; private set; }
        public TimelineAxis Timeline { get; private set; } = null!;
        public ForceDirectedGraph Simulation { get; private set; } = null!;
        public Camera? ViewCamera => ViewCameraOverride != null ? ViewCameraOverride : Camera.main;
        public bool AllLabels { get; private set; }

        public event Action<SongGraphLoader>? Built;

        readonly List<SongNode> nodes = new();
        readonly List<InfluenceEdge> edges = new();
        readonly List<SongNode> pinned = new();
        readonly Dictionary<FeaturedPath, GraphRoute> routes = new();
        readonly Dictionary<Mosaic, GraphRoute> mosaicRoutes = new();
        GameObject? graphRoot;

        public SongNode NodeById(int nodeId) => nodes[nodeId - 1];

        void Start()
        {
            if (!BuildOnStart) return;
            string path = ResolveDatabasePath(DbPath, out string reason);
            if (!File.Exists(path))
            {
                Debug.LogError($"MusicHistory: graph database not found: {path}. Run the pipeline (influence + layout) or pass {CommandLineFlag} <path>.");
                enabled = false;
                return;
            }
            DbPathReason = reason;
            Build(path);
        }

        /// <summary>Reads <paramref name="dbPath"/> and (re)builds the whole scene graph.</summary>
        public void Build(string dbPath)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Clear();
            ResolvedDbPath = Path.GetFullPath(dbPath);
            SongGraphData data = SongGraphReader.Read(ResolvedDbPath);
            Data = data;
            if (data.Problems.Count > 0)
                Debug.LogWarning($"MusicHistory: {data.Problems.Count} contract problems in {ResolvedDbPath}; first: " +
                                 string.Join(" | ", data.Problems.Take(5)));

            Vector3[] positions = GraphLayoutMapping.DisplayPositions(data, TimeAxis, out GraphFrame frame);
            Frame = frame;

            Simulation = GetComponent<ForceDirectedGraph>();
            Simulation.Clear();
            Simulation.Stepped -= RedrawEdges;
            Simulation.Stepped += RedrawEdges;
            Simulation.LockAxis = true;
            Simulation.LockedAxis = frame.TimeDir;

            graphRoot = new GameObject("Song Graph");
            graphRoot.transform.SetParent(transform, false);
            Transform songRoot = new GameObject("Songs").transform;
            songRoot.SetParent(graphRoot.transform, false);
            Transform treeRoot = new GameObject("Tree Edges").transform;
            treeRoot.SetParent(graphRoot.transform, false);
            Transform secondaryRoot = new GameObject("Secondary Edges").transform;
            secondaryRoot.SetParent(graphRoot.transform, false);
            RouteLine = RouteLine.Create(graphRoot.transform);

            for (int i = 0; i < data.Songs.Count; i++)
            {
                SongRecord song = data.Songs[i];
                SongNode node = SongNode.Create(song, positions[i], BubbleRadius(song, data.HasPositions), songRoot);
                nodes.Add(node);
                float mass = (float)(song.LayoutMass ?? 1.0 + Math.Log(1 + song.Descendants, 2));
                Simulation.AddNodeToGraph(node, song.NodeId - 1, Mathf.Max(.1f, mass));
            }
            foreach (SongNode node in nodes)
            {
                if (node.Song.TreeParent is int parent && parent >= 1 && parent <= nodes.Count)
                {
                    node.TreeParent = nodes[parent - 1];
                    node.TreeParent.TreeChildren.Add(node);
                }
            }

            foreach (EdgeRecord record in data.Edges)
            {
                if (record.Source < 1 || record.Source > nodes.Count || record.Target < 1 || record.Target > nodes.Count) continue;
                SongNode source = nodes[record.Source - 1], target = nodes[record.Target - 1];
                float similarity = Mathf.Clamp01((float)record.Similarity);
                // Tree edges into big subtrees are the trunks of the lineage: wider and brighter.
                int below = target.Song.Descendants;
                EdgeTier tier = below >= TrunkDescendants ? EdgeTier.Trunk : below >= 1 ? EdgeTier.Branch : EdgeTier.Twig;
                // Identity lineages: strong matches (exact shared passages) are drawn brightest.
                if (record.IsStrongMatch && data.IsIdentityLineage) tier = EdgeTier.Strong;
                float subtree = Mathf.Min(3f, 1f + .35f * Mathf.Log(1 + below, 2));
                float width = record.IsTree
                    ? TreeEdgeWidth * (.5f + .5f * similarity) * subtree
                    : SecondaryEdgeWidth * (.6f + .8f * similarity);
                InfluenceEdge edge = InfluenceEdge.Create(record, source, target, width, EdgePadding,
                    record.IsTree ? treeRoot : secondaryRoot, record.IsTree ? tier : EdgeTier.Branch);
                edges.Add(edge);
                source.Outgoing.Add(edge);
                target.Incoming.Add(edge);
                if (record.IsTree)
                {
                    target.TreeEdge = edge;
                    Simulation.AddEdgeToGraph(source, target);
                }
                edge.SetShown(record.IsTree || ShowAllSecondaryEdges);
            }

            Labels = GetOrAdd<LabelLayer>();
            Labels.ScreenSize = LabelScreenSize;
            foreach (SongNode node in nodes
                         .OrderByDescending(n => n.Song.Descendants)
                         .ThenByDescending(n => n.Song.RefCount)
                         .ThenBy(n => n.Song.CanonRank ?? int.MaxValue)
                         .ThenBy(n => n.NodeId)
                         .Take(AlwaysLabelledSongs))
            {
                node.LabelPinned = true;
                pinned.Add(node);
                node.RefreshLabel(Labels);
            }

            double minTime = data.MetaDouble("min_time") ?? data.MinTime;
            double maxTime = data.MetaDouble("max_time") ?? data.MaxTime;
            Timeline = TimelineAxis.Create(frame, Math.Min(minTime, data.MinTime), Math.Max(maxTime, data.MaxTime), Labels, graphRoot.transform);

            Camera? cam = ViewCamera;
            SceneLook.Apply(cam);

            Hud = GetOrAdd<GraphHud>();
            Hud.Build(this);
            Hud.SetLegend(LegendBody());
            // A label under a HUD panel or cut by the screen edge is hidden (focus labels excepted).
            Labels.KeepClear = c => Hud != null ? Hud.PanelScreenRects(c) : (IReadOnlyList<Rect>)Array.Empty<Rect>();

            Highlighter = GetOrAdd<HoverHighlighter>();
            Highlighter.Loader = this;
            Highlighter.FocusChanged -= OnFocusChanged;
            Highlighter.FocusChanged += OnFocusChanged;
            Highlighter.EdgeFocusChanged -= OnEdgeFocusChanged;
            Highlighter.EdgeFocusChanged += OnEdgeFocusChanged;
            Highlighter.ApplyFocus(null, force: true);

            Director = GetOrAdd<WalkthroughDirector>();
            Director.Initialize(this);
            string pathsFile = ResolvePathsPath(PathsFile, out string pathsReason);
            UseCatalog(PathCatalog.Load(pathsFile), pathsReason);
            string mashupsFile = ResolveMashupsPath(MashupsFile, out string mashupsReason);
            UseMashups(MashupCatalog.Load(mashupsFile), mashupsReason);
            string duetsFile = ResolveDuetsPath(DuetsFile, out string duetsReason);
            UseDuets(DuetCatalog.Load(duetsFile), duetsReason);
            string mosaicsFile = ResolveMosaicsPath(MosaicsFile, out string mosaicsReason);
            UseMosaics(MosaicCatalog.Load(mosaicsFile), mosaicsReason);
            string narrationFile = NarrationCatalog.ResolvePath(NarrationFile, out string narrationReason);
            string artistsFile = ArtistImages.ResolvePath(ArtistsFile, out string artistsReason);
            UseNarration(NarrationCatalog.Load(narrationFile), ArtistImages.Load(artistsFile), $"{narrationReason}; photos {artistsReason}");
            Layout = GetOrAdd<ViewerLayout>();
            Layout.Bind(this);

            if (cam != null) FrameOverview(cam);
            clock.Stop();
            BuildMilliseconds = clock.Elapsed.TotalMilliseconds;
            Debug.Log($"MusicHistory: built {nodes.Count} songs, {edges.Count} edges ({data.TreeEdgeCount} tree) from " +
                      $"{ResolvedDbPath} [{DbPathReason}] in {BuildMilliseconds:0} ms; {frame.Description}; " +
                      $"time fit residual {frame.MaxResidualYears:0.000} years; player: {Director.PlayerDescription}");
            Built?.Invoke(this);
        }

        /// <summary>
        /// Binds <paramref name="catalog"/> to this graph (work_id → song), hands it to the walkthrough
        /// and rebuilds the featured-paths panel.
        /// </summary>
        public void UseCatalog(PathCatalog catalog, string reason = "")
        {
            routes.Clear();
            Catalog = catalog ?? PathCatalog.Empty("none");
            if (Data != null)
            {
                Dictionary<string, int> byWork = new(StringComparer.Ordinal);
                foreach (SongRecord s in Data.Songs)
                    if (!string.IsNullOrEmpty(s.WorkId) && !byWork.ContainsKey(s.WorkId)) byWork[s.WorkId] = s.NodeId;
                Catalog.Bind(w => byWork.TryGetValue(w, out int id) ? id : (int?)null);
            }
            Director.UseCatalog(Catalog);
            // The mashups and duet loops follow the paths they play (the list shows which paths have one).
            if (Mashups.Mashups.Count > 0) BindMashups();
            if (Duets.Loops.Count > 0) BindDuets();
            Paths = GetOrAdd<FeaturedPathsPanel>();
            Paths.Build(this);
            string where = Catalog.SourcePath.Length > 0 ? Catalog.SourcePath : "(in memory)";
            Debug.Log($"MusicHistory: featured paths {Catalog.Status}; {Catalog.PlayableCount} playable; {where}" +
                      (reason.Length > 0 ? $" [{reason}]" : "") +
                      (Catalog.Problems.Count > 0 ? $"; {Catalog.Problems.Count} problems, first: {string.Join(" | ", Catalog.Problems.Take(3))}" : ""));
        }

        /// <summary>
        /// Binds <paramref name="catalog"/> (mashups.json) to this graph and the featured paths, hands it
        /// to the walkthrough and (re)builds the melody graph.
        /// </summary>
        public void UseMashups(MashupCatalog catalog, string reason = "")
        {
            Mashups = catalog ?? MashupCatalog.Empty("none");
            BindMashups();
            MelodyGraph = GetOrAdd<MelodyGraphPanel>();
            MelodyGraph.Build(this);
            // The list marks the paths that play a mashup mix (and their length).
            if (Paths != null) Paths.Build(this);
            string where = Mashups.SourcePath.Length > 0 ? Mashups.SourcePath : "(in memory)";
            Debug.Log($"MusicHistory: mashups {Mashups.Status}; {Mashups.PlayableCount} playable; {where}" +
                      (reason.Length > 0 ? $" [{reason}]" : "") +
                      (Mashups.Problems.Count > 0 ? $"; {Mashups.Problems.Count} problems, first: {string.Join(" | ", Mashups.Problems.Take(3))}" : ""));
        }

        /// <summary>
        /// Binds <paramref name="catalog"/> (duets.json) to this graph and the featured paths and hands
        /// it to the walkthrough; the featured-paths list offers each path's duet loop.
        /// </summary>
        public void UseDuets(DuetCatalog catalog, string reason = "")
        {
            Duets = catalog ?? DuetCatalog.Empty("none");
            BindDuets();
            if (Paths != null) Paths.Build(this);
            string where = Duets.SourcePath.Length > 0 ? Duets.SourcePath : "(in memory)";
            Debug.Log($"MusicHistory: duet loops {Duets.Status}; {Duets.PlayableCount} playable; {where}" +
                      (reason.Length > 0 ? $" [{reason}]" : "") +
                      (Duets.Problems.Count > 0 ? $"; {Duets.Problems.Count} problems, first: {string.Join(" | ", Duets.Problems.Take(3))}" : ""));
        }

        /// <summary>
        /// Binds <paramref name="catalog"/> (mosaics.json) to this graph and hands it to the walkthrough;
        /// the featured-paths panel lists the mosaics (O).
        /// </summary>
        public void UseMosaics(MosaicCatalog catalog, string reason = "")
        {
            Mosaics = catalog ?? MosaicCatalog.Empty("none");
            mosaicRoutes.Clear();
            if (Data != null)
            {
                Dictionary<string, int> byWork = new(StringComparer.Ordinal);
                foreach (SongRecord s in Data.Songs)
                    if (!string.IsNullOrEmpty(s.WorkId) && !byWork.ContainsKey(s.WorkId)) byWork[s.WorkId] = s.NodeId;
                Mosaics.Bind(w => byWork.TryGetValue(w, out int id) ? id : (int?)null);
            }
            Director.UseMosaics(Mosaics);
            if (Paths != null) Paths.Build(this);
            string where = Mosaics.SourcePath.Length > 0 ? Mosaics.SourcePath : "(in memory)";
            Debug.Log($"MusicHistory: mosaics {Mosaics.Status}; {Mosaics.PlayableCount} playable; {where}" +
                      (reason.Length > 0 ? $" [{reason}]" : "") +
                      (Mosaics.Problems.Count > 0 ? $"; {Mosaics.Problems.Count} problems, first: {string.Join(" | ", Mosaics.Problems.Take(3))}" : ""));
        }

        void BindDuets()
        {
            if (Data != null)
            {
                Dictionary<string, int> byWork = new(StringComparer.Ordinal);
                foreach (SongRecord s in Data.Songs)
                    if (!string.IsNullOrEmpty(s.WorkId) && !byWork.ContainsKey(s.WorkId)) byWork[s.WorkId] = s.NodeId;
                Duets.Bind(w => byWork.TryGetValue(w, out int id) ? id : (int?)null, Catalog);
            }
            Director.UseDuets(Duets);
        }

        /// <summary>
        /// The narrated walkthroughs (narration.json) and the artist photos (artists.json): the
        /// narration follows the walkthrough's mashup mix; the overlay shows its caption and photos;
        /// the photos also go onto their songs' bubbles.
        /// </summary>
        public void UseNarration(NarrationCatalog catalog, ArtistImages images, string reason = "")
        {
            NarrationCatalog = catalog ?? NarrationCatalog.Empty("none");
            // The bubble photos let go of the old textures first.
            if (BubblePhotos != null) BubblePhotos.Clear();
            ArtistImages.Release();
            ArtistImages = images ?? ArtistImages.Empty("none");
            Narration = GetOrAdd<NarrationPlayer>();
            if (Director != null && Director.MashupAudio != null)
                Narration.Bind(Director.MashupAudio, () => Director != null ? Director.CurrentMashup : null, NarrationCatalog);
            else Narration.UseCatalog(NarrationCatalog);
            NarrationOverlay = GetOrAdd<NarrationOverlay>();
            NarrationOverlay.Build(this);
            BubblePhotos = GetOrAdd<BubblePhotos>();
            BubblePhotos.Apply(this);
            string where = NarrationCatalog.SourcePath.Length > 0 ? NarrationCatalog.SourcePath : "(in memory)";
            Debug.Log($"MusicHistory: narration {NarrationCatalog.Status}; photos {ArtistImages.Status} (bubbles: {BubblePhotos.Status}); {where}" +
                      (reason.Length > 0 ? $" [{reason}]" : "") +
                      (NarrationCatalog.Problems.Count > 0 ? $"; {NarrationCatalog.Problems.Count} narration problems, first: {string.Join(" | ", NarrationCatalog.Problems.Take(3))}" : "") +
                      (ArtistImages.Problems.Count > 0 ? $"; {ArtistImages.Problems.Count} photo problems, first: {string.Join(" | ", ArtistImages.Problems.Take(3))}" : ""));
        }

        void BindMashups()
        {
            if (Data != null)
            {
                Dictionary<string, int> byWork = new(StringComparer.Ordinal);
                foreach (SongRecord s in Data.Songs)
                    if (!string.IsNullOrEmpty(s.WorkId) && !byWork.ContainsKey(s.WorkId)) byWork[s.WorkId] = s.NodeId;
                Mashups.Bind(w => byWork.TryGetValue(w, out int id) ? id : (int?)null, Catalog);
            }
            Director.UseMashups(Mashups);
        }

        /// <summary>The path's songs and the graph edges between consecutive songs (cached per catalog).</summary>
        public GraphRoute RouteFor(FeaturedPath path)
        {
            if (routes.TryGetValue(path, out GraphRoute? cached)) return cached;
            GraphRoute route = new(path);
            SongNode? previous = null;
            Dictionary<EdgeChannel, int> channels = new();
            foreach (PathStep step in path.Steps)
            {
                SongNode? node = step.NodeId >= 1 && step.NodeId <= nodes.Count ? nodes[step.NodeId - 1] : null;
                InfluenceEdge? edge = null;
                if (node != null)
                {
                    route.Nodes.Add(node);
                    if (previous != null) edge = GraphRoute.Between(previous, node);
                    if (edge != null)
                    {
                        route.Edges.Add(edge);
                        channels[edge.Channel] = channels.TryGetValue(edge.Channel, out int n) ? n + 1 : 1;
                    }
                }
                route.StepEdges.Add(edge);
                previous = node;
            }
            // The route's colour: the channel most of its edges carry (edge colours in the graph).
            EdgeChannel best = EdgeChannel.Loop;
            int bestCount = 0;
            foreach (KeyValuePair<EdgeChannel, int> kv in channels)
                if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key < best)) (best, bestCount) = (kv.Key, kv.Value);
            route.Color = SongPalette.ChannelColor(best);
            routes[path] = route;
            return route;
        }

        /// <summary>
        /// A mosaic's songs on the graph, for the list's preview: the target, the pieces' songs in
        /// piece order, the harmony voices (no edges: a mosaic is no lineage). Cached per catalog.
        /// </summary>
        public GraphRoute RouteFor(Mosaic mosaic)
        {
            if (mosaicRoutes.TryGetValue(mosaic, out GraphRoute? cached)) return cached;
            GraphRoute route = new(mosaic) { Color = SongPalette.Hex("#ffcf4a") };
            foreach (MosaicSong s in mosaic.Songs)
                if (s.NodeId >= 1 && s.NodeId <= nodes.Count && !route.Nodes.Contains(nodes[s.NodeId - 1])) route.Nodes.Add(nodes[s.NodeId - 1]);
            mosaicRoutes[mosaic] = route;
            return route;
        }

        /// <summary>After a live-simulation step: every edge follows its (moved) endpoints once.</summary>
        void RedrawEdges()
        {
            foreach (InfluenceEdge edge in edges) edge.UpdateGeometry();
        }

        void OnFocusChanged(SongNode? node)
        {
            if (Hud != null) Hud.ShowSong(node);
        }

        void OnEdgeFocusChanged(InfluenceEdge edge)
        {
            if (Hud != null) Hud.ShowEdge(edge);
        }

        T GetOrAdd<T>() where T : Component
        {
            T existing = GetComponent<T>();
            return existing != null ? existing : gameObject.AddComponent<T>();
        }

        float BubbleRadius(SongRecord song, bool hasLayout)
        {
            float radius = UseLayoutDisplayRadius && hasLayout && song.LayoutDisplayRadius is double d && d > 0
                ? (float)d
                : BubbleRadiusScale * Mathf.Sqrt(song.Descendants + 1);
            return Mathf.Clamp(radius, MinBubbleRadius, MaxBubbleRadius);
        }

        string LegendBody()
        {
            SongGraphData data = Data!;
            string m = GraphHud.Muted;
            StringBuilder b = new();
            bool lineage = data.IsIdentityLineage;
            b.Append($"<size=125%><b>MusicHistory</b></size>  <color={m}>{(lineage ? "shared-identity lineages" : "song influence graph")}</color>");
            if (data.IsSynthetic) b.Append("  <color=#ffcf4a>[synthetic data]</color>");
            b.Append(lineage
                ? $"\n{data.Songs.Count} songs · {data.Edges.Count} shared-identity links ({data.TreeEdgeCount} tree) · {data.RootCount} roots" +
                  (data.HasFamilies ? $" · {data.Families.Count} families" : "")
                : $"\n{data.Songs.Count} songs · {data.Edges.Count} influences ({data.TreeEdgeCount} tree) · {data.RootCount} roots");
            if (data.Songs.Count > 0) b.Append($" · {data.Songs[0].Year}–{data.Songs[data.Songs.Count - 1].Year}");
            b.Append(TimeAxis switch
            {
                TimeAxisView.LeftToRight => " · time →",
                TimeAxisView.BottomToTop => " · time ↑",
                _ => ""
            });
            b.Append($"\n<color={m}>Fill: key</color> ");
            int[] fifths = { 0, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10, 5 };
            foreach (int pc in fifths)
                b.Append($"<color={SongPalette.ToHex(SongPalette.KeyColor(pc, false))}>{SongPalette.PitchName(pc, false)}</color> ");
            b.Append($"<color={m}>(minor darker)</color>");
            b.Append($"\n<color={m}>Ring: decade</color> ");
            for (int decade = SongPalette.FirstDecade; decade <= SongPalette.LastDecade; decade += 10)
                b.Append($"<color={SongPalette.ToHex(SongPalette.DecadeColor(decade))}>{decade % 100:00}s</color> ");
            b.Append($"<color={m}>· area = descendants + 1</color>");
            if (lineage)
            {
                // DESIGN.md §8b: an edge is a shared musical identity, labelled as such.
                int strong = data.Edges.Count(e => e.IsStrongMatch);
                b.Append($"\n<color={m}>Edge = two songs share a musical identity (shared DNA, not proven copying) · wide end = earlier song</color>");
                b.Append($"\n<color={m}>Shared:</color> ");
                (EdgeChannel channel, string label)[] kinds =
                {
                    (EdgeChannel.Chord, "chord progression"), (EdgeChannel.Loop, "loop"), (EdgeChannel.Bass, "bass line"),
                    (EdgeChannel.Melody, "melody"), (EdgeChannel.Both, "melody + harmony")
                };
                b.Append(string.Join($" <color={m}>·</color> ",
                    kinds.Select(k => $"<color={SongPalette.ToHex(SongPalette.ChannelColor(k.channel))}>{k.label}</color>")));
                b.Append($"\n<color={GraphHud.StrongColor}><b>Bright edge = strong match</b></color>" +
                         $"<color={m}>: an exact shared passage above the calibrated evidence threshold ({strong} edges)</color>");
            }
            else
            {
                b.Append($"\n<color={m}>Edges</color> ");
                foreach (EdgeChannel c in new[] { EdgeChannel.Chord, EdgeChannel.Melody, EdgeChannel.Both, EdgeChannel.Bass, EdgeChannel.Loop })
                    b.Append($"<color={SongPalette.ToHex(SongPalette.ChannelColor(c))}>{SongPalette.ChannelLabel(c)}</color> ");
                b.Append($"<color={m}>· wide end = influencer</color>");
            }
            b.Append($"\n<size=80%><color={m}>{GraphHud.Esc(Path.GetFileName(ResolvedDbPath))} · {GraphHud.Esc(Frame.Description)}</color></size>");
            return b.ToString();
        }

        /// <summary>Places <paramref name="cam"/> so the whole graph and its timeline are in view.</summary>
        public void FrameOverview(Camera cam)
        {
            if (Data == null || nodes.Count == 0) return;
            List<(Vector3, float)> items = new(nodes.Count + 2);
            foreach (SongNode n in nodes) items.Add((n.transform.position, n.Radius));
            if (Timeline != null)
            {
                items.Add((Timeline.AxisPoint(Timeline.FirstYear), 1.5f));
                items.Add((Timeline.AxisPoint(Timeline.LastYear), 1.5f));
            }
            // Beside the featured-paths list while it is open, else the usual HUD-clear area.
            Rect viewport = Paths != null && Paths.IsOpen ? Paths.FreeViewport : OverviewViewport;
            (Vector3 position, Quaternion rotation) = CameraFraming.Frame(cam, items, Frame.ViewForward, 1.02f, 5f, viewport);
            cam.transform.SetPositionAndRotation(position, rotation);
            float extent = Frame.Bounds.size.magnitude;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, Vector3.Distance(position, Frame.Bounds.center) + extent * 2f);
            CameraControl control = cam.GetComponent<CameraControl>();
            if (control != null)
            {
                control.SyncRotationFromTransform();
                control.Speed = Mathf.Max(control.Speed, extent * .12f);
            }
        }

        /// <summary>
        /// Frames the songs dated <paramref name="firstYear"/>..<paramref name="lastYear"/> and that
        /// stretch of the timeline into <paramref name="viewport"/> (the featured-paths list uses it).
        /// </summary>
        public void FrameYears(Camera cam, double firstYear, double lastYear, Rect viewport)
        {
            if (Data == null || nodes.Count == 0) return;
            double lo = Math.Max(firstYear, Math.Floor(Data.MinTime)), hi = Math.Min(lastYear, Math.Ceiling(Data.MaxTime) + 1);
            List<(Vector3, float)> items = new();
            foreach (SongNode n in nodes)
                if (n.Song.TimeValue >= lo && n.Song.TimeValue <= hi) items.Add((n.transform.position, n.Radius));
            if (items.Count < 2)
            {
                FrameOverview(cam);
                return;
            }
            if (Timeline != null)
            {
                items.Add((Timeline.AxisPoint(lo), 1.5f));
                items.Add((Timeline.AxisPoint(hi), 1.5f));
            }
            (Vector3 position, Quaternion rotation) = CameraFraming.Frame(cam, items, Frame.ViewForward, 1.04f, 5f, viewport);
            cam.transform.SetPositionAndRotation(position, rotation);
            float extent = Frame.Bounds.size.magnitude;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, Vector3.Distance(position, Frame.Bounds.center) + extent * 2f);
            CameraControl control = cam.GetComponent<CameraControl>();
            if (control != null) control.SyncRotationFromTransform();
        }

        /// <summary>Labels, timeline widths and HUD for <paramref name="cam"/> now (edit-mode rendering).</summary>
        public void RefreshView(Camera cam)
        {
            // Edit-mode captures show highlighted bubbles at their full size (play mode eases them in LateUpdate).
            if (!Application.isPlaying) SongNode.SettleScales();
            if (RouteLine != null) RouteLine.Refresh(cam);
            // Edit-mode captures: the photos of the bubbles in view (play mode loads them a few per frame).
            if (BubblePhotos != null) BubblePhotos.Refresh(cam);
            Labels.Refresh(cam);
            Timeline.Refresh(cam);
            Labels.ForceMeshUpdate();
            Hud.ForceUpdate();
            if (MelodyGraph != null) MelodyGraph.ForceUpdate();
        }

        /// <summary>Highlighted bubbles grow and shrink back smoothly (before the labels place themselves).</summary>
        void LateUpdate() => SongNode.TickScales(Time.deltaTime);

        void Update()
        {
            Keyboard? k = Keyboard.current;
            if (k == null || Data == null) return;
            if (k.fKey.wasPressedThisFrame) ToggleSimulation();
            if (k.lKey.wasPressedThisFrame) SetAllLabels(!AllLabels);
            if (k.vKey.wasPressedThisFrame)
            {
                ShowAllSecondaryEdges = !ShowAllSecondaryEdges;
                if (Director.IsTouring) Director.RefreshStepHighlight();
                else if (Highlighter.Suspended) Highlighter.ApplyFocus(Highlighter.Focus, force: true);
                else Highlighter.Reapply();
            }
            if (k.hKey.wasPressedThisFrame) Hud.ToggleHelp();
            if ((k.rKey.wasPressedThisFrame || k.homeKey.wasPressedThisFrame) && !Director.IsTouring && ViewCamera != null)
                FrameOverview(ViewCamera);
            // The lyric-themes cloud of the same songs (its G key comes back here).
            if (k.tKey.wasPressedThisFrame && !Director.IsTouring
                && UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath("Assets/Scenes/LyricThemes.unity") >= 0)
                UnityEngine.SceneManagement.SceneManager.LoadScene("LyricThemes");
        }

        public void ToggleSimulation()
        {
            Simulation.LockAxis = true;
            Simulation.LockedAxis = Frame.TimeDir;
            if (Simulation.IsRunning) Simulation.StopGraph();
            else Simulation.StartGraph();
        }

        public void SetAllLabels(bool all)
        {
            AllLabels = all;
            foreach (SongNode n in nodes)
            {
                n.LabelPinned = all || pinned.Contains(n);
                n.RefreshLabel(Labels);
            }
        }

        /// <summary>Destroys everything <see cref="Build"/> created.</summary>
        public void Clear()
        {
            if (Director != null) Director.Exit();
            if (BubblePhotos != null) BubblePhotos.Clear();
            routes.Clear();
            mosaicRoutes.Clear();
            if (Simulation != null) Simulation.Clear();
            if (Labels != null) Labels.Clear();
            if (graphRoot != null) Discard(graphRoot);
            graphRoot = null;
            if (Hud != null) Hud.Discard();
            if (MelodyGraph != null) MelodyGraph.Discard();
            if (NarrationOverlay != null) NarrationOverlay.Discard();
            ArtistImages.Release();
            nodes.Clear();
            edges.Clear();
            pinned.Clear();
            AllLabels = false;
            GraphMaterials.Clear();
            SongNode.ReleaseSharedMesh();
            InfluenceEdge.ReleaseSharedMesh();
            Data = null;
        }

        static void Discard(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void OnDestroy()
        {
            GraphMaterials.Clear();
            ArtistImages.Release();
        }

        /// <summary>The MusicHistory pipeline repository (see <see cref="MusicHistory.PipelinePaths"/>).</summary>
        public static string RepoRoot() => MusicHistory.PipelinePaths.Root();

        /// <summary>
        /// A user-supplied path: absolute as given; relative paths are tried against the working
        /// directory (the Unity project folder in the editor), then against the repository root.
        /// </summary>
        public static string ResolveUserPath(string path)
        {
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string fromCwd = Path.GetFullPath(path);
            if (File.Exists(fromCwd)) return fromCwd;
            string fromRepo = Path.GetFullPath(Path.Combine(RepoRoot(), path));
            return File.Exists(fromRepo) ? fromRepo : fromCwd;
        }

        /// <summary>paths.json: -musicHistoryPaths, else <paramref name="configured"/>, else &lt;repo&gt;/data/audio/renders/paths.json.</summary>
        public static string ResolvePathsPath(string configured, out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], PathCatalog.CommandLineFlag, StringComparison.OrdinalIgnoreCase)) continue;
                reason = "command line";
                return ResolveUserPath(args[i + 1]);
            }
            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = "inspector";
                return ResolveUserPath(configured);
            }
            reason = "default";
            return Path.Combine(RepoRoot(), "data", "audio", "renders", PathCatalog.FileName);
        }

        /// <summary>mashups.json: -musicHistoryMashups, else <paramref name="configured"/>, else &lt;repo&gt;/data/audio/mashups/mashups.json.</summary>
        public static string ResolveMashupsPath(string configured, out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], MashupCatalog.CommandLineFlag, StringComparison.OrdinalIgnoreCase)) continue;
                reason = "command line";
                return ResolveUserPath(args[i + 1]);
            }
            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = "inspector";
                return ResolveUserPath(configured);
            }
            reason = "default";
            return Path.Combine(RepoRoot(), "data", "audio", "mashups", MashupCatalog.FileName);
        }

        /// <summary>duets.json: -musicHistoryDuets, else <paramref name="configured"/>, else &lt;data&gt;/audio/duets/duets.json.</summary>
        public static string ResolveDuetsPath(string configured, out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], DuetCatalog.CommandLineFlag, StringComparison.OrdinalIgnoreCase)) continue;
                reason = "command line";
                return ResolveUserPath(args[i + 1]);
            }
            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = "inspector";
                return ResolveUserPath(configured);
            }
            reason = "default";
            return DuetCatalog.DefaultPath();
        }

        /// <summary>mosaics.json: -musicHistoryMosaics, else <paramref name="configured"/>, else &lt;data&gt;/audio/mosaics/mosaics.json.</summary>
        public static string ResolveMosaicsPath(string configured, out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], MosaicCatalog.CommandLineFlag, StringComparison.OrdinalIgnoreCase)) continue;
                reason = "command line";
                return ResolveUserPath(args[i + 1]);
            }
            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = "inspector";
                return ResolveUserPath(configured);
            }
            reason = "default";
            return MosaicCatalog.DefaultPath();
        }

        public static string ResolveDatabasePath(string configured, out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], CommandLineFlag, StringComparison.OrdinalIgnoreCase)) continue;
                reason = "command line";
                return ResolveUserPath(args[i + 1]);
            }
            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = "inspector";
                return ResolveUserPath(configured);
            }
            string repo = RepoRoot();
            (string path, string why)[] candidates =
            {
                (Path.Combine(repo, "data", "graph", "music_graph.db"), "pipeline graph"),
                (Path.Combine(Application.streamingAssetsPath, "music_graph.db"), "StreamingAssets"),
                (Path.Combine(repo, "data", "graph", "demo_graph.db"), "demo graph (music_graph.db not found)")
            };
            foreach ((string path, string why) in candidates)
            {
                if (!File.Exists(path)) continue;
                reason = why;
                return path;
            }
            reason = "missing";
            return candidates[0].path;
        }
    }
}
