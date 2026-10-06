using System;
using System.Collections.Generic;
using System.Text;

namespace Sandbox.Autofit;

/// <summary>
/// One level of detail of a compiled model with its skinning and skeleton, all meshes merged.
/// </summary>
public sealed class SkinnedGeometry
{
	public Vec3[] Positions;
	public Vec3[] Normals;
	public Vec2[] Uvs;

	/// <summary>Four bone slots per vertex, as indices into <see cref="BoneNames"/>. -1 for a bone the model's skeleton doesn't list.</summary>
	public int[] BoneIndex;

	/// <summary>Four weights per vertex, matching <see cref="BoneIndex"/>.</summary>
	public float[] BoneWeight;

	public int[] Indices;

	/// <summary>Index ranges and the material each is drawn with.</summary>
	public List<(int First, int Count, string Material)> Draws = new();

	// The model's skeleton in its bind pose. Positions are in model space.
	public string[] BoneNames;
	public int[] BoneParents;
	public Vec3[] BonePositions;

	/// <summary>Bind pose rotation of each bone in model space, as x, y, z, w.</summary>
	public float[][] BoneRotations;

	// The same pose relative to each bone's parent, as stored. Rotations are x, y, z, w.
	public Vec3[] BoneLocalPositions;
	public float[][] BoneLocalRotations;

	/// <summary>False when the normals are stored in a packing this reader doesn't know. <see cref="Normals"/> is filler then.</summary>
	public bool HasNormals = true;

	/// <summary>Which of the model's LOD levels draw this geometry, one bit per level. All of them for a model without LODs.</summary>
	public int LodMask = 255;

	/// <summary>The model's LOD switch distances, by level.</summary>
	public float[] LodDistances = Array.Empty<float>();

	/// <summary>Morph targets of the meshes that have them. Usually only the most detailed LOD does.</summary>
	public List<MorphSet> Morphs = new();
}

/// <summary>
/// Reads geometry, bone weights and the skeleton from a .vmdl_c.
///
/// The engine hands out positions, normals and UVs of a model (Model.GetVertices) but not its
/// skinning, and without the weights a refitted garment can't be turned back into a model that
/// animates. Everything needed is in the file: each mesh is an MDAT block (a KV3 document with
/// its bones and draw calls) followed by an MBUF block (vertex and index buffers, usually
/// meshoptimizer-compressed), and the DATA block says which LOD each mesh belongs to.
///
/// If the engine ever exposes blend indices and weights, this whole file can go.
/// </summary>
public static class CompiledModel
{
	// DXGI_FORMAT values the vertex layouts of models actually use.
	const int R32G32B32_FLOAT = 6;
	const int R16G16B16A16_UINT = 12;
	const int R10G10B10A2_UNORM = 24;
	const int R16G16_FLOAT = 34;

	sealed class Buffers
	{
		public int FirstVertex;
		public List<int> VertexStart = new();   // where each vertex buffer starts in the merged arrays
		public List<int[]> Index = new();
		public List<byte[]> BoneSlots = new();  // mesh-local bone numbers, 4 per vertex, per vertex buffer
	}

	/// <summary>The most detailed LOD.</summary>
	public static SkinnedGeometry Read( byte[] file )
	{
		var model = Open( file );
		return ReadMeshes( model, model.MeshesOf( 0 ), 255 );
	}

	/// <summary>Just the skeleton, for a model whose meshes can't be read.</summary>
	public static SkinnedGeometry ReadSkeletonOnly( byte[] file )
	{
		var result = new SkinnedGeometry();
		ReadSkeleton( Open( file ).Doc, result );
		return result;
	}

	/// <summary>
	/// Every LOD, most detailed first. Levels that draw the same meshes come back as one entry
	/// with all their bits in <see cref="SkinnedGeometry.LodMask"/>, so a model without LODs
	/// gives a single entry.
	/// </summary>
	public static List<SkinnedGeometry> ReadLods( byte[] file )
	{
		var model = Open( file );
		var levels = new List<SkinnedGeometry>();
		string previous = null;
		for ( int lod = 0; lod < 8; lod++ )
		{
			var meshes = model.MeshesOf( lod );
			if ( meshes.Count == 0 ) continue;

			string same = string.Join( ",", meshes );
			if ( same == previous )
			{
				levels[^1].LodMask |= 1 << lod;
				continue;
			}

			previous = same;
			levels.Add( ReadMeshes( model, meshes, 1 << lod ) );
		}

		if ( levels.Count == 0 )
			throw new InvalidOperationException( "Model has no meshes that are on by default" );
		return levels;
	}

	// A file with its blocks found and the DATA block parsed.
	sealed class Opened
	{
		public byte[] File;
		public List<int> Mdat = new();
		public List<int> Mbuf = new();
		public List<int> Blocks = new();           // where every block of the file starts, in file order
		public Dictionary<int, int> MorphBlock = new();  // mesh number -> start of its MRPH block
		public object Doc;
		public List<object> LodMasks, GroupMasks;
		public long DefaultGroups;

		// The meshes drawn at a LOD level. Only the ones that are on by default: a body has an
		// empty alternative for every body group.
		public List<int> MeshesOf( int lod )
		{
			var meshes = new List<int>();
			for ( int m = 0; m < Mdat.Count; m++ )
			{
				if ( LodMasks != null && m < LodMasks.Count && (Kv3.Int( LodMasks[m] ) & (1L << lod)) == 0 ) continue;
				if ( GroupMasks != null && m < GroupMasks.Count && (Kv3.Int( GroupMasks[m] ) & DefaultGroups) == 0 ) continue;
				meshes.Add( m );
			}

			return meshes;
		}
	}

	static Opened Open( byte[] file )
	{
		var model = new Opened { File = file };
		int data = -1, control = -1;
		int table = 8 + BitConverter.ToInt32( file, 8 );
		int count = BitConverter.ToInt32( file, 12 );
		for ( int i = 0; i < count; i++ )
		{
			int entry = table + i * 12;
			string kind = Encoding.ASCII.GetString( file, entry, 4 );
			int start = entry + 4 + BitConverter.ToInt32( file, entry + 4 );
			model.Blocks.Add( start );
			if ( kind == "MDAT" ) model.Mdat.Add( start );
			else if ( kind == "MBUF" ) model.Mbuf.Add( start );
			else if ( kind == "DATA" ) data = start;
			else if ( kind == "CTRL" ) control = start;
		}

		if ( data < 0 || model.Mdat.Count == 0 || model.Mdat.Count != model.Mbuf.Count )
			throw new InvalidOperationException( "Model has no embedded meshes" );

		model.Doc = Kv3.Parse( file, data );
		model.LodMasks = Kv3.List( Kv3.Get( model.Doc, "m_refLODGroupMasks" ) );
		model.GroupMasks = Kv3.List( Kv3.Get( model.Doc, "m_refMeshGroupMasks" ) );
		model.DefaultGroups = Kv3.Int( Kv3.Get( model.Doc, "m_nDefaultMeshGroupMask" ), -1 );

		// The CTRL block lists the meshes and says which block holds each one's morphs.
		if ( control >= 0 )
		{
			foreach ( var mesh in Kv3.List( Kv3.Get( Kv3.Parse( file, control ), "embedded_meshes" ) ) ?? new List<object>() )
			{
				int index = (int)Kv3.Int( Kv3.Get( mesh, "mesh_index" ), -1 );
				int block = (int)Kv3.Int( Kv3.Get( mesh, "morph_block" ), -1 );
				if ( index >= 0 && block >= 0 && block < model.Blocks.Count )
					model.MorphBlock[index] = model.Blocks[block];
			}
		}

		return model;
	}

	static SkinnedGeometry ReadMeshes( Opened model, List<int> meshes, int lodMask )
	{
		byte[] file = model.File;
		var result = new SkinnedGeometry { LodMask = lodMask };
		ReadSkeleton( model.Doc, result );
		var boneByName = new Dictionary<string, int>();
		for ( int i = 0; i < result.BoneNames.Length; i++ )
			boneByName[result.BoneNames[i]] = i;

		var distances = Kv3.List( Kv3.Get( model.Doc, "m_lodGroupSwitchDistances" ) );
		if ( distances != null )
		{
			result.LodDistances = new float[distances.Count];
			for ( int i = 0; i < distances.Count; i++ )
				result.LodDistances[i] = (float)Kv3.Number( distances[i] );
		}

		var positions = new List<Vec3>();
		var normals = new List<Vec3>();
		var uvs = new List<Vec2>();
		var boneIndex = new List<int>();
		var boneWeight = new List<float>();
		var indices = new List<int>();

		foreach ( int m in meshes )
		{
			var mesh = Kv3.Parse( file, model.Mdat[m] );
			int firstVertex = positions.Count;
			var buffers = ReadBuffers( file, model.Mbuf[m], positions, normals, uvs, boneWeight, ref result.HasNormals );
			if ( model.MorphBlock.TryGetValue( m, out int morphs ) )
				result.Morphs.Add( ReadMorphs( Kv3.Parse( file, morphs ), firstVertex, positions.Count - firstVertex ) );

			// The vertices index the mesh's own bone list. Turn that into model bones.
			var meshBones = Kv3.List( Kv3.Get( Kv3.Get( mesh, "m_skeleton" ), "m_bones" ) );
			var remap = new int[meshBones?.Count ?? 0];
			for ( int i = 0; i < remap.Length; i++ )
				remap[i] = boneByName.TryGetValue( (string)Kv3.Get( meshBones[i], "m_boneName" ) ?? "", out int found ) ? found : -1;
			foreach ( var slots in buffers.BoneSlots )
				foreach ( byte slot in slots )
					boneIndex.Add( slot < remap.Length ? remap[slot] : -1 );

			foreach ( var sceneObject in Kv3.List( Kv3.Get( mesh, "m_sceneObjects" ) ) ?? new List<object>() )
			{
				foreach ( var draw in Kv3.List( Kv3.Get( sceneObject, "m_drawCalls" ) ) ?? new List<object>() )
				{
					int baseVertex = (int)Kv3.Int( Kv3.Get( draw, "m_nBaseVertex" ) );
					int first = (int)Kv3.Int( Kv3.Get( draw, "m_nStartIndex" ) );
					int n = (int)Kv3.Int( Kv3.Get( draw, "m_nIndexCount" ) );
					int ib = (int)Kv3.Int( Kv3.Get( Kv3.Get( draw, "m_indexBuffer" ), "m_hBuffer" ) );
					var vbs = Kv3.List( Kv3.Get( draw, "m_vertexBuffers" ) );
					int vb = vbs != null && vbs.Count > 0 ? (int)Kv3.Int( Kv3.Get( vbs[0], "m_hBuffer" ) ) : 0;

					result.Draws.Add( (indices.Count, n, Kv3.Get( draw, "m_material" ) as string ?? "") );
					var source = buffers.Index[ib];
					int offset = baseVertex + buffers.VertexStart[vb];
					for ( int i = 0; i < n; i++ )
						indices.Add( source[first + i] + offset );
				}
			}
		}

		result.Positions = positions.ToArray();
		result.Normals = normals.ToArray();
		result.Uvs = uvs.ToArray();
		result.BoneIndex = boneIndex.ToArray();
		result.BoneWeight = boneWeight.ToArray();
		result.Indices = indices.ToArray();
		return result;
	}

	static MorphSet ReadMorphs( object doc, int firstVertex, int vertexCount )
	{
		var set = new MorphSet
		{
			AtlasPath = Kv3.Get( doc, "m_pTextureAtlas" ) as string,
			Width = (int)Kv3.Int( Kv3.Get( doc, "m_nWidth" ) ),
			Height = (int)Kv3.Int( Kv3.Get( doc, "m_nHeight" ) ),
			FirstVertex = firstVertex,
			VertexCount = vertexCount,
		};

		// Each rectangle carries one bundle per entry of this list, in the same order.
		int positionBundle = -1, normalBundle = -1;
		var types = Kv3.List( Kv3.Get( doc, "m_bundleTypes" ) ) ?? new List<object>();
		for ( int i = 0; i < types.Count; i++ )
		{
			string type = types[i] as string ?? "";
			if ( type.Contains( "POSITION" ) ) positionBundle = i;
			else if ( type.Contains( "NORMAL" ) ) normalBundle = i;
		}

		foreach ( var data in Kv3.List( Kv3.Get( doc, "m_morphDatas" ) ) ?? new List<object>() )
		{
			var morph = new MorphSet.Morph { Name = Kv3.Get( data, "m_name" ) as string ?? "" };
			foreach ( var rectData in Kv3.List( Kv3.Get( data, "m_morphRectDatas" ) ) ?? new List<object>() )
			{
				var bundles = Kv3.List( Kv3.Get( rectData, "m_bundleDatas" ) ) ?? new List<object>();
				morph.Rects.Add( new MorphSet.Rect
				{
					X = (int)Kv3.Int( Kv3.Get( rectData, "m_nXLeftDst" ) ),
					Y = (int)Kv3.Int( Kv3.Get( rectData, "m_nYTopDst" ) ),
					Width = (float)Kv3.Number( Kv3.Get( rectData, "m_flUWidthSrc" ) ),
					Height = (float)Kv3.Number( Kv3.Get( rectData, "m_flVHeightSrc" ) ),
					Position = ReadBundle( bundles, positionBundle ),
					Normal = ReadBundle( bundles, normalBundle ),
				} );
			}

			set.Morphs.Add( morph );
		}

		return set;
	}

	static MorphSet.Bundle ReadBundle( List<object> bundles, int index )
	{
		if ( index < 0 || index >= bundles.Count ) return null;

		var bundle = new MorphSet.Bundle
		{
			U = (float)Kv3.Number( Kv3.Get( bundles[index], "m_flULeftSrc" ) ),
			V = (float)Kv3.Number( Kv3.Get( bundles[index], "m_flVTopSrc" ) ),
		};

		var offsets = Kv3.List( Kv3.Get( bundles[index], "m_offsets" ) );
		var ranges = Kv3.List( Kv3.Get( bundles[index], "m_ranges" ) );
		for ( int i = 0; i < 4; i++ )
		{
			if ( offsets != null && i < offsets.Count ) bundle.Offsets[i] = (float)Kv3.Number( offsets[i] );
			if ( ranges != null && i < ranges.Count ) bundle.Ranges[i] = (float)Kv3.Number( ranges[i] );
		}

		return bundle;
	}

	static void ReadSkeleton( object doc, SkinnedGeometry result )
	{
		var skeleton = Kv3.Get( doc, "m_modelSkeleton" );
		var names = Kv3.List( Kv3.Get( skeleton, "m_boneName" ) ) ?? new List<object>();
		var parents = Kv3.List( Kv3.Get( skeleton, "m_nParent" ) );
		var localPos = Kv3.List( Kv3.Get( skeleton, "m_bonePosParent" ) );
		var localRot = Kv3.List( Kv3.Get( skeleton, "m_boneRotParent" ) );

		int n = names.Count;
		result.BoneNames = new string[n];
		result.BoneParents = new int[n];
		result.BonePositions = new Vec3[n];
		result.BoneLocalPositions = new Vec3[n];
		result.BoneLocalRotations = new float[n][];
		var rotation = new float[n][];   // x, y, z, w in model space

		for ( int i = 0; i < n; i++ )
		{
			result.BoneNames[i] = (string)names[i];
			int parent = (int)Kv3.Int( parents[i], -1 );
			result.BoneParents[i] = parent;

			var p = Kv3.List( localPos[i] );
			var r = Kv3.List( localRot[i] );
			var pos = new Vec3( (float)Kv3.Number( p[0] ), (float)Kv3.Number( p[1] ), (float)Kv3.Number( p[2] ) );
			var rot = new[] { (float)Kv3.Number( r[0] ), (float)Kv3.Number( r[1] ), (float)Kv3.Number( r[2] ), (float)Kv3.Number( r[3] ) };

			result.BoneLocalPositions[i] = pos;
			result.BoneLocalRotations[i] = rot;

			// Bones are stored parents first.
			if ( parent < 0 )
			{
				result.BonePositions[i] = pos;
				rotation[i] = rot;
			}
			else
			{
				result.BonePositions[i] = result.BonePositions[parent] + Quat.Rotate( rotation[parent], pos );
				rotation[i] = Quat.Multiply( rotation[parent], rot );
			}
		}

		result.BoneRotations = rotation;
	}

	/// <summary>
	/// Appends the vertices of every vertex buffer in one MBUF block to the merged arrays and
	/// returns its index buffers.
	/// </summary>
	static Buffers ReadBuffers( byte[] file, int start, List<Vec3> positions, List<Vec3> normals, List<Vec2> uvs, List<float> boneWeight, ref bool normalsKnown )
	{
		int vbOffset = BitConverter.ToInt32( file, start );
		int vbCount = BitConverter.ToInt32( file, start + 4 );
		int ibOffset = BitConverter.ToInt32( file, start + 8 );
		int ibCount = BitConverter.ToInt32( file, start + 12 );
		var result = new Buffers { FirstVertex = positions.Count };

		for ( int k = 0; k < vbCount; k++ )
		{
			int p = start + vbOffset + k * 24;
			int vertexCount = BitConverter.ToInt32( file, p );
			int vertexSize = BitConverter.ToInt32( file, p + 4 ) & 0x3FFFFFF;   // top bits are flags
			int attributes = p + 8 + BitConverter.ToInt32( file, p + 8 );
			int attributeCount = BitConverter.ToInt32( file, p + 12 );
			int data = p + 16 + BitConverter.ToInt32( file, p + 16 );
			int dataSize = BitConverter.ToInt32( file, p + 20 );

			byte[] raw = dataSize == vertexCount * vertexSize
				? Slice( file, data, dataSize )
				: MeshOpt.DecodeVertexBuffer( file, data, dataSize, vertexCount, vertexSize );

			int posAt = -1, uvAt = -1, uvFormat = 0, normalAt = -1, normalFormat = 0, indexAt = -1, indexFormat = 0, weightAt = -1;
			for ( int a = 0; a < attributeCount; a++ )
			{
				int at = attributes + a * 56;
				int end = Array.IndexOf( file, (byte)0, at, 32 );
				// Older compilers wrote these in lower case, and "blendweights" with an s.
				string name = Encoding.ASCII.GetString( file, at, (end < 0 ? at + 32 : end) - at ).ToUpperInvariant();
				int semanticIndex = BitConverter.ToInt32( file, at + 32 );
				int format = BitConverter.ToInt32( file, at + 36 );
				int offset = BitConverter.ToInt32( file, at + 40 );
				if ( semanticIndex != 0 ) continue;

				if ( name == "POSITION" ) posAt = offset;
				else if ( name == "TEXCOORD" ) { uvAt = offset; uvFormat = format; }
				else if ( name == "NORMAL" ) { normalAt = offset; normalFormat = format; }
				else if ( name == "BLENDINDICES" ) { indexAt = offset; indexFormat = format; }
				else if ( name == "BLENDWEIGHT" || name == "BLENDWEIGHTS" ) weightAt = offset;
			}

			if ( posAt < 0 )
				throw new InvalidOperationException( "Vertex buffer has no positions" );

			// Some models pack the normal and tangent together into four bytes. That packing
			// isn't decoded here; the caller works the normals out from the triangles instead.
			if ( normalAt >= 0 && normalFormat != R10G10B10A2_UNORM && normalFormat != R32G32B32_FLOAT )
			{
				normalAt = -1;
				normalsKnown = false;
			}

			result.VertexStart.Add( positions.Count );
			var slots = new byte[vertexCount * 4];
			for ( int v = 0; v < vertexCount; v++ )
			{
				int o = v * vertexSize;
				positions.Add( new Vec3( BitConverter.ToSingle( raw, o + posAt ), BitConverter.ToSingle( raw, o + posAt + 4 ), BitConverter.ToSingle( raw, o + posAt + 8 ) ) );

				if ( uvAt < 0 ) uvs.Add( default );
				else if ( uvFormat == R16G16_FLOAT ) uvs.Add( new Vec2( (float)BitConverter.ToHalf( raw, o + uvAt ), (float)BitConverter.ToHalf( raw, o + uvAt + 2 ) ) );
				else uvs.Add( new Vec2( BitConverter.ToSingle( raw, o + uvAt ), BitConverter.ToSingle( raw, o + uvAt + 4 ) ) );

				normals.Add( normalAt < 0 ? Vec3.UnitZ : ReadNormal( raw, o + normalAt, normalFormat ) );

				// No blend indices: the mesh hangs off its first bone. Indices without weights:
				// one bone per vertex.
				for ( int j = 0; j < 4; j++ )
				{
					byte bone = 0;
					if ( indexAt >= 0 )
						bone = indexFormat == R16G16B16A16_UINT ? (byte)BitConverter.ToUInt16( raw, o + indexAt + j * 2 ) : raw[o + indexAt + j];
					slots[v * 4 + j] = bone;
					boneWeight.Add( weightAt >= 0 ? raw[o + weightAt + j] / 255f : (j == 0 ? 1f : 0f) );
				}
			}

			result.BoneSlots.Add( slots );
		}

		for ( int k = 0; k < ibCount; k++ )
		{
			int p = start + 8 + ibOffset + k * 24;
			int indexCount = BitConverter.ToInt32( file, p );
			int indexSize = BitConverter.ToInt32( file, p + 4 ) & 0x3FFFFFF;
			int data = p + 16 + BitConverter.ToInt32( file, p + 16 );
			int dataSize = BitConverter.ToInt32( file, p + 20 );
			byte[] raw = dataSize == indexCount * indexSize
				? Slice( file, data, dataSize )
				: MeshOpt.DecodeIndexBuffer( file, data, dataSize, indexCount, indexSize );

			var list = new int[indexCount];
			for ( int i = 0; i < indexCount; i++ )
				list[i] = indexSize == 2 ? BitConverter.ToUInt16( raw, i * 2 ) : BitConverter.ToInt32( raw, i * 4 );
			result.Index.Add( list );
		}

		return result;
	}

	static Vec3 ReadNormal( byte[] raw, int at, int format )
	{
		Vec3 n;
		if ( format == R10G10B10A2_UNORM )
		{
			// Three 10-bit components, 0..1 mapped to -1..1.
			uint v = BitConverter.ToUInt32( raw, at );
			n = new Vec3( (v & 1023) / 1023f, ((v >> 10) & 1023) / 1023f, ((v >> 20) & 1023) / 1023f ) * 2f - Vec3.One;
		}
		else
		{
			n = new Vec3( BitConverter.ToSingle( raw, at ), BitConverter.ToSingle( raw, at + 4 ), BitConverter.ToSingle( raw, at + 8 ) );
		}

		float length = n.Length();
		return length > 1e-9f ? n / length : Vec3.UnitZ;
	}

	static byte[] Slice( byte[] source, int start, int length )
	{
		var result = new byte[length];
		Array.Copy( source, start, result, 0, length );
		return result;
	}
}

/// <summary>
/// LZ4 block decoder. KV3 documents inside compiled resources are stored as one LZ4 block.
/// </summary>
static class Lz4
{
	public static byte[] Decode( byte[] source, int start, int length, int outputSize )
	{
		var output = new byte[outputSize];
		int s = start, end = start + length, o = 0;
		while ( s < end )
		{
			int token = source[s++];

			int literals = token >> 4;
			if ( literals == 15 )
			{
				int b;
				do { b = source[s++]; literals += b; } while ( b == 255 );
			}

			Array.Copy( source, s, output, o, literals );
			s += literals;
			o += literals;
			if ( s >= end ) break;   // the last sequence is literals only

			int offset = source[s] | (source[s + 1] << 8);
			s += 2;

			int match = token & 15;
			if ( match == 15 )
			{
				int b;
				do { b = source[s++]; match += b; } while ( b == 255 );
			}
			match += 4;

			// Byte by byte on purpose: the match may overlap what it is writing.
			for ( int i = 0; i < match; i++, o++ )
				output[o] = output[o - offset];
		}

		return output;
	}
}

/// <summary>
/// Decoders for meshoptimizer's vertex and index codecs, ported from vertexcodec.cpp and
/// indexcodec.cpp (scalar paths). s&box compiles most models with both.
/// </summary>
static class MeshOpt
{
	const int BlockBytes = 8192;
	const int BlockMaxVertices = 256;
	const int Group = 16;

	static readonly int[] BitsV0 = { 0, 2, 4, 8 };
	static readonly int[] BitsV1 = { 0, 1, 2, 4, 8 };

	public static byte[] DecodeVertexBuffer( byte[] source, int start, int length, int vertexCount, int vertexSize )
	{
		int data = start, end = start + length;
		int header = source[data++];
		if ( (header & 0xf0) != 0xa0 )
			throw new InvalidOperationException( "Not a meshoptimizer vertex stream" );
		int version = header & 0x0f;
		if ( version > 1 )
			throw new InvalidOperationException( $"Vertex codec version {version} is not handled" );

		int tailSize = vertexSize + (version == 0 ? 0 : vertexSize / 4);
		int tailMin = version == 0 ? 32 : 24;
		int tailPadded = Math.Max( tailSize, tailMin );
		int tail = end - tailSize;

		var last = new byte[256];
		Array.Copy( source, tail, last, 0, vertexSize );
		int channels = tail + vertexSize;   // version 1 only

		int blockSize = Math.Min( (BlockBytes / vertexSize) & ~(Group - 1), BlockMaxVertices );
		var output = new byte[vertexCount * vertexSize];
		var buffer = new byte[BlockMaxVertices * 4 + Group];
		var bits = new int[4];

		for ( int done = 0; done < vertexCount; )
		{
			int n = Math.Min( blockSize, vertexCount - done );
			int aligned = (n + Group - 1) & ~(Group - 1);
			int target = done * vertexSize;

			int control = data;
			if ( version != 0 ) data += vertexSize / 4;

			for ( int k = 0; k < vertexSize; k += 4 )
			{
				int controlByte = version == 0 ? 0 : source[control + k / 4];
				for ( int j = 0; j < 4; j++ )
				{
					int mode = (controlByte >> (j * 2)) & 3;
					if ( mode == 3 )
					{
						Array.Copy( source, data, buffer, j * n, n );
						data += n;
					}
					else if ( mode == 2 )
					{
						Array.Clear( buffer, j * n, n );
					}
					else
					{
						for ( int b = 0; b < 4; b++ )
							bits[b] = version == 0 ? BitsV0[b] : BitsV1[b + mode];
						data = DecodeBytes( source, data, buffer, j * n, aligned, bits );
					}
				}

				int channel = version == 0 ? 0 : source[channels + k / 4];
				switch ( channel & 3 )
				{
					case 0: Deltas( buffer, output, target + k, n, vertexSize, last, k, 1, false, 0 ); break;
					case 1: Deltas( buffer, output, target + k, n, vertexSize, last, k, 2, false, 0 ); break;
					case 2: Deltas( buffer, output, target + k, n, vertexSize, last, k, 4, true, (32 - (channel >> 4)) & 31 ); break;
					default: throw new InvalidOperationException( "Bad vertex channel type" );
				}
			}

			Array.Copy( output, target + vertexSize * (n - 1), last, 0, vertexSize );
			done += n;
		}

		if ( end - data != tailPadded )
			throw new InvalidOperationException( "Vertex stream did not end where it should" );

		return output;
	}

	static int DecodeBytes( byte[] source, int data, byte[] buffer, int at, int size, int[] bits )
	{
		int header = data;
		data += (size / Group + 3) / 4;
		for ( int i = 0; i < size; i += Group )
		{
			int group = i / Group;
			int which = (source[header + group / 4] >> ((group % 4) * 2)) & 3;
			data = DecodeGroup( source, data, buffer, at + i, bits[which] );
		}

		return data;
	}

	static int DecodeGroup( byte[] source, int data, byte[] buffer, int at, int bits )
	{
		if ( bits == 0 )
		{
			Array.Clear( buffer, at, Group );
			return data;
		}

		if ( bits == 8 )
		{
			Array.Copy( source, data, buffer, at, Group );
			return data + Group;
		}

		// Each value is `bits` wide. The all-ones value means "the real byte follows", and
		// those bytes sit right after the packed ones.
		int sentinel = (1 << bits) - 1;
		int perByte = 8 / bits;
		int extra = data + Group / perByte;
		for ( int i = 0; i < Group; i += perByte )
		{
			int packed = source[data++];
			if ( bits == 1 )
				packed = ReverseBits( packed );   // 1-bit groups are stored lowest bit first

			for ( int j = 0; j < perByte; j++ )
			{
				int value = (packed >> (8 - bits)) & sentinel;
				packed <<= bits;
				buffer[at + i + j] = value == sentinel ? source[extra++] : (byte)value;
			}
		}

		return extra;
	}

	static int ReverseBits( int b )
	{
		b = ((b & 0xF0) >> 4) | ((b & 0x0F) << 4);
		b = ((b & 0xCC) >> 2) | ((b & 0x33) << 2);
		return ((b & 0xAA) >> 1) | ((b & 0x55) << 1);
	}

	/// <summary>
	/// Undoes the delta coding of one 4-byte column of the vertex. The column is coded as
	/// four 1-byte, two 2-byte or one 4-byte value per vertex, depending on the channel.
	/// </summary>
	static void Deltas( byte[] buffer, byte[] output, int target, int count, int vertexSize, byte[] last, int lastAt, int width, bool xor, int rotate )
	{
		int source = 0;
		for ( int k = 0; k < 4; k += width )
		{
			uint previous = 0;
			for ( int j = 0; j < width; j++ )
				previous |= (uint)last[lastAt + k + j] << (8 * j);

			uint mask = width == 4 ? 0xFFFFFFFF : (1u << (8 * width)) - 1;
			int at = target + k;
			for ( int i = 0; i < count; i++ )
			{
				uint v = 0;
				for ( int j = 0; j < width; j++ )
					v |= (uint)buffer[source + i + count * j] << (8 * j);

				if ( xor )
					v = ((v << rotate) | (v >> ((32 - rotate) & 31))) ^ previous;
				else
					v = (((0 - (v & 1)) ^ (v >> 1)) + previous) & mask;

				for ( int j = 0; j < width; j++ )
					output[at + j] = (byte)(v >> (8 * j));

				previous = v;
				at += vertexSize;
			}

			source += count * width;
		}
	}

	public static byte[] DecodeIndexBuffer( byte[] source, int start, int length, int indexCount, int indexSize )
	{
		if ( (source[start] & 0xf0) != 0xe0 )
			throw new InvalidOperationException( "Not a meshoptimizer index stream" );
		int version = source[start] & 0x0f;
		if ( version > 1 )
			throw new InvalidOperationException( $"Index codec version {version} is not handled" );

		var edges = new uint[16, 2];
		var verts = new uint[16];
		for ( int i = 0; i < 16; i++ ) { edges[i, 0] = edges[i, 1] = verts[i] = uint.MaxValue; }
		int edgeAt = 0, vertAt = 0;
		uint next = 0, last = 0;
		int fecMax = version >= 1 ? 13 : 15;

		int code = start + 1;
		int codeEnd = code + indexCount / 3;
		int data = codeEnd;
		int auxTable = start + length - 16;

		var output = new byte[indexCount * indexSize];
		int o = 0;

		void WriteOne( uint v )
		{
			output[o++] = (byte)v;
			output[o++] = (byte)(v >> 8);
			if ( indexSize == 2 ) return;
			output[o++] = (byte)(v >> 16);
			output[o++] = (byte)(v >> 24);
		}

		void Write( uint a, uint b, uint c )
		{
			WriteOne( a );
			WriteOne( b );
			WriteOne( c );
		}

		void PushEdge( uint a, uint b ) { edges[edgeAt, 0] = a; edges[edgeAt, 1] = b; edgeAt = (edgeAt + 1) & 15; }
		void PushVert( uint v, bool advance = true ) { verts[vertAt] = v; if ( advance ) vertAt = (vertAt + 1) & 15; }

		uint ReadIndex()
		{
			// Variable-length integer, then zigzag, relative to the last index read this way.
			uint v = source[data++];
			if ( v >= 128 )
			{
				v &= 127;
				for ( int shift = 7, i = 0; i < 4; i++, shift += 7 )
				{
					uint g = source[data++];
					v |= (g & 127) << shift;
					if ( g < 128 ) break;
				}
			}

			return last + ((v >> 1) ^ (uint)-(int)(v & 1));
		}

		while ( code < codeEnd )
		{
			int tri = source[code++];
			if ( tri < 0xf0 )
			{
				// Shares an edge with a recent triangle.
				int fe = tri >> 4;
				uint a = edges[(edgeAt - 1 - fe) & 15, 0];
				uint b = edges[(edgeAt - 1 - fe) & 15, 1];
				uint c;
				int fec = tri & 15;
				if ( fec < fecMax )
				{
					c = fec == 0 ? next : verts[(vertAt - 1 - fec) & 15];
					if ( fec == 0 ) next++;
					PushVert( c, fec == 0 );
				}
				else
				{
					last = c = fec != 15 ? last + (uint)(fec * 2 - 27) : ReadIndex();
					PushVert( c );
				}

				PushEdge( c, b );
				PushEdge( a, c );
				Write( a, b, c );
			}
			else if ( tri < 0xfe )
			{
				int aux = source[auxTable + (tri & 15)];
				int feb = aux >> 4, fec = aux & 15;

				uint a = next++;
				uint bf = verts[(vertAt - feb) & 15];
				uint b = feb == 0 ? next : bf;
				if ( feb == 0 ) next++;
				uint cf = verts[(vertAt - fec) & 15];
				uint c = fec == 0 ? next : cf;
				if ( fec == 0 ) next++;

				Write( a, b, c );
				PushVert( a );
				PushVert( b, feb == 0 );
				PushVert( c, fec == 0 );
				PushEdge( b, a );
				PushEdge( c, b );
				PushEdge( a, c );
			}
			else
			{
				int aux = source[data++];
				int fea = tri == 0xfe ? 0 : 15;
				int feb = aux >> 4, fec = aux & 15;
				if ( aux == 0 ) next = 0;

				uint a = fea == 0 ? next++ : 0;
				uint b = feb == 0 ? next++ : verts[(vertAt - feb) & 15];
				uint c = fec == 0 ? next++ : verts[(vertAt - fec) & 15];
				if ( fea == 15 ) last = a = ReadIndex();
				if ( feb == 15 ) last = b = ReadIndex();
				if ( fec == 15 ) last = c = ReadIndex();

				Write( a, b, c );
				PushVert( a );
				PushVert( b, feb == 0 || feb == 15 );
				PushVert( c, fec == 0 || fec == 15 );
				PushEdge( b, a );
				PushEdge( c, b );
				PushEdge( a, c );
			}
		}

		return output;
	}
}
