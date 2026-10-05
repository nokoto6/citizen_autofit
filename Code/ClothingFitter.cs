using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Autofit;

namespace Sandbox;

/// <summary>
/// Refits clothing to a body at runtime.
///
/// All citizen clothing is modelled on the stock citizen. Given any other body on the same
/// skeleton, this works out how the skin moved (once per body), moves each garment the same
/// way and builds a new skinned model out of the result. Fitted models are cached, so a
/// garment is only fitted the first time it is worn on a given body.
///
/// The fitting itself lives in Code/Autofit and has no engine dependencies. This class is the
/// engine side: reading the compiled models and building the fitted one.
/// </summary>
public static class ClothingFitter
{
	/// <summary>The body all citizen clothing is made for.</summary>
	public const string StockBody = "models/citizen/citizen.vmdl";

	/// <summary>
	/// Which of the stock bodies a model's skeleton is closest to. Most clothing comes in a
	/// citizen version and a version for each of the humans, and a garment sits and animates
	/// best when it was made for a skeleton with the same proportions.
	/// </summary>
	public enum BodyKind
	{
		Citizen,
		HumanMale,
		HumanFemale,
	}

	static readonly string[] ReferencePaths =
	{
		StockBody,
		"models/citizen_human/citizen_human_male.vmdl",
		"models/citizen_human/citizen_human_female.vmdl",
	};

	// A stock body that clothing is modelled on.
	sealed class Reference
	{
		public SkinnedGeometry Geo;
		public TriMesh Mesh;
		public SkinByBone Skin;
	}

	// A body brought into one reference's pose, and where that reference's skin went on it.
	sealed class Mapped
	{
		public Repose Pose;
		public TriMesh Mesh;
		public BodyMap Map;
	}

	sealed class Body
	{
		public string Path;
		public SkinnedGeometry Geo;
		public BodyKind Kind;
		public Dictionary<BodyKind, Mapped> Maps = new();
	}

	static readonly Reference[] references = new Reference[3];
	static readonly bool[] referenceMissing = new bool[3];
	static readonly Dictionary<string, Body> bodies = new();
	static readonly Dictionary<string, Model> fitted = new();
	static readonly HashSet<string> failed = new();
	/// <summary>
	/// Forgets everything fitted so far. Call it after a body model has been re-exported,
	/// otherwise clothing keeps being fitted to the old shape.
	/// </summary>
	public static void Clear()
	{
		Array.Clear( references, 0, references.Length );
		Array.Clear( referenceMissing, 0, referenceMissing.Length );
		bodies.Clear();
		fitted.Clear();
		failed.Clear();
	}

	/// <summary>
	/// Which stock body this model's skeleton is closest to. Citizen if it can't be told.
	/// </summary>
	public static BodyKind KindOf( Model body )
	{
		if ( body is null ) return BodyKind.Citizen;
		try
		{
			return GetBody( Normalize( body.ResourcePath ) ).Kind;
		}
		catch ( Exception e )
		{
			Log.Warning( $"ClothingFitter: couldn't read {body.ResourcePath}. {e.GetType().Name}: {e.Message} {Where( e )}" );
			return BodyKind.Citizen;
		}
	}

	/// <summary>
	/// The garment refitted to this body, or null if there is nothing to do (the body is the
	/// stock one the garment was made for) or the garment couldn't be fitted. Fall back to the
	/// garment's own model then.
	/// </summary>
	/// <param name="madeFor">Which stock body this garment model was made for.</param>
	public static Model Fit( Model body, string garmentPath, BodyKind madeFor = BodyKind.Citizen )
	{
		if ( body is null || string.IsNullOrEmpty( garmentPath ) )
			return null;

		string bodyPath = Normalize( body.ResourcePath );
		if ( bodyPath == ReferencePaths[(int)madeFor] )
			return null;

		string key = $"{bodyPath}|{Normalize( garmentPath )}";
		if ( fitted.TryGetValue( key, out var cached ) && cached.IsValid() )
			return cached;
		if ( failed.Contains( key ) )
			return null;

		try
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var reference = GetReference( madeFor ) ?? throw new InvalidOperationException( $"{ReferencePaths[(int)madeFor]} is not available" );
			var target = GetMapped( GetBody( bodyPath ), madeFor );
			var garment = CompiledModel.Read( ReadCompiled( garmentPath ) );
			var bones = GarmentFit.BonesOnBody( garment, reference.Geo );

			// Not done along with the mapping: working out which stock body is closest maps
			// against more than one of them, and only the one that gets worn needs this.
			target.Mesh.FindExposed();

			var positions = GarmentFit.Fit( garment.Positions, garment.Indices, bones, garment.BoneWeight, reference.Mesh, target.Mesh, target.Map.SkinMove, reference.Skin );

			// The fit happens in the stock skeleton's proportions. Take it back to the body's.
			positions = target.Pose.FromStockProportions( positions, bones, garment.BoneWeight, reference.Geo );

			var model = Build( garment, positions, $"fitted/{key}" );
			fitted[key] = model;
			Log.Info( $"ClothingFitter: fitted {System.IO.Path.GetFileNameWithoutExtension( garmentPath )} to {System.IO.Path.GetFileNameWithoutExtension( bodyPath )} in {watch.ElapsedMilliseconds} ms ({garment.Positions.Length} verts)" );
			return model;
		}
		catch ( Exception e )
		{
			// One garment in a format we don't read must not take the whole outfit down.
			failed.Add( key );
			Log.Warning( $"ClothingFitter: couldn't fit {garmentPath} to {bodyPath}, using it as is. {e.GetType().Name}: {e.Message} {Where( e )}" );
			return null;
		}
	}

	/// <summary>
	/// Fits one garment to one body and reports what came out, without touching any scene.
	/// The third argument says which stock body the garment was made for: citizen (default), male or female.
	/// clothingfitter_test models/citizen_custom/citizen_custom.vmdl models/citizen_clothes/jacket/biker_jacket/models/biker_jacket.vmdl
	/// </summary>
	[ConCmd( "clothingfitter_test" )]
	public static void Test( string bodyPath, string garmentPath, string madeFor = "citizen" )
	{
		var body = Model.Load( bodyPath );
		if ( !body.IsValid() || body.IsError )
		{
			Log.Warning( $"clothingfitter_test: no body model at {bodyPath}" );
			return;
		}

		var kind = madeFor == "male" ? BodyKind.HumanMale : madeFor == "female" ? BodyKind.HumanFemale : BodyKind.Citizen;

		// Start from scratch so the command always shows the current code's result.
		bodies.Remove( Normalize( body.ResourcePath ) );
		fitted.Remove( $"{Normalize( body.ResourcePath )}|{Normalize( garmentPath )}" );
		failed.Clear();
		var model = Fit( body, garmentPath, kind );
		if ( model is null )
		{
			Log.Warning( "clothingfitter_test: nothing was fitted" );
			return;
		}

		var original = Model.Load( garmentPath );
		Log.Info( $"clothingfitter_test: ok. {model.BoneCount} bones (original {original.BoneCount}), {model.MeshCount} meshes, {model.Materials.Count()} materials, bounds {model.Bounds.Size} (original {original.Bounds.Size})" );
	}
	// The first couple of frames of where an exception came from, for the log.
	static string Where( Exception e ) =>
		string.Join( " <- ", (e.StackTrace ?? "").Split( '\n' ).Take( 3 ).Select( x => x.Trim() ) );

	/// <summary>
	/// Prints what the fitter sees in a body model: its size, which stock skeleton it is closest
	/// to and how it compares to each.
	/// clothingfitter_info models/citizen_custom/citizen_custom.vmdl
	/// </summary>
	[ConCmd( "clothingfitter_info" )]
	public static void Info( string bodyPath )
	{
		try
		{
			string path = Normalize( bodyPath );
			bodies.Remove( path );
			var body = GetBody( path );
			var geo = body.Geo;

			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in geo.Positions )
			{
				min = new Vec3( MathF.Min( min.X, p.X ), MathF.Min( min.Y, p.Y ), MathF.Min( min.Z, p.Z ) );
				max = new Vec3( MathF.Max( max.X, p.X ), MathF.Max( max.Y, p.Y ), MathF.Max( max.Z, p.Z ) );
			}

			Log.Info( $"clothingfitter_info {bodyPath}: {geo.Positions.Length} verts, {geo.Indices.Length / 3} tris, {geo.BoneNames.Length} bones, size {max.X - min.X:F1} x {max.Y - min.Y:F1} x {max.Z - min.Z:F1}. Closest stock skeleton: {body.Kind}" );
			for ( int k = 0; k < references.Length; k++ )
			{
				var reference = GetReference( (BodyKind)k );
				if ( reference is null ) continue;
				float spread = Proportions( geo, reference.Geo, out int shared, out float size );
				Log.Info( $"clothingfitter_info   vs {(BodyKind)k}: {shared} of {reference.Geo.BoneNames.Length} bones in common, {size:F2} times its size, proportions off by {spread * 100f:F0}%" );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"clothingfitter_info {bodyPath}: {e.GetType().Name}: {e.Message} {Where( e )}" );
		}
	}

	static Reference GetReference( BodyKind kind )
	{
		int k = (int)kind;

		// After a hotload the cached objects can be left over from older code and lack fields
		// added since. BoneRotations being empty is how that shows; start over then.
		if ( references[k] is not null && references[k].Geo?.BoneRotations is null )
			Clear();

		if ( references[k] is not null || referenceMissing[k] )
			return references[k];

		try
		{
			var geo = CompiledModel.Read( ReadCompiled( ReferencePaths[k] ) );
			var mesh = new TriMesh( geo.Positions, geo.Indices );
			references[k] = new Reference { Geo = geo, Mesh = mesh, Skin = new SkinByBone( mesh, geo ) };
		}
		catch ( Exception e )
		{
			referenceMissing[k] = true;
			Log.Warning( $"ClothingFitter: can't use {ReferencePaths[k]} as a reference body. {e.GetType().Name}: {e.Message}" );
		}

		return references[k];
	}

	static Body GetBody( string bodyPath )
	{
		GetReference( BodyKind.Citizen );   // also throws out caches left over from older code
		if ( bodies.TryGetValue( bodyPath, out var body ) )
			return body;

		body = new Body { Path = bodyPath, Geo = CompiledModel.Read( ReadCompiled( bodyPath ) ) };

		// The closest stock skeleton is the one with the most bones in common and, among those,
		// the most similar proportions. Overall size and facing don't matter for that.
		float best = float.MaxValue;
		int mostShared = 0;
		for ( int k = 0; k < references.Length; k++ )
		{
			var reference = GetReference( (BodyKind)k );
			if ( reference is null ) continue;
			float spread = Proportions( body.Geo, reference.Geo, out int shared, out _ );
			if ( shared < mostShared * 0.9f ) continue;
			if ( shared > mostShared * 1.1f || spread < best )
			{
				best = spread;
				mostShared = Math.Max( mostShared, shared );
				body.Kind = (BodyKind)k;
			}
		}

		bodies[bodyPath] = body;

		// The two humans share a skeleton, so bones can't tell them apart. The shape can:
		// take the one whose skin has to move less to become this body.
		if ( body.Kind != BodyKind.Citizen && GetReference( BodyKind.HumanMale ) is not null && GetReference( BodyKind.HumanFemale ) is not null )
		{
			float male = AverageMove( GetMapped( body, BodyKind.HumanMale ) );
			float female = AverageMove( GetMapped( body, BodyKind.HumanFemale ) );
			body.Kind = female < male ? BodyKind.HumanFemale : BodyKind.HumanMale;
		}

		return body;
	}

	static float AverageMove( Mapped mapped )
	{
		double sum = 0;
		foreach ( var move in mapped.Map.SkinMove ) sum += move.Length();
		return (float)(sum / Math.Max( 1, mapped.Map.SkinMove.Length ));
	}

	/// <summary>
	/// How differently a skeleton is proportioned from a reference: the spread of the ratios of
	/// their bone lengths. 0 when one is just a bigger or smaller copy of the other.
	/// </summary>
	static float Proportions( SkinnedGeometry body, SkinnedGeometry reference, out int shared, out float size )
	{
		var byName = new Dictionary<string, int>();
		for ( int i = 0; i < reference.BoneNames.Length; i++ ) byName[reference.BoneNames[i]] = i;

		shared = 0;
		var logs = new List<float>();
		for ( int i = 0; i < body.BoneNames.Length; i++ )
		{
			if ( !byName.TryGetValue( body.BoneNames[i], out int r ) ) continue;
			shared++;
			int parent = body.BoneParents[i], refParent = reference.BoneParents[r];
			if ( parent < 0 || refParent < 0 || body.BoneNames[parent] != reference.BoneNames[refParent] ) continue;
			float theirs = (body.BonePositions[i] - body.BonePositions[parent]).Length();
			float ours = (reference.BonePositions[r] - reference.BonePositions[refParent]).Length();
			if ( ours < 0.02f * Units.Metre || theirs < 1e-4f ) continue;
			// Attachment bones can sit metres away from the hand. Don't let a few of those
			// drown out the rest.
			logs.Add( Math.Clamp( MathF.Log( theirs / ours ), -0.7f, 0.7f ) );
		}

		size = 1f;
		if ( logs.Count < 4 ) return float.MaxValue;
		float mean = logs.Average();
		size = MathF.Exp( mean );
		return MathF.Sqrt( logs.Sum( x => (x - mean) * (x - mean) ) / logs.Count );
	}

	static Mapped GetMapped( Body body, BodyKind kind )
	{
		if ( body.Maps.TryGetValue( kind, out var mapped ) )
			return mapped;

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var reference = GetReference( kind );
		var geo = body.Geo;

		// Clothing is bone-merged, so a body with its own rest pose has to be compared in the
		// reference's rest pose and proportions. No-op for a body that already shares them.
		mapped = new Mapped { Pose = Repose.ToStockPose( geo, reference.Geo ) };
		mapped.Mesh = new TriMesh( mapped.Pose.Positions, geo.Indices );

		// The body's own skinning, in terms of the reference's bones, tells which part of it
		// belongs to which bone.
		var skin = new SkinByBone( mapped.Mesh, GarmentFit.BonesOnBody( geo, reference.Geo ), geo.BoneWeight, reference.Geo.BoneParents );
		mapped.Map = BodyMap.Build( reference.Geo, reference.Mesh, mapped.Mesh, geo.Uvs, skin, mapped.Pose.Landmarks );
		body.Maps[kind] = mapped;

		string name = System.IO.Path.GetFileNameWithoutExtension( body.Path );
		float low = mapped.Pose.BoneScale.Min(), high = mapped.Pose.BoneScale.Max();
		Log.Info( $"ClothingFitter: mapped {name} against {kind} in {watch.ElapsedMilliseconds} ms. {mapped.Map.ByUv} skin points matched by UV, {mapped.Map.ByRays} by bone rays, {mapped.Map.NoMatch} filled in from neighbours. Rest pose differs by {mapped.Pose.Moved / Units.Metre * 100f:F0} cm on average, bones are {low:F2} to {high:F2} times as long" );
		if ( mapped.Pose.SharedBones < reference.Geo.BoneNames.Length / 2 )
			Log.Warning( $"ClothingFitter: {name} has only {mapped.Pose.SharedBones} of {kind}'s {reference.Geo.BoneNames.Length} bones. Clothing follows bones by name, so it won't sit or animate properly on it" );
		return mapped;
	}

	static string Normalize( string path ) => (path ?? "").Replace( '\\', '/' ).ToLowerInvariant();

	static byte[] ReadCompiled( string modelPath )
	{
		string path = Normalize( modelPath );
		if ( !path.EndsWith( "_c" ) ) path += "_c";
		if ( !FileSystem.Mounted.FileExists( path ) )
			throw new System.IO.FileNotFoundException( $"No compiled model at {path}" );
		return FileSystem.Mounted.ReadAllBytes( path ).ToArray();
	}

	struct FittedVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.Tangent] public Vector4 Tangent;
		[VertexLayout.TexCoord] public Vector2 TexCoord;
		[VertexLayout.BlendIndices] public Color32 BlendIndices;
		[VertexLayout.BlendWeight] public Color32 BlendWeights;
	}

	static Model Build( SkinnedGeometry g, Vec3[] positions, string name )
	{
		var normals = g.HasNormals ? Shading.Normals( g.Positions, positions, g.Indices, g.Normals ) : Shading.Smooth( positions, g.Indices );
		var tangents = Shading.Tangents( positions, normals, g.Uvs, g.Indices, out var signs );

		var builder = Model.Builder.WithName( name );

		// Same skeleton as the original garment, in the same order: the vertices index it.
		// The builder wants each bone in model space. Its doc comment says "relative to the
		// parent", but the engine's own importers (Sandbox.Mounting.GoldSrc) pass model space,
		// and parent-relative transforms come out as a wrecked bind pose.
		for ( int i = 0; i < g.BoneNames.Length; i++ )
		{
			var p = g.BonePositions[i];
			var r = g.BoneRotations[i];
			int parent = g.BoneParents[i];
			builder.AddBone( g.BoneNames[i], new Vector3( p.X, p.Y, p.Z ), new Rotation( r[0], r[1], r[2], r[3] ), parent >= 0 ? g.BoneNames[parent] : null );
		}

		// One mesh per draw call, each with only the vertices it uses.
		for ( int d = 0; d < g.Draws.Count; d++ )
		{
			var (first, count, materialPath) = g.Draws[d];
			var remap = new Dictionary<int, int>();
			var vertices = new List<FittedVertex>();
			var indices = new List<int>( count );
			for ( int i = first; i < first + count; i++ )
			{
				int v = g.Indices[i];
				if ( !remap.TryGetValue( v, out int local ) )
				{
					local = vertices.Count;
					remap[v] = local;
					vertices.Add( MakeVertex( g, v, positions[v], normals[v], tangents[v], signs[v] ) );
				}
				indices.Add( local );
			}

			if ( vertices.Count == 0 ) continue;

			var material = string.IsNullOrEmpty( materialPath ) ? null : Material.Load( materialPath );
			var mesh = new Mesh( $"{name}_{d}", material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = BBox.FromPoints( vertices.Select( x => x.Position ) );
			builder.AddMesh( mesh );
		}

		return builder.Create();
	}

	static FittedVertex MakeVertex( SkinnedGeometry g, int v, Vec3 p, Vec3 n, Vec3 t, float sign )
	{
		// Weights go out as bytes and have to add up to exactly 255.
		Span<int> bone = stackalloc int[4];
		Span<int> weight = stackalloc int[4];
		int total = 0, heaviest = 0;
		for ( int j = 0; j < 4; j++ )
		{
			int index = g.BoneIndex[v * 4 + j];
			float w = index >= 0 ? g.BoneWeight[v * 4 + j] : 0f;
			bone[j] = Math.Max( index, 0 );
			weight[j] = (int)MathF.Round( w * 255f );
			total += weight[j];
			if ( weight[j] > weight[heaviest] ) heaviest = j;
		}
		weight[heaviest] += 255 - total;

		return new FittedVertex
		{
			Position = new Vector3( p.X, p.Y, p.Z ),
			Normal = new Vector3( n.X, n.Y, n.Z ),
			Tangent = new Vector4( t.X, t.Y, t.Z, sign ),
			TexCoord = new Vector2( g.Uvs[v].X, g.Uvs[v].Y ),
			BlendIndices = new Color32( (byte)bone[0], (byte)bone[1], (byte)bone[2], (byte)bone[3] ),
			BlendWeights = new Color32( (byte)Math.Clamp( weight[0], 0, 255 ), (byte)Math.Clamp( weight[1], 0, 255 ), (byte)Math.Clamp( weight[2], 0, 255 ), (byte)Math.Clamp( weight[3], 0, 255 ) ),
		};
	}
}
