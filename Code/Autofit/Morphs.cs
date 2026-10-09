using System;
using System.Collections.Generic;

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
	public Dictionary<string, List<(int Vertex, Vec3 Position, Vec3 Normal)>> Decode( byte[] rgba, int textureWidth, int textureHeight )
	{
		var result = new Dictionary<string, List<(int, Vec3, Vec3)>>();
		foreach ( var morph in Morphs )
		{
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
	const float SkinShare = 0.05f;                  // a piece of the body with less than this share of the biggest one's area is on the skin, not skin
	const float SpreadMin = 0.003f * Units.Metre;   // the least skin round its closest point a garment vertex takes the move of
	const float Contact = 0.03f * Units.Metre;      // how close to the skin a solid object is held by it

	/// <param name="positions">The garment, fitted, in the body's rest pose (the space of the body's morphs).</param>
	/// <param name="solid">Per garment vertex, whether it is part of a solid object (<see cref="GarmentFit.Fit"/>). Can be null.</param>
	/// <param name="skin">The body's morphs, a move and a turn per body vertex.</param>
	/// <param name="morphs">The garment's own morphs, a move and a turn per garment vertex. Changed in place, and the body's morphs the garment lacks are added.</param>
	/// <param name="seen">Whether a move and a turn are big enough to keep.</param>
	/// <param name="turnedAsMade">Per own morph, the triangles it turns over on the garment as made (see <see cref="TurnedOver"/>). Those are left as they are.</param>
	public static void TakeFromSkin( Vec3[] positions, int[] tris, bool[] solid, Vec3[] bodyPositions, int[] bodyTris,
		Dictionary<string, (Vec3[] Position, Vec3[] Normal)> skin, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Func<Vec3, Vec3, bool> seen,
		Dictionary<string, bool[]> turnedAsMade )
	{
		if ( skin.Count == 0 ) return;
		var (skinTris, area) = Skin( bodyPositions, bodyTris );
		if ( skinTris.Length == 0 ) return;
		var surface = new TriMesh( bodyPositions, skinTris );

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

		Settle( positions, tris, solid, surface, morphs, turnedAsMade );
	}

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
		var weld = MeshTools.Weld( positions, out int count );
		var piece = GarmentFit.Islands( count, MeshTools.Edges( tris, weld ) );
		var pieceArea = new Dictionary<int, float>();
		var triArea = new float[tris.Length / 3];
		for ( int t = 0; t < triArea.Length; t++ )
		{
			Vec3 a = positions[tris[t * 3]], b = positions[tris[t * 3 + 1]], c = positions[tris[t * 3 + 2]];
			triArea[t] = 0.5f * Vec3.Cross( b - a, c - a ).Length();
			int p = piece[weld[tris[t * 3]]];
			pieceArea[p] = (pieceArea.TryGetValue( p, out float have ) ? have : 0) + triArea[t];
		}
		float biggest = 0;
		foreach ( float a in pieceArea.Values ) biggest = MathF.Max( biggest, a );

		var kept = new List<int>();
		var area = new float[positions.Length];
		for ( int t = 0; t < triArea.Length; t++ )
		{
			if ( pieceArea[piece[weld[tris[t * 3]]]] < SkinShare * biggest ) continue;
			for ( int k = 0; k < 3; k++ )
			{
				kept.Add( tris[t * 3 + k] );
				area[tris[t * 3 + k]] += triArea[t] / 3f;
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
	/// </summary>
	static void Settle( Vec3[] positions, int[] tris, bool[] solid, TriMesh surface, Dictionary<string, (Vec3[] Position, Vec3[] Normal)> morphs, Dictionary<string, bool[]> turnedAsMade )
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

		var points = new Vec3[count];
		for ( int v = 0; v < n; v++ ) points[weld[v]] = positions[v];
		var welded = new Vec3[count];
		var weldedTurn = new Vec3[count];
		var hits = new int[count];
		var skip = new bool[tris.Length / 3];
		foreach ( var (name, (move, turn)) in morphs )
		{
			foreach ( var idx in solids.Values ) Rigid( idx, positions, held, move, turn );

			// Cloth, one value per welded vertex. Solid objects can't turn over now, and what the
			// morph turns over on the garment as made is meant.
			bool[] meant = turnedAsMade != null && turnedAsMade.TryGetValue( name, out var flags ) ? flags : null;
			for ( int t = 0; t < skip.Length; t++ )
				skip[t] = (meant != null && meant[t]) || (solid != null && (solid[tris[t * 3]] || solid[tris[t * 3 + 1]] || solid[tris[t * 3 + 2]]));
			Array.Clear( welded, 0, count );
			Array.Clear( weldedTurn, 0, count );
			Array.Clear( hits, 0, count );
			for ( int v = 0; v < n; v++ ) { welded[weld[v]] += move[v]; weldedTurn[weld[v]] += turn[v]; hits[weld[v]]++; }
			for ( int i = 0; i < count; i++ ) if ( hits[i] > 0 ) { welded[i] /= hits[i]; weldedTurn[i] /= hits[i]; }

			if ( !MeshTools.Untangle( points, tris, weld, edges, welded, weldedTurn, skip ) ) continue;
			for ( int v = 0; v < n; v++ )
			{
				if ( solid != null && solid[v] ) continue;
				move[v] = welded[weld[v]];
				turn[v] = weldedTurn[weld[v]];
			}
		}
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
