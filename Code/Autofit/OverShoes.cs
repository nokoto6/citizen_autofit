using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// Trousers worn with shoes or boots. The stock dresser puts both on as they are and nothing
/// decides which goes over which; the clothing itself is made with the trouser leg over the
/// shoe (on the stock bodies 85 to 99 percent of a trouser leg that comes down past the top of
/// a boot is outside it). Where the two were modelled into each other, the boot comes through:
/// a hem inside the heel of an ankle boot, a buckle out through the cloth. A fit to another body
/// moves both and makes more of those spots. So the cloth is let out over the shoe wherever
/// the shoe would show through it, and stays where it was everywhere else.
/// </summary>
public static class OverShoes
{
	const float Gap = 0.003f * Units.Metre;        // how far outside the shoe the cloth over it ends up
	const float Reach = 0.08f * Units.Metre;       // a shoe this far out past a bit of cloth, seen from the skin, still covers it
	const float SkinFar = 0.15f * Units.Metre;     // cloth further than this from the skin is not on the leg
	const float SkinDepth = 0.002f * Units.Metre;  // the way out to a bit of cloth starts this far under the skin
	const float MostOut = 0.05f * Units.Metre;     // no bit of cloth is let out further than this
	const float Column = 0.025f * Units.Metre;     // cloth this close above a bit let out, seen from above, is the same trouser leg over it
	const float Slope = 3f;                         // the leg above widens over a height this many times what the bit below it was let out
	const float Together = 0.015f * Units.Metre;   // cloth this close to a bit the shoe comes through goes out with it: a turned-up hem with the leg it lies on
	const int Rounds = 6;      // a bit of cloth let out over one part of the shoe can end up in another part
	const int Smoothing = 4;   // how many edges round a bit let out the cloth flares over it

	/// <summary>
	/// Lets cloth out over shoes. All three in the body's rest pose. The shoes are the left and
	/// the right one together; each leg of the cloth is checked against its own side's.
	/// Returns how many of the cloth's vertices moved and the furthest any of them went.
	/// </summary>
	public static (int Moved, float Furthest) Fit( Vec3[] positions, int[] tris, TriMesh shoes, TriMesh body )
	{
		if ( shoes.TriCount == 0 || positions.Length == 0 ) return (0, 0);

		// The model's left is +Y. A ray from the inside of one ankle can reach the other shoe, so
		// each side of the cloth only sees its own.
		var sides = new List<int>[2] { new(), new() };
		for ( int t = 0; t < shoes.TriCount; t++ )
		{
			float y = shoes.Verts[shoes.Tris[t * 3]].Y + shoes.Verts[shoes.Tris[t * 3 + 1]].Y + shoes.Verts[shoes.Tris[t * 3 + 2]].Y;
			var list = sides[y >= 0 ? 0 : 1];
			list.Add( shoes.Tris[t * 3] ); list.Add( shoes.Tris[t * 3 + 1] ); list.Add( shoes.Tris[t * 3 + 2] );
		}
		var side = new TriMesh[] { new( shoes.Verts, sides[0].ToArray() ), new( shoes.Verts, sides[1].ToArray() ) };

		// Only cloth that comes near a shoe can be covered by one.
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var v in shoes.Verts )
		{
			lo = new Vec3( MathF.Min( lo.X, v.X ), MathF.Min( lo.Y, v.Y ), MathF.Min( lo.Z, v.Z ) );
			hi = new Vec3( MathF.Max( hi.X, v.X ), MathF.Max( hi.Y, v.Y ), MathF.Max( hi.Z, v.Z ) );
		}

		var weld = MeshTools.Weld( positions, out int n );
		var pts = new Vec3[n];
		for ( int i = 0; i < positions.Length; i++ ) pts[weld[i]] = positions[i];
		var edges = MeshTools.Edges( tris, weld );
		var welded = new int[tris.Length];
		for ( int i = 0; i < tris.Length; i++ ) welded[i] = weld[tris[i]];
		var was = new Vec3[n];
		Array.Copy( pts, was, n );

		var near = new bool[n];
		bool any = false;
		for ( int i = 0; i < n; i++ )
		{
			var p = pts[i];
			near[i] = p.X > lo.X - Reach && p.X < hi.X + Reach && p.Y > lo.Y - Reach && p.Y < hi.Y + Reach && p.Z > lo.Z - Reach && p.Z < hi.Z + Reach;
			any |= near[i];
		}
		if ( !any ) return (0, 0);

		var start = new Vec3[n];
		var known = new bool[n];
		var move = new Vec3[n];
		var need = new Vec3[n];     // how far each bit of cloth the shoe came through has to go, all rounds together
		var spread = new Vec3[n];
		var push = new Vec3[n];
		var all = new bool[n];
		Array.Fill( all, true );
		var needy = new List<int>();
		var hits = new List<(float T, bool Leaving, int Tri)>();

		for ( int i = 0; i < n; i++ )
			known[i] = near[i] && Start( pts[i], body, out start[i] );

		for ( int round = 0; round < Rounds; round++ )
		{
			// How far each bit of cloth has to go to clear the shoe from where it is now, along
			// the same way out from the skin every round: from where it is now, a bit let out
			// over the back of a heel would find the heel under it and go on out without end.
			Array.Clear( move );
			for ( int i = 0; i < n; i++ )
				if ( known[i] ) move[i] = Clear( pts[i], start[i], side[pts[i].Y >= 0 ? 0 : 1], hits );

			// A buckle can come through the middle of a triangle with its corners all outside.
			for ( int t = 0; t < tris.Length; t += 3 )
			{
				int a = weld[tris[t]], b = weld[tris[t + 1]], c = weld[tris[t + 2]];
				if ( !known[a] || !known[b] || !known[c] ) continue;
				foreach ( var w in Between )
				{
					var p = pts[a] * w[0] + pts[b] * w[1] + pts[c] * w[2];
					var from = start[a] * w[0] + start[b] * w[1] + start[c] * w[2];
					var d = Clear( p, from, side[p.Y >= 0 ? 0 : 1], hits );
					float length = d.LengthSquared();
					if ( length == 0 ) continue;
					if ( length > move[a].LengthSquared() ) move[a] = d;
					if ( length > move[b].LengthSquared() ) move[b] = d;
					if ( length > move[c].LengthSquared() ) move[c] = d;
				}
			}

			// A strap's end can stand out through the middle of a big triangle of cloth with all of
			// it outside the shoe: seen from the skin, any of the shoe's own corners with cloth
			// under it pushes that cloth out.
			var cloth = new TriMesh( pts, welded );
			for ( int s = 0; s < shoes.Verts.Length; s++ )
			{
				var corner = shoes.Verts[s];
				if ( !Start( corner, body, out var from ) ) continue;
				var way = corner - from;
				float length = way.Length();
				if ( length < 1e-6f * Units.Metre ) continue;
				way /= length;
				// Downwards is the sole and the heel, which cloth doesn't go over.
				if ( way.Z < -Downhill ) continue;
				cloth.Crossings( from, way, length + Gap, hits );
				if ( hits.Count == 0 ) continue;
				var (t, _, tri) = hits[^1];
				if ( length + Gap - t > Reach ) continue;
				var d = way * (length + Gap - t);
				for ( int k = 0; k < 3; k++ )
				{
					int c = welded[tri * 3 + k];
					if ( near[c] && d.LengthSquared() > move[c].LengthSquared() ) move[c] = d;
				}
			}

			bool through = false;
			for ( int i = 0; i < n; i++ )
			{
				if ( move[i].LengthSquared() == 0 ) continue;
				if ( need[i].LengthSquared() == 0 ) needy.Add( i );
				need[i] += move[i];
				// A hem deep in the foot of the shoe would have to go further; it goes this far and
				// the rest of it stays in the shoe, out of sight, rather than stand out in a spike.
				float far = need[i].Length();
				if ( far > MostOut ) need[i] *= MostOut / far;
				through = true;
			}
			if ( !through ) break;

			// Cloth lying on cloth goes out as a whole, as far as its deepest layer has to, so a
			// hem turned up over the leg stays over it instead of ending up level with it or under it.
			// The leg above a bit let out goes out too, less the higher up it is, so the trouser leg
			// widens down to the shoe like a bell instead of standing out round it in a brim.
			for ( int i = 0; i < n; i++ )
			{
				spread[i] = need[i];
				if ( !near[i] ) continue;
				float most = need[i].Length();
				foreach ( int j in needy )
				{
					var apart = was[i] - was[j];
					float flat = apart.X * apart.X + apart.Y * apart.Y;
					float wanted = need[j].Length();
					float share;
					if ( apart.LengthSquared() < Together * Together ) share = 1f;
					else if ( flat < Column * Column && apart.Z > 0 ) share = 1f - apart.Z / (Slope * wanted);
					else continue;
					if ( share * wanted <= most ) continue;
					most = share * wanted;
					spread[i] = need[j] * share;
				}
			}

			// Smoothed into the cloth around, so it flares out over the shoe rather than kinks
			// round its edge. What the shoe came through goes all the way.
			Array.Copy( spread, push, n );
			for ( int k = 0; k < Smoothing; k++ )
			{
				MeshTools.Relax( push, all, edges, 1, 0.5f );
				for ( int i = 0; i < n; i++ )
					if ( spread[i].LengthSquared() > 0 ) push[i] = spread[i];
			}
			for ( int i = 0; i < n; i++ )
				pts[i] = was[i] + push[i];
		}

		int moved = 0;
		float furthest = 0;
		for ( int i = 0; i < n; i++ )
		{
			float d = (pts[i] - was[i]).Length();
			if ( d < 1e-5f * Units.Metre ) continue;
			moved++;
			furthest = MathF.Max( furthest, d );
		}
		for ( int i = 0; i < positions.Length; i++ ) positions[i] = pts[weld[i]];
		return (moved, furthest);
	}

	// Points inside a triangle to test besides its corners: the centre and the edge midpoints.
	static readonly float[][] Between =
	{
		new[] { 1 / 3f, 1 / 3f, 1 / 3f }, new[] { 0.5f, 0.5f, 0f }, new[] { 0f, 0.5f, 0.5f }, new[] { 0.5f, 0f, 0.5f },
	};

	// A point just under the skin nearest to a bit of cloth: where the way out to it starts.
	// False for cloth under the skin (the inside of a hem sunk into an ankle): nobody sees it,
	// and the way out from the far side of the leg would throw it across the other.
	static bool Start( Vec3 p, TriMesh body, out Vec3 from )
	{
		from = Vec3.Zero;
		if ( body.Nearest( p, out var skin, out int tri ) > SkinFar || tri < 0 ) return false;
		var normal = body.TriNormals[tri];
		float area = normal.Length();
		if ( area < 1e-12f || Vec3.Dot( p - skin, normal ) < 0 ) return false;
		from = skin - normal / area * SkinDepth;
		return true;
	}

	// Where a bit of cloth has to go to be Gap outside the shoe, as a move from where it is;
	// zero when the shoe isn't over it. The shoe is over it when the way out from the skin to it
	// meets the shoe's surface past the cloth (or less than Gap short of it). The cloth goes on
	// along that way, or to the nearest bit of the shoe's outside if that is closer: a hem sunk
	// into the top of a foot comes up out of it, not forward along the foot. Never down: a
	// trouser leg hangs, and under the arch of a heel is no place for it. Zero too when there is
	// no way out but down; that bit stays inside the shoe, out of sight.
	static Vec3 Clear( Vec3 p, Vec3 from, TriMesh shoe, List<(float T, bool Leaving, int Tri)> hits )
	{
		var way = p - from;
		float length = way.Length();
		if ( length < 1e-6f * Units.Metre || shoe.TriCount == 0 ) return Vec3.Zero;
		way /= length;
		shoe.Crossings( from, way, length + Reach, hits );
		float leave = -1;
		foreach ( var hit in hits )
		{
			if ( hit.T < length - Gap || !hit.Leaving ) continue;
			leave = hit.T;
			break;
		}
		if ( leave < 0 ) return Vec3.Zero;

		var best = Vec3.Zero;
		if ( way.Z > -Downhill ) best = way * (leave + Gap - length);

		shoe.Nearest( p, out var surface, out int tri );
		if ( tri < 0 ) return best;
		var normal = shoe.TriNormals[tri];
		float area = normal.Length();
		// Only the shoe's outside will do (its lining faces the leg), and only where it faces up
		// or sideways.
		if ( area < 1e-12f || Vec3.Dot( normal, surface - from ) <= 0 || normal.Z < -Downhill * area ) return best;
		var outside = surface + normal / area * Gap - p;
		return best.LengthSquared() == 0 || outside.LengthSquared() < best.LengthSquared() ? outside : best;
	}

	const float Downhill = 0.35f;   // a way out sloping down more steeply than this (the sine of 20 degrees) is not taken
}
