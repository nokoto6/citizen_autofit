using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Works out where every skin vertex of the stock body ends up on another body.
///
/// Two ways of finding the matching point, tried in this order:
///  - by UV. As long as the other body keeps the stock UV layout (sculpting, moving vertices,
///    adding loops all do), the same UV is the same bit of skin. Exact.
///  - by bone rays. The bodies share a skeleton, so shoot a ray from the bone axis through the
///    skin vertex and see where the other body's skin is along that same ray. Works for any
///    mesh, but only knows "further from the bone", so skin that slides along the surface is
///    not tracked.
/// This is done once per body. Every garment then reuses the result.
/// </summary>
public sealed class BodyMap
{
	/// <summary>Displacement of each stock body vertex.</summary>
	public Vec3[] SkinMove;

	public int ByUv, ByRays, NoMatch, Outliers;

	const float LookBeyond = 0.5f * Units.Metre;    // how far past the old skin we look for the new one
	const float UvMaxMove = 0.15f * Units.Metre;    // a UV match further away than this is a different UV layout
	const int MinOwnTriangles = 8;                  // a bone needs this many skin triangles of its own to get an axis
	const float MinWeight = 0.05f;
	const float LandmarkReach = 0.07f * Units.Metre;  // how far around a landmark the skin moves with it

	public static BodyMap Build( SkinnedGeometry stock, TriMesh old, TriMesh other, Vec2[] otherUvs ) =>
		Build( stock, old, other, otherUvs, null, null );

	/// <param name="otherSkin">
	/// The other body's skin by stock bone, if it is skinned. A ray from a bone then only counts
	/// hits on skin that bone or a bone near it drives, so a ray from the spine can't slip
	/// between two ribs and land on an arm.
	/// </param>
	/// <param name="landmarks">Face landmarks from <see cref="Repose"/>: skin near each one shifts along the surface with it.</param>
	public static BodyMap Build( SkinnedGeometry stock, TriMesh old, TriMesh other, Vec2[] otherUvs, SkinByBone otherSkin, List<(Vec3 At, Vec3 Shift)> landmarks )
	{
		var result = new BodyMap();
		var weld = MeshTools.Weld( old.Verts, out int n );
		var firstOf = new int[n];
		for ( int i = old.Verts.Length - 1; i >= 0; i-- )
			firstOf[weld[i]] = i;

		BoneAxes( stock, old, out var axisA, out var axisB );
		var uv = otherUvs != null ? new UvLookup( other, otherUvs ) : null;

		var oldNormals = new Vec3[old.Verts.Length];
		for ( int t = 0; t < old.TriCount; t++ )
			for ( int k = 0; k < 3; k++ )
				oldNormals[old.Tris[t * 3 + k]] += old.TriNormals[t];

		// A body with its own UV layout still gets the odd accidental match. Only trust UVs
		// when they line up for a good part of the skin.
		if ( uv != null )
		{
			int matches = 0;
			for ( int i = 0; i < n; i++ )
				if ( UvMatch( uv, stock, old, oldNormals, firstOf[i], out _ ) ) matches++;
			if ( matches < 0.3f * n ) uv = null;
		}

		var move = new Vec3[n];
		var valid = new bool[n];
		var exact = new bool[n];
		var hits = new List<(float, bool, int)>();

		for ( int i = 0; i < n; i++ )
		{
			int v = firstOf[i];
			Vec3 s = old.Verts[v];

			if ( uv != null && UvMatch( uv, stock, old, oldNormals, v, out var p ) )
			{
				move[i] = p - s;
				valid[i] = exact[i] = true;
				result.ByUv++;
				continue;
			}

			float total = 0;
			var sum = Vec3.Zero;
			for ( int j = 0; j < 4; j++ )
			{
				int bone = stock.BoneIndex[v * 4 + j];
				float w = stock.BoneWeight[v * 4 + j];
				if ( bone < 0 || w < MinWeight ) continue;
				if ( !FollowSkin( old, other, otherSkin, bone, axisA[bone], axisB[bone], s, hits, out var target ) ) continue;
				sum += (target - s) * w;
				total += w;
			}

			if ( total > 1e-6f )
			{
				move[i] = sum / total;
				valid[i] = true;
				result.ByRays++;

				// Rays only move skin towards or away from the bone. Near a landmark the skin
				// also slides along the surface with it (eyes sitting lower on the face, say).
				if ( landmarks != null )
				{
					Vec3 normal = oldNormals[v] / MathF.Max( oldNormals[v].Length(), 1e-20f );
					foreach ( var (at, shift) in landmarks )
					{
						float d = (s - at).Length() / LandmarkReach;
						float w = MathF.Exp( -0.5f * d * d );
						if ( w > 0.01f ) move[i] += (shift - normal * Vec3.Dot( normal, shift )) * w;
					}
				}
			}
			else
			{
				result.NoMatch++;
			}
		}

		var edges = MeshTools.Edges( old.Tris, weld );
		var kept = MeshTools.DropOutliers( move, valid, edges );
		for ( int i = 0; i < n; i++ )
			if ( valid[i] && !kept[i] ) result.Outliers++;

		var found = MeshTools.Copy( move );
		var known = MeshTools.Copy( kept );
		MeshTools.Relax( move, known, edges, 2, 0.5f );

		// Ray results are noisy and want the smoothing. UV matches are exact, leave them be.
		for ( int i = 0; i < n; i++ )
			if ( exact[i] && kept[i] ) move[i] = found[i];

		// Whatever is still unknown sits on a patch nothing reached. Let it spread in.
		for ( int pass = 0; pass < 50 && Array.IndexOf( known, false ) >= 0; pass++ )
			MeshTools.Relax( move, known, edges, 1, 0f );

		result.SkinMove = new Vec3[old.Verts.Length];
		for ( int i = 0; i < old.Verts.Length; i++ )
			result.SkinMove[i] = move[weld[i]];
		return result;
	}

	static bool UvMatch( UvLookup uv, SkinnedGeometry stock, TriMesh old, Vec3[] oldNormals, int v, out Vec3 p )
	{
		if ( !uv.Find( stock.Uvs[v], out p, out var normal ) ) return false;

		// Same UV only means same skin if the layout is the same. A point that lands far away
		// or faces the other way came from someone else's layout.
		float facing = Vec3.Dot( normal, oldNormals[v] ) / (normal.Length() * oldNormals[v].Length() + 1e-20f);
		return (p - old.Verts[v]).Length() <= UvMaxMove && facing > 0.3f;
	}

	/// <summary>
	/// One segment per bone, lying inside the body. It starts at the joint and runs through the
	/// centre of the skin that bone drives, about as far again. For a limb that lands on the
	/// next joint, for the head it gives a vertical axis through the skull. Bones that drive too
	/// little skin stay a single point.
	/// </summary>
	static void BoneAxes( SkinnedGeometry stock, TriMesh body, out Vec3[] a, out Vec3[] b )
	{
		int bones = stock.BoneNames.Length;
		a = MeshTools.Copy( stock.BonePositions );
		b = MeshTools.Copy( stock.BonePositions );

		// Twist and other helper bones (children with no children of their own) take skin
		// away from the bone they help. Count their skin as the parent's too.
		var hasChild = new bool[bones];
		for ( int i = 0; i < bones; i++ )
			if ( stock.BoneParents[i] >= 0 ) hasChild[stock.BoneParents[i]] = true;
		var owner = new int[bones];
		for ( int i = 0; i < bones; i++ )
			owner[i] = stock.BoneParents[i] >= 0 && !hasChild[i] ? stock.BoneParents[i] : -1;

		// Area-weighted, so a densely modelled face doesn't drag the head's centre forward.
		var area = new float[bones];
		var centre = new Vec3[bones];
		var triangles = new int[bones];
		var weights = new Dictionary<int, float>();
		for ( int t = 0; t < body.TriCount; t++ )
		{
			int dominant = MeshTools.DominantBone( stock, body.Tris, t, weights );
			if ( dominant < 0 ) continue;
			float size = 0.5f * body.TriNormals[t].Length();
			Vec3 mid = (body.Verts[body.Tris[t * 3]] + body.Verts[body.Tris[t * 3 + 1]] + body.Verts[body.Tris[t * 3 + 2]]) / 3f;
			for ( int bone = dominant; bone >= 0; bone = bone == dominant ? owner[dominant] : -1 )
			{
				area[bone] += size;
				centre[bone] += mid * size;
				triangles[bone]++;
			}
		}

		for ( int bone = 0; bone < bones; bone++ )
		{
			if ( triangles[bone] < MinOwnTriangles || area[bone] < 1e-9f ) continue;
			Vec3 c = centre[bone] / area[bone];
			if ( !body.Inside( c ) ) continue;
			float radius = body.Nearest( c, out _, out _ );

			var ends = new[] { stock.BonePositions[bone], c * 2f - stock.BonePositions[bone] };
			for ( int k = 0; k < 2; k++ )
			{
				// Pull the end towards the centre until it is properly inside. Rays that
				// start on the skin are useless.
				for ( int tries = 0; tries < 12; tries++ )
				{
					if ( body.Inside( ends[k] ) && body.Nearest( ends[k], out _, out _ ) >= 0.5f * radius ) break;
					ends[k] = c + (ends[k] - c) * 0.75f;
				}
			}

			a[bone] = ends[0];
			b[bone] = ends[1];
		}
	}

	/// <summary>
	/// Where the skin point s of the old body sits on the other body, seen from one bone.
	///
	/// The ray runs from the bone axis through s. Usually s is where the ray finally leaves
	/// the body, but not always: on an ear it leaves twice, inside the mouth it may be entering
	/// rather than leaving. So find which crossing s is on the old body and take the matching
	/// one on the other body.
	/// </summary>
	static bool FollowSkin( TriMesh old, TriMesh other, SkinByBone otherSkin, int bone, Vec3 a, Vec3 b, Vec3 s, List<(float T, bool Leaving, int Tri)> hits, out Vec3 target )
	{
		target = s;
		Vec3 q = MeshTools.ClosestOnSegment( s, a, b );
		Vec3 v = s - q;
		float r0 = v.Length();
		if ( r0 < 1e-6f * Units.Metre ) return false;
		Vec3 d = v / r0;
		float reach = r0 + LookBeyond;

		old.Crossings( q, d, Units.Metre * 10f, hits );
		int at = -1;
		for ( int i = 0; i < hits.Count; i++ )
			if ( MathF.Abs( hits[i].T - r0 ) <= 0.001f * Units.Metre && (at < 0 || MathF.Abs( hits[i].T - r0 ) < MathF.Abs( hits[at].T - r0 )) ) at = i;
		if ( at < 0 ) return false;   // the ray never registered s, most likely it ran along the surface

		bool leaving = hits[at].Leaving;
		bool outer = leaving && Exterior( hits, at );
		int order = 0, oldCount = 0;
		for ( int i = 0; i < hits.Count && hits[i].T <= reach; i++ )
		{
			if ( !Matches( hits, i, leaving, outer ) ) continue;
			if ( i < at ) order++;
			oldCount++;
		}

		other.Crossings( q, d, Units.Metre * 10f, hits );
		var same = new List<float>();
		for ( int i = 0; i < hits.Count && hits[i].T <= reach; i++ )
		{
			if ( !Matches( hits, i, leaving, outer ) ) continue;
			if ( otherSkin != null && otherSkin.OwnsNearby( hits[i].Tri, bone ) < 0.3f ) continue;   // some other limb
			same.Add( hits[i].T );
		}
		if ( same.Count == 0 ) return false;

		float r1 = same[0];
		if ( same.Count == oldCount && order < same.Count )
		{
			r1 = same[order];
		}
		else
		{
			foreach ( float t in same )
				if ( MathF.Abs( t - r0 ) < MathF.Abs( r1 - r0 ) ) r1 = t;
		}

		// A ray can slip through a gap in the other body (between two boxes at an elbow, say)
		// and land on some far away part. Skin doesn't end up several times further from its bone.
		if ( r1 > 3f * r0 + 0.05f * Units.Metre || r1 < 0.2f * r0 ) return false;

		target = q + d * r1;
		return true;
	}

	/// <summary>
	/// True if the ray is outside the body right after hit number `at`. On a body made of
	/// overlapping pieces most surfaces are buried inside another piece and don't count as skin.
	/// </summary>
	static bool Exterior( List<(float T, bool Leaving, int Tri)> hits, int at )
	{
		int depth = 0;
		for ( int i = at + 1; i < hits.Count; i++ )
			depth += hits[i].Leaving ? 1 : -1;
		return depth <= 0;
	}

	static bool Matches( List<(float T, bool Leaving, int Tri)> hits, int i, bool leaving, bool outer )
	{
		if ( hits[i].Leaving != leaving ) return false;
		return !outer || Exterior( hits, i );
	}

	/// <summary>Finds the 3D point on a body for a UV coordinate.</summary>
	sealed class UvLookup
	{
		const int Cells = 128;
		readonly TriMesh body;
		readonly Vec2[] uvs;
		readonly List<int>[] grid = new List<int>[Cells * Cells];
		readonly float minU, minV, scaleU, scaleV;

		public UvLookup( TriMesh body, Vec2[] uvs )
		{
			this.body = body;
			this.uvs = uvs;
			float maxU = float.MinValue, maxV = float.MinValue;
			minU = minV = float.MaxValue;
			foreach ( var uv in uvs )
			{
				minU = MathF.Min( minU, uv.X ); maxU = MathF.Max( maxU, uv.X );
				minV = MathF.Min( minV, uv.Y ); maxV = MathF.Max( maxV, uv.Y );
			}
			scaleU = Cells / MathF.Max( maxU - minU, 1e-6f );
			scaleV = Cells / MathF.Max( maxV - minV, 1e-6f );

			for ( int t = 0; t < body.TriCount; t++ )
			{
				Vec2 a = uvs[body.Tris[t * 3]], b = uvs[body.Tris[t * 3 + 1]], c = uvs[body.Tris[t * 3 + 2]];
				int u0 = Cell( MathF.Min( a.X, MathF.Min( b.X, c.X ) ), minU, scaleU ), u1 = Cell( MathF.Max( a.X, MathF.Max( b.X, c.X ) ), minU, scaleU );
				int v0 = Cell( MathF.Min( a.Y, MathF.Min( b.Y, c.Y ) ), minV, scaleV ), v1 = Cell( MathF.Max( a.Y, MathF.Max( b.Y, c.Y ) ), minV, scaleV );
				for ( int v = v0; v <= v1; v++ )
					for ( int u = u0; u <= u1; u++ )
						(grid[v * Cells + u] ??= new List<int>()).Add( t );
			}
		}

		static int Cell( float value, float min, float scale ) => Math.Clamp( (int)((value - min) * scale), 0, Cells - 1 );

		public bool Find( Vec2 uv, out Vec3 point, out Vec3 normal )
		{
			point = normal = default;
			var cell = grid[Cell( uv.Y, minV, scaleV ) * Cells + Cell( uv.X, minU, scaleU )];
			if ( cell == null ) return false;

			foreach ( int t in cell )
			{
				int ia = body.Tris[t * 3], ib = body.Tris[t * 3 + 1], ic = body.Tris[t * 3 + 2];
				Vec2 a = uvs[ia], b = uvs[ib], c = uvs[ic];
				float e0x = b.X - a.X, e0y = b.Y - a.Y, e1x = c.X - a.X, e1y = c.Y - a.Y, px = uv.X - a.X, py = uv.Y - a.Y;
				float den = e0x * e1y - e1x * e0y;
				if ( MathF.Abs( den ) < 1e-14f ) continue;
				float v = (px * e1y - e1x * py) / den;
				float w = (e0x * py - px * e0y) / den;
				if ( v < -1e-4f || w < -1e-4f || v + w > 1.0001f ) continue;

				point = body.Verts[ia] * (1f - v - w) + body.Verts[ib] * v + body.Verts[ic] * w;
				normal = body.TriNormals[t];
				return true;
			}

			return false;
		}
	}
}

/// <summary>
/// Shared mesh helpers: welding, edges, smoothing a per-vertex field.
/// </summary>
public static class MeshTools
{
	/// <summary>
	/// Vertices split along seams share a position. Returns, for each vertex, the index of the
	/// welded vertex it belongs to.
	/// </summary>
	public static int[] Weld( Vec3[] verts, out int count )
	{
		float scale = 1f / (1e-5f * Units.Metre);
		var seen = new Dictionary<(long, long, long), int>();
		var map = new int[verts.Length];
		for ( int i = 0; i < verts.Length; i++ )
		{
			var key = ((long)MathF.Round( verts[i].X * scale ), (long)MathF.Round( verts[i].Y * scale ), (long)MathF.Round( verts[i].Z * scale ));
			if ( !seen.TryGetValue( key, out int index ) )
			{
				index = seen.Count;
				seen[key] = index;
			}
			map[i] = index;
		}

		count = seen.Count;
		return map;
	}

	/// <summary>Unique edges between welded vertices.</summary>
	public static List<(int A, int B)> Edges( int[] tris, int[] weld )
	{
		var seen = new HashSet<long>();
		var edges = new List<(int, int)>();
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			for ( int k = 0; k < 3; k++ )
			{
				int a = weld[tris[t + k]], b = weld[tris[t + (k + 1) % 3]];
				if ( a == b ) continue;
				if ( a > b ) (a, b) = (b, a);
				if ( seen.Add( ((long)a << 32) | (uint)b ) ) edges.Add( (a, b) );
			}
		}

		return edges;
	}

	static void NeighbourMean( Vec3[] field, bool[] valid, List<(int A, int B)> edges, Vec3[] mean, int[] counts )
	{
		Array.Clear( mean, 0, mean.Length );
		Array.Clear( counts, 0, counts.Length );
		foreach ( var (a, b) in edges )
		{
			if ( valid[b] ) { mean[a] += field[b]; counts[a]++; }
			if ( valid[a] ) { mean[b] += field[a]; counts[b]++; }
		}

		for ( int i = 0; i < mean.Length; i++ )
			if ( counts[i] > 0 ) mean[i] = mean[i] / counts[i];
	}

	/// <summary>
	/// A ray that slips between two fingers or grazes an ear gives a wild value. Throws out
	/// anything that disagrees badly with its neighbours; smoothing fills it back in.
	/// </summary>
	public static bool[] DropOutliers( Vec3[] field, bool[] valid, List<(int A, int B)> edges )
	{
		var kept = MeshTools.Copy( valid );
		var mean = new Vec3[field.Length];
		var counts = new int[field.Length];
		for ( int pass = 0; pass < 2; pass++ )
		{
			NeighbourMean( field, kept, edges, mean, counts );
			for ( int i = 0; i < field.Length; i++ )
				if ( counts[i] >= 2 && (field[i] - mean[i]).Length() > 0.01f * Units.Metre + 0.75f * mean[i].Length() ) kept[i] = false;
		}

		return kept;
	}

	/// <summary>
	/// Laplacian smoothing of a per-vertex vector field, in place. Vertices without a value
	/// pick one up from their neighbours and are marked known.
	/// </summary>
	public static void Relax( Vec3[] field, bool[] known, List<(int A, int B)> edges, int iterations, float amount )
	{
		var mean = new Vec3[field.Length];
		var counts = new int[field.Length];
		for ( int pass = 0; pass < iterations; pass++ )
		{
			NeighbourMean( field, known, edges, mean, counts );
			for ( int i = 0; i < field.Length; i++ )
			{
				if ( counts[i] == 0 ) continue;
				field[i] = known[i] ? field[i] + (mean[i] - field[i]) * amount : mean[i];
			}

			for ( int i = 0; i < field.Length; i++ )
				if ( counts[i] > 0 ) known[i] = true;
		}
	}

	/// <summary>A copy of an array. (Array.Clone is not on the s&box whitelist.)</summary>
	public static T[] Copy<T>( T[] source )
	{
		var copy = new T[source.Length];
		Array.Copy( source, copy, source.Length );
		return copy;
	}

	public static Vec3 ClosestOnSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		Vec3 ab = b - a;
		float den = ab.LengthSquared();
		if ( den < 1e-12f ) return a;
		return a + ab * Math.Clamp( Vec3.Dot( p - a, ab ) / den, 0f, 1f );
	}

	/// <summary>The bone with the most weight over a triangle's three vertices, or -1.</summary>
	public static int DominantBone( SkinnedGeometry geo, int[] tris, int tri, Dictionary<int, float> scratch )
	{
		scratch.Clear();
		for ( int k = 0; k < 3; k++ )
		{
			int v = tris[tri * 3 + k];
			for ( int j = 0; j < 4; j++ )
			{
				int bone = geo.BoneIndex[v * 4 + j];
				float w = geo.BoneWeight[v * 4 + j];
				if ( bone < 0 || w <= 0 ) continue;
				scratch[bone] = scratch.TryGetValue( bone, out float sum ) ? sum + w : w;
			}
		}

		int best = -1;
		float most = 0;
		foreach ( var pair in scratch )
			if ( pair.Value > most ) { most = pair.Value; best = pair.Key; }
		return best;
	}
}
