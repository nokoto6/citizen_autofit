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
