using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// A triangle mesh with a bounding volume hierarchy: closest point, ray crossings and an
/// inside test. The mesh does not have to be one closed surface. Several closed shells that
/// overlap (a body built from separate pieces) work too, because inside/outside is decided by
/// counting how many times a ray leaves a surface versus how many times it enters one.
/// </summary>
public sealed class TriMesh
{
	public readonly Vec3[] Verts;
	public readonly int[] Tris;          // three vertex indices per triangle
	public readonly Vec3[] TriNormals;   // pointing out of the body, length = twice the area
	public int TriCount => Tris.Length / 3;

	// BVH, flattened. A node is a leaf when Count > 0.
	readonly List<Vec3> boxMin = new(), boxMax = new();
	readonly List<int> left = new(), right = new(), first = new(), count = new();
	readonly int[] order;

	const int LeafSize = 4;

	public TriMesh( Vec3[] verts, int[] tris )
	{
		Verts = verts;
		Tris = tris;
		int n = tris.Length / 3;
		TriNormals = new Vec3[n];
		order = new int[n];
		var centre = new Vec3[n];

		// Signed volume tells which way the triangles are wound.
		double volume = 0;
		for ( int i = 0; i < n; i++ )
		{
			Vec3 a = verts[tris[i * 3]], b = verts[tris[i * 3 + 1]], c = verts[tris[i * 3 + 2]];
			TriNormals[i] = Vec3.Cross( b - a, c - a );
			volume += Vec3.Dot( a, Vec3.Cross( b, c ) );
			centre[i] = (a + b + c) / 3f;
			order[i] = i;
		}

		if ( volume < 0 )
		{
			for ( int i = 0; i < n; i++ )
				TriNormals[i] = -TriNormals[i];
		}

		if ( n > 0 )
			Build( 0, n, centre );
	}

	int Build( int start, int end, Vec3[] centre )
	{
		int node = boxMin.Count;
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		for ( int i = start; i < end; i++ )
		{
			for ( int k = 0; k < 3; k++ )
			{
				var v = Verts[Tris[order[i] * 3 + k]];
				lo = new Vec3( MathF.Min( lo.X, v.X ), MathF.Min( lo.Y, v.Y ), MathF.Min( lo.Z, v.Z ) );
				hi = new Vec3( MathF.Max( hi.X, v.X ), MathF.Max( hi.Y, v.Y ), MathF.Max( hi.Z, v.Z ) );
			}
		}

		boxMin.Add( lo ); boxMax.Add( hi );
		left.Add( -1 ); right.Add( -1 ); first.Add( start ); count.Add( end - start );
		if ( end - start <= LeafSize )
			return node;

		// Split in the middle of the longest side.
		var size = hi - lo;
		int axis = size.X >= size.Y && size.X >= size.Z ? 0 : (size.Y >= size.Z ? 1 : 2);
		Array.Sort( order, start, end - start, Comparer<int>.Create( ( p, q ) => Axis( centre[p], axis ).CompareTo( Axis( centre[q], axis ) ) ) );
		int mid = (start + end) / 2;

		count[node] = 0;
		int l = Build( start, mid, centre );
		int r = Build( mid, end, centre );
		left[node] = l;
		right[node] = r;
		return node;
	}

	static float Axis( Vec3 v, int axis ) => axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);

	/// <summary>Closest point on the surface, the triangle it is on and the distance.</summary>
	public float Nearest( Vec3 p, out Vec3 point, out int tri )
	{
		float best = float.MaxValue;
		point = p;
		tri = -1;
		if ( TriCount == 0 ) return best;

		var stack = new Stack<int>();
		stack.Push( 0 );
		while ( stack.Count > 0 )
		{
			int node = stack.Pop();
			if ( BoxDistanceSquared( p, node ) >= best ) continue;

			if ( count[node] > 0 )
			{
				for ( int i = first[node]; i < first[node] + count[node]; i++ )
				{
					int t = order[i];
					var c = ClosestOnTriangle( p, Verts[Tris[t * 3]], Verts[Tris[t * 3 + 1]], Verts[Tris[t * 3 + 2]] );
					float d = (c - p).LengthSquared();
					if ( d < best ) { best = d; point = c; tri = t; }
				}
				continue;
			}

			// Visit the nearer child first so the other one is more likely to be skipped.
			float dl = BoxDistanceSquared( p, left[node] ), dr = BoxDistanceSquared( p, right[node] );
			if ( dl < dr ) { stack.Push( right[node] ); stack.Push( left[node] ); }
			else { stack.Push( left[node] ); stack.Push( right[node] ); }
		}

		return MathF.Sqrt( best );
	}

	float BoxDistanceSquared( Vec3 p, int node )
	{
		Vec3 lo = boxMin[node], hi = boxMax[node];
		float dx = MathF.Max( MathF.Max( lo.X - p.X, 0 ), p.X - hi.X );
		float dy = MathF.Max( MathF.Max( lo.Y - p.Y, 0 ), p.Y - hi.Y );
		float dz = MathF.Max( MathF.Max( lo.Z - p.Z, 0 ), p.Z - hi.Z );
		return dx * dx + dy * dy + dz * dz;
	}

	static Vec3 ClosestOnTriangle( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		Vec3 ab = b - a, ac = c - a, ap = p - a;
		float d1 = Vec3.Dot( ab, ap ), d2 = Vec3.Dot( ac, ap );
		if ( d1 <= 0 && d2 <= 0 ) return a;

		Vec3 bp = p - b;
		float d3 = Vec3.Dot( ab, bp ), d4 = Vec3.Dot( ac, bp );
		if ( d3 >= 0 && d4 <= d3 ) return b;

		float vc = d1 * d4 - d3 * d2;
		if ( vc <= 0 && d1 >= 0 && d3 <= 0 ) return a + ab * (d1 / (d1 - d3));

		Vec3 cp = p - c;
		float d5 = Vec3.Dot( ab, cp ), d6 = Vec3.Dot( ac, cp );
		if ( d6 >= 0 && d5 <= d6 ) return c;

		float vb = d5 * d2 - d1 * d6;
		if ( vb <= 0 && d2 >= 0 && d6 <= 0 ) return a + ac * (d2 / (d2 - d6));

		float va = d3 * d6 - d5 * d4;
		if ( va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0 ) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

		float denom = 1f / (va + vb + vc);
		return a + ab * (vb * denom) + ac * (vc * denom);
	}

	/// <summary>
	/// Every surface the ray goes through, nearest first, with the triangle that was hit.
	/// Leaving is true when the ray passes from inside to outside at that hit.
	/// </summary>
	public void Crossings( Vec3 origin, Vec3 direction, float maxDistance, List<(float T, bool Leaving, int Tri)> hits )
	{
		hits.Clear();
		if ( TriCount == 0 ) return;

		var stack = new Stack<int>();
		stack.Push( 0 );
		while ( stack.Count > 0 )
		{
			int node = stack.Pop();
			if ( !RayHitsBox( origin, direction, maxDistance, node ) ) continue;

			if ( count[node] > 0 )
			{
				for ( int i = first[node]; i < first[node] + count[node]; i++ )
				{
					int t = order[i];
					if ( RayTriangle( origin, direction, Verts[Tris[t * 3]], Verts[Tris[t * 3 + 1]], Verts[Tris[t * 3 + 2]], out float dist ) && dist <= maxDistance )
						hits.Add( (dist, Vec3.Dot( TriNormals[t], direction ) > 0, t) );
				}
				continue;
			}

			stack.Push( left[node] );
			stack.Push( right[node] );
		}

		hits.Sort( ( a, b ) => a.T.CompareTo( b.T ) );

		// A ray through a shared edge hits both triangles at the same spot. Count it once.
		for ( int i = hits.Count - 1; i > 0; i-- )
		{
			if ( hits[i].T - hits[i - 1].T < Units.Metre * 1e-5f && hits[i].Leaving == hits[i - 1].Leaving )
				hits.RemoveAt( i );
		}
	}

	bool RayHitsBox( Vec3 o, Vec3 d, float maxDistance, int node )
	{
		Vec3 lo = boxMin[node], hi = boxMax[node];
		float t0 = 0, t1 = maxDistance;
		for ( int axis = 0; axis < 3; axis++ )
		{
			float origin = Axis( o, axis ), dir = Axis( d, axis ), min = Axis( lo, axis ), max = Axis( hi, axis );
			if ( MathF.Abs( dir ) < 1e-12f )
			{
				if ( origin < min || origin > max ) return false;
				continue;
			}

			float a = (min - origin) / dir, b = (max - origin) / dir;
			if ( a > b ) (a, b) = (b, a);
			t0 = MathF.Max( t0, a );
			t1 = MathF.Min( t1, b );
			if ( t0 > t1 ) return false;
		}

		return true;
	}

	static bool RayTriangle( Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c, out float t )
	{
		t = 0;
		Vec3 e1 = b - a, e2 = c - a;
		Vec3 p = Vec3.Cross( d, e2 );
		float det = Vec3.Dot( e1, p );
		if ( MathF.Abs( det ) < 1e-12f ) return false;

		float inv = 1f / det;
		Vec3 s = o - a;
		float u = Vec3.Dot( s, p ) * inv;
		if ( u < 0 || u > 1 ) return false;

		Vec3 q = Vec3.Cross( s, e1 );
		float v = Vec3.Dot( d, q ) * inv;
		if ( v < 0 || u + v > 1 ) return false;

		t = Vec3.Dot( e2, q ) * inv;
		return t > Units.Metre * 1e-5f;
	}

	static readonly Vec3 ProbeA = Normalized( new Vec3( 0.3f, 0.5f, 0.81f ) );
	static readonly Vec3 ProbeB = Normalized( new Vec3( -0.62f, 0.2f, -0.76f ) );

	static Vec3 Normalized( Vec3 v ) => v / v.Length();

	[ThreadStatic] static List<(float T, bool Leaving, int Tri)> scratch;

	/// <summary>
	/// True when the point is inside the body. Looks along two directions and wants both to
	/// agree, in case one of them grazes an edge.
	/// </summary>
	public bool Inside( Vec3 p )
	{
		scratch ??= new List<(float, bool, int)>();
		return Depth( p, ProbeA ) > 0 && Depth( p, ProbeB ) > 0;
	}

	int Depth( Vec3 p, Vec3 direction )
	{
		Crossings( p, direction, Units.Metre * 10f, scratch );
		int depth = 0;
		foreach ( var hit in scratch )
			depth += hit.Leaving ? 1 : -1;
		return depth;
	}

	/// <summary>Distance to the surface, negative when the point is under it. Also the closest point.</summary>
	public float SignedGap( Vec3 p, out Vec3 nearest )
	{
		float distance = (exposed ?? this).Nearest( p, out nearest, out _ );
		return Inside( p ) ? -distance : distance;
	}

	// The part of the surface that can be seen from outside. Null until FindExposed is called.
	TriMesh exposed;

	const float ExposedPatch = 0.025f * Units.Metre;   // exposed or buried is decided for patches about this big
	const float ProbeOut = 0.001f * Units.Metre;

	/// <summary>
	/// On a body built from pieces that overlap (limbs stuck into a torso, eyeballs inside a
	/// head) a lot of the surface is buried inside another piece. The closest surface to a
	/// point is then often one of those buried faces, which is no use for keeping clothing
	/// outside the body. After this call <see cref="SignedGap"/> measures to the exposed
	/// surface only. Rays and the inside test still see everything.
	/// </summary>
	public void FindExposed()
	{
		if ( exposed != null ) return;

		var verts = new List<Vec3>();
		var todo = new Stack<(Vec3 A, Vec3 B, Vec3 C)>();
		for ( int t = 0; t < TriCount; t++ )
		{
			float size = TriNormals[t].Length();
			if ( size < 1e-12f ) continue;
			Vec3 lift = TriNormals[t] / size * ProbeOut;

			todo.Push( (Verts[Tris[t * 3]], Verts[Tris[t * 3 + 1]], Verts[Tris[t * 3 + 2]]) );
			while ( todo.Count > 0 )
			{
				var (a, b, c) = todo.Pop();

				// Big triangles are cut up first: a box face can be half inside another box.
				float ab = (b - a).LengthSquared(), bc = (c - b).LengthSquared(), ca = (a - c).LengthSquared();
				if ( MathF.Max( ab, MathF.Max( bc, ca ) ) > ExposedPatch * ExposedPatch )
				{
					if ( ab >= bc && ab >= ca ) { Vec3 m = (a + b) * 0.5f; todo.Push( (a, m, c) ); todo.Push( (m, b, c) ); }
					else if ( bc >= ca ) { Vec3 m = (b + c) * 0.5f; todo.Push( (a, b, m) ); todo.Push( (a, m, c) ); }
					else { Vec3 m = (c + a) * 0.5f; todo.Push( (a, b, m) ); todo.Push( (m, b, c) ); }
					continue;
				}

				// Keep the patch if any part of it has open air just above it.
				Vec3 centre = (a + b + c) / 3f;
				if ( Inside( centre + lift ) && Inside( centre + (a - centre) * 0.6f + lift ) && Inside( centre + (b - centre) * 0.6f + lift ) && Inside( centre + (c - centre) * 0.6f + lift ) )
					continue;

				verts.Add( a ); verts.Add( b ); verts.Add( c );
			}
		}

		var tris = new int[verts.Count];
		for ( int i = 0; i < tris.Length; i++ ) tris[i] = i;
		exposed = verts.Count > 0 ? new TriMesh( verts.ToArray(), tris ) : this;
	}
}

/// <summary>
/// Engine models are in inches. The fitting constants are easier to reason about in metres.
/// </summary>
public static class Units
{
	public const float Metre = 39.37f;
}
