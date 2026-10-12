using System;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox.Autofit;

/// <summary>
/// The morph targets of one mesh, the way a compiled model stores them.
///
/// The deltas are not in the model file. They are pixels of a texture, one pixel per vertex,
/// packed into rectangles so that vertices a morph doesn't move take no space. Each rectangle
/// says where its pixels are in the texture and how to scale them back into a displacement.
/// </summary>
public sealed class MorphSet
{
	/// <summary>The texture with the deltas, as the model names it.</summary>
	public string AtlasPath;

	/// <summary>Vertices are laid out on a grid this wide: vertex number = y * Width + x.</summary>
	public int Width, Height;

	/// <summary>Where this mesh's vertices start in the merged geometry.</summary>
	public int FirstVertex, VertexCount;

	public List<Morph> Morphs = new();

	public sealed class Morph
	{
		public string Name;
		public List<Rect> Rects = new();
	}

	public sealed class Rect
	{
		public int X, Y;                 // top left corner on the vertex grid
		public float Width, Height;      // size, as a fraction of the texture
		public Bundle Position, Normal;  // either can be missing
	}

	public sealed class Bundle
	{
		public float U, V;               // top left corner in the texture, as a fraction of it
		public float[] Offsets = new float[4];
		public float[] Ranges = new float[4];
	}

	/// <summary>
	/// The deltas of every morph, read out of the texture's pixels (RGBA, four bytes each, top
	/// row first). Vertex numbers are those of the merged geometry.
	/// </summary>
	/// <param name="wanted">If given, only the morphs it takes by name are read.</param>
	public Dictionary<string, List<(int Vertex, Vec3 Position, Vec3 Normal)>> Decode( byte[] rgba, int textureWidth, int textureHeight, Func<string, bool> wanted = null )
	{
		var result = new Dictionary<string, List<(int, Vec3, Vec3)>>();
		foreach ( var morph in Morphs )
		{
			if ( wanted != null && !wanted( morph.Name ) ) continue;
			var position = new Dictionary<int, Vec3>();
			var normal = new Dictionary<int, Vec3>();
			foreach ( var rect in morph.Rects )
			{
				int columns = (int)MathF.Round( rect.Width * textureWidth );
				int rows = (int)MathF.Round( rect.Height * textureHeight );
				Read( rect, rect.Position, columns, rows, rgba, textureWidth, textureHeight, position );
				Read( rect, rect.Normal, columns, rows, rgba, textureWidth, textureHeight, normal );
			}

			var deltas = new List<(int, Vec3, Vec3)>();
			foreach ( var (vertex, move) in position )
			{
				normal.TryGetValue( vertex, out var turn );
				if ( move.LengthSquared() < 1e-10f && turn.LengthSquared() < 1e-10f ) continue;
				deltas.Add( (FirstVertex + vertex, move, turn) );
			}

			if ( deltas.Count > 0 )
				result[morph.Name] = deltas;
		}

		return result;
	}

	void Read( Rect rect, Bundle bundle, int columns, int rows, byte[] rgba, int textureWidth, int textureHeight, Dictionary<int, Vec3> into )
	{
		if ( bundle == null ) return;

		int left = (int)MathF.Round( bundle.U * textureWidth );
		int top = (int)MathF.Round( bundle.V * textureHeight );
		for ( int row = 0; row < rows; row++ )
		{
			for ( int column = 0; column < columns; column++ )
			{
				int x = left + column, y = top + row;
				int vertex = (rect.Y + row) * Width + rect.X + column;
				if ( x >= textureWidth || y >= textureHeight || vertex < 0 || vertex >= VertexCount ) continue;

				int at = (y * textureWidth + x) * 4;
				into[vertex] = new Vec3(
					rgba[at] / 255f * bundle.Ranges[0] + bundle.Offsets[0],
					rgba[at + 1] / 255f * bundle.Ranges[1] + bundle.Offsets[1],
					rgba[at + 2] / 255f * bundle.Ranges[2] + bundle.Offsets[2] );
			}
		}
	}
}

/// <summary>
/// A garment's morphs are made for the stock face: a beard's jaw drops so far and its lips
/// spread so wide. On a face of other proportions they open the beard where the mouth isn't.
/// So a garment on a face takes, for every morph of the body, the move of the skin under it,
/// in full close to the skin and less further off, where the garment's own morph of that name
/// takes over.
/// </summary>
public static class GarmentMorphs
{
	const float Full = 0.015f * Units.Metre;        // a garment this close to the skin moves with it in full
	const float Reach = 0.04f * Units.Metre;        // and from this far keeps its own morphs
	const float SpreadMin = 0.003f * Units.Metre;   // the least skin round its closest point a garment vertex takes the move of
	const float Contact = 0.03f * Units.Metre;      // how close to the skin a solid object is held by it

	/// <param name="positions">The garment, fitted, in the body's rest pose (the space of the body's morphs).</param>
	/// <param name="solid">Per garment vertex, whether it is part of a solid object (<see cref="GarmentFit.Fit"/>). Can be null.</param>
	/// <param name="skin">The body's morphs, a move and a turn per body vertex.</param>
	/// <param name="morphs">The garment's own morphs, a move and a turn per garment vertex. Changed in place, and the body's morphs the garment lacks are added.</param>
	/// <param name="seen">Whether a move and a turn are big enough to keep.</param>
	/// <param name="turnedAsMade">Per own morph, the triangles it turns over on the garment as made (see <see cref="TurnedOver"/>). Those are left as they are.</param>
	/// <param name="shapes">Which of the body's morphs shape the face for good (a stronger jaw, the eyes further apart). Can be null.</param>
	/// <param name="bent">Per garment vertex, whether it is on a solid object the fit bent to the body (see <see cref="BentSolids"/>). Those go with the skin under them in the morphs <paramref name="shapes"/> takes, as cloth does. Can be null.</param>
	public static void TakeFromSkin( Vec3[] positions, int[] tris, bool[] solid, Vec3[] bodyPositions, int[] bodyTris,
		Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Func<Vec3, Vec3, bool> seen,
		Dictionary<string, bool[]> turnedAsMade, Func<string, bool> shapes = null, bool[] bent = null )
	{
		if ( skin.Count == 0 ) return;

		// A garment with no morphs of its own (a mask on a face shaped by morphs) only needs the
		// vertices the skin's moves can reach: a vertex takes the move of the skin no further
		// than twice the reach from it. Most garments are nowhere near the face, and this keeps
		// them from paying for it.
		bool[] reachable = morphs.Count == 0 ? Reachable( positions, bodyPositions, skin ) : null;
		if ( reachable != null && Array.IndexOf( reachable, true ) < 0 ) return;

		var (skinTris, area, surface) = SkinOf( bodyPositions, bodyTris );
		if ( skinTris.Length == 0 ) return;

		// Body vertices by cell, for the skin round a point.
		var cells = new Dictionary<(int, int, int), List<int>>();
		const float cell = 0.01f * Units.Metre;
		(int, int, int) CellOf( Vec3 p ) => ((int)MathF.Floor( p.X / cell ), (int)MathF.Floor( p.Y / cell ), (int)MathF.Floor( p.Z / cell ));
		for ( int b = 0; b < bodyPositions.Length; b++ )
		{
			if ( area[b] <= 0 ) continue;
			var key = CellOf( bodyPositions[b] );
			if ( !cells.TryGetValue( key, out var list ) ) cells[key] = list = new List<int>();
			list.Add( b );
		}

		// Per garment vertex: how much it goes with the skin, and the skin it goes with. Skin
		// isn't moved by a morph the same everywhere under a garment held off it: an eyelid
		// drops in a blink and the cheek under it doesn't, a lip goes up and the one under it
		// down. Taken from the one closest point, neighbouring bits of a mask over the eye
		// followed the lid and the cheek each, crossed and turned inside out. So the move is the
		// skin's under the vertex averaged over as wide a patch as the vertex is far from it:
		// on the skin it is the skin's own, and a mask spanning an eye goes the way the eye
		// socket does as a whole.
		int n = positions.Length;
		var share = new float[n];
		var near = new List<(int Body, float Weight)>[n];
		for ( int v = 0; v < n; v++ )
		{
			if ( reachable != null && !reachable[v] ) continue;
			float d = surface.Nearest( positions[v], out var at, out int tri );
			if ( tri < 0 ) continue;
			share[v] = Math.Clamp( (Reach - d) / (Reach - Full), 0f, 1f );
			if ( share[v] <= 0 ) continue;

			var list = new List<(int, float)>();
			float spread = MathF.Max( d, SpreadMin ), sigma2 = 2f * (spread * 0.5f) * (spread * 0.5f);
			int a = skinTris[tri * 3], b = skinTris[tri * 3 + 1], c = skinTris[tri * 3 + 2];
			GarmentFit.Barycentric( at, bodyPositions[a], bodyPositions[b], bodyPositions[c], out float u, out float bv, out float bw );
			// The closest point itself, as much as a vertex of the skin right there.
			float own = (area[a] + area[b] + area[c]) / 3f;
			list.Add( (a, own * u) ); list.Add( (b, own * bv) ); list.Add( (c, own * bw) );
			var (cx, cy, cz) = CellOf( at );
			int r = (int)MathF.Ceiling( spread / cell );
			for ( int dx = -r; dx <= r; dx++ )
				for ( int dy = -r; dy <= r; dy++ )
					for ( int dz = -r; dz <= r; dz++ )
					{
						if ( !cells.TryGetValue( (cx + dx, cy + dy, cz + dz), out var inCell ) ) continue;
						foreach ( int k in inCell )
						{
							float d2 = (bodyPositions[k] - at).LengthSquared();
							if ( d2 > spread * spread ) continue;
							list.Add( (k, area[k] * MathF.Exp( -d2 / sigma2 )) );
						}
					}
			float total = 0;
			foreach ( var (_, w) in list ) total += w;
			if ( total <= 0 ) { share[v] = 0; continue; }
			for ( int i = 0; i < list.Count; i++ ) list[i] = (list[i].Item1, list[i].Item2 / total);
			near[v] = list;
		}

		foreach ( var (name, (move, turn)) in skin )
		{
			Vec3[] carried = null, carriedTurn = null;
			bool any = false;
			for ( int v = 0; v < n; v++ )
			{
				if ( near[v] == null ) continue;
				var dp = Vec3.Zero;
				var dn = Vec3.Zero;
				foreach ( var (b, w) in near[v] ) { dp += move[b] * w; dn += turn[b] * w; }
				if ( dp.LengthSquared() == 0 && dn.LengthSquared() == 0 ) continue;
				carried ??= new Vec3[n];
				carriedTurn ??= new Vec3[n];
				carried[v] = dp;
				carriedTurn[v] = dn;
				if ( seen( dp * share[v], dn * share[v] ) ) any = true;
			}
			if ( carried == null ) continue;
			if ( !morphs.TryGetValue( name, out var mine ) )
			{
				if ( !any ) continue;
				morphs[name] = mine = (new Vec3[n], new Vec3[n]);
			}
			for ( int v = 0; v < n; v++ )
			{
				if ( share[v] <= 0 ) continue;
				mine.Position[v] = mine.Position[v] * (1 - share[v]) + carried[v] * share[v];
				mine.Normal[v] = mine.Normal[v] * (1 - share[v]) + carriedTurn[v] * share[v];
			}
		}

		if ( morphs.Count > 0 ) Settle( positions, tris, solid, surface, morphs, turnedAsMade, shapes, bent );
	}

	const float LashPatch = 0.004f * Units.Metre;      // the lid round a lash card's root that the card moves and turns with
	const float LashTogether = 0.012f * Units.Metre;   // and cards rooted this close turn alike
	const float LidMargin = 0.016f * Units.Metre;   // how far round the lashes or the paint on the lids the skin is looked at
	const float LidMaterial = 0.33f;                // a material with this share of the area of the one the lids are drawn with or more is skin there

	/// <summary>
	/// Lashes take the body's morphs card by card. A card stands off the lid it grows from, its
	/// tip out in front of the eye. A garment without morphs of its own takes only the morphs
	/// that shape the face (see <see cref="TakeFromSkin"/>), so on a body that blinks by morphs
	/// the lid shut and the lashes stayed where it had been. Taken vertex by vertex off the skin
	/// round each, the root would go with the lid and the tip with the skin closest to it, and a
	/// blink would crush the card. So each card takes every morph of the body the way the patch
	/// of lid at its root moves in it, moved and turned as a whole, as the citizen's lashes go
	/// with its lid bones.
	/// </summary>
	/// <param name="positions">The garment, fitted, in the body's rest pose (the space of the body's morphs).</param>
	/// <param name="normals">The garment's normals there.</param>
	/// <param name="roots">Per garment vertex, the vertex at the root of its card (see <see cref="GarmentFit.LashRoots"/>), or -1.</param>
	/// <param name="bodyDraws">The body's draws, to tell its eyes from its skin (see <see cref="LidSkin"/>). Can be null.</param>
	/// <param name="skin">The body's morphs, a move and a turn per body vertex.</param>
	/// <param name="morphs">The garment's morphs. Those of the body are set on the cards, and added if the garment lacks them.</param>
	/// <param name="seen">Whether a move and a turn are big enough to keep.</param>
	public static void LashesFromSkin( Vec3[] positions, Vec3[] normals, int[] roots, Vec3[] bodyPositions, int[] bodyTris, List<(int First, int Count, string Material)> bodyDraws,
		Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Func<Vec3, Vec3, bool> seen )
	{
		if ( skin.Count == 0 ) return;
		var cards = new Dictionary<int, List<int>>();
		for ( int v = 0; v < roots.Length; v++ )
		{
			if ( roots[v] < 0 ) continue;
			if ( !cards.TryGetValue( roots[v], out var card ) ) cards[roots[v]] = card = new List<int>();
			card.Add( v );
		}
		if ( cards.Count == 0 ) return;
		var rootsAt = cards.Keys.Select( r => positions[r] ).ToList();
		var (lidTris, area, surface) = LidSkin( rootsAt, rootsAt, bodyPositions, bodyTris, bodyDraws );
		if ( surface == null ) return;
		var lidVerts = new List<int>();
		var seenVert = new HashSet<int>();
		foreach ( int b in lidTris ) if ( seenVert.Add( b ) ) lidVerts.Add( b );

		// Per card: the closest point of the lid to its root and the patch of lid round it, its
		// vertices and its triangles, as wide as the root is off the skin and no narrower than
		// LashPatch. A patch of lid is close to flat, and the points of it alone barely tell how
		// it turns, so the way the surface faces counts as well.
		var patches = new List<(List<int> Card, Vec3 At, int A, int B, int C, float U, float V, float W, float Reach, List<(int Body, float Weight)> Points, List<(int Tri, float Weight)> Faces)>();
		foreach ( var (root, card) in cards )
		{
			float d = surface.Nearest( positions[root], out var at, out int tri );
			if ( tri < 0 ) continue;
			int a = lidTris[tri * 3], b = lidTris[tri * 3 + 1], c = lidTris[tri * 3 + 2];
			GarmentFit.Barycentric( at, bodyPositions[a], bodyPositions[b], bodyPositions[c], out float u, out float bv, out float bw );
			float reach = MathF.Max( LashPatch, 2f * d ), sigma2 = 2f * (reach * 0.5f) * (reach * 0.5f);
			var points = new List<(int, float)> { (a, area[a] * u), (b, area[b] * bv), (c, area[c] * bw) };
			foreach ( int k in lidVerts )
			{
				float d2 = (bodyPositions[k] - at).LengthSquared();
				if ( d2 <= reach * reach ) points.Add( (k, area[k] * MathF.Exp( -d2 / sigma2 )) );
			}
			var faces = new List<(int, float)> { (tri * 3, 1f) };
			for ( int t = 0; t < lidTris.Length; t += 3 )
			{
				if ( t == tri * 3 ) continue;
				var middle = (bodyPositions[lidTris[t]] + bodyPositions[lidTris[t + 1]] + bodyPositions[lidTris[t + 2]]) * (1f / 3f);
				float d2 = (middle - at).LengthSquared();
				if ( d2 <= reach * reach ) faces.Add( (t, MathF.Exp( -d2 / sigma2 )) );
			}
			patches.Add( (card, at, a, b, c, u, bv, bw, reach, points, faces) );
		}

		// Cards near each other turn alike: a small patch of a lid that a blink folds can turn
		// another way than the one next to it, and the cards fanned out every which way.
		var together = new List<(int Card, float Weight)>[patches.Count];
		float sigma = LashTogether * 0.5f;
		for ( int i = 0; i < patches.Count; i++ )
		{
			together[i] = new List<(int, float)>();
			for ( int j = 0; j < patches.Count; j++ )
			{
				float d2 = (patches[j].At - patches[i].At).LengthSquared();
				if ( d2 <= LashTogether * LashTogether ) together[i].Add( (j, MathF.Exp( -d2 / (2f * sigma * sigma) )) );
			}
		}

		// Which way the lid faces under each card.
		var facings = new Vec3[patches.Count];
		for ( int i = 0; i < patches.Count; i++ )
		{
			var facing = Vec3.Zero;
			foreach ( var (t, w) in patches[i].Faces )
			{
				int a = lidTris[t], b = lidTris[t + 1], c = lidTris[t + 2];
				facing += Vec3.Cross( bodyPositions[b] - bodyPositions[a], bodyPositions[c] - bodyPositions[a] ) * w;
			}
			facings[i] = facing;
		}

		int n = positions.Length;
		var turns = new float[patches.Count][];
		var sum = new float[9];
		foreach ( var (name, (move, turn)) in skin )
		{
			// Per card, how the patch of lid at its root turns: the sums of its points' products
			// about where the patch is before and after (see GarmentFit.Rotation). A patch the
			// morph doesn't move counts too, as not turning: next to it the cards turn less.
			bool movesAny = false;
			for ( int i = 0; i < patches.Count; i++ )
			{
				var (_, _, _, _, _, _, _, _, reach, points, faces) = patches[i];
				turns[i] = null;
				float total = 0;
				Vec3 from = Vec3.Zero, to = Vec3.Zero;
				bool moved = false;
				foreach ( var (k, w) in points )
				{
					total += w;
					from += bodyPositions[k] * w;
					to += (bodyPositions[k] + move[k]) * w;
					moved |= move[k].LengthSquared() > 0;
				}
				if ( total <= 0 ) continue;
				movesAny |= moved;
				from /= total;
				to /= total;
				var h = turns[i] = new float[9];
				void Pair( Vec3 p, Vec3 q, float w )
				{
					h[0] += w * q.X * p.X; h[1] += w * q.X * p.Y; h[2] += w * q.X * p.Z;
					h[3] += w * q.Y * p.X; h[4] += w * q.Y * p.Y; h[5] += w * q.Y * p.Z;
					h[6] += w * q.Z * p.X; h[7] += w * q.Z * p.Y; h[8] += w * q.Z * p.Z;
				}
				foreach ( var (k, w) in points ) Pair( bodyPositions[k] - from, bodyPositions[k] + move[k] - to, w );
				Vec3 facing = Vec3.Zero, facingNow = Vec3.Zero;
				foreach ( var (t, w) in faces )
				{
					int a = lidTris[t], b = lidTris[t + 1], c = lidTris[t + 2];
					facing += Vec3.Cross( bodyPositions[b] - bodyPositions[a], bodyPositions[c] - bodyPositions[a] ) * w;
					facingNow += Vec3.Cross( bodyPositions[b] + move[b] - bodyPositions[a] - move[a], bodyPositions[c] + move[c] - bodyPositions[a] - move[a] ) * w;
				}
				float f0 = facing.Length(), f1 = facingNow.Length();
				if ( f0 > 1e-12f && f1 > 1e-12f ) Pair( facing * (reach / f0), facingNow * (reach / f1), total );
			}
			if ( !movesAny ) continue;

			// Each card moves with the spot of lid at its root and turns as the lid round it and
			// round the cards next to it does.
			var cardMove = new Vec3[n];
			var cardTurn = new Vec3[n];
			bool any = false;
			for ( int i = 0; i < patches.Count; i++ )
			{
				if ( turns[i] == null ) continue;
				var (card, at, a, b, c, u, bv, bw, _, _, _) = patches[i];
				Array.Clear( sum );
				foreach ( var (j, w) in together[i] )
					if ( turns[j] != null )
						for ( int k = 0; k < 9; k++ ) sum[k] += turns[j][k] * w;
				var r = Swing( GarmentFit.Rotation( sum ), facings[i] );
				Vec3 Turn( Vec3 x ) => r == null ? x : new Vec3( r[0] * x.X + r[1] * x.Y + r[2] * x.Z, r[3] * x.X + r[4] * x.Y + r[5] * x.Z, r[6] * x.X + r[7] * x.Y + r[8] * x.Z );
				var shift = move[a] * u + move[b] * bv + move[c] * bw;
				foreach ( int v in card )
				{
					cardMove[v] = at + shift + Turn( positions[v] - at ) - positions[v];
					cardTurn[v] = normals != null ? Turn( normals[v] ) - normals[v] : Vec3.Zero;
					if ( seen( cardMove[v], cardTurn[v] ) ) any = true;
				}
			}
			if ( !morphs.TryGetValue( name, out var mine ) )
			{
				if ( !any ) continue;
				morphs[name] = mine = (new Vec3[n], new Vec3[n]);
			}
			foreach ( var patch in patches )
				foreach ( int v in patch.Card )
				{
					mine.Position[v] = cardMove[v];
					mine.Normal[v] = cardTurn[v];
				}
		}
	}

	// Of a turn (a row-major rotation), only what tips the given direction over: the turn about
	// the axis square to it and to where it goes. A lid shutting tips its lashes down over the
	// eye; the twist about the lid's own facing that a fold at the corner of an anime eye adds
	// stood the cards there on end.
	static float[] Swing( float[] r, Vec3 facing )
	{
		float length = facing.Length();
		if ( r == null || length < 1e-12f ) return r;
		var from = facing / length;
		var to = new Vec3( r[0] * from.X + r[1] * from.Y + r[2] * from.Z, r[3] * from.X + r[4] * from.Y + r[5] * from.Z, r[6] * from.X + r[7] * from.Y + r[8] * from.Z );
		var axis = Vec3.Cross( from, to );
		float sin = axis.Length(), cos = Vec3.Dot( from, to );
		if ( sin < 1e-6f ) return cos > 0 ? new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 } : r;
		var k = axis / sin;
		float t = 1 - cos;
		return new[]
		{
			cos + k.X * k.X * t, k.X * k.Y * t - k.Z * sin, k.X * k.Z * t + k.Y * sin,
			k.Y * k.X * t + k.Z * sin, cos + k.Y * k.Y * t, k.Y * k.Z * t - k.X * sin,
			k.Z * k.X * t - k.Y * sin, k.Z * k.Y * t + k.X * sin, cos + k.Z * k.Z * t,
		};
	}

	const float OwnBrowNear = 0.015f * Units.Metre;   // a piece of the body's own this close to the marks of a brow (FaceMarks) can be that brow
	const float OwnBrowOn = 0.006f * Units.Metre;     // and lies on a mark when this close to it
	const float BrowAlong = 0.2f;                      // a dressed brow goes with its own brow's moves this far along it, as a share of its length
	const float BrowPastFull = 0.004f * Units.Metre;    // past the end of the own brow, a shell under a dressed one goes with it in full this far
	const float BrowPastReach = 0.016f * Units.Metre;   // and with the skin from this far
	const float BrowAcrossFull = 0.013f * Units.Metre;  // across it, from the middle of the own brow, in full this far
	const float BrowAcrossReach = 0.019f * Units.Metre; // and with the skin from this far
	const float ShellGap = 0.0015f * Units.Metre;       // a piece of a brow no further off the skin than this anywhere is drawn on it
	const int ShellLeast = 12;                          // and has at least this many vertices (a single flat card has fewer)

	/// <summary>
	/// The body's own brows, one per side: the piece of its own (not skin, see <see cref="Skin"/>)
	/// lying on the skin where <see cref="FaceMarks"/> found the brow. Anime faces draw their brows
	/// so, and move them by their morphs over skin that stays put. Null if the body has none.
	/// </summary>
	/// <param name="posed">The body in the stock proportions, where the marks were found.</param>
	/// <param name="rest">The body's vertices in its rest pose, in the same order.</param>
	public static List<int>[] OwnBrows( List<(string Name, Vec3 Stock, Vec3 Body)> features, TriMesh posed, Vec3[] rest, int[] tris )
	{
		if ( features == null ) return null;
		var (_, area, _) = SkinOf( rest, tris );
		int[] weld = null, piece = null;
		var result = new List<int>[2];
		bool any = false;
		for ( int side = 0; side < 2; side++ )
		{
			string brow = side == 0 ? "browL" : "browR";
			var marks = new List<Vec3>();
			foreach ( var (name, _, body) in features )
			{
				if ( name != brow && !name.StartsWith( brow + "." ) ) continue;
				posed.Nearest( body, out var on, out int tri );
				if ( tri < 0 ) continue;
				int a = posed.Tris[tri * 3], b = posed.Tris[tri * 3 + 1], c = posed.Tris[tri * 3 + 2];
				GarmentFit.Barycentric( on, posed.Verts[a], posed.Verts[b], posed.Verts[c], out float u, out float v, out float w );
				marks.Add( rest[a] * u + rest[b] * v + rest[c] * w );
			}
			if ( marks.Count == 0 ) continue;
			if ( weld == null )
			{
				weld = MeshTools.Weld( rest, out int count );
				piece = GarmentFit.Islands( count, MeshTools.Edges( tris, weld ) );
			}

			// Pieces that aren't skin near the marks; the brow is the one lying on most of them
			// (its middle, top and both ends), not a line drawn in the fold of a lid under it.
			var near = new Dictionary<int, (List<int> Verts, float[] Closest)>();
			for ( int k = 0; k < rest.Length; k++ )
			{
				if ( area[k] > 0 ) continue;
				float closest = float.MaxValue;
				foreach ( var m in marks ) closest = MathF.Min( closest, (rest[k] - m).LengthSquared() );
				if ( closest > OwnBrowNear * OwnBrowNear ) continue;
				int p = piece[weld[k]];
				if ( !near.TryGetValue( p, out var entry ) )
				{
					var none = new float[marks.Count];
					Array.Fill( none, float.MaxValue );
					near[p] = entry = (new List<int>(), none);
				}
				entry.Verts.Add( k );
				for ( int i = 0; i < marks.Count; i++ ) entry.Closest[i] = MathF.Min( entry.Closest[i], (rest[k] - marks[i]).LengthSquared() );
			}
			int bestPiece = -1, bestScore = 0, bestCount = 0;
			foreach ( var (p, (verts, closest)) in near )
			{
				int score = closest.Count( d => d <= OwnBrowOn * OwnBrowOn );
				if ( score > bestScore || (score == bestScore && score > 0 && verts.Count > bestCount) ) { bestPiece = p; bestScore = score; bestCount = verts.Count; }
			}
			if ( bestPiece < 0 ) continue;
			var own = new List<int>();
			for ( int k = 0; k < rest.Length; k++ )
				if ( area[k] <= 0 && piece[weld[k]] == bestPiece ) own.Add( k );
			result[side] = own;
			any = true;
		}
		return any ? result : null;
	}

	// Per garment vertex, whether it is on a shell lying on the skin all over (a cap, a brow drawn
	// on), not on a card standing off it.
	static bool[] Shells( Vec3[] positions, int[] tris, Vec3[] bodyPositions, int[] bodyTris )
	{
		var result = new bool[positions.Length];
		var (_, _, surface) = SkinOf( bodyPositions, bodyTris );
		if ( surface == null ) return result;
		var weld = MeshTools.Weld( positions, out int count );
		var piece = GarmentFit.Islands( count, MeshTools.Edges( tris, weld ) );
		var size = new Dictionary<int, int>();
		var off = new Dictionary<int, float>();
		foreach ( int v in tris.Distinct() )
		{
			int p = piece[weld[v]];
			size[p] = (size.TryGetValue( p, out int k ) ? k : 0) + 1;
			off[p] = MathF.Max( off.TryGetValue( p, out float d ) ? d : 0f, surface.Nearest( positions[v], out _, out _ ) );
		}
		for ( int v = 0; v < positions.Length; v++ )
		{
			int p = piece[weld[v]];
			result[v] = size.TryGetValue( p, out int k ) && k >= ShellLeast && off[p] <= ShellGap;
		}
		return result;
	}

	/// <summary>
	/// A dressed brow goes the way the body's own brow does, in every morph. An anime face moves
	/// its brows as pieces of their own over skin that stays put, and under the lower edge of a
	/// brow there is the top of a lid that a blink moves two centimetres: taken off the skin, the
	/// part of a dressed brow over the lid went with it and the rest stayed, so it was stretched
	/// by a blink and stuck while the face frowned or raised its brows. Each vertex takes the
	/// move of the own brow at the same point along it, from the inner end to the outer one, so the
	/// brow keeps its width and bends along its length as the own one does. Between the inner ends
	/// (a monobrow) it goes from one brow's move to the other's. A shell drawn on the skin under
	/// it (the cap a brow's hairs grow from) goes back to the skin's moves past the own brow's
	/// ends and away from it across, where it is only the see-through patch round the brow.
	/// </summary>
	/// <param name="positions">The garment, fitted, in the body's rest pose.</param>
	/// <param name="own">The body's own brows, see <see cref="OwnBrows"/>.</param>
	/// <param name="skin">The body's morphs, a move and a turn per body vertex.</param>
	/// <param name="morphs">The garment's morphs, changed in place; those of the body it lacks are added.</param>
	/// <param name="seen">Whether a move and a turn are big enough to keep.</param>
	public static void BrowsFromOwn( Vec3[] positions, int[] tris, List<int>[] own, Vec3[] bodyPositions, int[] bodyTris,
		Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Func<Vec3, Vec3, bool> seen )
	{
		// Per own brow: its middle, the line along it, outwards from the middle of the face, and
		// how far along that line each of its vertices is.
		var middle = new Vec3[2];
		var along = new Vec3[2];
		var at = new float[2][];
		float[] first = new float[2], last = new float[2];
		int sides = 0;
		var face = Vec3.Zero;
		for ( int s = 0; s < 2; s++ )
		{
			if ( own[s] == null || own[s].Count < 3 ) continue;
			var c = Vec3.Zero;
			foreach ( int k in own[s] ) c += bodyPositions[k];
			middle[s] = c / own[s].Count;
			face += middle[s];
			sides++;
		}
		if ( sides == 0 ) return;
		face /= sides;
		for ( int s = 0; s < 2; s++ )
		{
			if ( own[s] == null || own[s].Count < 3 ) continue;
			// The longest way across it, as the inner and outer ends: the main axis of its vertices.
			var axis = new Vec3( 0, s == 0 ? 1 : -1, 0 );
			for ( int pass = 0; pass < 8; pass++ )
			{
				var next = Vec3.Zero;
				foreach ( int k in own[s] ) { var d = bodyPositions[k] - middle[s]; next += d * Vec3.Dot( d, axis ); }
				float l = next.Length();
				if ( l < 1e-12f ) break;
				axis = next / l;
			}
			if ( sides == 2 ? Vec3.Dot( axis, middle[s] - face ) < 0 : axis.Y * (s == 0 ? 1 : -1) < 0 ) axis = -axis;
			along[s] = axis;
			at[s] = own[s].Select( k => Vec3.Dot( bodyPositions[k] - middle[s], axis ) ).ToArray();
			first[s] = at[s].Min();
			last[s] = at[s].Max();
		}

		// Per garment vertex: which brow, how far along it, between the two inner ends how much
		// of the other one, and how much it goes with the brow at all.
		int n = positions.Length;
		var side = new int[n];
		var t = new float[n];
		var other = new float[n];
		var with = new float[n];
		var shell = Shells( positions, tris, bodyPositions, bodyTris );
		for ( int v = 0; v < n; v++ )
		{
			int s = sides == 2 ? (Vec3.Dot( positions[v] - face, middle[0] - face ) >= 0 ? 0 : 1) : (own[0] != null && own[0].Count >= 3 ? 0 : 1);
			side[v] = s;
			float x = Vec3.Dot( positions[v] - middle[s], along[s] );
			t[v] = Math.Clamp( x, first[s], last[s] );

			// A shell drawn on the skin (the cap under a brow's hairs, a brow drawn on), past the
			// outer end of the own brow, or past the inner one with no other brow to go to, and
			// away from the brow across it: there it is no longer brow but the patch a brow is
			// drawn on, see-through, and it goes back to the skin's moves. Moved with the brow as
			// far out as it reaches, its corners rose up the forehead past the brow's ends. Hair
			// cards go with the brow wherever they reach.
			with[v] = 1f;
			if ( shell[v] )
			{
				float past = x > last[s] ? x - last[s] : sides == 1 && x < first[s] ? first[s] - x : 0f;
				var centre = Vec3.Zero;
				float total = 0, sigma = MathF.Max( BrowAlong * (last[s] - first[s]), 1e-4f * Units.Metre );
				for ( int i = 0; i < own[s].Count; i++ )
				{
					float d = at[s][i] - t[v], w = MathF.Exp( -d * d / (2f * sigma * sigma) );
					centre += bodyPositions[own[s][i]] * w;
					total += w;
				}
				var away = positions[v] - centre / total;
				away -= along[s] * Vec3.Dot( away, along[s] );
				with[v] = Math.Clamp( (BrowPastReach - past) / (BrowPastReach - BrowPastFull), 0f, 1f )
					* Math.Clamp( (BrowAcrossReach - away.Length()) / (BrowAcrossReach - BrowAcrossFull), 0f, 1f );
			}
			if ( sides == 2 && x < first[s] )
			{
				// Past the inner end, towards the other brow's: more and more with the other one,
				// half each halfway between them.
				var inner = middle[s] + along[s] * first[s];
				var otherInner = middle[1 - s] + along[1 - s] * first[1 - s];
				var gap = otherInner - inner;
				float g = gap.LengthSquared();
				other[v] = g > 1e-12f ? Math.Clamp( Vec3.Dot( positions[v] - inner, gap ) / g, 0f, 1f ) : 0f;
			}
		}

		// An own brow's move at a point along it: its vertices' moves near there.
		(Vec3 Move, Vec3 Turn) MoveAt( int s, float x, Vec3[] move, Vec3[] turn )
		{
			float sigma = MathF.Max( BrowAlong * (last[s] - first[s]), 1e-4f * Units.Metre );
			float total = 0;
			Vec3 m = Vec3.Zero, r = Vec3.Zero;
			for ( int i = 0; i < own[s].Count; i++ )
			{
				float d = at[s][i] - x, w = MathF.Exp( -d * d / (2f * sigma * sigma) );
				total += w;
				m += move[own[s][i]] * w;
				r += turn[own[s][i]] * w;
			}
			return total > 0 ? (m / total, r / total) : (Vec3.Zero, Vec3.Zero);
		}

		// The skin under each vertex that doesn't go with the brow all the way.
		var under = new (int A, int B, int C, float U, float V, float W)[n];
		var (skinTris, _, surface) = SkinOf( bodyPositions, bodyTris );
		for ( int v = 0; v < n; v++ )
		{
			under[v] = (-1, -1, -1, 0, 0, 0);
			if ( with[v] >= 1f || surface == null ) continue;
			surface.Nearest( positions[v], out var on, out int tri );
			if ( tri < 0 ) continue;
			int a = skinTris[tri * 3], b = skinTris[tri * 3 + 1], c = skinTris[tri * 3 + 2];
			GarmentFit.Barycentric( on, bodyPositions[a], bodyPositions[b], bodyPositions[c], out float u, out float bv, out float bw );
			under[v] = (a, b, c, u, bv, bw);
		}

		foreach ( var (name, (move, turn)) in skin )
		{
			var moves = new Vec3[n];
			var turns = new Vec3[n];
			bool any = false;
			for ( int v = 0; v < n; v++ )
			{
				int s = side[v];
				var (m, r) = MoveAt( s, t[v], move, turn );
				if ( other[v] > 0 )
				{
					var (m2, r2) = MoveAt( 1 - s, first[1 - s], move, turn );
					m = m * (1 - other[v]) + m2 * other[v];
					r = r * (1 - other[v]) + r2 * other[v];
				}
				if ( with[v] < 1f )
				{
					var (a, b, c, u, bv, bw) = under[v];
					var sm = a >= 0 ? move[a] * u + move[b] * bv + move[c] * bw : Vec3.Zero;
					var sr = a >= 0 ? turn[a] * u + turn[b] * bv + turn[c] * bw : Vec3.Zero;
					m = m * with[v] + sm * (1 - with[v]);
					r = r * with[v] + sr * (1 - with[v]);
				}
				moves[v] = m;
				turns[v] = r;
				any |= seen( m, r );
			}
			if ( !morphs.TryGetValue( name, out var mine ) )
			{
				if ( !any ) continue;
				morphs[name] = mine = (moves, turns);
				continue;
			}
			Array.Copy( moves, mine.Position, n );
			Array.Copy( turns, mine.Normal, n );
		}
	}

	const float LidMorph = 0.5f;                        // a morph that moves the edges of the lids this much of the most it moves the skin anywhere
	const float LidMorphLeast = 0.001f * Units.Metre;   // and at least this far moves the lids

	/// <summary>
	/// Paint (eyeliner, eyeshadow) goes with the skin under it in every morph that moves the lids:
	/// a blink, the eyes opening wide or squinting. <see cref="TakeFromSkin"/> averages the skin's
	/// move over a patch round each vertex and evens out whatever a morph turns over, which
	/// suits cloth held off the face. A lid shutting folds the skin at its edge and moves it two
	/// or three centimetres on an anime face, and paint there stayed several millimetres off the
	/// lid on the humans and up to two centimetres on Aurora. Paint is the skin's colour, so in
	/// those morphs it takes the move of the very spot it lies on. Run after TakeFromSkin; the
	/// other morphs are left as it made them.
	/// </summary>
	/// <param name="positions">The garment, fitted, in the body's rest pose.</param>
	/// <param name="painted">Per garment vertex, whether the fit laid it on the skin as paint.</param>
	/// <param name="lids">Points along the edges of the lids, in the body's rest pose (see <see cref="LidMarks"/>).</param>
	/// <param name="bodyDraws">The body's draws, to tell its eyes from its skin (see <see cref="LidSkin"/>). Can be null.</param>
	/// <param name="skin">The body's morphs, a move and a turn per body vertex.</param>
	/// <param name="morphs">The garment's morphs, changed in place; lid morphs of the body it lacks are added.</param>
	/// <param name="seen">Whether a move and a turn are big enough to keep.</param>
	/// <param name="shapes">Morphs that shape the face for good, left to TakeFromSkin even where they move the lids. Can be null.</param>
	public static void PaintFromSkin( Vec3[] positions, bool[] painted, Vec3[] lids, Vec3[] bodyPositions, int[] bodyTris, List<(int First, int Count, string Material)> bodyDraws,
		Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Func<Vec3, Vec3, bool> seen, Func<string, bool> shapes = null )
	{
		if ( skin.Count == 0 || lids == null || lids.Length == 0 ) return;
		var paint = new List<int>();
		for ( int v = 0; v < painted.Length; v++ ) if ( painted[v] ) paint.Add( v );
		if ( paint.Count == 0 ) return;
		var (lidTris, area, surface) = LidSkin( paint.Select( v => positions[v] ).Concat( lids ), lids, bodyPositions, bodyTris, bodyDraws );
		if ( surface == null ) return;

		(int A, int B, int C, float U, float V, float W) Spot( Vec3 p )
		{
			surface.Nearest( p, out var at, out int tri );
			if ( tri < 0 ) return (-1, -1, -1, 0, 0, 0);
			int a = lidTris[tri * 3], b = lidTris[tri * 3 + 1], c = lidTris[tri * 3 + 2];
			GarmentFit.Barycentric( at, bodyPositions[a], bodyPositions[b], bodyPositions[c], out float u, out float bv, out float bw );
			return (a, b, c, u, bv, bw);
		}
		var edges = lids.Select( Spot ).Where( x => x.A >= 0 ).ToList();
		var spots = paint.Select( v => (Vertex: v, Spot: Spot( positions[v] )) ).Where( x => x.Spot.A >= 0 ).ToList();

		int n = positions.Length;
		foreach ( var (name, (move, turn)) in skin )
		{
			if ( shapes != null && shapes( name ) ) continue;
			float most = 0, atLids = 0;
			for ( int b = 0; b < move.Length; b++ ) if ( area[b] > 0 ) most = MathF.Max( most, move[b].LengthSquared() );
			foreach ( var (a, b, c, u, bv, bw) in edges ) atLids = MathF.Max( atLids, (move[a] * u + move[b] * bv + move[c] * bw).LengthSquared() );
			if ( most <= 0 || atLids < LidMorph * LidMorph * most || atLids < LidMorphLeast * LidMorphLeast ) continue;

			bool any = false;
			foreach ( var (_, (a, b, c, u, bv, bw)) in spots )
				if ( seen( move[a] * u + move[b] * bv + move[c] * bw, turn[a] * u + turn[b] * bv + turn[c] * bw ) ) { any = true; break; }
			if ( !morphs.TryGetValue( name, out var mine ) )
			{
				if ( !any ) continue;
				morphs[name] = mine = (new Vec3[n], new Vec3[n]);
			}
			foreach ( var (v, (a, b, c, u, bv, bw)) in spots )
			{
				mine.Position[v] = move[a] * u + move[b] * bv + move[c] * bw;
				mine.Normal[v] = turn[a] * u + turn[b] * bv + turn[c] * bw;
			}
		}
	}

	/// <summary>
	/// The marks along the edges of the lids (<see cref="FaceMarks.Outline"/>), found on the body
	/// in the stock proportions, put on the body in its rest pose: the same spot of the same
	/// triangle.
	/// </summary>
	/// <param name="posed">The body in the stock proportions, where the marks were found.</param>
	/// <param name="rest">The body's vertices in its rest pose, in the same order.</param>
	public static Vec3[] LidMarks( List<(string Name, Vec3 Stock, Vec3 Body)> features, TriMesh posed, Vec3[] rest )
	{
		if ( features == null ) return null;
		var result = new List<Vec3>();
		foreach ( var (name, _, body) in features )
		{
			if ( !name.StartsWith( "eye" ) || Array.IndexOf( FaceMarks.Outline, name.Length > 4 ? name.Substring( 4 ) : "" ) < 0 ) continue;
			posed.Nearest( body, out var at, out int tri );
			if ( tri < 0 ) continue;
			int a = posed.Tris[tri * 3], b = posed.Tris[tri * 3 + 1], c = posed.Tris[tri * 3 + 2];
			GarmentFit.Barycentric( at, posed.Verts[a], posed.Verts[b], posed.Verts[c], out float u, out float v, out float w );
			result.Add( rest[a] * u + rest[b] * v + rest[c] * w );
		}
		return result.Count > 0 ? result.ToArray() : null;
	}

	/// <summary>
	/// The skin round some points (lashes, paint) without the eyes: the skin (see <see cref="Skin"/>)
	/// within <see cref="LidMargin"/> of their box, less what is drawn with a material much
	/// smaller than the one the lids are drawn with, the one most of the given points on the lids
	/// are closest to. An anime
	/// eye's white is welded to the face round it, so it is skin to <see cref="Skin"/>; a lid
	/// shuts over it and it stays put, and lashes and liner taken off it stayed put too. Also
	/// the area round each skin vertex.
	/// </summary>
	static (int[] Tris, float[] Area, TriMesh Surface) LidSkin( IEnumerable<Vec3> points, IEnumerable<Vec3> onLids, Vec3[] bodyPositions, int[] bodyTris, List<(int First, int Count, string Material)> draws )
	{
		var (_, area, _) = SkinOf( bodyPositions, bodyTris );
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		bool any = false;
		foreach ( var p in points )
		{
			any = true;
			lo = new Vec3( MathF.Min( lo.X, p.X ), MathF.Min( lo.Y, p.Y ), MathF.Min( lo.Z, p.Z ) );
			hi = new Vec3( MathF.Max( hi.X, p.X ), MathF.Max( hi.Y, p.Y ), MathF.Max( hi.Z, p.Z ) );
		}
		if ( !any ) return (Array.Empty<int>(), area, null);
		bool Near( Vec3 p ) => p.X >= lo.X - LidMargin && p.X <= hi.X + LidMargin && p.Y >= lo.Y - LidMargin && p.Y <= hi.Y + LidMargin && p.Z >= lo.Z - LidMargin && p.Z <= hi.Z + LidMargin;

		// Skin triangles near the points, with what each is drawn with. A triangle of a piece of
		// its own that isn't skin has no area on its corners.
		int count = bodyTris.Length / 3;
		var material = new int[count];
		var materialArea = new List<float>();
		var names = new Dictionary<string, int>();
		if ( draws != null )
			foreach ( var (first, length, name) in draws )
			{
				if ( !names.TryGetValue( name ?? "", out int id ) ) { names[name ?? ""] = id = names.Count; materialArea.Add( 0 ); }
				for ( int k = first; k + 2 < first + length && k / 3 < count; k += 3 )
				{
					material[k / 3] = id;
					Vec3 a = bodyPositions[bodyTris[k]], b = bodyPositions[bodyTris[k + 1]], c = bodyPositions[bodyTris[k + 2]];
					materialArea[id] += 0.5f * Vec3.Cross( b - a, c - a ).Length();
				}
			}
		var near = new List<int>();
		for ( int t = 0; t < count; t++ )
		{
			int a = bodyTris[t * 3], b = bodyTris[t * 3 + 1], c = bodyTris[t * 3 + 2];
			if ( area[a] <= 0 || area[b] <= 0 || area[c] <= 0 ) continue;
			if ( Near( bodyPositions[a] ) || Near( bodyPositions[b] ) || Near( bodyPositions[c] ) ) near.Add( t );
		}
		if ( near.Count == 0 ) return (Array.Empty<int>(), area, null);

		int[] Corners( List<int> tris )
		{
			var result = new int[tris.Count * 3];
			for ( int i = 0; i < tris.Count; i++ ) { result[i * 3] = bodyTris[tris[i] * 3]; result[i * 3 + 1] = bodyTris[tris[i] * 3 + 1]; result[i * 3 + 2] = bodyTris[tris[i] * 3 + 2]; }
			return result;
		}
		if ( draws == null || materialArea.Count < 2 )
		{
			var all = Corners( near );
			return (all, area, new TriMesh( bodyPositions, all ));
		}

		// The material most of the points on the lids are closest to is what the lids are drawn with.
		var mesh = new TriMesh( bodyPositions, Corners( near ) );
		var votes = new int[materialArea.Count];
		foreach ( var p in onLids )
		{
			mesh.Nearest( p, out _, out int tri );
			if ( tri >= 0 ) votes[material[near[tri]]]++;
		}
		int lid = 0;
		for ( int m = 1; m < votes.Length; m++ ) if ( votes[m] > votes[lid] ) lid = m;
		var kept = near.Where( t => materialArea[material[t]] >= LidMaterial * materialArea[lid] ).ToList();
		if ( kept.Count == near.Count ) return (Corners( near ), area, mesh);
		var corners = Corners( kept );
		return (corners, area, new TriMesh( bodyPositions, corners ));
	}

	// Per garment vertex, whether it is within twice the reach of the box round the skin that
	// any of the morphs moves. Null when that is all of them.
	static bool[] Reachable( Vec3[] positions, Vec3[] bodyPositions, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin )
	{
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var (move, turn) in skin.Values )
			for ( int b = 0; b < bodyPositions.Length; b++ )
			{
				if ( move[b].LengthSquared() == 0 && turn[b].LengthSquared() == 0 ) continue;
				var p = bodyPositions[b];
				lo = new Vec3( MathF.Min( lo.X, p.X ), MathF.Min( lo.Y, p.Y ), MathF.Min( lo.Z, p.Z ) );
				hi = new Vec3( MathF.Max( hi.X, p.X ), MathF.Max( hi.Y, p.Y ), MathF.Max( hi.Z, p.Z ) );
			}
		var result = new bool[positions.Length];
		if ( lo.X > hi.X ) return result;
		float grow = 2f * Reach;
		for ( int v = 0; v < positions.Length; v++ )
		{
			var p = positions[v];
			result[v] = p.X >= lo.X - grow && p.X <= hi.X + grow && p.Y >= lo.Y - grow && p.Y <= hi.Y + grow && p.Z >= lo.Z - grow && p.Z <= hi.Z + grow;
		}
		return result;
	}

	/// <summary>
	/// Per garment vertex, whether it is on a solid object (see <see cref="GarmentFit.Fit"/>)
	/// that the fit bent to the body rather than moving it whole: a gaiter or a respirator
	/// skinned to the head alone, laid over the face and bulged out where the face is bigger.
	/// Glasses, a gas mask's filter or a bead come out of the fit exactly as made, moved and
	/// turned; one the fit only pushed out of the skin here and there is off that by a
	/// millimetre or two.
	/// </summary>
	/// <param name="made">The garment as made.</param>
	/// <param name="fitted">The garment fitted, in the same proportions (the stock fit).</param>
	public static bool[] BentSolids( Vec3[] made, Vec3[] fitted, int[] tris, bool[] solid )
	{
		var result = new bool[made.Length];
		if ( solid == null || Array.IndexOf( solid, true ) < 0 ) return result;
		var weld = MeshTools.Weld( made, out int count );
		var piece = GarmentFit.Islands( count, MeshTools.Edges( tris, weld ) );
		var pieces = new Dictionary<int, List<int>>();
		for ( int v = 0; v < made.Length; v++ )
		{
			if ( !solid[v] ) continue;
			int p = piece[weld[v]];
			if ( !pieces.TryGetValue( p, out var list ) ) pieces[p] = list = new List<int>();
			list.Add( v );
		}
		foreach ( var idx in pieces.Values )
		{
			if ( BentBy( idx, made, fitted ) <= Bend ) continue;
			foreach ( int v in idx ) result[v] = true;
		}
		return result;
	}

	const float Bend = 0.002f * Units.Metre;   // a solid object off a whole move by more than this was bent by the fit

	/// <summary>
	/// Per triangle, whether a move turns it over. Some morphs do that on the garment as made,
	/// on purpose: one that closes the lips only ever goes on with one that opens the jaw.
	/// </summary>
	public static bool[] TurnedOver( Vec3[] positions, int[] tris, Vec3[] move )
	{
		var result = new bool[tris.Length / 3];
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			var before = Vec3.Cross( positions[b] - positions[a], positions[c] - positions[a] );
			var after = Vec3.Cross( positions[b] + move[b] - positions[a] - move[a], positions[c] + move[c] - positions[a] - move[a] );
			float lb = before.Length(), la = after.Length();
			result[t / 3] = lb >= 1e-12f && (la <= 1e-12f || Vec3.Dot( before, after ) < MeshTools.Turned * lb * la);
		}
		return result;
	}

	// The skin of the last body asked for. Telling skin from what lies on it takes over a second
	// on a whole body, and every garment on the face of that body, at every level of detail, asks
	// for the same one.
	static readonly object skinLock = new();
	static (Vec3[] Positions, int[] Tris, int[] SkinTris, float[] Area, TriMesh Surface) lastSkin;

	static (int[] Tris, float[] Area, TriMesh Surface) SkinOf( Vec3[] positions, int[] tris )
	{
		lock ( skinLock )
		{
			if ( lastSkin.Positions == positions && lastSkin.Tris == tris )
				return (lastSkin.SkinTris, lastSkin.Area, lastSkin.Surface);
		}
		var (skinTris, area) = Skin( positions, tris );
		var surface = skinTris.Length > 0 ? new TriMesh( positions, skinTris ) : null;
		lock ( skinLock ) lastSkin = (positions, tris, skinTris, area, surface);
		return (skinTris, area, surface);
	}

	/// <summary>
	/// The body's skin, the triangles of it that are skin and not something on it: lashes,
	/// brows, a painted line round an eye, the eyes themselves, the inside of the mouth. Those
	/// are pieces of their own, small next to the skin, and they move on their own: a lash
	/// swings a few centimetres in a blink, an anime brow goes up and down over a forehead that
	/// stays put. A garment lies on the skin. Also the area round each skin vertex (a third of
	/// its triangles'), zero for the rest.
	/// </summary>
	static (int[] Tris, float[] Area) Skin( Vec3[] positions, int[] tris )
	{
		var isSkin = MeshTools.SkinTriangles( positions, tris );
		var kept = new List<int>();
		var area = new float[positions.Length];
		for ( int t = 0; t < isSkin.Length; t++ )
		{
			if ( !isSkin[t] ) continue;
			Vec3 a = positions[tris[t * 3]], b = positions[tris[t * 3 + 1]], c = positions[tris[t * 3 + 2]];
			float third = 0.5f * Vec3.Cross( b - a, c - a ).Length() / 3f;
			for ( int k = 0; k < 3; k++ )
			{
				kept.Add( tris[t * 3 + k] );
				area[tris[t * 3 + k]] += third;
			}
		}
		return (kept.ToArray(), area);
	}

	/// <summary>
	/// What no morph may do to a garment: turn a bit of it inside out. A solid object (a cigar
	/// in the mouth) moves as a whole, the way the skin holding it moves; vertex by vertex, its
	/// end in the mouth went with the lips and its far end kept the stock face's move, and the
	/// cigar was torn. Cloth that a morph still turns over (a crumpled mask whose folds lie a
	/// few millimetres apart, its own jaw drop made for a longer face) has the morph's moves
	/// evened out with the neighbours there, and if that isn't enough, made smaller there.
	/// A solid object the fit bent to the face (<paramref name="bent"/>) is cloth to the morphs
	/// that shape the face for good: a gaiter moved whole by a jaw that widens to both sides
	/// stays where it was, and the jaw comes through it. The other morphs part the lips and
	/// shut the eyes, and would tear it there, so it still moves whole with those.
	/// </summary>
	static void Settle( Vec3[] positions, int[] tris, bool[] solid, TriMesh surface, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Dictionary<string, bool[]> turnedAsMade, Func<string, bool> shapes, bool[] bent )
	{
		int n = positions.Length;
		var weld = MeshTools.Weld( positions, out int count );
		var edges = MeshTools.Edges( tris, weld );
		var piece = GarmentFit.Islands( count, edges );

		// Solid objects, piece by piece, held by the skin closest to them most.
		var solids = new Dictionary<int, List<int>>();
		var held = new float[n];
		if ( solid != null )
		{
			for ( int v = 0; v < n; v++ )
			{
				if ( !solid[v] ) continue;
				int p = piece[weld[v]];
				if ( !solids.TryGetValue( p, out var list ) ) solids[p] = list = new List<int>();
				list.Add( v );
				held[v] = MathF.Exp( -surface.Nearest( positions[v], out _, out _ ) / Contact );
			}
		}

		// What stays solid in the face's shape morphs: only what the fit didn't bend.
		bool[] whole = solid;
		if ( solid != null && bent != null && shapes != null && Array.IndexOf( bent, true ) >= 0 )
		{
			whole = new bool[n];
			for ( int v = 0; v < n; v++ ) whole[v] = solid[v] && !bent[v];
		}

		var points = new Vec3[count];
		for ( int v = 0; v < n; v++ ) points[weld[v]] = positions[v];
		var welded = new Vec3[count];
		var weldedTurn = new Vec3[count];
		var hits = new int[count];
		var skip = new bool[tris.Length / 3];
		foreach ( var (name, (move, turn)) in morphs )
		{
			var keep = whole != solid && shapes( name ) ? whole : solid;
			foreach ( var idx in solids.Values )
				if ( keep[idx[0]] ) Rigid( idx, positions, held, move, turn );

			// Cloth, one value per welded vertex. Solid objects can't turn over now, and what the
			// morph turns over on the garment as made is meant.
			bool[] meant = turnedAsMade != null && turnedAsMade.TryGetValue( name, out var flags ) ? flags : null;
			for ( int t = 0; t < skip.Length; t++ )
				skip[t] = (meant != null && meant[t]) || (keep != null && (keep[tris[t * 3]] || keep[tris[t * 3 + 1]] || keep[tris[t * 3 + 2]]));
			Array.Clear( welded, 0, count );
			Array.Clear( weldedTurn, 0, count );
			Array.Clear( hits, 0, count );
			for ( int v = 0; v < n; v++ ) { welded[weld[v]] += move[v]; weldedTurn[weld[v]] += turn[v]; hits[weld[v]]++; }
			for ( int i = 0; i < count; i++ ) if ( hits[i] > 0 ) { welded[i] /= hits[i]; weldedTurn[i] /= hits[i]; }

			if ( !MeshTools.Untangle( points, tris, weld, edges, welded, weldedTurn, skip ) ) continue;
			for ( int v = 0; v < n; v++ )
			{
				if ( keep != null && keep[v] ) continue;
				move[v] = welded[weld[v]];
				turn[v] = weldedTurn[weld[v]];
			}
		}
	}

	/// <summary>
	/// How far the most bent vertex of a piece is from where moving and turning the piece whole
	/// would put it, going from one shape to the other (95th percentile, so a stray vertex
	/// doesn't count).
	/// </summary>
	public static float BentBy( IReadOnlyList<int> idx, Vec3[] from, Vec3[] to )
	{
		if ( idx.Count < 3 ) return 0;
		Vec3 a = Vec3.Zero, b = Vec3.Zero;
		foreach ( int v in idx ) { a += from[v]; b += to[v]; }
		a /= idx.Count;
		b /= idx.Count;
		var h = new float[9];
		foreach ( int v in idx )
		{
			Vec3 p = from[v] - a, q = to[v] - b;
			h[0] += q.X * p.X; h[1] += q.X * p.Y; h[2] += q.X * p.Z;
			h[3] += q.Y * p.X; h[4] += q.Y * p.Y; h[5] += q.Y * p.Z;
			h[6] += q.Z * p.X; h[7] += q.Z * p.Y; h[8] += q.Z * p.Z;
		}
		var r = GarmentFit.Rotation( h );
		var off = new float[idx.Count];
		for ( int i = 0; i < idx.Count; i++ )
		{
			Vec3 p = from[idx[i]] - a;
			if ( r != null ) p = new Vec3( r[0] * p.X + r[1] * p.Y + r[2] * p.Z, r[3] * p.X + r[4] * p.Y + r[5] * p.Z, r[6] * p.X + r[7] * p.Y + r[8] * p.Z );
			off[i] = (b + p - to[idx[i]]).Length();
		}
		Array.Sort( off );
		return off[(int)((off.Length - 1) * 0.95f)];
	}

	// The turn and shift that best carry a solid piece the way its vertices would each move,
	// those held by the skin counting most; every vertex then moves by that.
	static void Rigid( List<int> idx, Vec3[] positions, float[] held, Vec3[] move, Vec3[] turn )
	{
		float total = 0;
		Vec3 from = Vec3.Zero, to = Vec3.Zero;
		foreach ( int v in idx ) { total += held[v]; from += positions[v] * held[v]; to += (positions[v] + move[v]) * held[v]; }
		if ( total <= 1e-6f ) return;
		from /= total;
		to /= total;
		var h = new float[9];
		foreach ( int v in idx )
		{
			Vec3 p = positions[v] - from, q = positions[v] + move[v] - to;
			float f = held[v] / total;
			h[0] += f * q.X * p.X; h[1] += f * q.X * p.Y; h[2] += f * q.X * p.Z;
			h[3] += f * q.Y * p.X; h[4] += f * q.Y * p.Y; h[5] += f * q.Y * p.Z;
			h[6] += f * q.Z * p.X; h[7] += f * q.Z * p.Y; h[8] += f * q.Z * p.Z;
		}
		var r = GarmentFit.Rotation( h );
		var meanTurn = Vec3.Zero;
		foreach ( int v in idx ) meanTurn += turn[v] * (held[v] / total);
		foreach ( int v in idx )
		{
			Vec3 p = positions[v] - from;
			if ( r != null ) p = new Vec3( r[0] * p.X + r[1] * p.Y + r[2] * p.Z, r[3] * p.X + r[4] * p.Y + r[5] * p.Z, r[6] * p.X + r[7] * p.Y + r[8] * p.Z );
			move[v] = to + p - positions[v];
			turn[v] = meanTurn;
		}
	}
}
