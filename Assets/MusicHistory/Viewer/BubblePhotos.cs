#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// Artist photos on the bubbles (DESIGN.md §16): every song whose artist has a catalogued photo
    /// (data/images/artists.json, <see cref="ArtistImages"/>; image id "artist-&lt;QID&gt;") shows it
    /// as a round, camera-facing picture on its bubble — a child quad drawn by
    /// MusicHistory/BubblePhoto just in front of the sphere, inside its ring (so the decade ring and
    /// its glow stay around it), scaling with the bubble and following its state (a dimmed bubble's
    /// photo dims and greys). It has no collider: hover and click still pick the bubble.
    ///
    /// Songs map to photos by the catalogue's <c>work_ids</c>, else by the artist's name (the
    /// graph's artist, or its lead artist before "feat." / "&amp;", against the photo's artist or
    /// subject). Only photos that may be shown (file present, licence given) are used; the credit
    /// stays on the narration's popup card.
    ///
    /// Textures load lazily: a photo is decoded (by <see cref="ArtistImages.Texture"/>, shared with
    /// the popup card) the first time one of its bubbles is in the camera's view, at most
    /// <see cref="DecodesPerFrame"/> per frame; until then its quad is off. Materials are shared per
    /// photo and bubble state (no renderer owns one).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BubblePhotos : MonoBehaviour
    {
        public const string ShaderName = "MusicHistory/BubblePhoto";

        [Tooltip("The photo's diameter as a fraction of the bubble's (inside the ring, which starts at 0.74–0.86).")]
        [Range(.2f, .86f)] public float PhotoScale = .72f;
        [Tooltip("A photo smaller than this on screen (px) is not drawn: the bubble shows its key colour.")]
        [Min(1f)] public float MinPixels = 12f;
        [Tooltip("Photos decoded per frame at most (each a JPG of up to 720 px).")]
        [Min(1)] public int DecodesPerFrame = 1;
        [Tooltip("Which part of a portrait-shaped photo the square crop keeps (0 = bottom, 1 = top).")]
        [Range(0f, 1f)] public float FocusY = .85f;

        SongGraphLoader? loader;
        readonly Dictionary<int, string> photoOfNode = new();
        readonly Dictionary<string, List<SongNode>> nodesOfPhoto = new(StringComparer.Ordinal);
        readonly Dictionary<int, string> why = new();
        readonly List<string> pending = new();
        readonly Plane[] planes = new Plane[6];

        static readonly Dictionary<(string id, BubbleState state), Material> materials = new();
        static readonly Dictionary<string, (Texture2D texture, Vector4 crop)> loaded = new(StringComparer.Ordinal);
        static Shader? shader;
        static float photoScale = .72f, minPixels = 12f;

        /// <summary>Songs that show a photo.</summary>
        public int Mapped => photoOfNode.Count;
        /// <summary>Photos used by at least one song.</summary>
        public int PhotosUsed => nodesOfPhoto.Count;
        /// <summary>Photos not decoded yet.</summary>
        public int Pending => pending.Count;
        /// <summary>Photos decoded and on their bubbles.</summary>
        public int Loaded
        {
            get
            {
                int n = 0;
                foreach (string id in nodesOfPhoto.Keys) if (loaded.ContainsKey(id)) n++;
                return n;
            }
        }
        public IReadOnlyDictionary<int, string> Mapping => photoOfNode;
        /// <summary>How a song got its photo ("work_ids" or "artist name").</summary>
        public string HowMapped(int nodeId) => photoOfNode.TryGetValue(nodeId, out string? id) && why.TryGetValue(nodeId, out string? w) ? $"{id} by {w}" : "";
        public string? PhotoFor(int nodeId) => photoOfNode.TryGetValue(nodeId, out string? id) ? id : null;
        public IReadOnlyList<SongNode> NodesWith(string id) => nodesOfPhoto.TryGetValue(id, out List<SongNode>? list) ? list : Array.Empty<SongNode>();
        public static int MaterialCount => materials.Count;
        public static bool IsShared(Material? m) => m != null && materials.ContainsValue(m);
        public string Status { get; private set; } = "";

        // ------------------------------------------------------------------ mapping

        /// <summary>Maps the songs to photos and puts a (not yet loaded) photo quad on each such bubble.</summary>
        public void Apply(SongGraphLoader owner)
        {
            Clear();
            loader = owner;
            photoScale = PhotoScale;
            minPixels = MinPixels;
            ArtistImages images = owner.ArtistImages;
            if (owner.Nodes.Count == 0 || images.Images.Count == 0)
            {
                Status = $"no photos ({images.Status})";
                return;
            }
            Dictionary<string, string> byWork = new(StringComparer.Ordinal);
            Dictionary<string, string> byName = new(StringComparer.Ordinal);
            List<string> ids = new(images.Images.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                ArtistImage image = images.Images[id];
                if (!image.Showable) continue;   // no file or no licence: never shown
                foreach (string w in image.WorkIds)
                    if (!byWork.ContainsKey(w)) byWork[w] = id;
                foreach (string name in new[] { image.ArtistName, image.Subject })
                {
                    string key = NameKey(name);
                    if (key.Length > 0 && !byName.ContainsKey(key)) byName[key] = id;
                }
            }
            Mesh quad = SongNode.QuadMesh();
            foreach (SongNode node in owner.Nodes)
            {
                string? id = null, how = "";
                if (byWork.TryGetValue(node.Song.WorkId, out string? w)) (id, how) = (w, "work_ids");
                else
                    foreach (string key in ArtistKeys(node.Song.Artist))
                        if (byName.TryGetValue(key, out string? n))
                        {
                            (id, how) = (n, "artist name");
                            break;
                        }
                if (id == null) continue;
                photoOfNode[node.NodeId] = id;
                why[node.NodeId] = how;
                if (!nodesOfPhoto.TryGetValue(id, out List<SongNode>? list)) nodesOfPhoto[id] = list = new List<SongNode>();
                list.Add(node);

                GameObject go = new("Photo");
                go.transform.SetParent(node.transform, false);
                go.transform.localScale = Vector3.one * (node.Radius * 2f * PhotoScale);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                node.AttachPhoto(mr, id);
                mr.enabled = loaded.ContainsKey(id);
            }
            foreach (string id in nodesOfPhoto.Keys) if (!loaded.ContainsKey(id)) pending.Add(id);
            pending.Sort(StringComparer.Ordinal);
            Status = $"{photoOfNode.Count} song{(photoOfNode.Count == 1 ? "" : "s")} with {nodesOfPhoto.Count} photo{(nodesOfPhoto.Count == 1 ? "" : "s")} of {images.Images.Count}";
        }

        /// <summary>A name to match on: lower case, "&amp;" as "and", no leading "the", letters and digits only.</summary>
        public static string NameKey(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name!.Trim().ToLowerInvariant().Replace("&", " and ");
            s = s.Normalize(NormalizationForm.FormD);
            StringBuilder b = new(s.Length);
            foreach (char c in s)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) b.Append(c);
                else if (b.Length > 0 && b[^1] != ' ') b.Append(' ');
            }
            string t = b.ToString().Trim();
            if (t.StartsWith("the ", StringComparison.Ordinal)) t = t.Substring(4);
            return t.Replace(" ", "");
        }

        /// <summary>The graph's artist as match keys: the whole credit, then the lead artist before "feat." / "&amp;" / "and" / "," / "with".</summary>
        public static IEnumerable<string> ArtistKeys(string? artist)
        {
            string whole = NameKey(artist);
            if (whole.Length > 0) yield return whole;
            if (string.IsNullOrWhiteSpace(artist)) yield break;
            string a = " " + artist!.ToLowerInvariant() + " ";
            int cut = a.Length;
            foreach (string sep in new[] { " feat. ", " feat ", " featuring ", " ft. ", " ft ", " & ", " and ", " with ", ", ", " x ", " / " })
            {
                int i = a.IndexOf(sep, StringComparison.Ordinal);
                if (i > 0 && i < cut) cut = i;
            }
            if (cut < a.Length)
            {
                string lead = NameKey(a.Substring(0, cut));
                if (lead.Length > 0 && lead != whole) yield return lead;
            }
        }

        // ------------------------------------------------------------------ lazy loading

        void LateUpdate()
        {
            if (pending.Count == 0 || loader == null) return;
            Refresh(loader.ViewCamera, DecodesPerFrame);
        }

        /// <summary>
        /// Decodes up to <paramref name="budget"/> photos (all when negative) whose bubbles are in
        /// <paramref name="cam"/>'s view (every pending photo when there is no camera).
        /// </summary>
        public void Refresh(Camera? cam, int budget = -1)
        {
            if (pending.Count == 0 || loader == null) return;
            if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, planes);
            for (int i = 0; i < pending.Count && budget != 0;)
            {
                string id = pending[i];
                bool wanted = cam == null;
                if (!wanted)
                    foreach (SongNode n in NodesWith(id))
                    {
                        if (n == null) continue;
                        Bounds b = new(n.transform.position, Vector3.one * (n.Radius * 2f));
                        if (GeometryUtility.TestPlanesAABB(planes, b))
                        {
                            wanted = true;
                            break;
                        }
                    }
                if (!wanted)
                {
                    i++;
                    continue;
                }
                pending.RemoveAt(i);
                if (budget > 0) budget--;
                Load(id);
            }
        }

        /// <summary>Decodes every photo in use now (edit-mode validation, captures).</summary>
        public void LoadAll() => Refresh(null, -1);

        void Load(string id)
        {
            if (loader == null) return;
            Texture2D? tex = loader.ArtistImages.Texture(id);
            if (tex == null) return;   // unreadable: the bubble keeps its colour (ArtistImages lists the problem)
            float w = Mathf.Max(1, tex.width), h = Mathf.Max(1, tex.height);
            // A centred square; a portrait keeps its upper part (FocusY), a landscape its middle.
            Vector4 crop = w >= h
                ? new Vector4(h / w, 1f, (1f - h / w) * .5f, 0f)
                : new Vector4(1f, w / h, 0f, (1f - w / h) * FocusY);
            loaded[id] = (tex, crop);
            foreach (KeyValuePair<(string id, BubbleState state), Material> kv in materials)
                if (kv.Key.id == id) Paint(kv.Value, tex, crop);
            foreach (SongNode n in NodesWith(id))
                if (n != null && n.PhotoRenderer != null)
                {
                    n.PhotoRenderer.sharedMaterial = MaterialFor(id, n.State);
                    n.PhotoRenderer.enabled = true;
                }
        }

        // ------------------------------------------------------------------ materials

        /// <summary>The shared material of photo <paramref name="id"/> in bubble state <paramref name="state"/>.</summary>
        public static Material MaterialFor(string id, BubbleState state)
        {
            if (materials.TryGetValue((id, state), out Material m) && m != null) return m;
            if (shader == null) shader = Shader.Find(ShaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            m = new Material(shader) { name = $"Bubble Photo {id} {state}", hideFlags = HideFlags.DontSave };
            (float dim, float saturation) = state switch
            {
                BubbleState.Dimmed => (.3f, .35f),
                BubbleState.Focus => (1.08f, 1f),
                _ => (1f, 1f)
            };
            m.SetFloat("_DimFactor", dim);
            m.SetFloat("_Saturation", saturation);
            m.SetFloat("_MinPixels", minPixels);
            m.SetFloat("_PhotoScale", photoScale);
            if (loaded.TryGetValue(id, out (Texture2D texture, Vector4 crop) t) && t.texture != null) Paint(m, t.texture, t.crop);
            materials[(id, state)] = m;
            return m;
        }

        static void Paint(Material m, Texture2D tex, Vector4 crop)
        {
            m.SetTexture("_MainTex", tex);
            m.SetVector("_Crop", crop);
        }

        /// <summary>Removes every photo quad and destroys the materials (the textures belong to <see cref="ArtistImages"/>).</summary>
        public void Clear()
        {
            if (loader != null)
                foreach (SongNode n in loader.Nodes)
                {
                    if (n == null || n.PhotoRenderer == null) continue;
                    GameObject go = n.PhotoRenderer.gameObject;
                    n.DetachPhoto();
                    Kill(go);
                }
            photoOfNode.Clear();
            nodesOfPhoto.Clear();
            why.Clear();
            pending.Clear();
            ReleaseMaterials();
            Status = "";
        }

        /// <summary>Destroys the shared photo materials and forgets the decoded textures (graph reload).</summary>
        public static void ReleaseMaterials()
        {
            foreach (Material m in materials.Values) if (m != null) Kill(m);
            materials.Clear();
            loaded.Clear();
        }

        static void Kill(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void OnDestroy() => ReleaseMaterials();
    }
}
