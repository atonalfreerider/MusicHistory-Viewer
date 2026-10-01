#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The melody graph's points of light, bloomed by URP. In this URP, uGUI canvases (overlay
    /// and camera space) are drawn after post-processing, so a light drawn on the panel itself
    /// never reaches the bloom. Instead each light is an HDR quad (MusicHistory/MelodyGlow, far
    /// above the bloom threshold) on a layer of its own (<see cref="Layer"/>), far below the
    /// graph, seen only by a dedicated orthographic camera that frames exactly the panel (one
    /// world unit = one reference pixel) and renders, with its own post-processing and its own
    /// bloom volume (on that layer, so the graph's look is untouched), into a texture. A RawImage
    /// on the panel adds that bloomed image on top of the melodies (additive), so the light and
    /// its bloom sit exactly on the melody line. The graph's camera neither sees the layer nor its
    /// volume.
    /// </summary>
    public sealed class MelodyLightRig
    {
        /// <summary>The layer of the light quads, their camera and their bloom volume (unused by the scene).</summary>
        public const int Layer = 31;
        /// <summary>The chord wheel's rig (<see cref="ChordRingView"/>): a layer of its own, so neither rig's camera or volume sees the other's.</summary>
        public const int WheelLayer = 30;
        /// <summary>Where the rig lives: far below the graph, never in the graph camera's view.</summary>
        static readonly Vector3 DefaultOrigin = new(0f, -100000f, 0f);

        public float BloomIntensity = 4f;
        /// <summary>Only the lights are in this camera's view, so its bloom can start low: the whole soft flare feeds it.</summary>
        public float BloomThreshold = .4f;
        public float BloomScatter = .7f;
        /// <summary>HDR intensity of a light's core disc and of its soft flare.</summary>
        public float CoreIntensity = 8f, FlareIntensity = 2.2f;

        readonly GameObject root;
        readonly Camera camera;
        readonly Volume volume;
        readonly VolumeProfile profile;
        readonly Bloom bloom;
        readonly RawImage image;
        readonly Material lightMaterial, compositeMaterial;
        readonly Mesh quad;
        readonly MaterialPropertyBlock block = new();
        readonly List<(MeshRenderer core, MeshRenderer flare)> lights = new();
        // Glow shapes (a feathered rect or arc, one mesh each): the current chord's light.
        readonly List<(MeshRenderer renderer, Mesh mesh, float intensity)> glows = new();
        readonly List<Vector3> glowVertices = new();
        readonly List<Color> glowColors = new();
        readonly List<int> glowIndices = new();
        readonly int layer;
        float panelWidth, panelHeight;
        RenderTexture? texture;

        public Camera Camera => camera;
        public Volume Volume => volume;
        public Bloom Bloom => bloom;
        public RawImage Image => image;
        public RenderTexture? Texture => texture;
        public Material LightMaterial => lightMaterial;
        public bool Active => root.activeSelf;
        public int Lights => lights.Count;
        /// <summary>The layer this rig's quads, camera and volume are on.</summary>
        public int RigLayer => layer;

        /// <summary>
        /// A rig for <paramref name="panel"/> (<paramref name="width"/> x <paramref name="height"/>
        /// reference px) on <paramref name="rigLayer"/>, its world objects at <paramref name="origin"/>
        /// (two rigs keep different layers and far-apart origins).
        /// </summary>
        public MelodyLightRig(Transform parent, RectTransform panel, float width, float height, Shader shader, int rigLayer = Layer, Vector3? origin = null)
        {
            panelWidth = width;
            panelHeight = height;
            layer = rigLayer;
            root = new GameObject(rigLayer == Layer ? "Melody Lights" : "Wheel Lights") { layer = layer };
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(origin ?? DefaultOrigin, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            GameObject cameraObject = new(rigLayer == Layer ? "Melody Light Camera" : "Wheel Light Camera") { layer = layer };
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.transform.localPosition = new Vector3(width * .5f, -height * .5f, -10f);
            camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = height * .5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0, 0, 0, 0);
            camera.cullingMask = 1 << layer;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 50f;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depth = -50;   // before the graph's camera, so the panel shows this frame's light
            camera.enabled = false;
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.volumeLayerMask = 1 << layer;
            data.volumeTrigger = cameraObject.transform;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;

            GameObject volumeObject = new(rigLayer == Layer ? "Melody Light Bloom" : "Wheel Light Bloom") { layer = layer };
            volumeObject.transform.SetParent(root.transform, false);
            volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "MusicHistory Melody Light Bloom";
            profile.hideFlags = HideFlags.DontSave;
            bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(BloomThreshold);
            bloom.intensity.Override(BloomIntensity);
            bloom.scatter.Override(BloomScatter);
            bloom.highQualityFiltering.Override(true);
            volume.sharedProfile = profile;

            quad = new Mesh { name = "Melody Light Quad", hideFlags = HideFlags.DontSave };
            quad.SetVertices(new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) });
            quad.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
            quad.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            quad.RecalculateBounds();

            lightMaterial = new Material(shader) { name = "MusicHistory Melody Light (HDR)", hideFlags = HideFlags.DontSave };
            lightMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            lightMaterial.SetFloat("_DstBlend", (float)BlendMode.One);   // soft discs add up on black
            compositeMaterial = new Material(shader) { name = "MusicHistory Melody Light Composite", hideFlags = HideFlags.DontSave };
            compositeMaterial.SetFloat("_SrcBlend", (float)BlendMode.One);
            compositeMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
            compositeMaterial.SetFloat("_Intensity", 1f);

            RectTransform r = UiKit.Rect("Light Composite", panel);
            UiKit.Place(r, 0, 0, width, height);
            r.gameObject.AddComponent<CanvasRenderer>();
            image = r.gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.material = compositeMaterial;
            image.color = Color.white;
            root.SetActive(false);
            image.gameObject.SetActive(false);
        }

        /// <summary>
        /// The panel changed size (the vertical layout's taller graph): the camera frames the new
        /// panel and the composite covers it; the texture follows at the next <see cref="Sync"/>.
        /// </summary>
        public void Resize(float width, float height)
        {
            if (Mathf.Approximately(width, panelWidth) && Mathf.Approximately(height, panelHeight)) return;
            panelWidth = width;
            panelHeight = height;
            camera.transform.localPosition = new Vector3(width * .5f, -height * .5f, -10f);
            camera.orthographicSize = height * .5f;
            UiKit.Place((RectTransform)image.transform, 0, 0, width, height);
        }

        /// <summary>One light per song (created on demand).</summary>
        public void EnsureLights(int count)
        {
            while (lights.Count < count)
            {
                int i = lights.Count;
                lights.Add((Quad($"Light {i + 1}", UiKit.Circle()), Quad($"Flare {i + 1}", UiKit.SoftDot())));
            }
            foreach ((MeshRenderer core, MeshRenderer flare) in lights)
            {
                core.gameObject.SetActive(false);
                flare.gameObject.SetActive(false);
            }
        }

        MeshRenderer Quad(string name, Sprite sprite)
        {
            GameObject go = new(name) { layer = layer };
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = lightMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.GetPropertyBlock(block);
            block.SetTexture("_MainTex", sprite.texture);
            mr.SetPropertyBlock(block);
            go.SetActive(false);
            return mr;
        }

        /// <summary>
        /// Light <paramref name="index"/> at <paramref name="panelPoint"/> (panel px, y down), its core
        /// <paramref name="core"/> px and flare <paramref name="flare"/> px wide, in <paramref name="color"/>
        /// scaled by <paramref name="level"/> (1 while singing).
        /// </summary>
        public void SetLight(int index, bool on, Vector2 panelPoint, float core, float flare, Color color, float level)
        {
            if (index < 0 || index >= lights.Count) return;
            (MeshRenderer c, MeshRenderer f) = lights[index];
            if (c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
            if (f.gameObject.activeSelf != on) f.gameObject.SetActive(on);
            if (!on) return;
            Vector3 at = new(panelPoint.x, -panelPoint.y, 0f);
            c.transform.localPosition = at + new Vector3(0, 0, -.01f);
            c.transform.localScale = new Vector3(core, core, 1f);
            f.transform.localPosition = at;
            f.transform.localScale = new Vector3(flare, flare, 1f);
            Paint(c, Color.Lerp(color, Color.white, .65f), CoreIntensity * level);
            Paint(f, color, FlareIntensity * level);
        }

        /// <summary>The bloom's tint (the playing melody's colour, so the glow takes its hue).</summary>
        public void SetTint(Color color)
        {
            Color t = Color.Lerp(color, Color.white, .2f);
            t.a = 1f;
            if (bloom.tint.value != t) bloom.tint.Override(t);
        }

        void Paint(MeshRenderer mr, Color color, float intensity)
        {
            mr.GetPropertyBlock(block);
            block.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            block.SetFloat("_Intensity", intensity);
            mr.SetPropertyBlock(block);
        }


        // ------------------------------------------------------------------ glow shapes

        /// <summary>Glow shapes shown now.</summary>
        public int GlowsShown
        {
            get
            {
                int n = 0;
                foreach ((MeshRenderer r, Mesh _, float _) in glows) if (r != null && r.gameObject.activeSelf) n++;
                return n;
            }
        }

        /// <summary>HDR intensity glow <paramref name="index"/> is drawn with (0 when hidden).</summary>
        public float GlowIntensityOf(int index) =>
            index >= 0 && index < glows.Count && glows[index].renderer != null && glows[index].renderer.gameObject.activeSelf ? glows[index].intensity : 0f;

        (MeshRenderer renderer, Mesh mesh) Glow(int index)
        {
            while (glows.Count <= index)
            {
                GameObject go = new($"Glow {glows.Count + 1}") { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, .01f);
                Mesh mesh = new() { name = "Glow Shape", hideFlags = HideFlags.DontSave };
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = lightMaterial;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                go.SetActive(false);
                glows.Add((mr, mesh, 0f));
            }
            return (glows[index].renderer, glows[index].mesh);
        }

        bool ShowGlow(int index, bool on)
        {
            if (!on && index >= glows.Count) return false;
            (MeshRenderer r, Mesh _) = Glow(index);
            if (r.gameObject.activeSelf != on) r.gameObject.SetActive(on);
            if (!on) glows[index] = (r, glows[index].mesh, 0f);
            return on;
        }

        void CommitGlow(int index, Color color, float intensity)
        {
            (MeshRenderer r, Mesh mesh) = Glow(index);
            mesh.Clear();
            mesh.SetVertices(glowVertices);
            mesh.SetColors(glowColors);
            mesh.SetTriangles(glowIndices, 0);
            mesh.RecalculateBounds();
            Paint(r, color, intensity);
            glows[index] = (r, mesh, intensity);
        }

        void GlowVertex(float x, float y, float alpha)
        {
            // Panel px, y down: the camera frames the panel from its top-left corner.
            glowVertices.Add(new Vector3(x, -y, 0f));
            glowColors.Add(new Color(1f, 1f, 1f, alpha));
        }

        /// <summary>
        /// Glow <paramref name="index"/> as a rectangle (panel px, y down) whose edges fade out over
        /// <paramref name="feather"/> px, in <paramref name="color"/> at HDR <paramref name="intensity"/>.
        /// </summary>
        public void SetGlowRect(int index, bool on, Rect panelRect, float feather, Color color, float intensity)
        {
            if (!ShowGlow(index, on)) return;
            glowVertices.Clear();
            glowColors.Clear();
            glowIndices.Clear();
            float f = Mathf.Max(0f, feather);
            float[] xs = { panelRect.xMin - f, panelRect.xMin, panelRect.xMax, panelRect.xMax + f };
            float[] ys = { panelRect.yMin - f, panelRect.yMin, panelRect.yMax, panelRect.yMax + f };
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                    GlowVertex(xs[i], ys[j], (i == 1 || i == 2) && (j == 1 || j == 2) ? 1f : 0f);
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    int a = j * 4 + i;
                    glowIndices.AddRange(new[] { a, a + 1, a + 5, a, a + 5, a + 4 });
                }
            CommitGlow(index, color, intensity);
        }

        /// <summary>
        /// Glow <paramref name="index"/> as a ring segment around <paramref name="center"/> (panel px,
        /// y down) between the radii, from <paramref name="startDegrees"/> to <paramref name="endDegrees"/>
        /// clockwise from 12 o'clock, every edge fading out over <paramref name="feather"/> px.
        /// </summary>
        public void SetGlowArc(int index, bool on, Vector2 center, float innerRadius, float outerRadius, float startDegrees, float endDegrees,
            float feather, Color color, float intensity)
        {
            float span = endDegrees - startDegrees;
            if (!ShowGlow(index, on && span > 0f && outerRadius > innerRadius)) return;
            glowVertices.Clear();
            glowColors.Clear();
            glowIndices.Clear();
            float f = Mathf.Max(0f, feather);
            float mid = Mathf.Max(1f, (innerRadius + outerRadius) * .5f);
            float fade = f / mid * Mathf.Rad2Deg;
            int steps = Mathf.Max(2, Mathf.CeilToInt(span / 3f));
            int columns = 0;
            void Column(float degrees, float alpha)
            {
                float rad = degrees * Mathf.Deg2Rad;
                Vector2 dir = new(Mathf.Sin(rad), -Mathf.Cos(rad));
                Vector2 o2 = center + dir * (outerRadius + f), o1 = center + dir * outerRadius;
                Vector2 i1 = center + dir * innerRadius, i2 = center + dir * Mathf.Max(0f, innerRadius - f);
                GlowVertex(o2.x, o2.y, 0f);
                GlowVertex(o1.x, o1.y, alpha);
                GlowVertex(i1.x, i1.y, alpha);
                GlowVertex(i2.x, i2.y, 0f);
                columns++;
            }
            Column(startDegrees - fade, 0f);
            for (int k = 0; k <= steps; k++) Column(Mathf.Lerp(startDegrees, endDegrees, k / (float)steps), 1f);
            Column(endDegrees + fade, 0f);
            for (int k = 0; k + 1 < columns; k++)
            {
                int a = k * 4, b = a + 4;
                for (int s = 0; s < 3; s++) glowIndices.AddRange(new[] { a + s, b + s, b + s + 1, b + s + 1, a + s + 1, a + s });
            }
            CommitGlow(index, color, intensity);
        }

        /// <summary>
        /// Shows or hides the rig. While shown, the texture matches the panel at <paramref name="pixelScale"/>
        /// screen pixels per reference pixel; <paramref name="renderNow"/> renders it at once (edit mode,
        /// captures: in play mode the camera renders every frame by itself).
        /// </summary>
        public void Sync(bool show, float pixelScale, bool renderNow)
        {
            if (root.activeSelf != show) root.SetActive(show);
            if (image.gameObject.activeSelf != show) image.gameObject.SetActive(show);
            camera.enabled = show && Application.isPlaying;
            if (!show) return;
            bloom.intensity.Override(BloomIntensity);
            bloom.scatter.Override(BloomScatter);
            bloom.threshold.Override(BloomThreshold);
            int w = Mathf.Clamp(Mathf.RoundToInt(panelWidth * pixelScale), 16, 8192);
            int h = Mathf.Clamp(Mathf.RoundToInt(panelHeight * pixelScale), 16, 8192);
            if (texture == null || texture.width != w || texture.height != h)
            {
                if (texture != null)
                {
                    camera.targetTexture = null;
                    texture.Release();
                    Kill(texture);
                }
                texture = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "Melody Lights", hideFlags = HideFlags.DontSave };
                texture.Create();
                camera.targetTexture = texture;
                image.texture = texture;
            }
            if (renderNow) camera.Render();
        }

        public void Dispose()
        {
            if (texture != null)
            {
                camera.targetTexture = null;
                texture.Release();
                Kill(texture);
            }
            texture = null;
            if (root != null) Kill(root);
            if (image != null) Kill(image.gameObject);
            Kill(profile);
            Kill(lightMaterial);
            Kill(compositeMaterial);
            Kill(quad);
            foreach ((MeshRenderer _, Mesh mesh, float _) in glows) Kill(mesh);
            glows.Clear();
            lights.Clear();
        }

        static void Kill(Object? o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
