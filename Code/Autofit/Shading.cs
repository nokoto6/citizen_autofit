using System;

namespace Sandbox.Autofit;

/// <summary>
/// Normals and tangents for a mesh whose vertices have been moved.
/// </summary>
public static class Shading
{
	/// <summary>
	/// Turns each stored normal the same way the surface around its vertex turned. That keeps
	/// whatever the artist did with the normals (hard edges, smoothing) instead of replacing
	/// it with plain averaged normals.
	/// </summary>
	public static Vec3[] Normals( Vec3[] before, Vec3[] after, int[] tris, Vec3[] normals )
	{
		var weld = MeshTools.Weld( before, out int n );
		var faceBefore = new Vec3[n];
		var faceAfter = new Vec3[n];
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			var n0 = Vec3.Cross( before[b] - before[a], before[c] - before[a] );
			var n1 = Vec3.Cross( after[b] - after[a], after[c] - after[a] );
			for ( int k = 0; k < 3; k++ )
			{
				faceBefore[weld[tris[t + k]]] += n0;
				faceAfter[weld[tris[t + k]]] += n1;
			}
		}

		var result = new Vec3[normals.Length];
		for ( int i = 0; i < normals.Length; i++ )
		{
			Vec3 from = faceBefore[weld[i]], to = faceAfter[weld[i]];
			float lf = from.Length(), lt = to.Length();
			result[i] = normals[i];
			if ( lf < 1e-12f || lt < 1e-12f ) continue;
			result[i] = Turn( normals[i], from / lf, to / lt );
		}

		return result;
	}

	/// <summary>Plain smooth normals from the triangles, for when the model's own aren't available.</summary>
	public static Vec3[] Smooth( Vec3[] positions, int[] tris )
	{
		var weld = MeshTools.Weld( positions, out int n );
		var sum = new Vec3[n];
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			var face = Vec3.Cross( positions[tris[t + 1]] - positions[tris[t]], positions[tris[t + 2]] - positions[tris[t]] );
			for ( int k = 0; k < 3; k++ )
				sum[weld[tris[t + k]]] += face;
		}

		var result = new Vec3[positions.Length];
		for ( int i = 0; i < result.Length; i++ )
		{
			float length = sum[weld[i]].Length();
			result[i] = length > 1e-12f ? sum[weld[i]] / length : Vec3.UnitZ;
		}

		return result;
	}

	/// <summary>Rotates v by the shortest rotation that takes unit vector a to unit vector b.</summary>
	static Vec3 Turn( Vec3 v, Vec3 a, Vec3 b )
	{
		Vec3 axis = Vec3.Cross( a, b );
		float sin = axis.Length(), cos = Vec3.Dot( a, b );
		if ( sin < 1e-6f ) return cos > 0 ? v : -v;
		axis = axis / sin;

		// Rodrigues' rotation formula.
		Vec3 turned = v * cos + Vec3.Cross( axis, v ) * sin + axis * (Vec3.Dot( axis, v ) * (1f - cos));
		float length = turned.Length();
		return length > 1e-9f ? turned / length : v;
	}

	/// <summary>
	/// Tangents from the UVs, for normal mapping. Signs tell which way the bitangent goes.
	/// </summary>
	public static Vec3[] Tangents( Vec3[] positions, Vec3[] normals, Vec2[] uvs, int[] tris, out float[] signs )
	{
		var tangent = new Vec3[positions.Length];
		var bitangent = new Vec3[positions.Length];
		for ( int t = 0; t < tris.Length; t += 3 )
		{
			int a = tris[t], b = tris[t + 1], c = tris[t + 2];
			Vec3 e1 = positions[b] - positions[a], e2 = positions[c] - positions[a];
			float u1 = uvs[b].X - uvs[a].X, v1 = uvs[b].Y - uvs[a].Y;
			float u2 = uvs[c].X - uvs[a].X, v2 = uvs[c].Y - uvs[a].Y;
			float den = u1 * v2 - u2 * v1;
			if ( MathF.Abs( den ) < 1e-12f ) continue;
			float r = 1f / den;
			Vec3 s = (e1 * v2 - e2 * v1) * r, w = (e2 * u1 - e1 * u2) * r;
			for ( int k = 0; k < 3; k++ )
			{
				tangent[tris[t + k]] += s;
				bitangent[tris[t + k]] += w;
			}
		}

		signs = new float[positions.Length];
		for ( int i = 0; i < positions.Length; i++ )
		{
			Vec3 nrm = normals[i];
			Vec3 tan = tangent[i] - nrm * Vec3.Dot( nrm, tangent[i] );   // make it square to the normal
			float length = tan.Length();
			if ( length < 1e-9f )
			{
				// No usable UVs here. Any direction across the normal will do.
				tan = Vec3.Cross( nrm, MathF.Abs( nrm.Z ) < 0.9f ? Vec3.UnitZ : new Vec3( 1, 0, 0 ) );
				length = tan.Length();
			}

			tangent[i] = tan / MathF.Max( length, 1e-9f );
			signs[i] = Vec3.Dot( Vec3.Cross( nrm, tangent[i] ), bitangent[i] ) < 0 ? -1f : 1f;
		}

		return tangent;
	}
}
