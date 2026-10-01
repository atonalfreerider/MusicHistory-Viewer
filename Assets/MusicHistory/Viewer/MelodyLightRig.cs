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
        /// <summary>Where the rig lives: far below the graph, never in the graph camera's view.</summary>
        static readonly Vector3 Origin = new(0f, -100000f, 0f);

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
        readonly float panelWidth, panelHeight;
        RenderTexture? texture;

        public Camera Camera => camera;
        public Volume Volume => volume;
        public Bloom Bloom => bloom;
        public RawImage Image => image;
        public RenderTexture? Texture => texture;
        public Material LightMaterial => lightMaterial;
        public bool Active => root.activeSelf;
        public int Lights => lights.Count;

        public MelodyLightRig(Transform parent, RectTransform panel, float width, float height, Shader shader)
        {
            panelWidth = width;
            panelHeight = height;
            root = new GameObject("Melody Lights") { layer = Layer };
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(Origin, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            GameObject cameraObject = new("Melody Light Camera") { layer = Layer };
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.transform.localPosition = new Vector3(width * .5f, -height * .5f, -10f);
            camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = height * .5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0, 0, 0, 0);
            camera.cullingMask = 1 << Layer;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 50f;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depth = -50;   // before the graph's camera, so the panel shows this frame's light
            camera.enabled = false;
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.volumeLayerMask = 1 << Layer;
            data.volumeTrigger = cameraObject.transform;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;

            GameObject volumeObject = new("Melody Light Bloom") { layer = Layer };
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
            GameObject go = new(name) { layer = Layer };
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
