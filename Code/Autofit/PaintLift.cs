using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Keeps paint on the skin (a tattoo, makeup) over it all the way across each of its triangles,
/// not only at its corners. Paint is cut in triangles two or three centimetres long, made to lie
/// flat over the citizen's big smooth head. Put on a thinner neck or a sharper face with each
/// corner just over the skin, a triangle cuts under the skin between them as a chord cuts under
/// an arc, and what is drawn there is gone. So its corners are lifted, each by as little as lets
/// the triangle clear the skin.
/// </summary>
public static class PaintLift
{
	const float Clear = 0.0003f * Units.Metre;     // paint stays this far over the skin at least
	const float OnSkin = 0.004f * Units.Metre;     // a corner this close to the skin is lying on it (a brow's hair stands further off)
	const float Hidden = 0.002f * Units.Metre;     // one this far under it is meant to be hidden (in an eye socket)
	const float MaxLift = 0.006f * Units.Metre;    // and no corner is lifted further than this: a triangle that would need more spans a fold or a nose, and floating that far it would show
	const float Margin = 0.03f * Units.Metre;      // the skin looked over reaches this far past the paint
	const int Grid = 3;                            // samples along each edge of a triangle
	const float Bulge = 2f;                        // the skin under a triangle is taken to bulge this many times as much as its corners' normals tell
	const float Flat = 0.0005f * Units.Metre;      // and by this much more, for what they don't
	const int Passes = 4;

	/// <summary>
	/// Lifts the corners of paint triangles that dip under the skin, along the skin's normal.
	/// </summary>
	/// <param name="positions">Paint positions, changed in place.</param>
	/// <param name="tris">Triangles, three indices each into <paramref name="positions"/>.</param>
	/// <param name="paint">Which positions are paint. Null for all.</param>
	/// <param name="skin">The body's skin in the same pose, normals out of the body (<see cref="TriMesh.Skin"/>).</param>
	/// <returns>How many corners were lifted.</returns>
	public static int Lift( Vec3[] positions, int[] tris, bool[] paint, TriMesh skin )
	{
		// Copies of a corner on either side of a seam in the texture go up together.
		var weld = MeshTools.Weld( positions, out int n );
		var at = new Vec3[n];
		var isPaint = new bool[n];
		for ( int v = 0; v < positions.Length; v++ )
		{
			at[weld[v]] = positions[v];
			isPaint[weld[v]] |= paint == null || paint[v];
		}
		var corners = new int[tris.Length];
		for ( int k = 0; k < tris.Length; k++ ) corners[k] = weld[tris[k]];
		int count = Lift( at, corners, isPaint, skin, n );
		for ( int v = 0; v < positions.Length; v++ ) positions[v] = at[weld[v]];
		return count;
	}

	static int Lift( Vec3[] positions, int[] tris, bool[] paint, TriMesh whole, int n )
	{
		var (skin, outward) = Near( whole, positions, paint );
		if ( skin == null ) return 0;
		float Gap( Vec3 p )
		{
			float d = skin.Nearest( p, out var q, out int t );
			if ( t < 0 ) return float.NaN;
			return Vec3.Dot( p - q, outward[t] ) >= 0 ? d : -d;
		}
		var up = new Vec3[n];
		var gap = new float[n];
		var lying = new bool[n];
		for ( int v = 0; v < n; v++ )
		{
			if ( !paint[v] ) continue;
			skin.Nearest( positions[v], out var q, out int t );
			if ( t < 0 ) continue;
			var normal = outward[t];
			float length = normal.Length();
			if ( length < 1e-12f ) continue;
			up[v] = normal / length;
			gap[v] = Vec3.Dot( positions[v] - q, up[v] );
			lying[v] = gap[v] > -Hidden && gap[v] < OnSkin;
		}

		var check = new List<int>();
		var around = new List<int>[n];   // the triangles lying on the skin round each corner
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			if ( !lying[a] || !lying[b] || !lying[c] ) continue;
			for ( int k = 0; k < 3; k++ ) (around[tris[t + k]] ??= new List<int>()).Add( t );
			// Skin that turns by an angle between two corners a length apart bulges by about an
			// eighth of their product between them: no more than the corners stand off it, and
			// the triangle clears it.
			float turn = MathF.Acos( Math.Clamp( MathF.Min( Vec3.Dot( up[a], up[b] ), MathF.Min( Vec3.Dot( up[b], up[c] ), Vec3.Dot( up[c], up[a] ) ) ), -1f, 1f ) );
			float longest = MathF.Max( (positions[b] - positions[a]).Length(), MathF.Max( (positions[c] - positions[b]).Length(), (positions[a] - positions[c]).Length() ) );
			if ( MathF.Min( gap[a], MathF.Min( gap[b], gap[c] ) ) - Clear > Bulge * longest * turn / 8 + Flat ) continue;
			check.Add( t );
		}

		// How far a triangle's samples are short of clearing the skin, all added up, and what its
		// corners want lifted for it.
		var shortOf = new Dictionary<int, float>();
		var want = new float[n];
		float Short( int t, bool wanting )
		{
			float sum = 0;
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			for ( int i = 0; i <= Grid; i++ )
				for ( int j = 0; j <= Grid - i; j++ )
				{
					float wb = (float)i / Grid, wc = (float)j / Grid, wa = 1 - wb - wc;
					float g = Gap( positions[a] * wa + positions[b] * wb + positions[c] * wc );
					if ( float.IsNaN( g ) || g >= Clear ) continue;
					float need = Clear - g;
					sum += need;
					if ( !wanting ) continue;
					// The least lift of the three corners that raises this point by what it needs.
					float squares = wa * wa + wb * wb + wc * wc;
					want[a] = MathF.Max( want[a], need * wa / squares );
					want[b] = MathF.Max( want[b], need * wb / squares );
					want[c] = MathF.Max( want[c], need * wc / squares );
				}
			shortOf[t] = sum;
			return sum;
		}

		var lifted = new float[n];
		var stuck = new bool[n];
		var moved = new HashSet<int>();
		for ( int pass = 0; pass < Passes && check.Count > 0; pass++ )
		{
			Array.Clear( want );
			var again = new List<int>();
			foreach ( int t in check )
				if ( Short( t, true ) > 0 ) again.Add( t );
			foreach ( int t in again )
				for ( int k = 0; k < 3; k++ )
				{
					int v = tris[t + k];
					float add = MathF.Min( want[v], MaxLift - lifted[v] );
					want[v] = 0;
					if ( add <= 0 || stuck[v] ) continue;
					// Kept only if the paint round the corner comes out from under the skin by it:
					// where it lies in a fold (the lips meeting), lifting it off one side pushes the
					// triangles next to it into the other.
					float before = 0, after = 0;
					foreach ( int u in around[v] ) before += shortOf.TryGetValue( u, out float was ) ? was : Short( u, false );
					positions[v] += up[v] * add;
					foreach ( int u in around[v] ) after += Short( u, false );
					if ( after >= before )
					{
						positions[v] -= up[v] * add;
						foreach ( int u in around[v] ) Short( u, false );
						stuck[v] = true;
						continue;
					}
					lifted[v] += add;
					moved.Add( v );
				}
			check = again;
		}
		return moved.Count;
	}

	// The skin round the paint, a few centimetres past it each way: what is looked over, as
	// a smaller mesh is quicker to look in. With its triangles' normals out of the body, taken
	// from the whole skin: a piece of it can't tell inside from out by itself.
	static (TriMesh Mesh, Vec3[] Outward) Near( TriMesh skin, Vec3[] positions, bool[] paint )
	{
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		bool any = false;
		for ( int v = 0; v < positions.Length; v++ )
		{
			if ( !paint[v] ) continue;
			var p = positions[v];
			lo = new Vec3( MathF.Min( lo.X, p.X ), MathF.Min( lo.Y, p.Y ), MathF.Min( lo.Z, p.Z ) );
			hi = new Vec3( MathF.Max( hi.X, p.X ), MathF.Max( hi.Y, p.Y ), MathF.Max( hi.Z, p.Z ) );
			any = true;
		}
		if ( !any ) return (null, null);
		lo -= new Vec3( Margin, Margin, Margin );
		hi += new Vec3( Margin, Margin, Margin );
		bool Inside( Vec3 p ) => p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z;
		var kept = new List<int>();
		var outward = new List<Vec3>();
		for ( int t = 0; t < skin.TriCount; t++ )
		{
			int a = skin.Tris[t * 3], b = skin.Tris[t * 3 + 1], c = skin.Tris[t * 3 + 2];
			if ( !Inside( skin.Verts[a] ) && !Inside( skin.Verts[b] ) && !Inside( skin.Verts[c] ) ) continue;
			kept.Add( a ); kept.Add( b ); kept.Add( c );
			outward.Add( skin.TriNormals[t] );
		}
		return kept.Count == 0 ? (null, null) : (new TriMesh( skin.Verts, kept.ToArray() ), outward.ToArray());
	}
}
