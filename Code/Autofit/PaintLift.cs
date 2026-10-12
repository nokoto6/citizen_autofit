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
	const int Grid = 4;                            // samples along each edge of a triangle
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

	static int Lift( Vec3[] positions, int[] tris, bool[] paint, TriMesh skin, int n )
	{
		var up = new Vec3[n];
		var lying = new bool[n];
		for ( int v = 0; v < n; v++ )
		{
			if ( !paint[v] ) continue;
			skin.Nearest( positions[v], out var q, out int t );
			if ( t < 0 ) continue;
			var normal = skin.TriNormals[t];
			float length = normal.Length();
			if ( length < 1e-12f ) continue;
			up[v] = normal / length;
			float gap = Vec3.Dot( positions[v] - q, up[v] );
			lying[v] = gap > -Hidden && gap < OnSkin;
		}

		var check = new List<int>();
		var around = new List<int>[n];   // the triangles lying on the skin round each corner
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			if ( !lying[tris[t]] || !lying[tris[t + 1]] || !lying[tris[t + 2]] ) continue;
			check.Add( t );
			for ( int k = 0; k < 3; k++ ) (around[tris[t + k]] ??= new List<int>()).Add( t );
		}

		// How far a triangle's samples are short of clearing the skin, all added up.
		float Short( int t )
		{
			float sum = 0;
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			for ( int i = 0; i <= Grid; i++ )
				for ( int j = 0; j <= Grid - i; j++ )
				{
					float wb = (float)i / Grid, wc = (float)j / Grid, wa = 1 - wb - wc;
					float g = Gap( skin, positions[a] * wa + positions[b] * wb + positions[c] * wc );
					if ( !float.IsNaN( g ) && g < Clear ) sum += Clear - g;
				}
			return sum;
		}

		var lifted = new float[n];
		var stuck = new bool[n];
		var want = new float[n];
		var moved = new HashSet<int>();
		for ( int pass = 0; pass < Passes && check.Count > 0; pass++ )
		{
			Array.Clear( want );
			var again = new List<int>();
			foreach ( int t in check )
			{
				int a = tris[t], b = tris[t + 1], c = tris[t + 2];
				bool dips = false;
				for ( int i = 0; i <= Grid; i++ )
					for ( int j = 0; j <= Grid - i; j++ )
					{
						float wb = (float)i / Grid, wc = (float)j / Grid, wa = 1 - wb - wc;
						float g = Gap( skin, positions[a] * wa + positions[b] * wb + positions[c] * wc );
						if ( float.IsNaN( g ) ) continue;
						float need = Clear - g;
						if ( need <= 1e-6f * Units.Metre ) continue;
						// The least lift of the three corners that raises this point by what it needs.
						float sum = wa * wa + wb * wb + wc * wc;
						want[a] = MathF.Max( want[a], need * wa / sum );
						want[b] = MathF.Max( want[b], need * wb / sum );
						want[c] = MathF.Max( want[c], need * wc / sum );
						dips = true;
					}
				if ( dips ) again.Add( t );
			}
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
					foreach ( int u in around[v] ) before += Short( u );
					var was = positions[v];
					positions[v] = was + up[v] * add;
					foreach ( int u in around[v] ) after += Short( u );
					if ( after >= before ) { positions[v] = was; stuck[v] = true; continue; }
					lifted[v] += add;
					moved.Add( v );
				}
			check = again;
		}
		return moved.Count;
	}

	// How far a point is over the skin, under it if negative.
	static float Gap( TriMesh skin, Vec3 p )
	{
		float d = skin.Nearest( p, out var q, out int t );
		if ( t < 0 ) return float.NaN;
		return Vec3.Dot( p - q, skin.TriNormals[t] ) >= 0 ? d : -d;
	}
}
