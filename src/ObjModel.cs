using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace NOInfantry
{
    // Minimal runtime OBJ/MTL loader for the viewmodel (Unity has no runtime model importer).
    // Supports v / vn / f (any of v, v/vt, v//vn, v/vt/vn, n-gons fanned), usemtl, and MTL Kd colours.
    //
    // Gun convention expected from the source file (Quaternius packs): barrel along +X, up +Y.
    // Mapped to the viewmodel frame (+Z forward, +Y up) by swapping X and Z, which is also the
    // right-handed -> left-handed conversion, so face winding is kept as-is.
    internal class ObjModel
    {
        internal Mesh Mesh;
        internal List<Color> SubmeshColors = new List<Color>();

        // Measured in the viewmodel frame after scaling, relative to the bore line at the rear of the gun.
        internal float SightHeight;   // front sight tip above the bore
        internal float FrontSightZ;
        internal float RearZ;         // rearmost point of the receiver area used for eye relief
        internal float MuzzleZ;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static ObjModel Load(string objPath, float targetLength)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var submeshTris = new Dictionary<string, List<int>>();
            var order = new List<string>();
            var outVerts = new List<Vector3>();
            var outNormals = new List<Vector3>();
            string current = "default";
            string mtlFile = null;

            foreach (string raw in File.ReadLines(objPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "v":
                        positions.Add(new Vector3(F(p[3]), F(p[2]), F(p[1]))); // swap X<->Z
                        break;
                    case "vn":
                        normals.Add(new Vector3(F(p[3]), F(p[2]), F(p[1])));
                        break;
                    case "usemtl":
                        current = p.Length > 1 ? p[1] : "default";
                        break;
                    case "mtllib":
                        mtlFile = line.Substring(7).Trim();
                        break;
                    case "f":
                        if (!submeshTris.TryGetValue(current, out List<int> tris))
                        {
                            tris = new List<int>();
                            submeshTris[current] = tris;
                            order.Add(current);
                        }
                        int first = outVerts.Count;
                        int count = p.Length - 1;
                        for (int i = 1; i < p.Length; i++)
                        {
                            string[] idx = p[i].Split('/');
                            outVerts.Add(positions[Index(idx[0], positions.Count)]);
                            outNormals.Add(idx.Length > 2 && idx[2].Length > 0 ? normals[Index(idx[2], normals.Count)] : Vector3.zero);
                        }
                        for (int i = 1; i < count - 1; i++)
                        {
                            tris.Add(first);
                            tris.Add(first + i);
                            tris.Add(first + i + 1);
                        }
                        break;
                }
            }
            if (outVerts.Count == 0) throw new InvalidDataException("no faces");

            // Bore line = centre of the muzzle cross-section; scale to the requested length.
            Bounds b = new Bounds(outVerts[0], Vector3.zero);
            foreach (Vector3 v in outVerts) b.Encapsulate(v);
            float length = b.size.z;
            float scale = targetLength / length;
            float muzzleZone = b.max.z - length * 0.02f;
            float muzzleMinY = float.MaxValue, muzzleMaxY = float.MinValue;
            foreach (Vector3 v in outVerts)
            {
                if (v.z < muzzleZone) continue;
                muzzleMinY = Mathf.Min(muzzleMinY, v.y);
                muzzleMaxY = Mathf.Max(muzzleMaxY, v.y);
            }
            float boreY = (muzzleMinY + muzzleMaxY) * 0.5f;

            // Front sight = highest point in the front half (excluding the muzzle device).
            Vector3 tip = new Vector3(0f, float.MinValue, 0f);
            foreach (Vector3 v in outVerts)
            {
                if (v.z < b.min.z + length * 0.5f || v.z > b.max.z - length * 0.04f) continue;
                if (v.y > tip.y) tip = v;
            }

            var model = new ObjModel();
            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(objPath) };
            if (outVerts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            for (int i = 0; i < outVerts.Count; i++)
            {
                Vector3 v = outVerts[i];
                outVerts[i] = new Vector3(v.x - b.center.x, v.y - boreY, v.z - b.min.z) * scale;
            }
            mesh.SetVertices(outVerts);
            mesh.subMeshCount = order.Count;
            for (int s = 0; s < order.Count; s++) mesh.SetTriangles(submeshTris[order[s]], s);
            bool hasNormals = outNormals.Exists(n => n != Vector3.zero);
            if (hasNormals) mesh.SetNormals(outNormals); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            model.Mesh = mesh;

            model.SightHeight = (tip.y - boreY) * scale;
            model.FrontSightZ = (tip.z - b.min.z) * scale;
            model.MuzzleZ = length * scale;
            model.RearZ = 0f;

            Dictionary<string, Color> colors = mtlFile != null
                ? LoadMtl(Path.Combine(Path.GetDirectoryName(objPath), mtlFile))
                : new Dictionary<string, Color>();
            foreach (string m in order)
            {
                model.SubmeshColors.Add(colors.TryGetValue(m, out Color c) ? c : new Color(0.15f, 0.15f, 0.15f));
            }
            return model;
        }

        private static Dictionary<string, Color> LoadMtl(string path)
        {
            var result = new Dictionary<string, Color>();
            if (!File.Exists(path)) return result;
            string current = null;
            foreach (string raw in File.ReadLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                if (p[0] == "newmtl") current = p.Length > 1 ? p[1] : null;
                else if (p[0] == "Kd" && current != null && p.Length >= 4)
                {
                    // Blender writes linear Kd; Material.color expects sRGB.
                    result[current] = new Color(
                        Mathf.LinearToGammaSpace(F(p[1])),
                        Mathf.LinearToGammaSpace(F(p[2])),
                        Mathf.LinearToGammaSpace(F(p[3])));
                }
            }
            return result;
        }

        private static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);

        private static int Index(string s, int count)
        {
            int i = int.Parse(s, Inv);
            return i < 0 ? count + i : i - 1;
        }
    }
}
