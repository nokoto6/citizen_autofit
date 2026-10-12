using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// What is worn in the mouth, on the teeth: vampire fangs, a grill, braces. Made for one body,
/// it sits on that body's teeth, behind its lips. Fitted like anything else on the face it is
/// close to the skin all over (the teeth are part of the body's mesh), so it was taken for paint
/// and laid on the other face's lips, a moustache of fangs. And a face's teeth say little about
/// its outside: the citizen's are a pair of blocks as wide as its whole mouth, an anime girl's a
/// thin white band half as wide as hers.
///
/// So the teeth of each body are found (see <see cref="Find"/>) and what is worn on them goes
/// from the stock body's rows to this one's: each piece onto its own row, the upper or the lower,
/// as far in front of it and as far below its edge, scaled by how much wider or narrower the row
/// is. In the morphs it goes with that row (see <see cref="Morphs"/>), the way the teeth go when
/// the mouth opens or the lips move, and behind lips that close it stays as hidden as the teeth.
/// </summary>
public static class Teeth
{
	const float Wide = 0.8f;          // teeth are looked for this many times the eyes' distance to either side of the face's middle
	const float High = 0.3f;          // from this far below the eyes
	const float Low = 1.6f;           // to this far
	const float Behind = 1.4f;        // and from this far behind their front
	const float Before = 0.6f;        // to this far in front of it
	const float Enclosed = 1.0f;      // the skin is in front of the inside of the mouth, or above and below it, within this
	const float Tongue = 0.85f;       // a piece narrower than this share of the widest one and further back is the tongue
	const float TongueBack = 0.1f;    // further back than the front of the others by this share of their width
	const float Skin = 0.25f;         // a piece with this share of the biggest one's vertices or more is skin (the citizen's head is a piece apart from its body)
	const float Midline = 0.12f;      // a row's front is looked for this close to the middle, as a share of its width
	const float WornShare = 0.8f;     // a garment this much of which lies in a mouth is worn in it
	const float WornMargin = 0.15f;   // round the teeth, as a share of their width
	const int MinRow = 12;            // vertices a row of teeth has at least
	const float Reach = 1.5f;         // the lips in front of the teeth are looked for this far, as a share of the row's width
	const float Under = 0.04f;        // and what came out in front of them goes this far behind them
	const int BehindPasses = 8;
	const float Touch = 0.01f;        // what the stock lips hid stays at least this far behind this body's (a share of the row's width), or as far as it was
	const float Slack = 0.005f;       // closer behind the lips than that by this share of the width is let be
	const float Aside = 0.61f;        // looked at from 35 degrees to either side too
	const float Tilt = 0.35f;         // and from 20 degrees above and below
	const int Rings = 3;
	const float MostBack = 0.3f;      // and no further than this share of the row's width
	const float SameRow = 0.01f;      // rows this close to each other everywhere, as a share of the width, are the same teeth
	const float Fang = 0.02f;         // what hangs past the upper row's biting edge by this share of its width is fang
	static readonly float[] Backs = { 0f, 0.25f, 0.5f };   // how far back fangs are tried leant, for how far down they reach
	const float LeastIn = 0.5f;       // what is on the upper row is moved in towards the middle down to this share of where it was
	const float InStep = 0.1f;
	const float Shut = 4f;            // a share of the fangs out through the closed lips counts this many times as much
	const float Near = 0.05f;         // a share of the fangs showing this close to the stock body's is as good
	const float LongerStep = 0.125f;  // fang lengths are tried in steps of this (one of them as fitted)
	const float LongerCost = 0.02f;   // a change of the length counts against it this much, so that a tie keeps it
	const float LeastLonger = 0.5f;   // fangs get no shorter than this many times what the row's width makes them
	const float MostLonger = 3f;      // and no longer than this
	const int Slices = 8;             // a row is followed round in this many slices across
	const float FrontDepth = 0.15f;   // what is this close behind a slice's front, as a share of the row's width, faces forward

	/// <summary>One row of teeth, in the positions it was found in.</summary>
	public sealed class Row
	{
		public int[] Verts;         // body vertices of the row
		public int[] Tris;          // body triangles of the row, three vertex indices each
		public Vec3 Front;          // the front of the row in the middle, at its biting edge
		public float Width;         // half its width, side to side
		public bool Upper;
		public float[] Ahead;       // per slice across it, from one side to the other: how far forward its front is
		public float[] Edge;        // the height of its biting edge there
		public float[] Root;        // and of the other end of what faces forward (the top of the upper row's gums)
		public float Tall;          // how tall what faces forward is, the middle of the slices

		// A slice's value at a place across the row (-1 at one side, 1 at the other), between slice middles.
		public static float At( float[] slices, float u )
		{
			float f = Math.Clamp( (u + 1f) * 0.5f * slices.Length - 0.5f, 0f, slices.Length - 1f );
			int a = (int)MathF.Floor( f ), b = Math.Min( a + 1, slices.Length - 1 );
			return slices[a] + (slices[b] - slices[a]) * (f - a);
		}
	}

	/// <summary>The rows of a mouth. Either can be null where it wasn't found.</summary>
	public sealed class Mouth
	{
		public Row Upper, Lower;
		public Row this[bool upper] => upper ? Upper ?? Lower : Lower ?? Upper;
	}

	/// <summary>
	/// The teeth of a body: the pieces of its mesh (welded islands) inside the mouth. The skin is
	/// one big piece and goes round the outside; teeth, gums and a tongue are small ones of their
	/// own inside it, across the face's middle, between the nose and the chin, with skin above
	/// and below them (the palate, the lips, the floor of the mouth). The tongue is narrower than
	/// the rows and is left out. A piece with both rows in it (the human's gums, joined at the
	/// back) is split where it is thinnest, between them. The pieces highest up are the upper
	/// row, the others the lower one.
	/// </summary>
	/// <param name="verts">The body's positions.</param>
	/// <param name="tris">Its triangles.</param>
	/// <param name="eyeLeft">The front of one eye.</param>
	/// <param name="eyeRight">The front of the other.</param>
	/// <returns>Null without teeth.</returns>
	public static Mouth Find( Vec3[] verts, int[] tris, Vec3 eyeLeft, Vec3 eyeRight )
	{
		float apart = (eyeLeft - eyeRight).Length();
		if ( apart <= 0 || verts.Length == 0 ) return null;
		var eyes = 0.5f * (eyeLeft + eyeRight);
		float front = MathF.Max( eyeLeft.X, eyeRight.X );
		var weld = MeshTools.Weld( verts, out int n );
		var edges = MeshTools.Edges( tris, weld );
		var piece = GarmentFit.Islands( n, edges );

		var members = new Dictionary<int, List<int>>();
		for ( int i = 0; i < verts.Length; i++ )
		{
			int p = piece[weld[i]];
			if ( !members.TryGetValue( p, out var list ) ) members[p] = list = new List<int>();
			list.Add( i );
		}
		int most = 0;
		foreach ( var list in members.Values ) most = Math.Max( most, list.Count );
		var big = new HashSet<int>();
		foreach ( var (p, list) in members ) if ( list.Count >= Skin * most ) big.Add( p );
		var skin = new List<int>();
		for ( int t = 0; t + 2 < tris.Length; t += 3 )
			if ( big.Contains( piece[weld[tris[t]]] ) ) { skin.Add( tris[t] ); skin.Add( tris[t + 1] ); skin.Add( tris[t + 2] ); }
		var outside = new TriMesh( verts, skin.ToArray() );

		// The pieces inside the mouth, each as its vertices.
		var found = new List<List<int>>();
		var hits = new List<(float T, bool Leaving, int Tri)>();
		foreach ( var (p, list) in members )
		{
			if ( big.Contains( p ) ) continue;
			bool fits = true;
			float left = float.MinValue, right = float.MaxValue;
			var centre = Vec3.Zero;
			foreach ( int v in list )
			{
				var d = verts[v] - eyes;
				if ( MathF.Abs( d.Y ) > Wide * apart || d.Z > -High * apart || d.Z < -Low * apart || verts[v].X > front + Before * apart || verts[v].X < front - Behind * apart ) { fits = false; break; }
				left = MathF.Max( left, d.Y ); right = MathF.Min( right, d.Y );
				centre += verts[v];
			}
			if ( !fits || left <= 0 || right >= 0 ) continue;
			centre /= list.Count;
			if ( !Covered( outside, centre, apart, hits ) ) continue;
			found.Add( list );
		}
		if ( found.Count == 0 ) return null;

		// Without the tongue: narrower than the rows and behind their front.
		var half = new float[found.Count];
		var ahead = new float[found.Count];
		for ( int k = 0; k < found.Count; k++ )
		{
			float l = 0, r = 0;
			ahead[k] = float.MinValue;
			foreach ( int v in found[k] ) { l = MathF.Max( l, verts[v].Y - eyes.Y ); r = MathF.Max( r, eyes.Y - verts[v].Y ); ahead[k] = MathF.Max( ahead[k], verts[v].X ); }
			half[k] = 0.5f * (l + r);
		}
		float widest = 0, foremost = float.MinValue;
		foreach ( var h in half ) widest = MathF.Max( widest, h );
		foreach ( var a in ahead ) foremost = MathF.Max( foremost, a );
		var rows = new List<(List<int> Verts, float Z)>();
		for ( int k = 0; k < found.Count; k++ )
		{
			if ( half[k] < Tongue * widest && ahead[k] < foremost - TongueBack * widest ) continue;
			rows.Add( (found[k], 0) );
		}

		// Both rows in one piece (the human's gums, welded where they meet at the back): its
		// parts as made, apart from the welding, are the rows.
		if ( rows.Count == 1 ) rows = Parts( rows[0].Verts, tris );
		for ( int k = 0; k < rows.Count; k++ )
		{
			float z = 0;
			foreach ( int v in rows[k].Verts ) z += verts[v].Z;
			rows[k] = (rows[k].Verts, z / rows[k].Verts.Count);
		}

		// Upper and lower: split where the pieces' heights are furthest apart.
		rows.Sort( ( a, b ) => a.Z.CompareTo( b.Z ) );
		int cut = rows.Count - 1;
		float gap = -1;
		for ( int k = 1; k < rows.Count; k++ ) if ( rows[k].Z - rows[k - 1].Z > gap ) { gap = rows[k].Z - rows[k - 1].Z; cut = k; }
		var upper = new List<int>();
		var lower = new List<int>();
		for ( int k = 0; k < rows.Count; k++ ) (rows.Count > 1 && k < cut ? lower : upper).AddRange( rows[k].Verts );
		var mouth = new Mouth { Upper = MakeRow( upper, verts, tris, eyes.Y, true ), Lower = MakeRow( lower, verts, tris, eyes.Y, false ) };
		return mouth.Upper == null && mouth.Lower == null ? null : mouth;
	}

	// Whether a point is inside the face: behind the lips, or between a palate and the floor of a mouth.
	static bool Covered( TriMesh skin, Vec3 p, float apart, List<(float T, bool Leaving, int Tri)> hits )
	{
		skin.Crossings( p, new Vec3( 1, 0, 0 ), Enclosed * apart, hits );
		if ( hits.Count > 0 ) return true;
		skin.Crossings( p, Vec3.UnitZ, Enclosed * apart, hits );
		if ( hits.Count == 0 ) return false;
		skin.Crossings( p, -Vec3.UnitZ, Enclosed * apart, hits );
		return hits.Count > 0;
	}

	static float Median( float[] high, float[] low )
	{
		var tall = new float[high.Length];
		for ( int k = 0; k < tall.Length; k++ ) tall[k] = high[k] - low[k];
		Array.Sort( tall );
		return tall[tall.Length / 2];
	}

	// The parts of a piece joined by its triangles alone, without welding what only shares a position.
	static List<(List<int> Verts, float Z)> Parts( List<int> list, int[] tris )
	{
		var parent = new Dictionary<int, int>();
		foreach ( int v in list ) parent[v] = v;
		int Find( int x ) { while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		for ( int t = 0; t + 2 < tris.Length; t += 3 )
		{
			if ( !parent.ContainsKey( tris[t] ) || !parent.ContainsKey( tris[t + 1] ) || !parent.ContainsKey( tris[t + 2] ) ) continue;
			parent[Find( tris[t] )] = Find( tris[t + 1] );
			parent[Find( tris[t + 1] )] = Find( tris[t + 2] );
		}
		var groups = new Dictionary<int, List<int>>();
		foreach ( int v in list )
		{
			int r = Find( v );
			if ( !groups.TryGetValue( r, out var g ) ) groups[r] = g = new List<int>();
			g.Add( v );
		}
		var result = new List<(List<int>, float)>();
		foreach ( var g in groups.Values ) result.Add( (g, 0f) );
		return result;
	}

	static Row MakeRow( List<int> list, Vec3[] verts, int[] tris, float middle, bool upper )
	{
		if ( list.Count < MinRow ) return null;
		var mine = new bool[verts.Length];
		foreach ( int v in list ) mine[v] = true;
		var rowTris = new List<int>();
		for ( int t = 0; t + 2 < tris.Length; t += 3 )
			if ( mine[tris[t]] && mine[tris[t + 1]] && mine[tris[t + 2]] ) { rowTris.Add( tris[t] ); rowTris.Add( tris[t + 1] ); rowTris.Add( tris[t + 2] ); }
		if ( rowTris.Count == 0 ) return null;

		// Half the width: as far to either side as the row goes, the two averaged.
		float l = 0, r = 0;
		foreach ( int v in list ) { l = MathF.Max( l, verts[v].Y - middle ); r = MathF.Max( r, middle - verts[v].Y ); }
		float width = 0.5f * (l + r);

		// The front in the middle: the furthest forward of what is close to the middle line, and
		// of that, the biting edge (the bottom of the upper row, the top of the lower one).
		float front = float.MinValue;
		foreach ( int v in list ) if ( MathF.Abs( verts[v].Y - middle ) <= Midline * width ) front = MathF.Max( front, verts[v].X );
		if ( front == float.MinValue ) return null;
		float edge = upper ? float.MaxValue : float.MinValue;
		foreach ( int v in list )
		{
			if ( MathF.Abs( verts[v].Y - middle ) > Midline * width || verts[v].X < front - Midline * width ) continue;
			edge = upper ? MathF.Min( edge, verts[v].Z ) : MathF.Max( edge, verts[v].Z );
		}

		// Round the row, slice by slice: its front, and the top and bottom of what faces forward.
		var ahead = new float[Slices];
		var low = new float[Slices];
		var high = new float[Slices];
		var any = new bool[Slices];
		int SliceOf( Vec3 p ) => Math.Clamp( (int)((p.Y - middle) / width * 0.5f * Slices + 0.5f * Slices), 0, Slices - 1 );
		Array.Fill( ahead, float.MinValue );
		foreach ( int v in list ) { int k = SliceOf( verts[v] ); ahead[k] = MathF.Max( ahead[k], verts[v].X ); any[k] = true; }
		Array.Fill( low, float.MaxValue );
		Array.Fill( high, float.MinValue );
		foreach ( int v in list )
		{
			int k = SliceOf( verts[v] );
			if ( verts[v].X < ahead[k] - FrontDepth * width ) continue;
			low[k] = MathF.Min( low[k], verts[v].Z );
			high[k] = MathF.Max( high[k], verts[v].Z );
		}
		for ( int k = 0; k < Slices; k++ )
		{
			if ( any[k] ) continue;
			int near = -1;
			for ( int d = 1; d < Slices && near < 0; d++ ) near = k - d >= 0 && any[k - d] ? k - d : k + d < Slices && any[k + d] ? k + d : -1;
			if ( near < 0 ) return null;
			(ahead[k], low[k], high[k]) = (ahead[near], low[near], high[near]);
		}

		return new Row
		{
			Verts = list.ToArray(), Tris = rowTris.ToArray(), Front = new Vec3( front, middle, edge ), Width = width, Upper = upper,
			Ahead = ahead, Edge = upper ? low : high, Root = upper ? high : low, Tall = Median( high, low ),
		};
	}

	/// <summary>Per used vertex of a garment as made, the row of the stock mouth it is worn on (true for the upper one); null if the garment isn't worn in the mouth.</summary>
	/// <param name="stock">The stock body's mouth, see <see cref="Find"/>.</param>
	public static bool[] Worn( Vec3[] verts, int[] tris, Vec3[] stockVerts, Mouth stock )
	{
		if ( stock == null ) return null;

		// The box round the teeth, a little wider.
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		float width = 0;
		foreach ( var row in new[] { stock.Upper, stock.Lower } )
		{
			if ( row == null ) continue;
			width = MathF.Max( width, row.Width );
			foreach ( int v in row.Verts )
			{
				var p = stockVerts[v];
				lo = new Vec3( MathF.Min( lo.X, p.X ), MathF.Min( lo.Y, p.Y ), MathF.Min( lo.Z, p.Z ) );
				hi = new Vec3( MathF.Max( hi.X, p.X ), MathF.Max( hi.Y, p.Y ), MathF.Max( hi.Z, p.Z ) );
			}
		}
		float m = WornMargin * width;
		var used = new bool[verts.Length];
		foreach ( int v in tris ) used[v] = true;
		int all = 0, inMouth = 0;
		for ( int i = 0; i < verts.Length; i++ )
		{
			if ( !used[i] ) continue;
			all++;
			var p = verts[i];
			if ( p.X >= lo.X - m && p.X <= hi.X + m && p.Y >= lo.Y - m && p.Y <= hi.Y + m && p.Z >= lo.Z - m && p.Z <= hi.Z + m ) inMouth++;
		}
		if ( all == 0 || inMouth < WornShare * all ) return null;

		// Each connected piece goes with the row it is closest to, on the whole.
		var rows = new TriMesh[2];
		if ( stock.Upper != null ) rows[0] = new TriMesh( stockVerts, stock.Upper.Tris );
		if ( stock.Lower != null ) rows[1] = new TriMesh( stockVerts, stock.Lower.Tris );
		if ( rows[0] == null ) return new bool[verts.Length];
		var weld = MeshTools.Weld( verts, out int n );
		var piece = GarmentFit.Islands( n, MeshTools.Edges( tris, weld ) );
		var sum = new Dictionary<int, (float Up, float Down)>();
		for ( int i = 0; i < verts.Length; i++ )
		{
			if ( !used[i] ) continue;
			float up = rows[0] != null ? rows[0].Nearest( verts[i], out _, out _ ) : float.MaxValue;
			float down = rows[1] != null ? rows[1].Nearest( verts[i], out _, out _ ) : float.MaxValue;
			int p = piece[weld[i]];
			var s = sum.TryGetValue( p, out var x ) ? x : (0f, 0f);
			sum[p] = (s.Item1 + MathF.Min( up, Units.Metre ), s.Item2 + MathF.Min( down, Units.Metre ));
		}
		var result = new bool[verts.Length];
		for ( int i = 0; i < verts.Length; i++ )
			if ( sum.TryGetValue( piece[weld[i]], out var s ) ) result[i] = s.Up <= s.Down;
		return result;
	}

	/// <summary>
	/// Where a garment worn in the mouth goes on this body. Each vertex keeps its place round the
	/// row (as far to the side, for the row's width), its depth off the row's front there and its
	/// height off the row's biting edge there, the height for how tall the row is and the depth
	/// for the smaller of the two; what hangs past the edge (fangs) for the width. Rows are shaped very differently from body to body (the
	/// citizen's a straight block as tall as its lip, an anime girl's a thin band bent round):
	/// scaled as a whole, fangs made for the citizen stood out of her cheeks and up past her
	/// nose. Both bodies are in the stock pose.
	/// </summary>
	/// <param name="upper">Per vertex, whether it is worn on the upper row (see <see cref="Worn"/>).</param>
	/// <returns>Null if this mouth lacks a row the garment is worn on.</returns>
	public static Vec3[] Fit( Vec3[] verts, bool[] upper, Mouth stock, Mouth body )
	{
		if ( body == null ) return null;
		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ )
		{
			Row from = stock[upper[i]], to = body[upper[i]];
			if ( from == null || to == null ) return null;
			var p = verts[i];
			float u = (p.Y - from.Front.Y) / from.Width;
			float wide = to.Width / from.Width;
			float tall = to.Tall / MathF.Max( from.Tall, 1e-4f * Units.Metre );
			float deep = MathF.Min( wide, tall );
			// Past the biting edge it is teeth of its own (fangs): as long for the row's width.
			bool past = upper[i] ? p.Z < Row.At( from.Edge, u ) : p.Z > Row.At( from.Edge, u );
			result[i] = new Vec3(
				Row.At( to.Ahead, u ) + (p.X - Row.At( from.Ahead, u )) * deep,
				to.Front.Y + u * to.Width,
				Row.At( to.Edge, u ) + (p.Z - Row.At( from.Edge, u )) * (past ? wide : tall) );
		}
		return result;
	}

	/// <summary>
	/// Whether a garment as made is worn in the stock body's mouth, and per vertex on which row
	/// (true for the upper one); null if it isn't. Only what is in the face below the eyes is
	/// looked at closer.
	/// </summary>
	/// <param name="stockMesh">The stock body.</param>
	/// <param name="eyeLeft">The front of one of the stock body's eyes.</param>
	/// <param name="eyeRight">The front of the other.</param>
	public static bool[] InMouth( Vec3[] verts, int[] tris, TriMesh stockMesh, Vec3 eyeLeft, Vec3 eyeRight, out Mouth stock )
	{
		stock = null;
		float apart = (eyeLeft - eyeRight).Length();
		if ( apart <= 0 || tris.Length == 0 ) return null;
		var eyes = 0.5f * (eyeLeft + eyeRight);
		float front = MathF.Max( eyeLeft.X, eyeRight.X );
		foreach ( int v in tris )
		{
			var d = verts[v] - eyes;
			if ( MathF.Abs( d.Y ) > Wide * apart || d.Z > -High * apart || d.Z < -Low * apart || verts[v].X > front + Before * apart || verts[v].X < front - Behind * apart ) return null;
		}
		stock = Find( stockMesh.Verts, stockMesh.Tris, eyeLeft, eyeRight );
		return Worn( verts, tris, stockMesh.Verts, stock );
	}

	/// <summary>
	/// A garment worn in the mouth (<see cref="InMouth"/>) put on this body's teeth, in the stock
	/// pose. Without teeth to find on this body it goes by the mouth instead: as far behind its
	/// middle as it was behind the stock one's, scaled by how much wider the mouth is. Null if
	/// the garment isn't worn in the mouth, or this face has neither.
	/// </summary>
	/// <param name="stockMesh">The stock body.</param>
	/// <param name="bodyMesh">This body, in the stock pose.</param>
	/// <param name="eyes">The fronts of the eyes, on the stock body and on this one.</param>
	/// <param name="features">The face's features on both (<see cref="Repose.Features"/>), for the mouth.</param>
	public static Vec3[] On( Vec3[] verts, int[] tris, TriMesh stockMesh, TriMesh bodyMesh, (Vec3 Stock, Vec3 Body)[] eyes, List<(string Name, Vec3 Stock, Vec3 Body)> features )
	{
		if ( eyes == null || eyes.Length != 2 ) return null;
		var upper = InMouth( verts, tris, stockMesh, eyes[0].Stock, eyes[1].Stock, out var stock );
		if ( upper == null ) return null;
		var body = Find( bodyMesh.Verts, bodyMesh.Tris, eyes[0].Body, eyes[1].Body );
		if ( Fit( verts, upper, stock, body ) is Vec3[] fitted ) return fitted;

		// No teeth: by the mouth.
		(string Name, Vec3 Stock, Vec3 Body) mid = default, left = default, right = default;
		if ( features != null )
			foreach ( var f in features )
			{
				if ( f.Name == "mouth.mid" ) mid = f;
				else if ( f.Name == "mouth.L" ) left = f;
				else if ( f.Name == "mouth.R" ) right = f;
			}
		if ( mid.Name == null || left.Name == null || right.Name == null ) return null;
		float wide = (left.Stock - right.Stock).Length();
		if ( wide <= 0 ) return null;
		float scale = (left.Body - right.Body).Length() / wide;
		var result = new Vec3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ ) result[i] = mid.Body + (verts[i] - mid.Stock) * scale;
		return result;
	}

	/// <summary>
	/// What was behind the stock body's closed lips stays behind this one's. A row of teeth set
	/// further back under thinner lips, or a jaw shut tighter, leaves the tip of a fang out in
	/// front of the lips, and a grill's edge through a cheek. Such a vertex goes straight back
	/// until the skin in front of it hides it again, and its neighbours with it, less and less.
	/// </summary>
	static void KeepBehindLips( Vec3[] made, int[] tris, Vec3[] fitted, TriMesh stockMesh, TriMesh bodyMesh, Mouth stock, Mouth body )
	{
		float most = MostBack * MathF.Max( body.Upper?.Width ?? 0, body.Lower?.Width ?? 0 );
		float width = MathF.Max( body.Upper?.Width ?? 0, body.Lower?.Width ?? 0 );
		float stockWidth = MathF.Max( stock.Upper?.Width ?? 0, stock.Lower?.Width ?? 0 );
		if ( width <= 0 || stockWidth <= 0 ) return;
		var forward = new Vec3( 1, 0, 0 );
		var back = new Vec3( -1, 0, 0 );
		// Seen from the front, from either side of it, from above and from below.
		var looks = new[] { forward, new Vec3( MathF.Cos( Aside ), MathF.Sin( Aside ), 0 ), new Vec3( MathF.Cos( Aside ), -MathF.Sin( Aside ), 0 ), new Vec3( MathF.Cos( Tilt ), 0, MathF.Sin( Tilt ) ), new Vec3( MathF.Cos( Tilt ), 0, -MathF.Sin( Tilt ) ) };
		var hits = new List<(float T, bool Leaving, int Tri)>();
		var weld = MeshTools.Weld( made, out int n );
		var edges = MeshTools.Edges( tris, weld );
		var used = new bool[made.Length];
		foreach ( int v in tris ) used[v] = true;
		var hidden = new bool[n * looks.Length];
		var clear = new float[n * looks.Length];   // how far behind the stock lips, for this row's width
		var at = new Vec3[n];
		var mine = new bool[n];
		for ( int i = 0; i < made.Length; i++ )
		{
			if ( !used[i] ) continue;
			int w = weld[i];
			mine[w] = true;
			at[w] = fitted[i];
			for ( int k = 0; k < looks.Length; k++ )
			{
				stockMesh.Crossings( made[i], looks[k], Reach * stockWidth, hits );
				hidden[w * looks.Length + k] = hits.Count > 0;
				if ( hits.Count > 0 ) clear[w * looks.Length + k] = MathF.Min( hits[0].T * width / stockWidth, Touch * width );
			}
		}
		var shift = new float[n];
		for ( int pass = 0; pass < BehindPasses; pass++ )
		{
			bool any = false;
			for ( int w = 0; w < n; w++ )
			{
				if ( !mine[w] ) continue;
				var p = at[w] + back * shift[w];
				float more = 0;
				if ( hidden[w * looks.Length] )
				{
					bodyMesh.Crossings( p, forward, Reach * width, hits );
					if ( hits.Count > 0 && hits[0].T < clear[w * looks.Length] - Slack * width ) more = clear[w * looks.Length] - hits[0].T;
					else if ( hits.Count == 0 )
					{
						// Out in front: back through the skin, and as far again behind it.
						bodyMesh.Crossings( p, back, Reach * width, hits );
						if ( hits.Count > 0 ) more = hits[0].T + Under * width;
					}
				}
				// Seen at a slant past the corner of the mouth or the nose: back a step at a time.
				for ( int k = 1; k < looks.Length && more == 0; k++ )
				{
					if ( !hidden[w * looks.Length + k] ) continue;
					bodyMesh.Crossings( p, looks[k], Reach * width, hits );
					if ( hits.Count == 0 || hits[0].T < clear[w * looks.Length + k] - Slack * width ) more = 2 * Under * width;
				}
				if ( more <= 0 || shift[w] >= most ) continue;
				shift[w] = MathF.Min( shift[w] + more, most );
				any = true;
			}
			if ( !any ) break;
			// The neighbours go back too, half as far a ring further out.
			for ( int ring = 0; ring < Rings; ring++ )
				foreach ( var (a, b) in edges )
				{
					shift[a] = MathF.Max( shift[a], 0.5f * shift[b] );
					shift[b] = MathF.Max( shift[b], 0.5f * shift[a] );
				}
		}
		for ( int i = 0; i < made.Length; i++ ) if ( used[i] ) fitted[i] += back * shift[weld[i]];
	}

	/// <summary>The morphs that open a mouth, by the names the bodies use: all of them at once open it once.</summary>
	public static readonly string[] OpenMouth = { "jawOpen", "openjawL", "openjawR", "openmouthL", "openmouthR" };

	/// <summary>The same rows, measured again in other positions of the same body (its rest pose, the mouth open).</summary>
	public static Mouth In( Mouth mouth, Vec3[] verts, int[] tris ) =>
		mouth == null ? null : new Mouth { Upper = Again( mouth.Upper, verts, tris ), Lower = Again( mouth.Lower, verts, tris ) };

	static Row Again( Row row, Vec3[] verts, int[] tris )
	{
		if ( row == null ) return null;
		float lo = float.MaxValue, hi = float.MinValue;
		foreach ( int v in row.Verts ) { lo = MathF.Min( lo, verts[v].Y ); hi = MathF.Max( hi, verts[v].Y ); }
		return MakeRow( new List<int>( row.Verts ), verts, tris, 0.5f * (lo + hi), row.Upper );
	}

	/// <summary>
	/// Puts a garment worn on the teeth right in the body's rest pose, once it is there: onto
	/// its rows as found there (see <see cref="Fit"/>), what the stock lips hid behind this
	/// body's closed lips (see <see cref="KeepBehindLips"/>), and fangs made to show with the
	/// mouth open as much as they do on the stock body (see <see cref="Lengthen"/>). Fangs are
	/// made for that: on the citizen, whose lip goes up over its teeth, they hang a little past
	/// its row; an anime girl's lip stays down over the whole row even wide open, and fangs as
	/// long for her teeth never showed.
	/// </summary>
	/// <param name="made">The garment as made, on the stock body.</param>
	/// <param name="fitted">The garment in the body's rest pose. Changed in place.</param>
	/// <param name="upper">Per vertex, its row (<see cref="InMouth"/>).</param>
	/// <param name="stock">The stock body's mouth (<see cref="InMouth"/>).</param>
	/// <param name="stockRest">The stock body's positions, as made.</param>
	/// <param name="stockOpen">The same with the mouth open (<see cref="OpenMouth"/>), or null if it doesn't open by morphs.</param>
	/// <param name="body">This body's mouth as found in the stock pose (<see cref="Find"/>).</param>
	/// <param name="rest">This body's positions in its rest pose.</param>
	/// <param name="open">The same with the mouth open, or null.</param>
	public static void Settle( Vec3[] made, int[] tris, Vec3[] fitted, bool[] upper, Mouth stock, Vec3[] stockRest, int[] stockTris, Vec3[] stockOpen, Mouth body, Vec3[] rest, int[] bodyTris, Vec3[] open )
	{
		var from = In( stock, stockRest, stockTris );
		var to = In( body, rest, bodyTris );
		if ( from?.Upper == null || to?.Upper == null ) return;
		// Onto the rows again, here: the way back from the stock pose carries the garment as a
		// whole with the head and leaves it a few millimetres off teeth that went their own way.
		if ( Fit( made, upper, from, to ) is Vec3[] again ) Array.Copy( again, fitted, fitted.Length );
		// The same teeth (the human male's and female's mouths are one mesh): it stays as made for them.
		if ( Same( from.Upper, to.Upper ) && (from.Lower == null || to.Lower == null || Same( from.Lower, to.Lower )) ) return;
		var stockMesh = new TriMesh( stockRest, stockTris );
		var bodyMesh = new TriMesh( rest, bodyTris );
		KeepBehindLips( made, tris, fitted, stockMesh, bodyMesh, from, to );
		if ( stockOpen == null || open == null ) return;
		Lengthen( made, tris, fitted, upper, from.Upper, stockMesh, stockRest, stockOpen, stockTris, to.Upper, bodyMesh, rest, open, bodyTris );
		// Moved in towards the middle, the rest of it can have come out somewhere else.
		KeepBehindLips( made, tris, fitted, stockMesh, bodyMesh, from, to );
	}

	// Whether two rows are the same teeth: as wide, and their front, edge and top in every slice
	// the same to within a hundredth of their width.
	static bool Same( Row a, Row b )
	{
		float tolerance = SameRow * a.Width;
		if ( MathF.Abs( a.Width - b.Width ) > tolerance ) return false;
		for ( int k = 0; k < a.Ahead.Length; k++ )
			if ( MathF.Abs( a.Ahead[k] - b.Ahead[k] ) > tolerance || MathF.Abs( a.Edge[k] - b.Edge[k] ) > tolerance || MathF.Abs( a.Root[k] - b.Root[k] ) > tolerance ) return false;
		return true;
	}

	// What is on the upper row moved in towards the middle by the factor, on the row's front and
	// as far below its edge, see Lengthen.
	static void Squeeze( Vec3[] fitted, bool[] upper, Row row, float f )
	{
		if ( f >= 1f ) return;
		for ( int i = 0; i < fitted.Length; i++ )
		{
			if ( !upper[i] ) continue;
			var p = fitted[i];
			float u = (p.Y - row.Front.Y) / row.Width, v = u * f;
			fitted[i] = new Vec3( p.X + Row.At( row.Ahead, v ) - Row.At( row.Ahead, u ), row.Front.Y + v * row.Width, p.Z + Row.At( row.Edge, v ) - Row.At( row.Edge, u ) );
		}
	}

	/// <summary>
	/// Fangs made to show with the mouth open as much as on the stock body, see <see cref="Settle"/>.
	/// On an anime face the open lips hardly part, and a fang long enough to show between them
	/// stood out through the lower lip when they closed; leant back a little it goes behind it.
	/// With the mouth open the garment goes the way its row goes at its closest point (as
	/// <see cref="Morphs"/> moves it), and a bit of a fang shows if nothing of the body is in
	/// front of it; the fangs are looked at in points spread over their triangles, so a fang
	/// that crosses the opening between its tip and root still counts. Tried: fangs from half
	/// to three times as long, and the upper row's garment moved in towards the middle (a
	/// human's or an anime girl's teeth reach well past the corners of the open mouth, the
	/// citizen's hardly do, and fangs as far round the row stood in the corners, half behind the
	/// cheek). What shows closest to the stock body's share wins, the least change among those
	/// as good; what comes out through the closed lips more than on the stock body counts
	/// heavily against.
	/// </summary>
	static void Lengthen( Vec3[] made, int[] tris, Vec3[] fitted, bool[] upper, Row from, TriMesh stockShut, Vec3[] stockRest, Vec3[] stockOpen, int[] stockTris, Row to, TriMesh shut, Vec3[] rest, Vec3[] open, int[] bodyTris )
	{
		var fromFang = FangTriangles( made, tris, upper, from );
		if ( fromFang.Count == 0 ) return;
		float margin = Touch * to.Width, stockMargin = Touch * from.Width;
		var fromPoints = Samples( made, tris, fromFang );
		float want = Showing( Carried( fromPoints, from, stockRest, stockOpen ), new TriMesh( stockOpen, stockTris ), stockMargin );
		float wantShut = Showing( fromPoints, stockShut, stockMargin, true );

		var openMesh = new TriMesh( open, bodyTris );
		var tried = new Vec3[fitted.Length];
		float bestIn = 1f, bestLonger = 1f, bestBack = 0f, bestOff = float.MaxValue;
		int ins = (int)MathF.Round( (1f - LeastIn) / InStep ), lengths = (int)MathF.Round( (MostLonger - LeastLonger) / LongerStep );
		for ( int a = 0; a <= ins; a++ )
			for ( int b = 0; b <= lengths; b++ )
				for ( int c = 0; c < Backs.Length; c++ )
			{
				float f = 1f - a * InStep, longer = LeastLonger + b * LongerStep, back = Backs[c];
				Array.Copy( fitted, tried, fitted.Length );
				Shape( tried, upper, to, f, longer, back );
				var fang = FangTriangles( tried, tris, upper, to );
				if ( fang.Count == 0 ) continue;
				var points = Samples( tried, tris, fang );
				float showing = Showing( Carried( points, to, rest, open ), openMesh, margin );
				// Out through the closed lips counts worst.
				float through = MathF.Max( 0f, Showing( points, shut, margin, true ) - wantShut );
				float off = MathF.Max( 0f, MathF.Abs( showing - want ) - Near ) + Shut * through + LongerCost * (MathF.Abs( longer - 1f ) + 2f * (1f - f) + back);
				if ( off < bestOff ) { bestOff = off; bestIn = f; bestLonger = longer; bestBack = back; }
			}
		Shape( fitted, upper, to, bestIn, bestLonger, bestBack );
	}

	// The upper row's garment moved in towards the middle by f, and what hangs past the row's
	// edge made longer by the factor and leant back by so much for each unit it hangs down.
	static void Shape( Vec3[] points, bool[] upper, Row row, float f, float longer, float back )
	{
		Squeeze( points, upper, row, f );
		for ( int i = 0; i < points.Length; i++ )
		{
			if ( !upper[i] ) continue;
			float b = Row.At( row.Edge, (points[i].Y - row.Front.Y) / row.Width ) - points[i].Z;
			if ( b <= Fang * row.Width ) continue;
			points[i].Z -= b * (longer - 1f);
			points[i].X -= b * longer * back;
		}
	}

	// Triangles of the upper row's garment with a corner past its biting edge.
	static List<int> FangTriangles( Vec3[] points, int[] tris, bool[] upper, Row row )
	{
		var result = new List<int>();
		for ( int t = 0; t + 2 < tris.Length; t += 3 )
			for ( int k = 0; k < 3; k++ )
			{
				int v = tris[t + k];
				if ( !upper[v] || Row.At( row.Edge, (points[v].Y - row.Front.Y) / row.Width ) - points[v].Z <= Fang * row.Width ) continue;
				result.Add( t );
				break;
			}
		return result;
	}

	static readonly (float A, float B, float C)[] Spread = { (1f, 0f, 0f), (0f, 1f, 0f), (0f, 0f, 1f), (1f / 3, 1f / 3, 1f / 3), (0.6f, 0.2f, 0.2f), (0.2f, 0.6f, 0.2f), (0.2f, 0.2f, 0.6f) };

	// Points spread over triangles, a few on each.
	static Vec3[] Samples( Vec3[] points, int[] tris, List<int> triangles )
	{
		var result = new Vec3[triangles.Count * Spread.Length];
		int n = 0;
		foreach ( int t in triangles )
			foreach ( var (a, b, c) in Spread )
				result[n++] = points[tris[t]] * a + points[tris[t + 1]] * b + points[tris[t + 2]] * c;
		return result;
	}

	// Points where the open mouth puts them: moved as their row moves at their closest point.
	static Vec3[] Carried( Vec3[] points, Row row, Vec3[] rest, Vec3[] open )
	{
		var mesh = new TriMesh( rest, row.Tris );
		var result = new Vec3[points.Length];
		for ( int k = 0; k < points.Length; k++ )
		{
			var p = points[k];
			mesh.Nearest( p, out var at, out int t );
			if ( t < 0 ) { result[k] = p; continue; }
			int a = row.Tris[t * 3], b = row.Tris[t * 3 + 1], c = row.Tris[t * 3 + 2];
			GarmentFit.Barycentric( at, rest[a], rest[b], rest[c], out float u, out float v, out float w );
			result[k] = p + (open[a] - rest[a]) * u + (open[b] - rest[b]) * v + (open[c] - rest[c]) * w;
		}
		return result;
	}

	// The share of points seen from the front (slanted: or from a little above or below):
	// nothing of the body in front of them, or only skin they touch from behind.
	static float Showing( Vec3[] points, TriMesh body, float margin, bool slanted = false )
	{
		var hits = new List<(float T, bool Leaving, int Tri)>();
		var looks = slanted ? Slanted : Straight;
		int seen = 0;
		foreach ( var p in points )
			foreach ( var look in looks )
			{
				body.Crossings( p, look, Units.Metre, hits );
				if ( hits.Count == 0 || hits[0].T < margin ) { seen++; break; }
			}
		return points.Length > 0 ? seen / (float)points.Length : 0;
	}

	static readonly Vec3[] Straight = { new( 1, 0, 0 ) };
	static readonly Vec3[] Slanted = { new( 1, 0, 0 ), new( MathF.Cos( Tilt ), 0, MathF.Sin( Tilt ) ), new( MathF.Cos( Tilt ), 0, -MathF.Sin( Tilt ) ) };

	/// <summary>
	/// The morphs of a garment worn on the teeth: every vertex goes the way the row of teeth it
	/// is worn on goes, at its closest point, in every morph of the body. Teeth don't stretch
	/// with the lips' skin around them: they go as a whole, the lower row with the jaw, and an
	/// anime face's with the lips they show between. Taken off the skin, fangs went with the
	/// lips and came out through them.
	/// </summary>
	/// <param name="positions">The garment, fitted, in the body's rest pose.</param>
	/// <param name="upper">Per vertex, its row (<see cref="InMouth"/>).</param>
	/// <param name="bodyMesh">The body in the stock pose, where its teeth are found.</param>
	/// <param name="eyeLeft">The front of one of its eyes, in the stock pose.</param>
	/// <param name="eyeRight">The front of the other.</param>
	/// <param name="rest">The body's positions in its rest pose, those of its morphs.</param>
	/// <param name="skin">The body's morphs, a move and a turn per body vertex.</param>
	/// <param name="morphs">The garment's morphs, added to.</param>
	/// <param name="seen">Whether a move and a turn are big enough to keep.</param>
	/// <returns>False if this body has no teeth: the garment is left to take the morphs of the skin.</returns>
	public static bool Morphs( Vec3[] positions, bool[] upper, TriMesh bodyMesh, Vec3 eyeLeft, Vec3 eyeRight, Vec3[] rest,
		Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Func<Vec3, Vec3, bool> seen )
	{
		var body = Find( bodyMesh.Verts, bodyMesh.Tris, eyeLeft, eyeRight );
		if ( body == null ) return false;
		var meshes = new Dictionary<Row, TriMesh>();
		int n = positions.Length;
		var at = new (int A, int B, int C, float U, float V, float W)[n];
		for ( int v = 0; v < n; v++ )
		{
			var row = body[upper[v]];
			if ( !meshes.TryGetValue( row, out var mesh ) ) meshes[row] = mesh = new TriMesh( rest, row.Tris );
			mesh.Nearest( positions[v], out var p, out int t );
			if ( t < 0 ) { at[v] = (-1, 0, 0, 0, 0, 0); continue; }
			int a = row.Tris[t * 3], b = row.Tris[t * 3 + 1], c = row.Tris[t * 3 + 2];
			GarmentFit.Barycentric( p, rest[a], rest[b], rest[c], out float u, out float bv, out float w );
			at[v] = (a, b, c, u, bv, w);
		}
		foreach ( var (name, (move, turn)) in skin )
		{
			Vec3[] dp = null, dn = null;
			for ( int v = 0; v < n; v++ )
			{
				var (a, b, c, u, bv, w) = at[v];
				if ( a < 0 ) continue;
				var m = move[a] * u + move[b] * bv + move[c] * w;
				var r = turn[a] * u + turn[b] * bv + turn[c] * w;
				if ( !seen( m, r ) ) continue;
				dp ??= new Vec3[n];
				dn ??= new Vec3[n];
				dp[v] = m;
				dn[v] = r;
			}
			if ( dp != null ) morphs[name] = (dp, dn);
		}
		return true;
	}
}
