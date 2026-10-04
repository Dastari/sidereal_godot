using System;
using System.Collections.Generic;
using System.Numerics;

/// <summary>Exact presentation clipping/profile algorithm from the pinned browser renderer.
/// Contains no game state, geometry authoring decisions, or Godot/native dependencies.</summary>
public static class ReplicatedWorldSurface
{
    public sealed record Channels(float[] Positions, float[] Normals, int[] Indices,
        float[]? Uvs = null, float[]? Uvs2 = null, float[]? Tangents = null);
    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2? Uv, Vector2? Uv2, Vector4? Tangent);

    public static Channels Transform(Channels source, float[] matrix, double[][] planes,
        double[][]? profile, double[] origin)
    {
        if (matrix.Length != 16 || origin.Length != 2 ||
            planes.Length > 64 || Array.Exists(matrix, v => !float.IsFinite(v)) ||
            Array.Exists(planes, p => p.Length != 4 || Array.Exists(p, v => !double.IsFinite(v))))
            throw new InvalidOperationException("Invalid authored surface transform.");
        var transform = new Matrix4x4(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5], matrix[6], matrix[7],
            matrix[8], matrix[9], matrix[10], matrix[11], matrix[12], matrix[13], matrix[14], matrix[15]);
        if (!Matrix4x4.Invert(transform, out var inverse)) throw new InvalidOperationException("Singular authored surface transform.");
        var normalTransform = Matrix4x4.Transpose(inverse);
        var reflected = transform.GetDeterminant() < 0;
        var vertices = new Vertex[source.Positions.Length / 3];
        for (var i = 0; i < vertices.Length; i++)
        {
            var position = Vector3.Transform(Read3(source.Positions, i), transform);
            var normal = Unit(Vector3.TransformNormal(Read3(source.Normals, i), normalTransform));
            Vector4? tangent = null;
            if (source.Tangents is { } tangents)
            {
                var t = Vector3.TransformNormal(new Vector3(tangents[i * 4], tangents[i * 4 + 1], tangents[i * 4 + 2]), transform);
                t = Unit(t - normal * Vector3.Dot(t, normal));
                tangent = new Vector4(t, tangents[i * 4 + 3] * (reflected ? -1 : 1));
            }
            if (profile != null)
            {
                if (profile.Length != 2 || Array.Exists(profile, row => row.Length != 3 || Array.Exists(row, v => !double.IsFinite(v))))
                    throw new InvalidOperationException("Invalid authored vertical profile.");
                var x = origin[0] - position.Z; var y = origin[1] - position.X; var s = position.Y;
                var b = profile[0]; var top = profile[1];
                var bottom = b[0] * x + b[1] * y + b[2];
                var height = (top[0] - b[0]) * x + (top[1] - b[1]) * y + top[2] - b[2];
                if (height <= 1e-8) throw new InvalidOperationException("Inverted authored vertical profile.");
                var dx = b[0] + s * (top[0] - b[0]); var dy = b[1] + s * (top[1] - b[1]);
                var jacobian = new Matrix4x4(1, (float)-dy, 0, 0, 0, (float)height, 0, 0, 0, (float)-dx, 1, 0, 0, 0, 0, 1);
                Matrix4x4.Invert(jacobian, out var inverseProfile);
                normal = Unit(Vector3.TransformNormal(normal, Matrix4x4.Transpose(inverseProfile)));
                if (tangent is { } prior)
                {
                    var t = Vector3.TransformNormal(new Vector3(prior.X, prior.Y, prior.Z), jacobian);
                    t = Unit(t - normal * Vector3.Dot(t, normal)); tangent = new Vector4(t, prior.W);
                }
                position.Y = (float)(bottom + s * height);
            }
            vertices[i] = new Vertex(position, normal, Read2(source.Uvs, i), Read2(source.Uvs2, i), tangent);
        }
        var positions = new List<float>(); var normals = new List<float>(); var indices = new List<int>();
        var uvs = source.Uvs == null ? null : new List<float>(); var uvs2 = source.Uvs2 == null ? null : new List<float>();
        var outputTangents = source.Tangents == null ? null : new List<float>();
        for (var index = 0; index < source.Indices.Length; index += 3)
        {
            var polygon = new List<Vertex> { vertices[source.Indices[index]], vertices[source.Indices[index + (reflected ? 2 : 1)]], vertices[source.Indices[index + (reflected ? 1 : 2)]] };
            foreach (var plane in planes)
            {
                var clipped = new List<Vertex>();
                for (var j = 0; j < polygon.Count; j++)
                {
                    var a = polygon[j]; var next = polygon[(j + 1) % polygon.Count];
                    var da = Distance(a.Position, plane); var db = Distance(next.Position, plane);
                    var inside = da >= -1e-9; var nextInside = db >= -1e-9;
                    if (inside) clipped.Add(a);
                    if (inside != nextInside) clipped.Add(Interpolate(a, next, (float)(da / (da - db))));
                }
                polygon = clipped; if (polygon.Count < 3) break;
            }
            for (var j = 1; j + 1 < polygon.Count; j++)
            {
                if (Vector3.Cross(polygon[j].Position - polygon[0].Position, polygon[j + 1].Position - polygon[0].Position).Length() < 1e-12) continue;
                foreach (var v in new[] { polygon[0], polygon[j], polygon[j + 1] })
                {
                    indices.Add(positions.Count / 3); Add3(positions, v.Position); Add3(normals, v.Normal);
                    if (v.Uv is { } uv) { uvs!.Add(uv.X); uvs.Add(uv.Y); }
                    if (v.Uv2 is { } uv2) { uvs2!.Add(uv2.X); uvs2.Add(uv2.Y); }
                    if (v.Tangent is { } t) { outputTangents!.Add(t.X); outputTangents.Add(t.Y); outputTangents.Add(t.Z); outputTangents.Add(t.W); }
                }
            }
        }
        return new Channels(positions.ToArray(), normals.ToArray(), indices.ToArray(), uvs?.ToArray(), uvs2?.ToArray(), outputTangents?.ToArray());
    }

    private static Vertex Interpolate(Vertex a, Vertex b, float fraction)
    {
        var normal = Unit(Vector3.Lerp(a.Normal, b.Normal, fraction)); Vector4? tangent = null;
        if (a.Tangent is { } ta && b.Tangent is { } tb)
        {
            var mixed = Vector4.Lerp(ta, tb, fraction); var t = new Vector3(mixed.X, mixed.Y, mixed.Z);
            t = Unit(t - normal * Vector3.Dot(t, normal)); tangent = new Vector4(t, ta.W);
        }
        return new Vertex(Vector3.Lerp(a.Position, b.Position, fraction), normal,
            a.Uv is { } ua && b.Uv is { } ub ? Vector2.Lerp(ua, ub, fraction) : null,
            a.Uv2 is { } u2a && b.Uv2 is { } u2b ? Vector2.Lerp(u2a, u2b, fraction) : null, tangent);
    }
    private static double Distance(Vector3 p, double[] plane) => plane[0] * p.X + plane[1] * p.Y + plane[2] * p.Z + plane[3];
    private static Vector3 Unit(Vector3 v) => v.LengthSquared() > 1e-24 ? Vector3.Normalize(v) : Vector3.UnitY;
    private static Vector3 Read3(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);
    private static Vector2? Read2(float[]? a, int i) => a == null ? null : new Vector2(a[i * 2], a[i * 2 + 1]);
    private static void Add3(List<float> target, Vector3 p) { target.Add(p.X); target.Add(p.Y); target.Add(p.Z); }
}
