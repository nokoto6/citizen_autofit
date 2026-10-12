using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Which of two maps each drawing in a layer of paint goes by: the face's (<see cref="BodyMap.OnSkin"/>)
/// or the neck's (<see cref="BodyMap.OnNeck"/>). The citizen's paint is one sheet over the
/// whole head and neck, and what is drawn on it is in its texture: a word, a motif, a band of
/// lettering round the throat. A drawing split between the two maps along the citizen's jaw line
/// was torn and drawn out where they part, so each goes by one of them as a whole: by the neck's
/// if most of its ink lies under the citizen's jaw line, else by the face's. A drawing is ink
/// joined up over the sheet's triangles.
/// </summary>
public static class PaintIslands
{
	const float Inked = 0.2f;   // a triangle with this much ink (texture alpha) somewhere on it is drawn on

	/// <summary>
	/// Per vertex, whether it goes by the neck's map.
	/// </summary>
	/// <param name="made">The layer's vertices as made on the stock body.</param>
	/// <param name="tris">Its triangles.</param>
	/// <param name="uvs">Its texture coordinates.</param>
	/// <param name="paint">Which vertices are paint.</param>
	/// <param name="ink">How much ink (0 to 1) the texture has at a texture coordinate, by a vertex of the triangle it is on (its material). Null when the texture couldn't be read: then each vertex goes by where it lies itself.</param>
	/// <param name="jawHeight">How high the stock body's jaw line is round its neck at a point (<see cref="BodyMap.JawHeight"/>); NaN where it isn't known (the point is the face's).</param>
	public static bool[] ByTheNeck( Vec3[] made, int[] tris, Vec2[] uvs, bool[] paint, Func<int, Vec2, float> ink, Func<Vec3, float> jawHeight )
	{
		int n = made.Length;
		var result = new bool[n];
		bool Under( Vec3 p ) { float jaw = jawHeight( p ); return !float.IsNaN( jaw ) && p.Z < jaw; }
		for ( int v = 0; v < n; v++ ) result[v] = paint[v] && Under( made[v] );
		if ( ink == null || uvs == null || uvs.Length != n ) return result;

		// The drawings: inked triangles joined at their corners (copies of a corner on either
		// side of a seam in the texture count as one).
		var weld = MeshTools.Weld( made, out int welded );
		var parent = new int[welded];
		for ( int i = 0; i < welded; i++ ) parent[i] = i;
		int Root( int i )
		{
			while ( parent[i] != i ) { parent[i] = parent[parent[i]]; i = parent[i]; }
			return i;
		}
		var weight = new float[tris.Length / 3];
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			if ( !paint[a] || !paint[b] || !paint[c] ) continue;
			// Ink at the middle and halfway along each edge.
			float most = 0, sum = 0;
			foreach ( var (wa, wb, wc) in Samples )
			{
				var uv = new Vec2( uvs[a].X * wa + uvs[b].X * wb + uvs[c].X * wc, uvs[a].Y * wa + uvs[b].Y * wb + uvs[c].Y * wc );
				float at = ink( a, uv );
				most = MathF.Max( most, at );
				sum += at;
			}
			if ( most < Inked ) continue;
			weight[t / 3] = sum / Samples.Length * 0.5f * Vec3.Cross( made[b] - made[a], made[c] - made[a] ).Length();
			int ra = Root( weld[a] ), rb = Root( weld[b] ), rc = Root( weld[c] );
			parent[rb] = ra;
			parent[Root( rc )] = Root( ra );
		}

		// How much of each drawing's ink lies under the jaw line.
		var under = new Dictionary<int, float>();
		var all = new Dictionary<int, float>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			float w = weight[t / 3];
			if ( w <= 0 ) continue;
			int root = Root( weld[tris[t]] );
			var middle = (made[tris[t]] + made[tris[t + 1]] + made[tris[t + 2]]) / 3;
			all[root] = all.GetValueOrDefault( root ) + w;
			if ( Under( middle ) ) under[root] = under.GetValueOrDefault( root ) + w;
		}
		// Every corner of a drawing goes its way, and so do its copies across texture seams.
		var drawing = new sbyte[welded];   // 0 on no drawing, 1 the face's, 2 the neck's
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			if ( weight[t / 3] <= 0 ) continue;
			int root = Root( weld[tris[t]] );
			sbyte way = (sbyte)(under.GetValueOrDefault( root ) > 0.5f * all[root] ? 2 : 1);
			for ( int k = 0; k < 3; k++ ) drawing[weld[tris[t + k]]] = way;
		}
		for ( int v = 0; v < n; v++ )
			if ( paint[v] && drawing[weld[v]] != 0 ) result[v] = drawing[weld[v]] == 2;
		return result;
	}

	static readonly (float, float, float)[] Samples =
	{
		(1 / 3f, 1 / 3f, 1 / 3f), (0.5f, 0.5f, 0), (0, 0.5f, 0.5f), (0.5f, 0, 0.5f),
		(2 / 3f, 1 / 6f, 1 / 6f), (1 / 6f, 2 / 3f, 1 / 6f), (1 / 6f, 1 / 6f, 2 / 3f),
	};

	/// <summary>Each vertex from one fit or the other.</summary>
	public static Vec3[] Pick( Vec3[] byFace, Vec3[] byNeck, bool[] neck )
	{
		var result = new Vec3[byFace.Length];
		for ( int v = 0; v < result.Length; v++ ) result[v] = neck[v] ? byNeck[v] : byFace[v];
		return result;
	}
}
