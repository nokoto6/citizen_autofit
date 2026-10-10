using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// What a garment worn over others rests on. Every garment is fitted to the body on its own,
/// by how far it was made to stand off the stock skin. A chain made to go over clothes stands
/// clear of them on the stock body, but on another body the clothes needn't keep to where they
/// were: a shirt bridges a bust bigger than the stock one and stands out over it, while the
/// chain, fitted to the skin under it, keeps to the skin. On Aurora the swag chain's pendant
/// hung in the hollow between her breasts, under the Hawaiian shirt that spans them, and the
/// links round the collar went in and out of it.
///
/// So a garment made of pieces that keep their shape (links, beads, a pendant, the frame of a
/// pair of glasses) is put back on top of what it was made to lie over, once everything is
/// fitted, in the body's rest pose, where all of it is worn. Cloth isn't: it is cut to the body
/// and is what the others lie on. A piece goes out as a whole, along the way out of the body,
/// until it is clear of every garment it was made further out than.
/// </summary>
public static class Layers
{
	const float SmallPiece = 0.06f * Units.Metre;  // a loose bit of cloth no bigger than this corner to corner keeps its shape: a chain's link, a bead
	const float Above = 0.002f * Units.Metre;      // made this much further off the stock skin than the garment under it, a piece was meant to lie over it
	const float Clear = 0.003f * Units.Metre;      // how far over that garment a piece is put; a pendant's physics lets it swing back a little at rest
	const float Reach = 0.04f * Units.Metre;       // how far in and out of a piece the garment under it is looked for
	const float MaxLift = 0.05f * Units.Metre;     // a piece that would have to go further than this is a misread and stays
	const float Touching = 0.002f * Units.Metre;   // solid pieces skinned alike this close together are one object (a pendant and its loop)
	const float Hold = 0.003f * Units.Metre;       // a solid object this close to another piece as made hangs from it (a pendant's ring round the block on the chain)
	const float MaxSwing = 1.0f;                   // radians a hanging object swings out at most (57 degrees)
	const float SwingStep = 0.02f;                 // radians a hanging object's swing is tried at a time (1.1 degrees)
	const float Settled = 0.0005f * Units.Metre;   // a hanging object lacking no more than this anywhere is clear
	const int Rounds = 8;                          // passes at a piece's move, each making up what one of its points still lacks

	/// <summary>A garment that others may rest on, fitted and in the body's rest pose.</summary>
	public sealed class Under
	{
		public TriMesh Mesh;
		public float[] Depth;   // per vertex of the mesh, how far off the stock skin it was made (see Depth)
	}

	/// <summary>
	/// Whether a garment lies on whatever is under it rather than being cut to the body: bits of
	/// cloth that keep their shape (links, beads) and solid objects hanging from them. Hair and
	/// paint don't, nor does anything with a piece of cloth bigger than a bead, nor a garment that
	/// is only solid (a hat, glasses), which is fitted as one.
	/// </summary>
	/// <param name="solid">Per vertex, fitted as part of a solid object (from the fit).</param>
	/// <param name="cloth">Per vertex, fitted as cloth (from the fit).</param>
	public static bool Loose( Vec3[] positions, int[] tris, bool[] solid, bool[] cloth )
	{
		if ( tris.Length == 0 ) return false;
		var (weld, pieces, _) = Pieces( positions, tris );
		var lo = new Dictionary<int, Vec3>();
		var hi = new Dictionary<int, Vec3>();
		foreach ( int v in tris )
		{
			if ( solid[v] ) continue;
			if ( !cloth[v] ) return false;
			int piece = pieces[weld[v]];
			var p = positions[v];
			lo[piece] = lo.TryGetValue( piece, out var a ) ? Min( a, p ) : p;
			hi[piece] = hi.TryGetValue( piece, out var b ) ? Max( b, p ) : p;
		}
		foreach ( var (piece, low) in lo )
			if ( (hi[piece] - low).Length() > SmallPiece ) return false;
		return lo.Count > 0;
	}

	/// <summary>Per vertex, how far off the stock skin the garment was made, negative under it.</summary>
	public static float[] Depth( Vec3[] positions, TriMesh skin )
	{
		var depth = new float[positions.Length];
		for ( int i = 0; i < positions.Length; i++ ) depth[i] = skin.SignedGap( positions[i], out _ );
		return depth;
	}

	/// <summary>
	/// The garment's pieces put back together and over the garments under it.
	///
	/// A solid object that hangs from another piece (a pendant by its ring from the block on the
	/// chain) is fitted on its own, the way the skin under each of them moved, and the two come
	/// apart: on Aurora the swag chain's pendant ended up 24 mm below the block it hangs from,
	/// on Akira 16 mm, with nothing between. It is put back where it touched that piece as made.
	///
	/// Then, where a piece was made further off the stock skin than one of the garments under it
	/// (by <see cref="Above"/>) and now is under it or level with it, it goes out along the way out
	/// of the body until it is <see cref="Clear"/> over it. Each piece moves whole, a solid object
	/// (a pendant and its ring) or a small bit of cloth (a link) on its own; one that hangs from
	/// another swings out about where it hangs instead. Nothing else moves.
	/// </summary>
	/// <param name="made">The garment as made, in the stock pose.</param>
	/// <param name="rest">The garment fitted, in the body's rest pose.</param>
	/// <param name="depth">Per vertex, how far off its stock skin the garment was made (<see cref="Depth"/>).</param>
	/// <param name="body">The body in its rest pose.</param>
	/// <param name="under">The garments worn under this one, fitted and in the body's rest pose. May be empty.</param>
	/// <param name="lifted">How many pieces went out over the garments under it.</param>
	public static Vec3[] Over( Vec3[] made, Vec3[] rest, int[] tris, int[] boneIndex, float[] boneWeight, bool[] solid, float[] depth, TriMesh body, IReadOnlyList<Under> under, out int lifted )
	{
		lifted = 0;
		var result = new Vec3[rest.Length];
		Array.Copy( rest, result, rest.Length );
		if ( tris.Length == 0 ) return result;
		under ??= Array.Empty<Under>();

		var (weld, pieces, n) = Pieces( made, tris );
		var first = new int[n];
		Array.Fill( first, -1 );
		foreach ( int v in tris )
			if ( first[weld[v]] < 0 ) first[weld[v]] = v;

		// Solid pieces that touch and are skinned alike move together: a pendant and its ring.
		var objectOf = Objects( made, weld, pieces, first, boneIndex, boneWeight, solid, out var rigid );
		var members = new Dictionary<int, List<int>>();
		for ( int i = 0; i < n; i++ )
		{
			if ( first[i] < 0 ) continue;
			int o = objectOf[pieces[i]];
			if ( !members.TryGetValue( o, out var list ) ) members[o] = list = new List<int>();
			list.Add( i );
		}
		var at = new Vec3[n];
		for ( int i = 0; i < n; i++ ) if ( first[i] >= 0 ) at[i] = rest[first[i]];

		// What a solid object hangs from: the piece closest to it as made, if that touched it, and
		// the points where they touched. A pendant's ring sits 1.4 mm from the block on the chain
		// and 2 mm from the nearest link.
		var holds = new Dictionary<int, List<(int Own, int Other)>>();
		foreach ( var (o, list) in members )
		{
			if ( !rigid[o] ) continue;
			int holder = -1;
			float closest = Hold * Hold;
			foreach ( var (other, theirs) in members )
			{
				if ( other == o ) continue;
				foreach ( int a in list )
					foreach ( int b in theirs )
					{
						float d = (made[first[a]] - made[first[b]]).LengthSquared();
						if ( d <= closest ) { closest = d; holder = other; }
					}
			}
			if ( holder < 0 ) continue;
			var pairs = new List<(int, int)>();
			foreach ( int a in list )
				foreach ( int b in members[holder] )
					if ( (made[first[a]] - made[first[b]]).LengthSquared() <= Hold * Hold ) pairs.Add( (a, b) );
			holds[o] = pairs;
		}

		var hits = new List<(float T, bool Leaving, int Tri)>();
		// How far out a point has to go to be over the garments it was made over, and which way is out.
		var skin = new Under { Mesh = body, Depth = new float[body.Verts.Length] };
		float Need( Vec3 p, float made0, bool onSkin, out Vec3 way )
		{
			way = Vec3.Zero;
			float distance = body.Nearest( p, out var s, out int tri );
			if ( tri < 0 ) return 0;
			way = distance > 0.001f * Units.Metre ? (p - s) / distance * (body.Inside( p ) ? -1f : 1f) : body.TriNormals[tri] / body.TriNormals[tri].Length();
			float need = 0;
			for ( int l = onSkin ? -1 : 0; l < under.Count; l++ )
			{
				var layer = l < 0 ? skin : under[l];
				layer.Mesh.Crossings( p - way * Reach, way, 2 * Reach, hits );
				foreach ( var hit in hits )
				{
					if ( made0 < DepthAt( layer, hit.Tri, p - way * Reach + way * hit.T ) + Above ) continue;
					need = MathF.Max( need, hit.T - Reach + Clear );
				}
			}
			return need;
		}

		// Everything that doesn't hang from something goes out as a whole.
		foreach ( var (o, list) in members )
		{
			if ( holds.ContainsKey( o ) || under.Count == 0 ) continue;
			var need = new float[list.Count];
			var way = new Vec3[list.Count];
			for ( int k = 0; k < list.Count; k++ ) need[k] = Need( at[list[k]], depth[first[list[k]]], false, out way[k] );
			var shift = Vec3.Zero;
			for ( int round = 0; round < Rounds; round++ )
			{
				float worst = 0;
				int at0 = -1;
				for ( int k = 0; k < list.Count; k++ )
				{
					float lack = need[k] - Vec3.Dot( shift, way[k] );
					if ( lack > worst ) { worst = lack; at0 = k; }
				}
				if ( at0 < 0 || worst < 1e-5f * Units.Metre ) break;
				shift += way[at0] * worst;
			}
			if ( shift.LengthSquared() < 1e-12f || shift.Length() > MaxLift ) continue;
			lifted++;
			foreach ( int i in list ) at[i] += shift;
		}

		// Then what hangs goes back onto what it hangs from, and swings out about it.
		foreach ( var (o, pairs) in holds )
		{
			var list = members[o];
			var back = Vec3.Zero;
			var pivot = Vec3.Zero;
			foreach ( var (a, b) in pairs )
			{
				back += at[b] - at[a] - (made[first[b]] - made[first[a]]);
				pivot += at[b];
			}
			back /= pairs.Count;
			pivot /= pairs.Count;
			foreach ( int i in list ) at[i] += back;

			// Put back where it hangs, it may lie in the body where the body is fuller than the stock
			// one (a pendant on a bust), as well as under the garments. It swings forward about where
			// it hangs as far as it takes to clear all of them: tried a step at a time, the least that
			// leaves nothing under, else the least that leaves least. Pushed out point by point
			// instead, each along the way out of the skin under it, the bottom of a pendant under a
			// bust, whose skin faces down, kept asking and it swung up level.
			var from = new Vec3[list.Count];
			var centre = Vec3.Zero;
			var forward = Vec3.Zero;
			for ( int k = 0; k < list.Count; k++ )
			{
				from[k] = at[list[k]];
				centre += from[k];
				if ( Need( from[k], depth[first[list[k]]], true, out var way ) > 0 ) forward += way;
			}
			centre /= list.Count;
			var axis = Vec3.Cross( centre - pivot, forward );
			if ( forward.LengthSquared() < 1e-12f || axis.LengthSquared() < 1e-12f ) continue;
			axis /= axis.Length();
			float WorstAt( float angle )
			{
				float h = MathF.Sin( angle / 2 );
				float[] q = { axis.X * h, axis.Y * h, axis.Z * h, MathF.Cos( angle / 2 ) };
				float worst = 0;
				for ( int k = 0; k < list.Count; k++ )
					worst = MathF.Max( worst, Need( pivot + Quat.Rotate( q, from[k] - pivot ), depth[first[list[k]]], true, out _ ) );
				return worst;
			}
			float best = 0, least = WorstAt( 0 );
			for ( float angle = SwingStep; angle <= MaxSwing && least > Settled; angle += SwingStep )
			{
				float worst = WorstAt( angle );
				if ( worst < least ) { least = worst; best = angle; }
			}
			float[] turn = { axis.X * MathF.Sin( best / 2 ), axis.Y * MathF.Sin( best / 2 ), axis.Z * MathF.Sin( best / 2 ), MathF.Cos( best / 2 ) };
			bool swung = false;
			for ( int k = 0; k < list.Count; k++ )
			{
				var p = pivot + Quat.Rotate( turn, from[k] - pivot );
				swung |= (p - from[k]).LengthSquared() > 1e-10f;
				at[list[k]] = p;
			}
			if ( swung ) lifted++;
		}

		for ( int v = 0; v < result.Length; v++ )
			if ( first[weld[v]] >= 0 ) result[v] = at[weld[v]];
		return result;
	}

	// The made depth of a garment under this one, at a point on one of its triangles.
	static float DepthAt( Under layer, int tri, Vec3 p )
	{
		var mesh = layer.Mesh;
		int a = mesh.Tris[tri * 3], b = mesh.Tris[tri * 3 + 1], c = mesh.Tris[tri * 3 + 2];
		GarmentFit.Barycentric( p, mesh.Verts[a], mesh.Verts[b], mesh.Verts[c], out float u, out float v, out float w );
		return layer.Depth[a] * u + layer.Depth[b] * v + layer.Depth[c] * w;
	}

	// Welded vertices and the connected piece each one is part of.
	static (int[] Weld, int[] Pieces, int Count) Pieces( Vec3[] positions, int[] tris )
	{
		var weld = MeshTools.Weld( positions, out int n );
		var pieces = GarmentFit.Islands( n, MeshTools.Edges( tris, weld ) );
		return (weld, pieces, n);
	}

	// Per piece, the object it moves with: itself, or for solid pieces the first of the solid
	// pieces it touches with the same skinning. Rigid: per object, whether it is a solid one.
	static Dictionary<int, int> Objects( Vec3[] made, int[] weld, int[] pieces, int[] first, int[] boneIndex, float[] boneWeight, bool[] solid, out Dictionary<int, bool> rigidObject )
	{
		var lo = new Dictionary<int, Vec3>();
		var hi = new Dictionary<int, Vec3>();
		var key = new Dictionary<int, string>();
		var rigid = new Dictionary<int, bool>();
		for ( int i = 0; i < first.Length; i++ )
		{
			int v = first[i];
			if ( v < 0 ) continue;
			int piece = pieces[i];
			var p = made[v];
			lo[piece] = lo.TryGetValue( piece, out var a ) ? Min( a, p ) : p;
			hi[piece] = hi.TryGetValue( piece, out var b ) ? Max( b, p ) : p;
			string k = WeightKey( v, boneIndex, boneWeight );
			if ( !key.TryGetValue( piece, out var had ) ) { key[piece] = k; rigid[piece] = solid[v]; }
			else if ( had != k || !solid[v] ) rigid[piece] = false;
		}

		var all = new List<int>( lo.Keys );
		var parent = new Dictionary<int, int>();
		foreach ( int piece in all ) parent[piece] = piece;
		int Find( int x )
		{
			while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; }
			return x;
		}
		for ( int i = 0; i < all.Count; i++ )
		{
			int r = all[i];
			if ( !rigid[r] ) continue;
			for ( int j = i + 1; j < all.Count; j++ )
			{
				int q = all[j];
				if ( !rigid[q] || key[q] != key[r] ) continue;
				if ( lo[r].X > hi[q].X + Touching || lo[q].X > hi[r].X + Touching || lo[r].Y > hi[q].Y + Touching || lo[q].Y > hi[r].Y + Touching || lo[r].Z > hi[q].Z + Touching || lo[q].Z > hi[r].Z + Touching ) continue;
				int a = Find( r ), b = Find( q );
				if ( a != b ) parent[a] = b;
			}
		}

		var objectOf = new Dictionary<int, int>();
		rigidObject = new Dictionary<int, bool>();
		foreach ( int piece in all )
		{
			int o = Find( piece );
			objectOf[piece] = o;
			rigidObject[o] = rigid[o];
		}
		return objectOf;
	}

	static string WeightKey( int vertex, int[] boneIndex, float[] boneWeight )
	{
		var parts = new List<(int, int)>();
		for ( int j = 0; j < 4; j++ )
		{
			float w = boneWeight[vertex * 4 + j];
			if ( w >= 0.025f ) parts.Add( (boneIndex[vertex * 4 + j], (int)MathF.Round( w * 20 )) );
		}
		parts.Sort();
		return string.Join( ",", parts );
	}

	static Vec3 Min( Vec3 a, Vec3 b ) => new( MathF.Min( a.X, b.X ), MathF.Min( a.Y, b.Y ), MathF.Min( a.Z, b.Z ) );
	static Vec3 Max( Vec3 a, Vec3 b ) => new( MathF.Max( a.X, b.X ), MathF.Max( a.Y, b.Y ), MathF.Max( a.Z, b.Z ) );
}
