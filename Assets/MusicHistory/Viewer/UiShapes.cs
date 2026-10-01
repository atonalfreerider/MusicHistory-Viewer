#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// A uGUI graphic that draws filled rectangles and anti-aliased polylines (a feathered edge of
    /// <see cref="Feather"/> px fades to transparent), laid out like <see cref="UiKit.Place"/>: in
    /// reference pixels from the graphic's top-left corner, y down. Build with <see cref="Clear"/>,
    /// <see cref="AddRect"/> / <see cref="AddPolyline"/>, then <see cref="Commit"/>; the mesh is only
    /// rebuilt then, so a static line costs nothing per frame.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class UiShapes : MaskableGraphic
    {
        /// <summary>Width of the fade at a line's edge, in reference pixels.</summary>
        public float Feather = 1f;
        /// <summary>A miter is never longer than this many half-widths (sharp turns are cut).</summary>
        public float MiterLimit = 2.2f;

        readonly List<UIVertex> vertices = new();
        readonly List<int> indices = new();

        public int VertexCount => vertices.Count;

        public void Clear()
        {
            vertices.Clear();
            indices.Clear();
        }

        /// <summary>Rebuilds the mesh from what was added since <see cref="Clear"/>.</summary>
        public void Commit() => SetVerticesDirty();

        public void AddRect(float x, float y, float w, float h, Color c)
        {
            if (w <= 0 || h <= 0) return;
            int i = vertices.Count;
            Vertex(x, y, c);
            Vertex(x + w, y, c);
            Vertex(x + w, y + h, c);
            Vertex(x, y + h, c);
            indices.Add(i);
            indices.Add(i + 1);
            indices.Add(i + 2);
            indices.Add(i + 2);
            indices.Add(i + 3);
            indices.Add(i);
        }

        /// <summary>A rectangle whose colour runs from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
        public void AddGradientRect(float x, float y, float w, float h, Color top, Color bottom)
        {
            if (w <= 0 || h <= 0) return;
            int i = vertices.Count;
            Vertex(x, y, top);
            Vertex(x + w, y, top);
            Vertex(x + w, y + h, bottom);
            Vertex(x, y + h, bottom);
            indices.Add(i);
            indices.Add(i + 1);
            indices.Add(i + 2);
            indices.Add(i + 2);
            indices.Add(i + 3);
            indices.Add(i);
        }

        /// <summary>
        /// An open polyline through <paramref name="points"/> (reference px, y down), core width
        /// <paramref name="width"/>, mitered joins, feathered edges.
        /// </summary>
        public void AddPolyline(IReadOnlyList<Vector2> points, float width, Color c)
        {
            int n = points.Count;
            if (n < 2) return;
            float half = Mathf.Max(.25f, width * .5f), feather = Mathf.Max(0f, Feather);
            Color edge = new(c.r, c.g, c.b, 0f);
            int first = vertices.Count;
            for (int k = 0; k < n; k++)
            {
                Vector2 p = points[k];
                Vector2 dirIn = k > 0 ? (p - points[k - 1]) : (points[1] - p);
                Vector2 dirOut = k + 1 < n ? (points[k + 1] - p) : dirIn;
                dirIn = dirIn.sqrMagnitude > 1e-8f ? dirIn.normalized : dirOut.normalized;
                dirOut = dirOut.sqrMagnitude > 1e-8f ? dirOut.normalized : dirIn;
                Vector2 tangent = dirIn + dirOut;
                tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : dirOut;
                Vector2 normal = new(-tangent.y, tangent.x);
                Vector2 segNormal = new(-dirOut.y, dirOut.x);
                float dot = Mathf.Abs(Vector2.Dot(normal, segNormal));
                float miter = dot > 1e-3f ? Mathf.Min(MiterLimit, 1f / dot) : MiterLimit;
                Vector2 inner = normal * (half * miter), outer = normal * ((half + feather) * miter);
                Vertex(p.x + outer.x, p.y + outer.y, edge);
                Vertex(p.x + inner.x, p.y + inner.y, c);
                Vertex(p.x - inner.x, p.y - inner.y, c);
                Vertex(p.x - outer.x, p.y - outer.y, edge);
            }
            for (int k = 0; k + 1 < n; k++)
            {
                int a = first + k * 4, b = a + 4;
                for (int s = 0; s < 3; s++)
                {
                    indices.Add(a + s);
                    indices.Add(b + s);
                    indices.Add(b + s + 1);
                    indices.Add(b + s + 1);
                    indices.Add(a + s + 1);
                    indices.Add(a + s);
                }
            }
        }

        void Vertex(float x, float y, Color c)
        {
            UIVertex v = UIVertex.simpleVert;
            v.position = new Vector3(x, -y, 0f);
            v.color = c;
            v.uv0 = new Vector4(.5f, .5f, 0f, 0f);
            vertices.Add(v);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (vertices.Count == 0) return;
            vh.AddUIVertexStream(vertices, indices);
        }
    }
}
