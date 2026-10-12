using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// The neck and the lower face of a body in the stock pose as a fan of profiles round the neck:
/// for each angle round it, the line the skin makes in the upright half-plane through the
/// neck's axis at that angle, from the neck's base up to under the lower lip in front and to
/// under the ear at the sides. Along each profile the skin is measured by its length from the
/// base, and the top of the throat (the chin's height) is marked on it.
///
/// What paint goes by below the mouth (<see cref="BodyMap.OnSkin"/>). The citizen's neck is as
/// thick as its head and goes straight up into its chin; a human's is half as thick and turns
/// forward under the jaw into the chin, an anime one's is thinner and longer still. Going by
/// the share of the way from the base to the top of the throat, and from there to the lip,
/// along the skin, a tattoo on the citizen's throat lies on the throat of the other body, what
/// was under its mouth goes under the jaw and onto the chin, and none of it reaches the mouth.
/// </summary>
public sealed class NeckChart
{
	const float Step = 4f;            // degrees between profiles
	const float MaxAngle = 140f;      // profiles go this far round from the front to either side
	const float Below = 0.025f * Units.Metre;   // profiles start this far below the base
	const float Reach = 0.5f * Units.Metre;     // and go no further out from the axis than this
	const float JumpShare = 0.3f;     // a chin stands out from the throat by this share of how far it is from the axis at least
	const float ChinBelowMouth = 1.2f;   // a face with no chin to find has it this many times the nose's height over the mouth under the mouth
	const float TopShare = 0.3f;      // profiles stop this share of the way from the lower lip (or the bottom of the ear) to the chin under it
	const float SideAngle = 75f;      // from this far round the top is the ear's, not the lip's

	readonly float[] angles;
	readonly List<(float R, float Z, float S)>[] profiles;
	readonly float[] chinS, topS;
	readonly Vec3 axisLow, axisHigh;

	/// <summary>The top of the throat: just under the bottom of the chin, found on the profile in front, or else as far under the mouth as a human's chin.</summary>
	public readonly float ChinZ;
	public readonly bool ChinFound;

	NeckChart( Vec3 axisLow, Vec3 axisHigh, float chinZ, bool chinFound, int count )
	{
		this.axisLow = axisLow; this.axisHigh = axisHigh;
		ChinZ = chinZ; ChinFound = chinFound;
		angles = new float[count];
		profiles = new List<(float, float, float)>[count];
		chinS = new float[count];
		topS = new float[count];
	}

	// The neck's axis at a height: along the line from the neck's joint to the head's.
	Vec3 AxisAt( float z )
	{
		float t = (z - axisLow.Z) / MathF.Max( axisHigh.Z - axisLow.Z, 1e-6f );
		return axisLow + (axisHigh - axisLow) * t;
	}

	static Vec3 Out( float degrees )
	{
		float a = degrees * MathF.PI / 180f;
		return new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 );
	}

	/// <param name="skin">The body's skin in the stock pose (<see cref="TriMesh.Skin"/>).</param>
	/// <param name="axisLow">The neck's joint, stock pose.</param>
	/// <param name="axisHigh">The head's joint.</param>
	/// <param name="baseZ">The neck's base: the clavicles' joints' height.</param>
	/// <param name="mouthZ">The mouth's middle (<see cref="FaceMarks"/>).</param>
	/// <param name="noseZ">The tip of the nose.</param>
	/// <param name="earLeft">The bottom of the ear on the +Y side, NaN if not found.</param>
	/// <param name="earRight">The bottom of the other ear.</param>
	/// <returns>Null when the mouth is under the base or the nose under the mouth.</returns>
	public static NeckChart Build( TriMesh skin, Vec3 axisLow, Vec3 axisHigh, float baseZ, float mouthZ, float noseZ, float earLeft, float earRight )
	{
		if ( mouthZ <= baseZ || noseZ <= mouthZ || axisHigh.Z <= axisLow.Z ) return null;
		int count = (int)(2 * MaxAngle / Step) + 1;
		var weld = MeshTools.Weld( skin.Verts, out int welded );
		float low = baseZ - Below;

		// The chin, on the profile in front: the lowest of it that stands well out from the
		// throat, the bottom of the chin. A face going straight down into its neck (the
		// citizen's) has none, and gets one as far under its mouth as a human's.
		float chinZ = mouthZ - ChinBelowMouth * (noseZ - mouthZ);
		bool found = false;
		var front = Slice( skin, weld, welded, axisLow, axisHigh, 0, low, mouthZ - TopShare * (mouthZ - chinZ) );
		if ( front != null )
		{
			float throat = float.MaxValue, most = 0;
			foreach ( var (r, z) in front ) if ( z >= baseZ ) { throat = MathF.Min( throat, r ); most = MathF.Max( most, r ); }
			if ( most - throat > JumpShare * most )
			{
				float half = 0.5f * (throat + most), lowest = float.MaxValue;
				foreach ( var (r, z) in front ) if ( z >= baseZ && r >= half ) lowest = MathF.Min( lowest, z );
				if ( lowest < mouthZ ) { chinZ = lowest - ChinDown; found = true; }
			}
		}
		// The top: under the lower lip in front, and round the sides under the ear. There the face's
		// height still tells where the skin goes (on an anime head both sit 11 cm lower than on the
		// citizen), where rays from the bones would leave it by the neck.
		float lip = mouthZ - TopShare * (mouthZ - chinZ);
		float Top( float angle )
		{
			float ear = angle >= 0 ? earLeft : earRight;
			if ( float.IsNaN( ear ) ) ear = float.IsNaN( angle >= 0 ? earRight : earLeft ) ? noseZ : angle >= 0 ? earRight : earLeft;
			ear -= TopShare * (ear - chinZ);
			if ( ear <= chinZ ) return lip;
			float w = Smooth( Math.Clamp( MathF.Abs( angle ) / SideAngle, 0f, 1f ) );
			return lip + (ear - lip) * w;
		}

		var chart = new NeckChart( axisLow, axisHigh, chinZ, found, count );
		for ( int k = 0; k < count; k++ )
		{
			float angle = -MaxAngle + k * Step;
			chart.angles[k] = angle;
			var raw = Slice( skin, weld, welded, axisLow, axisHigh, angle, low, Top( angle ) );
			if ( raw == null ) continue;
			var line = new List<(float R, float Z, float S)>();
			foreach ( var (r, z) in raw ) Add( line, r, z );

			// Lengths are from the base, where the profile first goes up past it; the chin is where it
			// first goes up past the chin's height, at the top of the throat, so the skin under the
			// jaw goes with the chin and the throat is ringed the same all round. The top is at its end.
			float baseS = Crossing( line, baseZ ), chin = Crossing( line, chinZ ), top = line[^1].S;
			if ( float.IsNaN( baseS ) || float.IsNaN( chin ) || chin <= baseS || top <= chin ) continue;
			for ( int i = 0; i < line.Count; i++ ) line[i] = (line[i].R, line[i].Z, line[i].S - baseS);
			chart.profiles[k] = line;
			chart.chinS[k] = chin - baseS;
			chart.topS[k] = top - baseS;
		}
		return chart;
	}

	const float ChinDown = 0.003f * Units.Metre;   // the throat ends this far under the bottom of the chin, where it turns forward under the jaw
	const float Join = 0.01f * Units.Metre;      // a profile that comes to an end (a seam between two pieces of the body) goes on from skin this close

	/// <summary>
	/// The skin in the upright half-plane through the axis at an angle round it, as (distance from
	/// the axis, height): the cut through the skin's triangles, followed from where it crosses the
	/// lowest height nearest the axis up to where it first reaches the top. Null if it doesn't get
	/// there. Under a jaw the profile turns forward and back down, which rays straight out from
	/// the axis can't follow.
	/// </summary>
	static List<(float R, float Z)> Slice( TriMesh skin, int[] weld, int welded, Vec3 axisLow, Vec3 axisHigh, float angle, float low, float top )
	{
		var dir = Out( angle );
		var up = axisHigh - axisLow;
		var normal = Vec3.Cross( up, dir );
		normal /= MathF.Max( normal.Length(), 1e-12f );
		var side = new float[welded];
		var seen = new bool[welded];
		for ( int v = 0; v < skin.Verts.Length; v++ )
		{
			int w = weld[v];
			if ( seen[w] ) continue;
			seen[w] = true;
			float d = Vec3.Dot( skin.Verts[v] - axisLow, normal );
			side[w] = d == 0 ? 1e-9f : d;
		}
		(float R, float Z) Place( Vec3 p )
		{
			float t = (p.Z - axisLow.Z) / up.Z;
			return (Vec3.Dot( p - (axisLow + up * t), dir ), p.Z);
		}

		// The cut: a point on each edge the plane goes through, joined across each triangle.
		var points = new Dictionary<long, (float R, float Z)>();
		var links = new Dictionary<long, List<long>>();
		var ends = new long[2];
		var at = new (float R, float Z)[2];
		for ( int t = 0; t < skin.TriCount; t++ )
		{
			int found = 0;
			for ( int e = 0; e < 3 && found < 2; e++ )
			{
				int a = skin.Tris[t * 3 + e], b = skin.Tris[t * 3 + (e + 1) % 3];
				int wa = weld[a], wb = weld[b];
				if ( wa == wb || (side[wa] > 0) == (side[wb] > 0) ) continue;
				float f = side[wa] / (side[wa] - side[wb]);
				var p = skin.Verts[a] + (skin.Verts[b] - skin.Verts[a]) * f;
				ends[found] = wa < wb ? ((long)wa << 32) | (uint)wb : ((long)wb << 32) | (uint)wa;
				at[found] = Place( p );
				found++;
			}
			if ( found < 2 || at[0].R <= 0 || at[1].R <= 0 || at[0].R > Reach || at[1].R > Reach ) continue;
			if ( MathF.Max( at[0].Z, at[1].Z ) < low - Below || MathF.Min( at[0].Z, at[1].Z ) > top + Below ) continue;
			for ( int j = 0; j < 2; j++ )
			{
				points[ends[j]] = at[j];
				if ( !links.TryGetValue( ends[j], out var list ) ) links[ends[j]] = list = new List<long>( 2 );
				list.Add( ends[1 - j] );
			}
		}

		// Start where the cut crosses the lowest height nearest the axis, going up.
		long from = -1, next = -1;
		float nearest = float.MaxValue;
		foreach ( var (key, list) in links )
		{
			var a = points[key];
			foreach ( long other in list )
			{
				var b = points[other];
				if ( (a.Z < low) == (b.Z < low) ) continue;
				float r = a.R + (b.R - a.R) * (low - a.Z) / (b.Z - a.Z);
				if ( r < nearest ) { nearest = r; (from, next) = a.Z < b.Z ? (key, other) : (other, key); }
			}
		}
		if ( from < 0 ) return null;
		var line = new List<(float R, float Z)>();
		line.Add( (nearest, low) );
		var visited = new HashSet<long> { from };
		long here = next;
		for ( int guard = 0; guard < 100000; guard++ )
		{
			visited.Add( here );
			var p = points[here];
			var last = line[^1];
			if ( p.Z >= top )
			{
				float f = (top - last.Z) / MathF.Max( p.Z - last.Z, 1e-9f );
				line.Add( (last.R + (p.R - last.R) * f, top) );
				return line;
			}
			line.Add( p );
			long go = -1;
			foreach ( long other in links[here] ) if ( !visited.Contains( other ) ) { go = other; break; }
			if ( go < 0 )
			{
				// An end: a seam or a hole. On from the nearest skin of the cut not yet gone over,
				// no lower than here.
				float best = Join * Join;
				foreach ( var (key, q) in points )
				{
					if ( visited.Contains( key ) || q.Z < p.Z - Join ) continue;
					float d2 = (q.R - p.R) * (q.R - p.R) + (q.Z - p.Z) * (q.Z - p.Z);
					if ( d2 < best ) { best = d2; go = key; }
				}
				if ( go < 0 ) return null;
			}
			here = go;
		}
		return null;
	}

	static void Add( List<(float R, float Z, float S)> line, float r, float z )
	{
		float s = 0;
		if ( line.Count > 0 )
		{
			var (pr, pz, ps) = line[^1];
			s = ps + MathF.Sqrt( (r - pr) * (r - pr) + (z - pz) * (z - pz) );
		}
		line.Add( (r, z, s) );
	}

	// The length along a profile where it first goes up past a height.
	static float Crossing( List<(float R, float Z, float S)> line, float z )
	{
		for ( int i = 1; i < line.Count; i++ )
		{
			var a = line[i - 1]; var b = line[i];
			if ( (a.Z <= z) == (b.Z <= z) ) continue;
			float t = (z - a.Z) / (b.Z - a.Z);
			return a.S + (b.S - a.S) * t;
		}
		return float.NaN;
	}

	// The length along a profile of its point nearest a point of its half-plane.
	static float Nearest( List<(float R, float Z, float S)> line, float r, float z )
	{
		float best = float.MaxValue, at = 0;
		for ( int i = 1; i < line.Count; i++ )
		{
			var a = line[i - 1]; var b = line[i];
			float dr = b.R - a.R, dz = b.Z - a.Z, l2 = dr * dr + dz * dz;
			float t = l2 > 1e-12f ? Math.Clamp( ((r - a.R) * dr + (z - a.Z) * dz) / l2, 0f, 1f ) : 0f;
			float er = a.R + dr * t - r, ez = a.Z + dz * t - z, d2 = er * er + ez * ez;
			if ( d2 < best ) { best = d2; at = a.S + (b.S - a.S) * t; }
		}
		return at;
	}

	static (float R, float Z) AtLength( List<(float R, float Z, float S)> line, float s )
	{
		if ( s <= line[0].S ) return (line[0].R, line[0].Z);
		for ( int i = 1; i < line.Count; i++ )
		{
			if ( line[i].S < s ) continue;
			var a = line[i - 1]; var b = line[i];
			float t = b.S > a.S ? (s - a.S) / (b.S - a.S) : 0f;
			return (a.R + (b.R - a.R) * t, a.Z + (b.Z - a.Z) * t);
		}
		return (line[^1].R, line[^1].Z);
	}

	const float OffProfile = 0.006f * Units.Metre;   // a point further off the profiles than this isn't on the skin they follow (inside the lips)
	const float FullAngle = 105f;     // the chart carries paint in full this far round from the front
	const float BottomFade = 0.6f;    // and from this share of the base-to-chin length under the base, none of it
	const float TopFade = 0.3f;       // from the top, this share of the chin-to-top length down to where it carries it in full

	/// <summary>
	/// Where a point on the stock skin goes on the other body: the same angle round the neck, and
	/// the same share of the way from the base to the chin or from the chin to the lower lip along
	/// the skin. How much it should go by that, from 0 to 1, fading out at the sides of the
	/// neck, towards the mouth and under the base, and for points off the profiles.
	/// </summary>
	public static Vec3 Map( NeckChart from, NeckChart to, Vec3 p, out float weight )
	{
		weight = 0;
		var axis = from.AxisAt( p.Z );
		float hx = p.X - axis.X, hy = p.Y - axis.Y, r = MathF.Sqrt( hx * hx + hy * hy );
		if ( r < 1e-6f ) return p;
		float angle = MathF.Atan2( hy, hx ) * 180f / MathF.PI;
		float f = (angle + MaxAngle) / Step;
		int k = (int)MathF.Floor( f );
		if ( k < 0 || k + 1 >= from.angles.Length ) return p;
		f -= k;
		var share = new float[2];
		var target = (R: 0f, Z: 0f);
		var self = (R: 0f, Z: 0f);   // where it lies on the stock profiles, between the two
		for ( int j = 0; j < 2; j++ )
		{
			var a = from.profiles[k + j]; var b = to.profiles[k + j];
			if ( a == null || b == null ) return p;
			float s = Nearest( a, r, p.Z );
			float c0 = from.chinS[k + j], t0 = from.topS[k + j], c1 = to.chinS[k + j], t1 = to.topS[k + j];
			float there = s <= c0 ? s / c0 * c1 : c1 + (s - c0) / (t0 - c0) * (t1 - c1);
			// How far it is into the chart: under the base, and down from the top.
			share[j] = s < 0 ? s / c0 : s <= c0 ? 0 : (s - c0) / (t0 - c0);
			var (rr, zz) = AtLength( b, there );
			var (rs, zs) = AtLength( a, s );
			float w = j == 0 ? 1 - f : f;
			target = (target.R + rr * w, target.Z + zz * w);
			self = (self.R + rs * w, self.Z + zs * w);
			if ( s > t0 ) return p;
		}
		float off = MathF.Sqrt( (r - self.R) * (r - self.R) + (p.Z - self.Z) * (p.Z - self.Z) );
		if ( off > OffProfile ) return p;
		float level = share[0] * (1 - f) + share[1] * f;
		float wAngle = Math.Clamp( (MaxAngle - MathF.Abs( angle )) / (MaxAngle - FullAngle), 0f, 1f );
		float wTop = Math.Clamp( (1 - level) / TopFade, 0f, 1f );
		float wBottom = Math.Clamp( 1 + level / BottomFade, 0f, 1f );
		weight = Smooth( wAngle ) * Smooth( wTop ) * Smooth( wBottom ) * Smooth( 1 - off / OffProfile );
		return to.AxisAt( target.Z ) + Out( angle ) * target.R;
	}

	static float Smooth( float w ) => w * w * (3 - 2 * w);

	/// <summary>The profiles, for looking at: per angle, its points (R, Z, length from the base) and where the chin and the top are along it.</summary>
	public IEnumerable<(float Angle, List<(float R, float Z, float S)> Line, float Chin, float Top)> Profiles()
	{
		for ( int k = 0; k < angles.Length; k++ )
			if ( profiles[k] != null ) yield return (angles[k], profiles[k], chinS[k], topS[k]);
	}
}
