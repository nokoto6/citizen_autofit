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
		public int[] Stiff;   // per bone, the foot it is part of (its ankle), -1 if none: a foot changes as a whole from body to body
	}

	// A body brought into one reference's pose, and where that reference's skin went on it.
	sealed class Mapped
	{
		public Repose Pose;
		public TriMesh Mesh;
		public BodyMap Map;
		public int[] BodyBones;     // the body's skinning, as the reference's bones
		public float[] BodyWeights;
		public bool[] Pivots;       // reference bones that only turn something (the eyes), not skin to follow
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
		public List<Draw> Draws = new();
		public long Milliseconds;

		// The fit in the stock pose and proportions, for rough levels to follow.
		public Vec3[] StockFit;

		// The garment's bones in the body's rest pose, in the garment's own bone order.
		public Vec3[] BonePositions;
		public float[][] BoneRotations;

		// The fitted model's skeleton: the garment's bones, less the ones the body lacks, plus
		// the stock bones that stand in for those. Parents first.
		public List<string> BoneNames = new();
		public List<int> BoneParents = new();
		public List<Vec3> OutPositions = new();
		public List<float[]> OutRotations = new();
	}

	// One draw call's worth of a fitted garment: what goes into one mesh.
	sealed class Draw
	{
		public string Material;
		public List<FittedVertex> Vertices = new();
		public List<int> Indices = new();
		public Dictionary<string, MorphDelta[]> Morphs = new();

		// World units per UV unit. Texture streaming works out from it which mip a mesh at a
		// given distance needs; a mesh without it gets the smallest and its textures stay
		// blurred no matter how close it is.
		public float UvDensity;
	}

	// The pixels of a morph texture, read on the main thread for a worker to decode.
	sealed class Atlas
	{
		public byte[] Rgba;
		public int Width, Height;
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
	static readonly Dictionary<string, bool> skinned = new();   // per garment, whether it draws part of itself with the body's skin
	static readonly Dictionary<string, (Vec3[] Positions, int[] Indices)> shapes = new();   // fitted garments in the stock pose, for others to be fitted under
	static readonly Dictionary<string, bool> proportioned = new();   // per body, whether it is built with proportions of its own
	static int generation;

	// Models taken out of use, held on to for a while. A model let go of right away can be
	// freed while the frame that still draws it is in flight, and the GPU then composites the
	// morphs of a texture that is gone (VK_ERROR_DEVICE_LOST in morph_composite).
	static readonly List<(Model Model, float At)> retired = new();
	const float RetireTime = 5f;

	/// <summary>
	/// Lets go of a model no longer worn, but not before the frames that may still draw it
	/// are done with it.
	/// </summary>
	internal static void Retire( Model model )
	{
		float now = RealTime.Now;
		retired.RemoveAll( x => now - x.At > RetireTime );
		if ( model.IsValid() ) retired.Add( (model, now) );
	}

	/// <summary>
	/// Forgets everything fitted so far. Call it after a body model has been re-exported,
	/// otherwise clothing keeps being fitted to the old shape.
	/// </summary>
	public static void Clear()
	{
		foreach ( var model in fitted.Values ) Retire( model );
		foreach ( var job in pending.Values ) Retire( job.Rough );
		Array.Clear( references, 0, references.Length );
		bodies.Clear();
		fitted.Clear();
		shapes.Clear();
		proportioned.Clear();
		atlasCache.Clear();
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
	/// <param name="withoutSkin">Leave out the parts of the garment that are drawn with the body's skin material. See <see cref="HasSkin"/>.</param>
	/// <param name="under">
	/// Another garment this one is worn under: hair under a hat. The hair is fitted so that it
	/// doesn't come through the hat, on any body, the stock one included.
	/// </param>
	public static Task<Model> FitAsync( Model body, string garmentPath, BodyKind madeFor = BodyKind.Citizen, Action<Model> onRough = null, bool withoutSkin = false, string under = null )
	{
		if ( body is null || string.IsNullOrEmpty( garmentPath ) )
			return Task.FromResult<Model>( null );
		return FitAsync( Normalize( body.ResourcePath ), garmentPath, madeFor, onRough, withoutSkin, under );
	}

	static Task<Model> FitAsync( string bodyPath, string garmentPath, BodyKind madeFor, Action<Model> onRough, bool withoutSkin, string under )
	{
		if ( bodyPath == ReferencePaths[(int)madeFor] && under == null )
			return Task.FromResult<Model>( null );

		string key = $"{bodyPath}|{Normalize( garmentPath )}{(withoutSkin ? "|no skin" : "")}{(under != null ? "|under " + Normalize( under ) : "")}";
		if ( fitted.TryGetValue( key, out var cached ) && cached.IsValid() )
			return Task.FromResult( cached );
		if ( failed.Contains( key ) )
			return Task.FromResult<Model>( null );

		// Two characters putting on the same thing at once share one job.
		if ( !pending.TryGetValue( key, out var job ) || job.Task.IsCompleted )
		{
			pending[key] = job = new Job();
			job.Task = FitJob( bodyPath, garmentPath, madeFor, key, job, withoutSkin, under );
		}

		if ( onRough != null && !job.Task.IsCompleted )
		{
			job.Watching.Add( onRough );
			if ( job.Rough.IsValid() ) onRough( job.Rough );
		}

		return job.Task;
	}

	/// <summary>
	/// True if this body is built with other proportions than every stock body: a leg, the
	/// spine or an arm more than a few percent longer or shorter. Such a body has to ignore the
	/// bone moves in the stock animations, or they would pull it to the stock proportions, so
	/// whatever the animations do by moving bones (making the body taller or shorter) it
	/// doesn't get.
	/// </summary>
	public static bool OwnProportions( Model body )
	{
		if ( body is null || body.IsError ) return false;
		string path = body.ResourcePath is null ? null : Normalize( body.ResourcePath );
		if ( path is not null && proportioned.TryGetValue( path, out bool known ) ) return known;

		bool own = true;
		foreach ( string reference in ReferencePaths )
		{
			var stock = Model.Load( reference );
			if ( stock is null || stock.IsError ) continue;
			bool same = true;
			foreach ( var (from, to) in Limbs )
			{
				float ours = Length( stock, from, to ), theirs = Length( body, from, to );
				if ( ours > 0 && theirs > 0 && MathF.Abs( theirs / ours - 1 ) > SameProportions ) same = false;
			}
			if ( same ) { own = false; break; }
		}

		if ( path is not null ) proportioned[path] = own;
		return own;
	}

	static readonly (string From, string To)[] Limbs = { ("leg_upper_L", "ankle_L"), ("pelvis", "neck_0"), ("arm_upper_L", "hand_L") };
	const float SameProportions = 0.03f;   // limbs this much longer or shorter still make the same body

	// From one bone to another in the bind pose. 0 if the model has either one missing.
	static float Length( Model model, string from, string to )
	{
		if ( model.Bones.GetBone( from ) is null || model.Bones.GetBone( to ) is null ) return 0;
		return model.GetBoneTransform( from ).Position.Distance( model.GetBoneTransform( to ).Position );
	}

	/// <summary>
	/// True if this garment draws part of itself with the body's skin material. Some clothing
	/// does that: it hides a part of the body and carries its own copy of that skin instead, cut
	/// to sit under the cloth. The copy has the stock body's shape, which is no use on another
	/// body.
	/// </summary>
	public static bool HasSkin( string garmentPath )
	{
		string path = Normalize( garmentPath );
		if ( skinned.TryGetValue( path, out bool has ) ) return has;
		var model = Model.Load( garmentPath );
		has = model.IsValid() && !model.IsError && model.Materials.Any( m => m.IsValid() && IsSkin( m.ResourcePath ) );
		skinned[path] = has;
		return has;
	}

	// The citizen's skin materials sit in models/citizen/skin, the humans' in
	// models/citizen_human/bodies/male and female (body_mode_dark, female_body_mode_dark,
	// body_male_light). Everything else in those folders is eyes, mouths and underwear.
	static bool IsSkin( string material )
	{
		string path = Normalize( material );
		if ( !path.StartsWith( "models/citizen" ) ) return false;
		if ( path.Contains( "/skin/" ) ) return true;
		return path.Contains( "/bodies/" ) && System.IO.Path.GetFileName( path ).Contains( "body" );
	}

	// The shape of a garment on a body, in the stock pose, for something else to be fitted
	// under it. Fitted first if it hasn't been; on the stock body it is the garment as made.
	static async Task<TriMesh> ShapeAsync( string bodyPath, string garmentPath, BodyKind madeFor )
	{
		if ( bodyPath == ReferencePaths[(int)madeFor] )
		{
			var file = ReadCompiled( garmentPath );
			return await OnWorker( () => { var geo = CompiledModel.Read( file ); return new TriMesh( geo.Positions, geo.Indices ); } );
		}

		await FitAsync( bodyPath, garmentPath, madeFor, null, false, null );
		await GameTask.MainThread();
		if ( !shapes.TryGetValue( $"{bodyPath}|{Normalize( garmentPath )}", out var shape ) )
			throw new FitException( $"{NameOf( garmentPath )} couldn't be fitted, so nothing can go under it" );
		return await OnWorker( () => new TriMesh( shape.Positions, shape.Indices ) );
	}

	static async Task<Model> FitJob( string bodyPath, string garmentPath, BodyKind madeFor, string key, Job job, bool withoutSkin, string under )
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

			TriMesh underMesh = null;
			if ( under != null )
			{
				underMesh = await ShapeAsync( bodyPath, under, madeFor );
				await GameTask.MainThread();
				if ( started != generation ) return null;
				Log.Info( $"ClothingFitter: fitting {NameOf( garmentPath )} under {NameOf( under )} ({underMesh.TriCount} triangles)" );
			}

			var file = ReadCompiled( garmentPath );
			var levels = await OnWorker( () => CompiledModel.ReadLods( file ) );
			if ( started != generation ) return null;
			var reading = System.Diagnostics.Stopwatch.StartNew();
			var atlases = ReadAtlases( levels );
			double readingMs = reading.Elapsed.TotalMilliseconds;

			// Roughest level first. Each one is a step of its own, so the rough levels of every
			// garment in the queue get done before anyone's detailed ones.
			var done = new List<Fitted>();
			Model model = null;
			long worker = 0, roughest = 0;
			double main = readingMs;

			// A garment with morph targets (a beard) goes on once, finished. Every model swapped
			// under a renderer that composites morphs is one more chance to hand the GPU a morph
			// texture that has just been freed.
			bool preview = !levels.Any( x => x.Morphs.Any( set => set.Morphs.Count > 0 ) );
			for ( int i = levels.Count - 1; i >= 0; i-- )
			{
				var level = levels[i];
				var result = await OnWorker( () => FitGarment( level, reference, target, withoutSkin, atlases, underMesh ) );
				if ( started != generation ) return null;
				if ( i == levels.Count - 1 ) roughest = result.Milliseconds;
				if ( i == 0 ) shapes[key] = (result.StockFit, level.Indices);

				done.Insert( 0, result );
				worker += result.Milliseconds;

				// With rough levels, the model is built once more below, after they follow the
				// detailed one. Building it here too would only make a model to throw away.
				if ( i == 0 && levels.Count > 1 ) break;
				if ( i > 0 && !preview ) continue;

				await BuildTurn();
				if ( started != generation ) return null;
				var building = System.Diagnostics.Stopwatch.StartNew();
				model = Build( done, $"fitted/{key}" + (i > 0 ? $"/from{i}" : "") );
				main += building.Elapsed.TotalMilliseconds;

				if ( i == 0 ) break;
				Retire( job.Rough );
				job.Rough = model;
				foreach ( var show in job.Watching ) show( model );
			}

			// The rough levels were fitted on their own, which on a body far from the stock
			// one leaves them a little off the detailed level. Now that it's there, they follow it.
			if ( levels.Count > 1 )
			{
				for ( int i = 1; i < levels.Count; i++ )
				{
					var level = levels[i];
					var detailed = done[0];
					var result = await OnWorker( () => FitGarment( level, reference, target, withoutSkin, atlases, underMesh, detailed ) );
					if ( started != generation ) return null;
					done[i] = result;
					worker += result.Milliseconds;
				}

				await BuildTurn();
				if ( started != generation ) return null;
				var building = System.Diagnostics.Stopwatch.StartNew();
				model = Build( done, $"fitted/{key}/final" );
				main += building.Elapsed.TotalMilliseconds;
			}

			job.Watching.Clear();
			Retire( job.Rough );
			job.Rough = null;
			fitted[key] = model;
			Log.Info( $"ClothingFitter: fitted {NameOf( garmentPath )} to {NameOf( bodyPath )} in {worker} ms on worker threads and {main:F1} ms on the main thread{(readingMs >= 1 ? $" ({readingMs:F1} of them reading its morphs)" : "")} ({levels.Count} LODs, {string.Join( " + ", levels.Select( x => x.Positions.Length ) )} verts, roughest ready after {roughest} ms)" );
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
		finally
		{
			// Done either way. Finished jobs aren't kept around: the result is in the cache,
			// and a task left here would be one more thing for a hotload to trip over.
			if ( pending.TryGetValue( key, out var mine ) && mine == job )
				pending.Remove( key );
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
	// How many steps run on worker threads at once. A scene full of dressers asks for every
	// garment the moment it loads; without a limit that was dozens of threads at once and a
	// main thread swamped with their results.
	const int Workers = 3;
	static int working;
	static readonly Queue<TaskCompletionSource<bool>> waiting = new();

	static async Task<T> OnWorker<T>( Func<T> step )
	{
		// Wait for a free worker, first come first served: the rough levels of every garment
		// asked for are queued before anyone's detailed ones.
		await GameTask.MainThread();
		if ( working >= Workers )
		{
			var turn = new TaskCompletionSource<bool>();
			waiting.Enqueue( turn );
			await turn.Task;
			await GameTask.MainThread();
		}
		else
		{
			working++;
		}

		// No await inside a catch or finally here: the compiler turns that into
		// ExceptionDispatchInfo, which the whitelist doesn't allow. The step's own errors are
		// caught on the worker; only the game stopping can come through.
		T result = default;
		string error = null;
		bool cancelled = false;
		try
		{
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
		}
		catch ( TaskCanceledException )
		{
			cancelled = true;
		}

		await GameTask.MainThread();
		// Hand the worker straight to whoever waits longest, or give it back.
		if ( waiting.Count > 0 ) waiting.Dequeue().SetResult( true );
		else working--;

		if ( cancelled ) throw new TaskCanceledException();
		if ( error != null ) throw new FitException( error );
		return result;
	}

	// Models are made on the main thread, and a scene of dressers finishes many at once. One
	// per frame or so keeps loading from stalling a frame on a pile of them.
	const float BuildEvery = 0.016f;
	static float lastBuild = float.MinValue;

	static async Task BuildTurn()
	{
		await GameTask.MainThread();
		while ( RealTime.Now - lastBuild < BuildEvery && RealTime.Now >= lastBuild )
		{
			await GameTask.DelayRealtime( 5 );
			await GameTask.MainThread();
		}
		lastBuild = RealTime.Now;
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
					var stiff = new int[geo.BoneNames.Length];
					for ( int b = 0; b < stiff.Length; b++ )
					{
						string name = geo.BoneNames[b];
						stiff[b] = name.StartsWith( "ankle_" ) ? b : name.StartsWith( "ball_" ) ? Array.IndexOf( geo.BoneNames, "ankle_" + name.Substring( 5 ) ) : -1;
					}
					return new Reference { Geo = geo, Mesh = mesh, Skin = new SkinByBone( mesh, geo ), Stiff = stiff };
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
				result.BodyBones = GarmentFit.BonesOnBody( geo, reference.Geo );
				result.BodyWeights = geo.BoneWeight;
				result.Pivots = reference.Geo.BoneNames.Select( x => x.StartsWith( "eye_" ) ).ToArray();
				var skin = new SkinByBone( result.Mesh, result.BodyBones, geo.BoneWeight, reference.Geo.BoneParents );
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
	// Main thread. A garment's morph targets (a beard that follows the mouth) are stored as
	// textures. Only the engine can read those, so the pixels are fetched here.
	// Atlases read so far. Reading one makes the main thread wait for the GPU, and the same
	// beard on several bodies needs the same atlas each time.
	static readonly Dictionary<string, Atlas> atlasCache = new();

	static Dictionary<string, Atlas> ReadAtlases( List<SkinnedGeometry> levels )
	{
		var atlases = new Dictionary<string, Atlas>();
		foreach ( var set in levels.SelectMany( level => level.Morphs ) )
		{
			if ( string.IsNullOrEmpty( set.AtlasPath ) || atlases.ContainsKey( set.AtlasPath ) ) continue;
			if ( atlasCache.TryGetValue( set.AtlasPath, out var known ) )
			{
				atlases[set.AtlasPath] = known;
				continue;
			}

			var texture = Texture.Load( set.AtlasPath, false );
			if ( !texture.IsValid() || texture.IsError || texture.Width <= 0 ) continue;

			var pixels = texture.GetPixels();
			var atlas = new Atlas { Width = texture.Width, Height = texture.Height, Rgba = new byte[pixels.Length * 4] };
			for ( int i = 0; i < pixels.Length; i++ )
			{
				atlas.Rgba[i * 4] = pixels[i].r;
				atlas.Rgba[i * 4 + 1] = pixels[i].g;
				atlas.Rgba[i * 4 + 2] = pixels[i].b;
				atlas.Rgba[i * 4 + 3] = pixels[i].a;
			}

			atlases[set.AtlasPath] = atlas;
			atlasCache[set.AtlasPath] = atlas;
		}

		return atlases;
	}

	/// <param name="detailed">A more detailed level already fitted, for this one to follow instead of being fitted on its own.</param>
	// Morph moves smaller than these aren't seen, and a beard that every jaw and mouth morph
	// moves as a whole has close to a million deltas, all copied on the main thread when the
	// model is made. Most of the ones under the floor are the atlas's 8-bit rounding anyway.
	const float MorphFloor = 0.0001f * Units.Metre;   // 0.1 mm
	const float TurnFloor = 0.02f;                    // a normal turned by about a degree

	static bool Seen( Vec3 move, Vec3 turn ) =>
		move.LengthSquared() >= MorphFloor * MorphFloor || turn.LengthSquared() >= TurnFloor * TurnFloor;

	static Fitted FitGarment( SkinnedGeometry garment, Reference reference, Mapped target, bool withoutSkin, Dictionary<string, Atlas> atlases, TriMesh under = null, Fitted detailed = null )
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var bones = GarmentFit.BonesOnBody( garment, reference.Geo );
		// A garment's own copy of the skin is cut to lie right on the body. Left in the fit
		// when the skin is to be shown instead, it is what gets lifted over a bump of the body,
		// and the cloth over it stays put. So it is fitted without that copy.
		var skinCopy = GarmentFit.SkinCopies( garment, IsSkin );
		var cloth = garment.Indices;
		if ( withoutSkin )
		{
			var keep = new List<int>();
			for ( int d = 0; d < garment.Draws.Count; d++ )
				if ( !skinCopy[d] ) keep.AddRange( garment.Indices.Skip( garment.Draws[d].First ).Take( garment.Draws[d].Count ) );
			cloth = keep.ToArray();
		}
		var given = detailed != null ? GarmentFit.Follow( garment.Positions, detailed.Garment.Positions, detailed.Garment.Indices, detailed.StockFit ) : null;
		var solid = new bool[garment.Positions.Length];
		var positions = GarmentFit.Fit( garment.Positions, cloth, bones, garment.BoneWeight, reference.Mesh, target.Mesh, target.Map.SkinMove, reference.Skin, target.Map.Found, withoutSkin, given, under, solid, target.Pose.Eyes.ToArray(), reference.Stiff );
		var stockFit = positions;

		// The fit happens in the stock skeleton's proportions and bind pose. Take it back to
		// the body's, each bit of cloth the way the skin under it came in.
		var (backBones, backWeights) = Repose.SkinUnder( positions, garment.Indices, target.Mesh, target.BodyBones, target.BodyWeights, bones, garment.BoneWeight, solid, target.Pivots );
		positions = target.Pose.FromStockProportions( positions, backBones, backWeights, reference.Geo );
		positions = target.Pose.ToBodyRest( positions, backBones, backWeights );

		var normals = garment.HasNormals ? Shading.Normals( garment.Positions, positions, garment.Indices, garment.Normals ) : Shading.Smooth( positions, garment.Indices );
		var tangents = Shading.Tangents( positions, normals, garment.Uvs, garment.Indices, out var signs );

		// Morph targets are displacements of the stock shape. They turn with the surface they
		// are on and grow with the body, like everything else about the garment.
		var morphs = new Dictionary<string, (Vec3[] Position, Vec3[] Normal)>();
		foreach ( var set in garment.Morphs )
		{
			if ( set.AtlasPath == null || !atlases.TryGetValue( set.AtlasPath, out var atlas ) ) continue;
			foreach ( var (name, deltas) in set.Decode( atlas.Rgba, atlas.Width, atlas.Height ) )
			{
				if ( !morphs.TryGetValue( name, out var morph ) )
					morphs[name] = morph = (new Vec3[positions.Length], new Vec3[positions.Length]);
				foreach ( var (vertex, move, turn) in deltas )
				{
					morph.Position[vertex] = move * target.Pose.ScaleAt( vertex, backBones, backWeights );
					morph.Normal[vertex] = turn;
				}
			}
		}

		// The same turn for every morph, worked out once: a beard has dozens of them.
		if ( morphs.Count > 0 )
		{
			var frames = Shading.TurnFrames( garment.Positions, positions, garment.Indices );
			foreach ( var (move, turn) in morphs.Values )
			{
				Shading.TurnInPlace( frames, move );
				Shading.TurnInPlace( frames, turn );
			}
		}

		// The garment's bones in the body's rest pose.
		var result = new Fitted { Garment = garment, StockFit = stockFit, BonePositions = new Vec3[garment.BoneNames.Length], BoneRotations = new float[garment.BoneNames.Length][] };
		var boneMap = GarmentFit.BoneMap( garment, reference.Geo );
		for ( int i = 0; i < garment.BoneNames.Length; i++ )
		{
			int s = boneMap[i];
			// A bone the body has is the body's bone: its frame is whatever the body says it
			// is, since that is what bone merging will hand the garment. Anything else hangs
			// off the nearest such bone where the garment itself puts it.
			(result.BonePositions[i], result.BoneRotations[i]) =
				s >= 0 && target.Pose.BodyHas[s] && reference.Geo.BoneNames[s] == garment.BoneNames[i] ? (target.Pose.RestPositions[s], target.Pose.RestRotations[s])
				: s >= 0 ? target.Pose.GarmentBoneRest( s, garment.BonePositions[i], garment.BoneRotations[i] )
				: (garment.BonePositions[i], garment.BoneRotations[i]);
		}

		// A stock bone the body doesn't have (an older citizen without the twist bones, say)
		// is left in its bind pose by bone merging while the rest of the body animates. So
		// whatever is skinned to one is skinned to the nearest ancestor the body does have
		// instead. The garment's own bones, like a hat's jiggle bone, are kept: those follow
		// their parent. That ancestor needn't be in the garment at all (a shirt's sleeves can
		// hang off the twist bones alone), so the fitted model gets a skeleton of its own.
		var stock = reference.Geo;
		var stockIndex = new Dictionary<string, int>();
		for ( int i = 0; i < stock.BoneNames.Length; i++ ) stockIndex[stock.BoneNames[i]] = i;
		var outIndex = new Dictionary<string, int>();
		var redirect = new int[garment.BoneNames.Length];   // garment bone -> bone of the fitted model
		for ( int i = 0; i < garment.BoneNames.Length; i++ )
		{
			string name = garment.BoneNames[i];
			if ( stockIndex.TryGetValue( name, out int s ) && !target.Pose.BodyHas[s] )
			{
				int a = stock.BoneParents[s];
				while ( a >= 0 && !target.Pose.BodyHas[a] ) a = stock.BoneParents[a];
				if ( a >= 0 ) { name = stock.BoneNames[a]; s = a; }
			}

			if ( !outIndex.TryGetValue( name, out int index ) )
			{
				index = result.BoneNames.Count;
				outIndex[name] = index;
				result.BoneNames.Add( name );
				result.BoneParents.Add( garment.BoneParents[i] >= 0 ? redirect[garment.BoneParents[i]] : -1 );
				// A stand-in is a bone the body has, so it is the body's bone, frame and all.
				var (position, rotation) = name == garment.BoneNames[i]
					? (result.BonePositions[i], result.BoneRotations[i])
					: (target.Pose.RestPositions[s], target.Pose.RestRotations[s]);
				result.OutPositions.Add( position );
				result.OutRotations.Add( rotation );
			}

			redirect[i] = index;
		}

		// The cloth is worn with the skin's weights under it (SkinUnder): the body then moves
		// it exactly as it moves that skin, and neither comes through the other when a knee
		// or an elbow bends. By its own weights, cloth made for a body with twist and helper
		// bones is turned by bones this body never animates. Those weights may name stock
		// bones the garment never had, which join its skeleton here. A vertex on a bone of
		// the garment's own (a hat's jiggle bone) keeps its own weights, as does a solid piece.
		int OutBone( int s )
		{
			if ( outIndex.TryGetValue( stock.BoneNames[s], out int have ) ) return have;
			int p = stock.BoneParents[s];
			while ( p >= 0 && !target.Pose.BodyHas[p] ) p = stock.BoneParents[p];
			int parent = p >= 0 ? OutBone( p ) : -1;
			int index = result.BoneNames.Count;
			outIndex[stock.BoneNames[s]] = index;
			result.BoneNames.Add( stock.BoneNames[s] );
			result.BoneParents.Add( parent );
			result.OutPositions.Add( target.Pose.RestPositions[s] );
			result.OutRotations.Add( target.Pose.RestRotations[s] );
			return index;
		}

		var wornBones = new int[garment.BoneIndex.Length];
		var wornWeights = new float[garment.BoneIndex.Length];
		for ( int v = 0; v < positions.Length; v++ )
		{
			bool own = solid[v];
			for ( int j = 0; j < 4 && !own; j++ )
			{
				int gb = garment.BoneIndex[v * 4 + j];
				own = gb >= 0 && garment.BoneWeight[v * 4 + j] > 0 && (boneMap[gb] < 0 || stock.BoneNames[boneMap[gb]] != garment.BoneNames[gb]);
			}
			for ( int j = 0; j < 4; j++ )
			{
				if ( own )
				{
					int gb = garment.BoneIndex[v * 4 + j];
					wornBones[v * 4 + j] = gb >= 0 ? redirect[gb] : -1;
					wornWeights[v * 4 + j] = gb >= 0 ? garment.BoneWeight[v * 4 + j] : 0;
				}
				else
				{
					int sb = backBones[v * 4 + j];
					wornBones[v * 4 + j] = sb >= 0 && target.Pose.BodyHas[sb] ? OutBone( sb ) : -1;
					wornWeights[v * 4 + j] = sb >= 0 ? backWeights[v * 4 + j] : 0;
				}
			}
		}

		for ( int d = 0; d < garment.Draws.Count; d++ )
		{
			var (first, count, material) = garment.Draws[d];
			if ( withoutSkin && skinCopy[d] ) continue;

			var draw = new Draw { Material = material };
			var remap = new Dictionary<int, int>();
			for ( int i = first; i < first + count; i++ )
			{
				int v = garment.Indices[i];
				if ( !remap.TryGetValue( v, out int local ) )
				{
					local = draw.Vertices.Count;
					remap[v] = local;
					draw.Vertices.Add( MakeVertex( garment.Uvs[v], v, positions[v], normals[v], tangents[v], signs[v], wornBones, wornWeights ) );
				}
				draw.Indices.Add( local );
			}

			if ( draw.Vertices.Count == 0 ) continue;
			draw.UvDensity = UvDensity( positions, garment.Uvs, garment.Indices, first, count );

			foreach ( var (name, (move, turn)) in morphs )
			{
				// Counted first so the array is made once at its size: these run to hundreds of
				// thousands, and every regrown list is garbage the size of a garment.
				int seen = 0;
				foreach ( var (v, _) in remap )
					if ( Seen( move[v], turn[v] ) ) seen++;
				if ( seen == 0 ) continue;

				var deltas = new MorphDelta[seen];
				int at = 0;
				foreach ( var (v, local) in remap )
					if ( Seen( move[v], turn[v] ) )
						deltas[at++] = new MorphDelta( local, new Vector3( move[v].X, move[v].Y, move[v].Z ), new Vector3( turn[v].X, turn[v].Y, turn[v].Z ) );
				draw.Morphs[name] = deltas;
			}

			result.Draws.Add( draw );
		}

		result.Milliseconds = watch.ElapsedMilliseconds;
		return result;
	}

	static string Normalize( string path ) => (path ?? "").Replace( '\\', '/' ).ToLowerInvariant();

	// Square root of the surface area over the UV area of a draw call's triangles, which is what
	// the model compiler stores for a mesh, give or take.
	static float UvDensity( Vec3[] positions, Vec2[] uvs, int[] indices, int first, int count )
	{
		double world = 0, uv = 0;
		for ( int i = first; i + 2 < first + count; i += 3 )
		{
			int a = indices[i], b = indices[i + 1], c = indices[i + 2];
			world += Vec3.Cross( positions[b] - positions[a], positions[c] - positions[a] ).Length() * 0.5;
			float ux = uvs[b].X - uvs[a].X, uy = uvs[b].Y - uvs[a].Y, vx = uvs[c].X - uvs[a].X, vy = uvs[c].Y - uvs[a].Y;
			uv += Math.Abs( ux * vy - uy * vx ) * 0.5;
		}
		return uv > 1e-12 && world > 0 ? (float)Math.Sqrt( world / uv ) : 0f;
	}

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
	static Mesh MakeMesh( Draw draw, string name )
	{
		var material = string.IsNullOrEmpty( draw.Material ) ? null : Material.Load( draw.Material );
		var mesh = new Mesh( name, material );
		if ( draw.UvDensity > 0 ) mesh.UvDensity = draw.UvDensity;
		mesh.CreateVertexBuffer( draw.Vertices.Count, draw.Vertices );
		mesh.CreateIndexBuffer( draw.Indices.Count, draw.Indices );
		mesh.Bounds = BBox.FromPoints( draw.Vertices.Select( x => x.Position ) );
		foreach ( var (morph, deltas) in draw.Morphs )
			mesh.AddMorph( morph, deltas );
		return mesh;
	}

	/// <summary>
	/// A model out of the LOD levels fitted so far, most detailed first. While the detailed
	/// ones are still missing, the best one there is stands in for them.
	/// </summary>
	static Model Build( List<Fitted> levels, string name )
	{
		var g = levels[0].Garment;
		var builder = Model.Builder.WithName( name );

		// The fitted model's skeleton, in the body's rest pose. The builder wants each bone in
		// model space. Its doc comment says "relative to the parent", but the engine's own
		// importers (Sandbox.Mounting.GoldSrc) pass model space, and parent-relative
		// transforms come out as a wrecked bind pose.
		var skeleton = levels[0];
		for ( int i = 0; i < skeleton.BoneNames.Count; i++ )
		{
			var p = skeleton.OutPositions[i];
			var r = skeleton.OutRotations[i];
			int parent = skeleton.BoneParents[i];
			builder.AddBone( skeleton.BoneNames[i], new Vector3( p.X, p.Y, p.Z ), new Rotation( r[0], r[1], r[2], r[3] ), parent >= 0 ? skeleton.BoneNames[parent] : null );
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

			// A mesh can go to one LOD or to all of them, there is no in between. And the same
			// mesh can't be added twice or shared with another model: tried, and the skinning
			// of some LODs came out wrong (sleeves that don't follow the arms). So every LOD of
			// every model gets a mesh of its own. The copies are of the rough levels, which
			// are small.
			for ( int d = 0; d < levels[k].Draws.Count; d++ )
			{
				if ( mask == 255 )
				{
					builder.AddMesh( MakeMesh( levels[k].Draws[d], $"{name}_{k}_{d}" ) );
					continue;
				}

				for ( int lod = 0; lod < 8; lod++ )
					if ( (mask & (1 << lod)) != 0 ) builder.AddMesh( MakeMesh( levels[k].Draws[d], $"{name}_{k}_{d}_lod{lod}" ), lod );
			}
		}

		return builder.Create();
	}

	static FittedVertex MakeVertex( Vec2 uv, int v, Vec3 p, Vec3 n, Vec3 t, float sign, int[] bones, float[] weights )
	{
		// Weights go out as bytes and have to add up to exactly 255. The four slots needn't add
		// up to one coming in (a slot the body lacks is dropped), so they are shared out first.
		Span<int> bone = stackalloc int[4];
		Span<int> weight = stackalloc int[4];
		float sum = 0;
		for ( int j = 0; j < 4; j++ ) if ( bones[v * 4 + j] >= 0 ) sum += weights[v * 4 + j];
		int total = 0, heaviest = 0;
		for ( int j = 0; j < 4; j++ )
		{
			int index = bones[v * 4 + j];
			float w = index >= 0 && sum > 0 ? weights[v * 4 + j] / sum : 0f;
			bone[j] = index >= 0 ? index : 0;
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
			TexCoord = new Vector2( uv.X, uv.Y ),
			BlendIndices = new Color32( (byte)bone[0], (byte)bone[1], (byte)bone[2], (byte)bone[3] ),
			BlendWeights = new Color32( (byte)Math.Clamp( weight[0], 0, 255 ), (byte)Math.Clamp( weight[1], 0, 255 ), (byte)Math.Clamp( weight[2], 0, 255 ), (byte)Math.Clamp( weight[3], 0, 255 ) ),
		};
	}
}
