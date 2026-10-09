using System;
using System.Collections.Generic;

namespace Sandbox.Autofit;

/// <summary>
/// The features of a face, each a point on its skin, found on a body in the stock pose by
/// looking at it straight from the front: the inner and outer corner of each eye's opening and
/// the lines over and under it, where the brows start over each eye, the corners and the middle
/// of the mouth, the tip of the nose, the chin and the crown.
///
/// A ray is cast at the face for every pixel of a picture as wide as the head and as tall as
/// from below the chin to the forehead, and what each ray meets first tells skin from the rest:
/// skin is drawn with the material the face is drawn with or one about as big, and the rest
/// (eyes, lashes, brows, a line painted round an eye, the inside of a mouth) with small ones of
/// its own. An eye's opening is where the eye shows, what an eye bone carries or a hole with the
/// skin far behind it; a mouth is where the profile dips between the lips.
///
/// What paint on the skin goes by (<see cref="BodyMap.OnSkin"/>), more than the eyes and the
/// mouth other landmarks go by: a line drawn round the eyes of a face whose eyes are twice as
/// tall has to go round them too.
/// </summary>
public static class FaceMarks
{
	const int Across = 64;              // pixels from one eye to the other
	const float Wide = 1.6f;            // the picture reaches this many times the eyes' distance to either side of their middle
	const float Above = 1.0f;           // and this far above them
	const float Below = 1.5f;           // and this far below
	const int HitsKept = 4;             // surfaces along a ray that are looked at
	const float SkinMaterial = 0.33f;   // a material with this share of the face's material's area or more is skin too
	const float EyeSearch = 0.2f;       // how far from an eye's front its opening is looked for, as a share of the eyes' distance
	const float EyeBox = 0.8f;          // and how far round it the opening can reach
	const float EyeDeep = 0.5f;         // through an anime eye's white the skin is at least this much further back
	const float EyeWeight = 0.9f;       // a vertex this much on an eye bone is part of the eye
	const int MinEye = 6;               // pixels an eye's opening has at least
	const float BrowSearch = 0.6f;      // how far above an eye's opening a brow is looked for
	const int MinBrow = 8;              // pixels a brow has at least
	const float LidStep = 0.06f;        // a lid stands out over the eye this much more than the forehead above it at least
	const float NoseSearch = 0.8f;      // how far below the eyes the tip of the nose can be
	const float MouthSearch = 1.2f;     // how far below the nose the mouth can be
	const float GrooveDepth = 0.01f;    // the groove between the lips is at least this much deeper than the lips over and under it
	const int MouthAround = 3;          // pixels above and below that the lips are looked for
	const int GrooveDrift = 1;          // how many rows the groove goes up or down from one column to the next
	const float MouthWide = 0.6f;       // how far to either side of the middle a mouth reaches at most
	const float ChinBack = 0.25f;       // going down from the mouth, the profile this far behind its front is the neck
	const float CrownBand = 0.1f;       // the crown is the highest skin this close to the middle

	/// <summary>The marks round an eye's opening, after "eyeL" or "eyeR": from the inner corner over the top to the outer one and back under it.</summary>
	public static readonly string[] Outline = { ".in", ".top.1", ".top.2", ".top.3", ".out", ".bottom.3", ".bottom.2", ".bottom.1" };

	/// <summary>Named points on the skin, in the given positions. A feature that couldn't be found is left out.</summary>
	/// <param name="mesh">The body in those positions.</param>
	/// <param name="left">The front of the eye on the +Y side.</param>
	/// <param name="right">The front of the other eye.</param>
	public static Dictionary<string, Vec3> Find( SkinnedGeometry geo, Vec3[] positions, TriMesh mesh, Vec3 left, Vec3 right )
	{
		var marks = new Dictionary<string, Vec3>();
		float apart = (left - right).Length();
		if ( apart < 1e-4f * Units.Metre || mesh.TriCount == 0 ) return marks;
		// What each triangle is drawn with.
		var materials = new Dictionary<string, int>();
		var materialOf = new int[mesh.TriCount];
		var materialArea = new List<float>();
		foreach ( var (first, count, material) in geo.Draws )
		{
			if ( !materials.TryGetValue( material ?? "", out int id ) ) { materials[material ?? ""] = id = materials.Count; materialArea.Add( 0 ); }
			for ( int k = first; k + 2 < first + count && k / 3 < materialOf.Length; k += 3 )
			{
				materialOf[k / 3] = id;
				Vec3 a = positions[geo.Indices[k]], b = positions[geo.Indices[k + 1]], c = positions[geo.Indices[k + 2]];
				materialArea[id] += 0.5f * Vec3.Cross( b - a, c - a ).Length();
			}
		}

		// The picture: what the ray meets first and the first few surfaces along it.
		float pixel = apart / Across;
		Vec3 middle = 0.5f * (left + right);
		int width = (int)(2f * Wide * Across) + 1, height = (int)((Above + Below) * Across) + 1;
		float y0 = middle.Y - Wide * apart, z0 = middle.Z + Above * apart;
		float front = float.MinValue;
		foreach ( var p in positions ) front = MathF.Max( front, p.X );
		front += 0.1f * Units.Metre;
		var hitCount = new int[width * height];
		var hitMaterial = new int[width * height * HitsKept];
		var hitX = new float[width * height * HitsKept];
		var hits = new List<(float T, bool Leaving, int Tri)>();
		for ( int j = 0; j < height; j++ )
		{
			for ( int i = 0; i < width; i++ )
			{
				int p = j * width + i;
				mesh.Crossings( new Vec3( front, y0 + i * pixel, z0 - j * pixel ), new Vec3( -1, 0, 0 ), 2 * Units.Metre, hits );
				hitCount[p] = Math.Min( hits.Count, HitsKept );
				for ( int k = 0; k < hitCount[p]; k++ )
				{
					hitMaterial[p * HitsKept + k] = materialOf[hits[k].Tri];
					hitX[p * HitsKept + k] = front - hits[k].T;
				}
			}
		}

		// Skin is what the face is drawn with, the material seen most at the height of the eyes,
		// and anything drawn with a material about as big. Eyes, lashes, brows and the inside of
		// a mouth have small materials of their own.
		var seenAtEyes = new int[materials.Count];
		int eyeRow0 = (int)(Above * Across);
		for ( int j = Math.Max( 0, eyeRow0 - Across / 4 ); j < Math.Min( height, eyeRow0 + Across / 4 ); j++ )
			for ( int i = 0; i < width; i++ )
				if ( hitCount[j * width + i] > 0 ) seenAtEyes[hitMaterial[(j * width + i) * HitsKept]]++;
		int faceMaterial = 0;
		for ( int m = 1; m < seenAtEyes.Length; m++ ) if ( seenAtEyes[m] > seenAtEyes[faceMaterial] ) faceMaterial = m;
		var skinMaterial = new bool[materials.Count];
		for ( int m = 0; m < skinMaterial.Length; m++ ) skinMaterial[m] = materialArea[m] >= SkinMaterial * materialArea[faceMaterial];

		// What an eye bone carries on its own is the eye: an eyeball, an anime eye's iris. Lashes
		// can go halfway with it.
		var eyeMaterial = new bool[materials.Count];
		var eyeBone = new bool[geo.BoneNames.Length];
		for ( int b = 0; b < eyeBone.Length; b++ ) eyeBone[b] = geo.BoneNames[b].StartsWith( "eye_" );
		for ( int t = 0; t < materialOf.Length; t++ )
		{
			if ( eyeMaterial[materialOf[t]] ) continue;
			for ( int k = 0; k < 3; k++ )
			{
				int v = geo.Indices[t * 3 + k];
				for ( int j = 0; j < 4; j++ )
				{
					int b = geo.BoneIndex[v * 4 + j];
					if ( b >= 0 && b < eyeBone.Length && eyeBone[b] && geo.BoneWeight[v * 4 + j] >= EyeWeight ) eyeMaterial[materialOf[t]] = true;
				}
			}
		}

		var hit = new bool[width * height];
		var onSkin = new bool[width * height];     // what the ray meets first is skin
		var skinX = new float[width * height];     // where it meets skin first, forward
		var hasSkin = new bool[width * height];
		var firstX = new float[width * height];   // where it meets anything first
		var firstMaterial = new int[width * height];
		for ( int p = 0; p < hit.Length; p++ )
		{
			if ( hitCount[p] == 0 ) continue;
			hit[p] = true;
			firstX[p] = hitX[p * HitsKept];
			firstMaterial[p] = hitMaterial[p * HitsKept];
			onSkin[p] = skinMaterial[hitMaterial[p * HitsKept]];
			for ( int k = 0; k < hitCount[p]; k++ )
			{
				if ( !skinMaterial[hitMaterial[p * HitsKept + k]] ) continue;
				hasSkin[p] = true;
				skinX[p] = hitX[p * HitsKept + k];
				break;
			}
		}

		bool Inside( int i, int j ) => i >= 0 && j >= 0 && i < width && j < height;
		bool Other( int i, int j ) => Inside( i, j ) && hit[j * width + i] && !onSkin[j * width + i];
		// An eye is what an eye bone carries, or a hole in the skin with the skin far behind it (the
		// white of an anime eye, held by the head). Lashes, a line painted round the eye and brows
		// lie on the skin, or stick out past the head with no skin behind them at all.
		bool Eye( int i, int j ) => Other( i, j ) && (eyeMaterial[firstMaterial[j * width + i]] || (hasSkin[j * width + i] && skinX[j * width + i] < firstX[j * width + i] - EyeDeep * apart));
		Vec3 At( int i, int j ) => new( skinX[j * width + i], y0 + i * pixel, z0 - j * pixel );
		int Column( float y ) => (int)MathF.Round( (y - y0) / pixel );
		int Row( float z ) => (int)MathF.Round( (z0 - z) / pixel );
		int mid = Column( middle.Y );

		// Going from a pixel of a region one way until it is left: the skin just past it.
		// The region was cut off at a box round the feature; what lies past the box and is still
		// part of it (the far corner of a big eye) is gone over too.
		bool Past( HashSet<int> region, Func<int, int, bool> part, int i, int j, int di, int dj, out Vec3 point )
		{
			point = default;
			while ( Inside( i, j ) && (region.Contains( j * width + i ) || part( i, j )) ) { i += di; j += dj; }
			for ( int k = 0; k < 3 && Inside( i, j ); k++, i += di, j += dj )
				if ( hasSkin[j * width + i] ) { point = At( i, j ); return true; }
			return false;
		}

		// The eyes' openings.
		var eyeRegions = new HashSet<int>[2];
		var eyeCentre = new (int I, int J)[2];
		for ( int e = 0; e < 2; e++ )
		{
			var eye = e == 0 ? left : right;
			string name = e == 0 ? "eyeL" : "eyeR";
			int ei = Column( eye.Y ), ej = Row( eye.Z );
			if ( !Nearest( Eye, ei, ej, (int)(EyeSearch * Across), out int si, out int sj ) ) continue;
			int reach = (int)(EyeBox * Across);
			var region = Fill( Eye, si, sj, width, ( i, j ) => Math.Abs( i - ei ) <= reach && Math.Abs( j - ej ) <= reach );
			if ( region.Count < MinEye ) continue;
			var (ci, cj) = Centre( region, width );
			eyeRegions[e] = region;
			eyeCentre[e] = (ci, cj);
			int inward = mid > ci ? 1 : -1;

			// The opening's outline: its inner and outer corner (furthest in and out), and the
			// line over it and the line under it between them, each held at a quarter, half and
			// three quarters of its length. The citizen's opening is half a disc under a lid
			// cut straight across, an anime eye's is round, a man's is long and narrow: going by
			// the length along the outline, the edge of a lid goes onto the edge of a lid
			// whatever its shape.
			int iIn = ci, iOut = ci;
			foreach ( int q in region )
			{
				int x = q % width;
				if ( (x - iIn) * inward > 0 ) iIn = x;
				if ( (x - iOut) * inward < 0 ) iOut = x;
			}
			if ( iIn == iOut ) continue;
			var topRow = new Dictionary<int, int>();
			var bottomRow = new Dictionary<int, int>();
			var rowsIn = new List<int>(); var rowsOut = new List<int>();
			foreach ( int q in region )
			{
				int x = q % width, y = q / width;
				topRow[x] = topRow.TryGetValue( x, out int t ) ? Math.Min( t, y ) : y;
				bottomRow[x] = bottomRow.TryGetValue( x, out int b ) ? Math.Max( b, y ) : y;
				if ( x == iIn ) rowsIn.Add( y );
				if ( x == iOut ) rowsOut.Add( y );
			}
			rowsIn.Sort(); rowsOut.Sort();
			if ( Past( region, Eye, iIn, rowsIn[rowsIn.Count / 2], inward, 0, out var p ) ) marks[name + ".in"] = p;
			if ( Past( region, Eye, iOut, rowsOut[rowsOut.Count / 2], -inward, 0, out p ) ) marks[name + ".out"] = p;
			foreach ( var (rows, up, label) in new[] { (topRow, -1, ".top"), (bottomRow, 1, ".bottom") } )
			{
				// The line from the inner corner to the outer one, column by column, and how long it is
				// up to each column.
				var line = new List<(int I, int J, float Along)>();
				float along = 0;
				for ( int x = iIn; ; x -= inward )
				{
					if ( rows.TryGetValue( x, out int y ) )
					{
						if ( line.Count > 0 ) along += MathF.Sqrt( (x - line[^1].I) * (x - line[^1].I) + (y - line[^1].J) * (y - line[^1].J) );
						line.Add( (x, y, along) );
					}
					if ( x == iOut ) break;
				}
				for ( int k = 1; k <= 3; k++ )
				{
					float want = along * k / 4f;
					var at = line.Find( c => c.Along >= want );
					if ( Past( region, Eye, at.I, at.J, 0, up, out p ) ) marks[$"{name}{label}.{k}"] = p;
				}
			}
		}

		// Where the brows start over each eye: under a brow of the body's own (a piece of its own
		// over the skin, apart from the eye), or else at the top of whatever stands out over the
		// eye, a lid the brows sit right above.
		for ( int e = 0; e < 2; e++ )
		{
			var region = eyeRegions[e];
			if ( region == null ) continue;
			string name = e == 0 ? "browL" : "browR";
			int iMin = int.MaxValue, iMax = int.MinValue, jMin = int.MaxValue, jMax = int.MinValue;
			foreach ( int p in region ) { iMin = Math.Min( iMin, p % width ); iMax = Math.Max( iMax, p % width ); jMin = Math.Min( jMin, p / width ); jMax = Math.Max( jMax, p / width ); }
			// A brow is apart from the eye. Lashes and a line round the eye run into it.
			int margin = (iMax - iMin) / 4, top = jMin - (int)(BrowSearch * Across);
			bool Box( int i, int j ) => i >= iMin - margin && i <= iMax + margin && j >= top && j <= jMax;
			HashSet<int> brow = null;
			var seen = new HashSet<int>();
			for ( int j = Math.Max( 0, top ); j < jMin - 1; j++ )
				for ( int i = Math.Max( 0, iMin - margin ); i <= Math.Min( width - 1, iMax + margin ); i++ )
				{
					int p = j * width + i;
					if ( !Other( i, j ) || region.Contains( p ) || seen.Contains( p ) ) continue;
					var piece = Fill( Other, i, j, width, Box );
					seen.UnionWith( piece );
					if ( piece.Overlaps( region ) ) continue;
					if ( piece.Count >= MinBrow && (brow == null || piece.Count > brow.Count) ) brow = piece;
				}
			if ( brow != null )
			{
				// The skin under it: under the middle of its lower edge, of its upper edge, and under
				// its inner and outer end.
				var (bi, _) = Centre( brow, width );
				int bottom = -1, upper = int.MaxValue, inner = bi, outer = bi;
				int sideIn = mid > bi ? 1 : -1;
				foreach ( int p in brow )
				{
					int x = p % width, y = p / width;
					if ( x == bi ) { bottom = Math.Max( bottom, y ); upper = Math.Min( upper, y ); }
					if ( (x - inner) * sideIn > 0 ) inner = x;
					if ( (x - outer) * sideIn < 0 ) outer = x;
				}
				int RowOf( int x ) { var rows = new List<int>(); foreach ( int p in brow ) if ( p % width == x ) rows.Add( p / width ); rows.Sort(); return rows[rows.Count / 2]; }
				var ends = new[] { (bi, bottom, ""), (bi, upper, ".top"), (inner, RowOf( inner ), ".in"), (outer, RowOf( outer ), ".out") };
				foreach ( var (x, y, label) in ends )
					if ( y >= 0 && y < height && hasSkin[y * width + x] ) marks[name + label] = At( x, y );
				continue;
			}

			// The top of a lid standing out over the eye (the citizen's): going up from the eye, the
			// skin comes forward over it, falls back a good way and levels out on the forehead.
			var (ci, _) = eyeCentre[e];
			int j0 = jMin;
			while ( j0 >= 0 && region.Contains( j0 * width + ci ) ) j0--;
			float most = float.MinValue;
			for ( int j = j0; j > Math.Max( 0, j0 - (int)(BrowSearch * Across) ); j-- )
			{
				int below = (j + 1) * width + ci, here = j * width + ci;
				if ( !hit[here] || !onSkin[here] || !hasSkin[here] ) break;
				most = MathF.Max( most, skinX[here] );
				if ( j == j0 || !onSkin[below] ) continue;
				if ( skinX[below] < most - LidStep * apart && skinX[here] >= skinX[below] ) { marks[e == 0 ? "lidL" : "lidR"] = At( ci, j + 1 ); break; }
			}
		}

		// The tip of the nose: the skin furthest forward down the middle below the eyes.
		int eyeRow = Row( middle.Z ), nose = -1;
		for ( int j = Math.Max( 0, eyeRow ); j < Math.Min( height, eyeRow + (int)(NoseSearch * Across) ); j++ )
		{
			int p = j * width + mid;
			if ( hit[p] && onSkin[p] && (nose < 0 || skinX[p] > skinX[nose * width + mid]) ) nose = j;
		}
		if ( nose < 0 ) return marks;
		marks["nose"] = At( mid, nose );

		// The mouth: going down the middle from the nose, the profile falls back under the nose,
		// comes forward over the upper lip and dips between the lips. From there the groove
		// between the lips is followed to either side, to the corners. Open or closed, every
		// mouth has that groove; through an open one the rays go deeper still.
		float groove = GrooveDepth * apart;
		bool Skin( int i, int j ) => Inside( i, j ) && hasSkin[j * width + i];
		// The bottom of the groove in a column, within a row or two of where it is expected.
		int Notch( int i, int near )
		{
			int best = -1;
			for ( int j = near - GrooveDrift; j <= near + GrooveDrift; j++ )
			{
				if ( !Skin( i, j ) || !Skin( i, j - MouthAround ) || !Skin( i, j + MouthAround ) ) continue;
				float x = skinX[j * width + i];
				if ( x > skinX[(j - MouthAround) * width + i] - groove || x > skinX[(j + MouthAround) * width + i] - groove ) continue;
				if ( best < 0 || x < skinX[best * width + i] ) best = j;
			}
			return best;
		}
		int lowest = Math.Min( height - 1, nose + (int)(MouthSearch * Across) );
		int valley = -1, lip = -1, slit = -1;
		for ( int j = nose + 1; j <= lowest && slit < 0; j++ )
		{
			if ( !Skin( mid, j ) || !Skin( mid, j - 1 ) ) continue;
			float x = skinX[j * width + mid], above = skinX[(j - 1) * width + mid];
			if ( valley < 0 ) { if ( x > above ) valley = j - 1; continue; }
			if ( lip < 0 ) { if ( x < above ) lip = j - 1; continue; }
			if ( x > above && skinX[lip * width + mid] - above >= groove ) slit = j - 1;
			else if ( x > above ) lip = -1;   // a bump too small to be a lip: look for the next one
		}
		int mouthBottom = -1;
		if ( slit >= 0 )
		{
			var ends = new (int I, int J)[2];
			for ( int side = 0; side < 2; side++ )
			{
				int step = (side == 0) == (left.Y > right.Y) ? 1 : -1;   // towards the left eye first
				int j = slit, last = mid, lastJ = slit;
				for ( int i = mid + step; Math.Abs( i - mid ) <= (int)(MouthWide * Across); i += step )
				{
					int found = Notch( i, j );
					if ( found < 0 ) break;
					j = found; last = i; lastJ = found;
				}
				ends[side] = (last, lastJ);
			}
			if ( ends[0].I != mid && ends[1].I != mid )
			{
				marks["mouth.L"] = At( ends[0].I, ends[0].J );
				marks["mouth.R"] = At( ends[1].I, ends[1].J );
				marks["mouth.mid"] = At( mid, slit );
				mouthBottom = slit + 1;
			}
		}

		// The chin: going down the middle from the mouth, the last of the profile before it falls
		// back to the neck.
		if ( mouthBottom >= 0 )
		{
			float most = float.MinValue;
			int chin = -1;
			for ( int j = mouthBottom; j < height; j++ )
			{
				int p = j * width + mid;
				if ( !hasSkin[p] ) continue;
				most = MathF.Max( most, skinX[p] );
				if ( skinX[p] < most - ChinBack * apart ) break;
				chin = j;
			}
			if ( chin >= 0 && chin < height - 1 ) marks["chin"] = At( mid, chin );
		}

		// The crown: the highest skin down the middle.
		float crown = float.MinValue;
		Vec3 crownAt = default;
		for ( int t = 0; t < materialOf.Length; t++ )
		{
			if ( !skinMaterial[materialOf[t]] ) continue;
			for ( int k = 0; k < 3; k++ )
			{
				var p = positions[geo.Indices[t * 3 + k]];
				if ( MathF.Abs( p.Y - middle.Y ) <= CrownBand * apart && p.Z > crown ) { crown = p.Z; crownAt = p; }
			}
		}
		if ( crown > middle.Z ) marks["crown"] = crownAt;
		return marks;
	}

	// The pixel nearest to (i, j) that passes the test, within a square of the given reach.
	static bool Nearest( Func<int, int, bool> test, int i, int j, int reach, out int bi, out int bj )
	{
		bi = bj = -1;
		int best = int.MaxValue;
		for ( int dj = -reach; dj <= reach; dj++ )
			for ( int di = -reach; di <= reach; di++ )
			{
				int d = di * di + dj * dj;
				if ( d >= best || !test( i + di, j + dj ) ) continue;
				best = d; bi = i + di; bj = j + dj;
			}
		return bi >= 0;
	}

	// The pixels that pass the test and are joined to (i, j) side by side, within a box.
	static HashSet<int> Fill( Func<int, int, bool> test, int i, int j, int width, Func<int, int, bool> box )
	{
		var region = new HashSet<int>();
		var todo = new Stack<(int, int)>();
		todo.Push( (i, j) );
		while ( todo.Count > 0 )
		{
			var (x, y) = todo.Pop();
			if ( !box( x, y ) || !test( x, y ) || !region.Add( y * width + x ) ) continue;
			todo.Push( (x + 1, y) ); todo.Push( (x - 1, y) ); todo.Push( (x, y + 1) ); todo.Push( (x, y - 1) );
		}
		return region;
	}

	static (int I, int J) Centre( HashSet<int> region, int width )
	{
		long si = 0, sj = 0;
		foreach ( int p in region ) { si += p % width; sj += p / width; }
		return ((int)MathF.Round( (float)si / region.Count ), (int)MathF.Round( (float)sj / region.Count ));
	}
}

/// <summary>
/// A smooth warp of a face seen from the front, that takes each of a set of points exactly where
/// it is told to and bends as little as it can in between: a thin plate spline over the face's
/// height and width (Y and Z). What lies in front of or behind a point is moved the way it is:
/// the depth of the features says little (the citizen's eyes stand three centimetres out of its
/// face, an anime eye is flat), and the warped point is put on the skin afterwards anyway. Away
/// from the points it goes over into the one affine map that fits them best.
/// </summary>
public sealed class Warp
{
	readonly Vec3[] from;
	readonly double[] wx, wy, wz;   // per point, then the affine part: constant, y, z

	Warp( Vec3[] from, double[] wx, double[] wy, double[] wz )
	{
		this.from = from; this.wx = wx; this.wy = wy; this.wz = wz;
	}

	static double Kernel( Vec3 a, Vec3 b )
	{
		double dy = a.Y - b.Y, dz = a.Z - b.Z, r2 = dy * dy + dz * dz;
		return r2 > 1e-12 ? 0.5 * r2 * Math.Log( r2 ) : 0;
	}

	/// <returns>Null if the points don't pin down a warp (fewer than three, or all in a line).</returns>
	public static Warp Fit( List<(Vec3 From, Vec3 To)> points )
	{
		int n = points.Count, size = n + 3;
		if ( n < 3 ) return null;
		var m = new double[size * size];
		var bx = new double[size]; var by = new double[size]; var bz = new double[size];
		for ( int i = 0; i < n; i++ )
		{
			var p = points[i].From;
			for ( int k = 0; k < n; k++ ) m[i * size + k] = Kernel( p, points[k].From );
			m[i * size + n] = 1; m[i * size + n + 1] = p.Y; m[i * size + n + 2] = p.Z;
			m[n * size + i] = 1; m[(n + 1) * size + i] = p.Y; m[(n + 2) * size + i] = p.Z;
			var d = points[i].To - p;
			bx[i] = d.X; by[i] = d.Y; bz[i] = d.Z;
		}
		if ( !Solve( m, size, bx, by, bz ) ) return null;
		var from = new Vec3[n];
		for ( int i = 0; i < n; i++ ) from[i] = points[i].From;
		return new Warp( from, bx, by, bz );
	}

	/// <summary>Where the warp takes a point.</summary>
	public Vec3 Apply( Vec3 p )
	{
		int n = from.Length;
		double dx = wx[n] + wx[n + 1] * p.Y + wx[n + 2] * p.Z;
		double dy = wy[n] + wy[n + 1] * p.Y + wy[n + 2] * p.Z;
		double dz = wz[n] + wz[n + 1] * p.Y + wz[n + 2] * p.Z;
		for ( int i = 0; i < n; i++ )
		{
			double k = Kernel( p, from[i] );
			dx += wx[i] * k; dy += wy[i] * k; dz += wz[i] * k;
		}
		return p + new Vec3( (float)dx, (float)dy, (float)dz );
	}

	// Gaussian elimination with partial pivoting, three right-hand sides at once, in place.
	static bool Solve( double[] m, int size, double[] bx, double[] by, double[] bz )
	{
		for ( int c = 0; c < size; c++ )
		{
			int pivot = c;
			for ( int r = c + 1; r < size; r++ ) if ( Math.Abs( m[r * size + c] ) > Math.Abs( m[pivot * size + c] ) ) pivot = r;
			if ( Math.Abs( m[pivot * size + c] ) < 1e-12 ) return false;
			if ( pivot != c )
			{
				for ( int k = 0; k < size; k++ ) (m[c * size + k], m[pivot * size + k]) = (m[pivot * size + k], m[c * size + k]);
				(bx[c], bx[pivot]) = (bx[pivot], bx[c]); (by[c], by[pivot]) = (by[pivot], by[c]); (bz[c], bz[pivot]) = (bz[pivot], bz[c]);
			}
			for ( int r = 0; r < size; r++ )
			{
				if ( r == c ) continue;
				double f = m[r * size + c] / m[c * size + c];
				if ( f == 0 ) continue;
				for ( int k = c; k < size; k++ ) m[r * size + k] -= f * m[c * size + k];
				bx[r] -= f * bx[c]; by[r] -= f * by[c]; bz[r] -= f * bz[c];
			}
		}
		for ( int c = 0; c < size; c++ ) { double d = m[c * size + c]; bx[c] /= d; by[c] /= d; bz[c] /= d; }
		return true;
	}
}
