#nullable enable
using UnityEngine;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// A featured path drawn over the overview: a glowing polyline through the route's songs in
    /// play order (a bright core and a soft additive halo), kept a constant width on screen like the
    /// timeline, so a path of small bubbles still reads at overview scale. Shown while the paths
    /// panel previews a route; hidden otherwise.
    /// </summary>
    public sealed class RouteLine : MonoBehaviour
    {
        [Tooltip("Core width in pixels.")]
        public float CorePixels = 3.5f;
        [Tooltip("Halo width in pixels.")]
        public float HaloPixels = 13f;

        LineRenderer core = null!, halo = null!;
        GraphRoute? route;
        Vector3 midpoint;

        public GraphRoute? Route => route;
        public bool Visible => route != null && core != null && core.enabled;

        public static RouteLine Create(Transform parent)
        {
            GameObject go = new("Route Line");
            go.transform.SetParent(parent, false);
            RouteLine line = go.AddComponent<RouteLine>();
            line.halo = MakeLine("Halo", go.transform);
            line.core = MakeLine("Core", go.transform);
            line.Show(null);
            return line;
        }

        static LineRenderer MakeLine(string name, Transform parent)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            LineRenderer l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.numCapVertices = 4;
            l.numCornerVertices = 4;
            l.alignment = LineAlignment.View;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            l.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return l;
        }

        /// <summary>Draws <paramref name="r"/> (null hides the line).</summary>
        public void Show(GraphRoute? r)
        {
            route = r;
            bool on = r != null && r.Nodes.Count >= 2;
            if (core == null || halo == null) return;
            core.enabled = halo.enabled = on;
            if (!on) return;
            int n = r!.Nodes.Count;
            core.positionCount = halo.positionCount = n;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = r.Nodes[i] != null ? r.Nodes[i].transform.position : Vector3.zero;
                core.SetPosition(i, p);
                halo.SetPosition(i, p);
                sum += p;
            }
            midpoint = sum / n;
            Color c = r.Color;
            core.sharedMaterial = GraphMaterials.Line(new Color(c.r * .8f, c.g * .8f, c.b * .8f, 1f), .9f, additive: false);
            halo.sharedMaterial = GraphMaterials.Line(new Color(c.r * .22f, c.g * .22f, c.b * .22f, 1f), 0f, additive: true);
            Refresh(Camera.main);
        }

        void LateUpdate() => Refresh(Camera.main);

        /// <summary>Keeps the widths constant in pixels for <paramref name="cam"/>.</summary>
        public void Refresh(Camera? cam)
        {
            if (cam == null || route == null || core == null || !core.enabled) return;
            float distance = Vector3.Distance(cam.transform.position, midpoint);
            float worldPerPixel = cam.orthographic
                ? cam.orthographicSize * 2f / Mathf.Max(1, cam.pixelHeight)
                : 2f * distance * Mathf.Tan(cam.fieldOfView * .5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight);
            core.widthMultiplier = worldPerPixel * CorePixels;
            halo.widthMultiplier = worldPerPixel * HaloPixels;
        }
    }
}
