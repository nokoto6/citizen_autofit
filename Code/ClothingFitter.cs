using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Autofit;

namespace Sandbox;

/// <summary>
/// Refits clothing to a body at runtime.
///
/// All citizen clothing is modelled on the stock citizen (or one of the stock humans). Given
/// any other body on the same skeleton, this works out how the skin moved (once per body),
/// moves each garment the same way and builds a new skinned model out of the result. Fitted
/// models are cached, so a garment is only fitted the first time it is worn on a given body.
///
/// The fitting itself lives in Code/Autofit and has no engine dependencies. This class is the
/// engine side: reading the compiled models, running the fit on worker threads and building
/// the fitted model.
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
		public long Milliseconds;
		public Task Exposed;   // finding the body's outer surface, started when a garment first needs it
	}

	sealed class Body
	{
		public string Path;
		public SkinnedGeometry Geo;
		public BodyKind Kind;
		public Dictionary<BodyKind, Task<Mapped>> Maps = new();
	}

	// One LOD level of a garment as a worker hands it back, for the main thread to make meshes of.
	sealed class Fitted
	{
		public SkinnedGeometry Garment;
		public List<(string Material, List<FittedVertex> Vertices, List<int> Indices)> Draws = new();
		public List<Mesh> Meshes = new();   // made on the main thread
		public long Milliseconds;
	}

	// A garment being fitted, and whoever wants to see it before it is finished.
	sealed class Job
	{
		public Task<Model> Task;
		public Model Rough;
		public List<Action<Model>> Watching = new();
	}

	// Something went wrong on a worker thread. Carries the original exception's text.
	sealed class FitException : Exception
	{
		public FitException( string message ) : base( message ) { }
	}

	// All of this is only touched from the main thread. The heavy steps (reading a model,
	// mapping a body, fitting a garment) each run as a task of their own on a worker thread
	// and touch nothing but what they were given, so there is nothing to lock. Whoever needs
	// a step's result awaits its task; steps that don't depend on each other run side by side.
	static readonly Task<Reference>[] references = new Task<Reference>[3];
	static readonly Dictionary<string, Task<Body>> bodies = new();
	static readonly Dictionary<string, Model> fitted = new();
	static readonly Dictionary<string, Job> pending = new();
	static readonly HashSet<string> failed = new();
	static int generation;

	/// <summary>
	/// Forgets everything fitted so far. Call it after a body model has been re-exported,
	/// otherwise clothing keeps being fitted to the old shape.
	/// </summary>
	public static void Clear()
	{
		Array.Clear( references, 0, references.Length );
		bodies.Clear();
		fitted.Clear();
		pending.Clear();
		failed.Clear();
		generation++;
	}

	/// <summary>
	/// The garment refitted to this body, or null if there is nothing to do (the body is the
	/// stock one the garment was made for) or the garment couldn't be fitted. Wear the
	/// garment's own model then.
	///
	/// The work is done on worker threads and takes from a few milliseconds to a second
	/// depending on the garment, plus a second or two the first time a body is seen. Only the
	/// last step, creating the model, happens on the main thread. Fitted models are cached, so
	/// asking again is free. Call it from the main thread.
	///
	/// A garment's LODs are fitted one at a time, roughest first, because the roughest takes a
	/// fraction of the time. Pass <paramref name="onRough"/> to wear those while waiting.
	/// </summary>
	/// <param name="madeFor">Which stock body this garment model was made for.</param>
	/// <param name="onRough">
	/// Called on the main thread with a model made of the LODs fitted so far, each time one more
	/// is ready. Not called for the finished model, that is what the task returns.
	/// </param>
	public static Task<Model> FitAsync( Model body, string garmentPath, BodyKind madeFor = BodyKind.Citizen, Action<Model> onRough = null )
	{
		if ( body is null || string.IsNullOrEmpty( garmentPath ) )
			return Task.FromResult<Model>( null );

		string bodyPath = Normalize( body.ResourcePath );
		if ( bodyPath == ReferencePaths[(int)madeFor] )
			return Task.FromResult<Model>( null );

		string key = $"{bodyPath}|{Normalize( garmentPath )}";
		if ( fitted.TryGetValue( key, out var cached ) && cached.IsValid() )
			return Task.FromResult( cached );
		if ( failed.Contains( key ) )
			return Task.FromResult<Model>( null );

		// Two characters putting on the same thing at once share one job.
		if ( !pending.TryGetValue( key, out var job ) || job.Task.IsCompleted )
		{
			pending[key] = job = new Job();
			job.Task = FitJob( bodyPath, garmentPath, madeFor, key, job );
		}

		if ( onRough != null && !job.Task.IsCompleted )
		{
			job.Watching.Add( onRough );
			if ( job.Rough.IsValid() ) onRough( job.Rough );
		}

		return job.Task;
	}

	static async Task<Model> FitJob( string bodyPath, string garmentPath, BodyKind madeFor, string key, Job job )
	{
		int started = generation;
		try
		{
			// The caches are only touched on the main thread, so get back on it after every wait.
			var reference = await ReferenceAsync( madeFor ) ?? throw new FitException( $"{ReferencePaths[(int)madeFor]} is not available" );
			await GameTask.MainThread();
			var body = await BodyAsync( bodyPath );
			await GameTask.MainThread();
			var target = await MappedAsync( body, madeFor );
			await GameTask.MainThread();
			await ExposedAsync( body, target );
			await GameTask.MainThread();
			if ( started != generation ) return null;   // Clear() was called meanwhile, whoever wants this will ask again

			var file = ReadCompiled( garmentPath );
			var levels = await OnWorker( () => CompiledModel.ReadLods( file ) );

			// Roughest level first. Each one is a step of its own, so the rough levels of every
			// garment in the queue get done before anyone's detailed ones.
			var done = new List<Fitted>();
			Model model = null;
			long worker = 0;
			double main = 0;
			for ( int i = levels.Count - 1; i >= 0; i-- )
			{
				var level = levels[i];
				var result = await OnWorker( () => FitGarment( level, reference, target ) );
				if ( started != generation ) return null;

				var building = System.Diagnostics.Stopwatch.StartNew();
				MakeMeshes( result, $"fitted/{key}/{i}" );
				done.Insert( 0, result );
				model = Build( done, $"fitted/{key}" + (i > 0 ? $"/from{i}" : "") );
				worker += result.Milliseconds;
				main += building.Elapsed.TotalMilliseconds;

				if ( i == 0 ) break;
				job.Rough = model;
				foreach ( var show in job.Watching ) show( model );
			}

			job.Watching.Clear();
			fitted[key] = model;
			Log.Info( $"ClothingFitter: fitted {NameOf( garmentPath )} to {NameOf( bodyPath )} in {worker} ms on worker threads and {main:F1} ms on the main thread ({levels.Count} LODs, {string.Join( " + ", levels.Select( x => x.Positions.Length ) )} verts, roughest ready after {done[^1].Milliseconds} ms)" );
			return model;
		}
		catch ( TaskCanceledException )
		{
			// The game was stopped while this was in the works. Not the garment's fault.
			return null;
		}
		catch ( Exception e )
		{
			// One garment in a format we don't read must not take the whole outfit down.
			await GameTask.MainThread();
			if ( started == generation )
			{
				failed.Add( key );
				Log.Warning( $"ClothingFitter: couldn't fit {garmentPath} to {bodyPath}, using it as is. {Describe( e )}" );
			}
			return null;
		}
	}

	// A step that was cut short because the game stopped must not be kept: it would hand its
	// cancellation to everyone who asks later.
	static bool Usable( Task job ) => job is not null && !job.IsCanceled;

	/// <summary>
	/// Which stock body this model's skeleton is closest to. Citizen if it can't be told.
	/// Worked out on worker threads the first time a body is asked about. Call it from the
	/// main thread.
	/// </summary>
	public static async Task<BodyKind> KindOfAsync( Model body )
	{
		if ( body is null ) return BodyKind.Citizen;
		string bodyPath = Normalize( body.ResourcePath );

		try
		{
			var kind = (await BodyAsync( bodyPath )).Kind;
			await GameTask.MainThread();
			return kind;
		}
		catch ( TaskCanceledException )
		{
		}
		catch ( Exception e )
		{
			Log.Warning( $"ClothingFitter: couldn't read {bodyPath}. {Describe( e )}" );
		}

		await GameTask.MainThread();
		return BodyKind.Citizen;
	}

	/// <summary>
	/// Fits one garment to one body and reports what came out, without touching any scene.
	/// The third argument says which stock body the garment was made for: citizen (default), male or female.
	/// clothingfitter_test models/citizen_custom/citizen_custom.vmdl models/citizen_clothes/jacket/biker_jacket/models/biker_jacket.vmdl
	/// </summary>
	[ConCmd( "clothingfitter_test" )]
	public static void Test( string bodyPath, string garmentPath, string madeFor = "citizen" )
	{
		_ = TestAsync( bodyPath, garmentPath, madeFor );
	}

	static async Task TestAsync( string bodyPath, string garmentPath, string madeFor )
	{
		var body = Model.Load( bodyPath );
		if ( !body.IsValid() || body.IsError )
		{
			Log.Warning( $"clothingfitter_test: no body model at {bodyPath}" );
			return;
		}

		var kind = madeFor == "male" ? BodyKind.HumanMale : madeFor == "female" ? BodyKind.HumanFemale : BodyKind.Citizen;

		// Start from scratch so the command always shows the current code's result.
		Clear();
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var model = await FitAsync( body, garmentPath, kind );
		if ( model is null )
		{
			Log.Warning( "clothingfitter_test: nothing was fitted" );
			return;
		}

		var original = Model.Load( garmentPath );
		Log.Info( $"clothingfitter_test: ok, {watch.ElapsedMilliseconds} ms from a cold start. {model.BoneCount} bones (original {original.BoneCount}), {model.MeshCount} meshes, {model.Materials.Count()} materials, bounds {model.Bounds.Size} (original {original.Bounds.Size})" );
	}

	/// <summary>
	/// Prints what the fitter sees in a body model: its size, which stock skeleton it is closest
	/// to and how it compares to each.
	/// clothingfitter_info models/citizen_custom/citizen_custom.vmdl
	/// </summary>
	[ConCmd( "clothingfitter_info" )]
	public static void Info( string bodyPath )
	{
		_ = InfoAsync( bodyPath );
	}

	static async Task InfoAsync( string bodyPath )
	{
		try
		{
			var body = await BodyAsync( Normalize( bodyPath ) );
			await GameTask.MainThread();
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
				var reference = await ReferenceAsync( (BodyKind)k );
				await GameTask.MainThread();
				if ( reference is null ) continue;
				float spread = Proportions( geo, reference.Geo, out int shared, out float size );
				Log.Info( $"clothingfitter_info   vs {(BodyKind)k}: {shared} of {reference.Geo.BoneNames.Length} bones in common, {size:F2} times its size, proportions off by {spread * 100f:F0}%" );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"clothingfitter_info {bodyPath}: {Describe( e )}" );
		}
	}

	// Runs one step on a worker thread and comes back to the main thread with what it made.
	// The engine warns about a step that keeps a worker busy for over a second, which is why
	// the work is cut into steps rather than done in one go.
	static async Task<T> OnWorker<T>( Func<T> step )
	{
		T result = default;
		string error = null;
		await GameTask.RunInThreadAsync( () =>
		{
			try
			{
				result = step();
			}
			catch ( Exception e )
			{
				error = $"{e.GetType().Name}: {e.Message} {Where( e )}";
			}
		} );
		await GameTask.MainThread();

		if ( error != null ) throw new FitException( error );
		return result;
	}

	static string Describe( Exception e ) =>
		e is FitException ? e.Message : $"{e.GetType().Name}: {e.Message} {Where( e )}";

	// The first couple of frames of where an exception came from, for the log.
	static string Where( Exception e ) =>
		string.Join( " <- ", (e.StackTrace ?? "").Split( '\n' ).Take( 3 ).Select( x => x.Trim() ) );

	static string NameOf( string path ) => System.IO.Path.GetFileNameWithoutExtension( path );

	// Null if the reference body isn't there or can't be read. Said once in the log.
	static Task<Reference> ReferenceAsync( BodyKind kind )
	{
		if ( !Usable( references[(int)kind] ) )
			references[(int)kind] = Load( ReferencePaths[(int)kind] );
		return references[(int)kind];

		static async Task<Reference> Load( string path )
		{
			try
			{
				var file = ReadCompiled( path );
				return await OnWorker( () =>
				{
					var geo = CompiledModel.Read( file );
					var mesh = new TriMesh( geo.Positions, geo.Indices );
					return new Reference { Geo = geo, Mesh = mesh, Skin = new SkinByBone( mesh, geo ) };
				} );
			}
			catch ( TaskCanceledException )
			{
				throw;
			}
			catch ( Exception e )
			{
				await GameTask.MainThread();
				Log.Warning( $"ClothingFitter: can't use {path} as a reference body. {Describe( e )}" );
				return null;
			}
		}
	}

	static Task<Body> BodyAsync( string bodyPath )
	{
		if ( !bodies.TryGetValue( bodyPath, out var job ) || !Usable( job ) )
			bodies[bodyPath] = job = Load( bodyPath );
		return job;

		static async Task<Body> Load( string bodyPath )
		{
			var file = ReadCompiled( bodyPath );
			var body = new Body { Path = bodyPath, Geo = await OnWorker( () => CompiledModel.Read( file ) ) };

			// The closest stock skeleton is the one with the most bones in common and, among those,
			// the most similar proportions. Overall size and facing don't matter for that.
			float best = float.MaxValue;
			int mostShared = 0;
			for ( int k = 0; k < references.Length; k++ )
			{
				var reference = await ReferenceAsync( (BodyKind)k );
				await GameTask.MainThread();
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

			// The two humans share a skeleton, so bones can't tell them apart. The shape can:
			// take the one whose skin has to move less to become this body.
			if ( body.Kind != BodyKind.Citizen && await ReferenceAsync( BodyKind.HumanMale ) is not null && await ReferenceAsync( BodyKind.HumanFemale ) is not null )
			{
				await GameTask.MainThread();
				var male = MappedAsync( body, BodyKind.HumanMale );
				var female = MappedAsync( body, BodyKind.HumanFemale );
				body.Kind = AverageMove( await female ) < AverageMove( await male ) ? BodyKind.HumanFemale : BodyKind.HumanMale;
			}

			await GameTask.MainThread();
			return body;
		}
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

	// Main thread.
	static Task<Mapped> MappedAsync( Body body, BodyKind kind )
	{
		if ( !body.Maps.TryGetValue( kind, out var job ) || !Usable( job ) )
			body.Maps[kind] = job = Map( body, kind );
		return job;

		static async Task<Mapped> Map( Body body, BodyKind kind )
		{
			var reference = await ReferenceAsync( kind ) ?? throw new FitException( $"{ReferencePaths[(int)kind]} is not available" );
			var geo = body.Geo;
			var mapped = await OnWorker( () =>
			{
				var watch = System.Diagnostics.Stopwatch.StartNew();

				// Clothing is bone-merged, so a body with its own rest pose has to be compared in the
				// reference's rest pose and proportions. No-op for a body that already shares them.
				var result = new Mapped { Pose = Repose.ToStockPose( geo, reference.Geo ) };
				result.Mesh = new TriMesh( result.Pose.Positions, geo.Indices );

				// The body's own skinning, in terms of the reference's bones, tells which part of it
				// belongs to which bone.
				var skin = new SkinByBone( result.Mesh, GarmentFit.BonesOnBody( geo, reference.Geo ), geo.BoneWeight, reference.Geo.BoneParents );
				result.Map = BodyMap.Build( reference.Geo, reference.Mesh, result.Mesh, geo.Uvs, skin, result.Pose.Landmarks );
				result.Milliseconds = watch.ElapsedMilliseconds;
				return result;
			} );

			string name = NameOf( body.Path );
			float low = mapped.Pose.BoneScale.Min(), high = mapped.Pose.BoneScale.Max();
			Log.Info( $"ClothingFitter: mapped {name} against {kind} in {mapped.Milliseconds} ms. {mapped.Map.ByUv} skin points matched by UV, {mapped.Map.ByRays} by bone rays, {mapped.Map.NoMatch} filled in from neighbours. Rest pose differs by {mapped.Pose.Moved / Units.Metre * 100f:F0} cm on average, bones are {low:F2} to {high:F2} times as long" );
			if ( mapped.Pose.SharedBones < reference.Geo.BoneNames.Length / 2 )
				Log.Warning( $"ClothingFitter: {name} has only {mapped.Pose.SharedBones} of {kind}'s {reference.Geo.BoneNames.Length} bones. Clothing follows bones by name, so it won't sit or animate properly on it" );
			return mapped;
		}
	}

	// Main thread. Not done along with the mapping: working out which stock body is closest
	// maps against more than one of them, and only the one that gets worn needs this.
	static Task ExposedAsync( Body body, Mapped mapped )
	{
		if ( !Usable( mapped.Exposed ) )
			mapped.Exposed = Find( body, mapped );
		return mapped.Exposed;

		static async Task Find( Body body, Mapped mapped )
		{
			long took = await OnWorker( () =>
			{
				var watch = System.Diagnostics.Stopwatch.StartNew();
				mapped.Mesh.FindExposed();
				return watch.ElapsedMilliseconds;
			} );
			Log.Info( $"ClothingFitter: found the outer surface of {NameOf( body.Path )} in {took} ms" );
		}
	}

	// Worker thread. Fits one LOD level of a garment and lays its vertices out the way the mesh buffers want
	// them. Only reads the reference and the body, so any number of these can run at once.
	static Fitted FitGarment( SkinnedGeometry garment, Reference reference, Mapped target )
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var bones = GarmentFit.BonesOnBody( garment, reference.Geo );
		var positions = GarmentFit.Fit( garment.Positions, garment.Indices, bones, garment.BoneWeight, reference.Mesh, target.Mesh, target.Map.SkinMove, reference.Skin );

		// The fit happens in the stock skeleton's proportions. Take it back to the body's.
		positions = target.Pose.FromStockProportions( positions, bones, garment.BoneWeight, reference.Geo );

		var normals = garment.HasNormals ? Shading.Normals( garment.Positions, positions, garment.Indices, garment.Normals ) : Shading.Smooth( positions, garment.Indices );
		var tangents = Shading.Tangents( positions, normals, garment.Uvs, garment.Indices, out var signs );

		// One mesh per draw call, each with only the vertices it uses.
		var result = new Fitted { Garment = garment };
		foreach ( var (first, count, material) in garment.Draws )
		{
			var remap = new Dictionary<int, int>();
			var vertices = new List<FittedVertex>();
			var indices = new List<int>( count );
			for ( int i = first; i < first + count; i++ )
			{
				int v = garment.Indices[i];
				if ( !remap.TryGetValue( v, out int local ) )
				{
					local = vertices.Count;
					remap[v] = local;
					vertices.Add( MakeVertex( garment, v, positions[v], normals[v], tangents[v], signs[v] ) );
				}
				indices.Add( local );
			}

			if ( vertices.Count > 0 )
				result.Draws.Add( (material, vertices, indices) );
		}

		result.Milliseconds = watch.ElapsedMilliseconds;
		return result;
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

	// Main thread: the engine only makes meshes and models there.
	static void MakeMeshes( Fitted level, string name )
	{
		for ( int d = 0; d < level.Draws.Count; d++ )
		{
			var (materialPath, vertices, indices) = level.Draws[d];
			var material = string.IsNullOrEmpty( materialPath ) ? null : Material.Load( materialPath );
			var mesh = new Mesh( $"{name}_{d}", material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = BBox.FromPoints( vertices.Select( x => x.Position ) );
			level.Meshes.Add( mesh );
		}

		level.Draws = null;   // the buffers have them now
	}

	/// <summary>
	/// A model out of the LOD levels fitted so far, most detailed first. While the detailed
	/// ones are still missing, the best one there is stands in for them.
	/// </summary>
	static Model Build( List<Fitted> levels, string name )
	{
		var g = levels[0].Garment;
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

		for ( int lod = 0; lod < g.LodDistances.Length && lod < 8; lod++ )
			builder.WithLodDistance( lod, g.LodDistances[lod] );

		// Every LOD the engine can ask for has to draw something. A level takes the LODs the
		// garment gave it, the most detailed one also takes whatever is below it, and the
		// roughest takes everything above.
		for ( int k = 0; k < levels.Count; k++ )
		{
			int mask = levels[k].Garment.LodMask;
			int lowest = 0;
			while ( lowest < 7 && (mask & (1 << lowest)) == 0 ) lowest++;
			if ( k == 0 ) mask |= (1 << lowest) - 1;
			if ( k == levels.Count - 1 ) mask |= 255 & ~((1 << lowest) - 1);

			foreach ( var mesh in levels[k].Meshes )
			{
				if ( mask == 255 )
				{
					builder.AddMesh( mesh );
					continue;
				}

				for ( int lod = 0; lod < 8; lod++ )
					if ( (mask & (1 << lod)) != 0 ) builder.AddMesh( mesh, lod );
			}
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
