using System;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox.Autofit;

/// <summary>
/// Which stock body a garment's model was made on, told from the model itself. The clothing
/// resource says it by the field the model is in (the citizen's, the human male's, the human
/// female's), and a workshop item can have it wrong: a mask modelled on the human head put in
/// the citizen's field. Fitted from the wrong body, it comes out offset and sized for a head
/// it was never on.
///
/// A garment sits on the body it was made on: put there as bone merging puts it (see
/// <see cref="GarmentFit.ToStockBind"/>), most of it lies a few millimetres off that body's
/// skin and almost none of it is under it. On another body it is under the skin in one place
/// and off it in another. The citizen and the humans also have skeletons of their own, ten
/// centimetres apart at the head, so a garment's bones tell those two apart as well. The two
/// humans share one skeleton and only the shape tells them apart.
/// </summary>
public static class MadeFor
{
	/// <summary>How a garment sits on one stock body as it was made.</summary>
	public struct Sitting
	{
		public float Median;     // middle gap off the skin, negative under it
		public float Under;      // share of the garment more than UnderDepth under the skin
		public float Near;       // share within NearBand of the skin, either side
		public float Misfit;     // the average of how far each part is from lying on the skin, see Misfit
		public float Bones;      // middle distance of the bones the garment hangs off from this body's, -1 if none are shared
		public float Skeleton;   // middle distance of all the garment's bones from this body's as exported, -1 if none are shared
		public int Missing;      // bones of the garment's skeleton this body has no bone of that name for
		public int Count;        // vertices looked at
	}

	const int MostSamples = 6000;                          // vertices looked at, spread over the garment
	const float UnderDepth = 0.003f * Units.Metre;         // deeper than this under the skin is really under it
	const float NearBand = 0.012f * Units.Metre;           // cloth on the skin lies within this of it
	const float FarCap = 0.06f * Units.Metre;              // a brim or a hem hangs off the skin anyway: further than this counts the same
	const float UnderWeight = 3f;                          // under the skin is worse than off it by this much

	const float ClearlyBetter = 2f;                        // the body it was made on fits at least this many times better than the field's
	const float LeastGain = 0.001f * Units.Metre;          // and by this much: on two bodies it fits all but perfectly there is nothing to tell
	const float UnderCue = 0.10f;                          // and a tenth more of it is under the field's body's skin
	const float NearCue = 0.20f;                           // or a fifth more of it lies on the other body's skin
	const float UnderSlack = 0.01f;                        // and no more than a hundredth more of it is under the other body's skin
	const float SkeletonSlack = 0.02f * Units.Metre;       // a skeleton closer than this to the field's body by as much speaks for it

	/// <summary>
	/// Of the stock bodies (as many as <paramref name="on"/> has, in <c>ClothingFitter.BodyKind</c>
	/// order: the citizen, the human male, the human female), the one the garment was made on.
	/// The field's, <paramref name="field"/>, unless another one is clearly it:
	/// <list type="bullet">
	/// <item>the garment fits it at least twice as well (<see cref="Sitting.Misfit"/>), and by a millimetre;</item>
	/// <item>a tenth more of the garment is under the field's body's skin, or a fifth more of it
	/// lies on the other body's skin: the gap is not all in the brims and hems;</item>
	/// <item>no more of it is under the other body's skin, give or take a hundredth: skin coming
	/// through is what shows. A hoodie hugging the human male with its back and sleeves just under
	/// his skin was made on the female it floats a little off; a head-sized orange made to go
	/// round the human head is closer to the citizen's bigger head, through which it doesn't go;</item>
	/// <item>between the citizen and a human, the garment's skeleton isn't the field body's.</item>
	/// </list>
	/// Stock clothing always comes out as its field says.
	/// </summary>
	public static int Pick( Sitting[] on, int field )
	{
		int best = field;
		for ( int k = 0; k < on.Length; k++ )
			if ( on[k].Count > 0 && on[k].Misfit < on[best].Misfit ) best = k;
		if ( best == field || on[field].Count == 0 ) return field;

		var made = on[best];
		var told = on[field];
		if ( told.Misfit < made.Misfit * ClearlyBetter || told.Misfit - made.Misfit < LeastGain ) return field;
		if ( made.Under - told.Under > UnderSlack ) return field;
		if ( told.Under - made.Under < UnderCue && made.Near - told.Near < NearCue ) return field;
		bool citizenOrNot = (field == 0) != (best == 0);
		if ( citizenOrNot && told.Skeleton >= 0 && made.Skeleton >= 0 && made.Skeleton > told.Skeleton + SkeletonSlack ) return field;
		return best;
	}

	/// <summary>The numbers <see cref="Pick"/> went by, for the log.</summary>
	public static string Describe( Sitting[] on, string[] names )
	{
		const float mm = 1000f / Units.Metre;
		var parts = new List<string>();
		for ( int k = 0; k < on.Length; k++ )
			parts.Add( $"{names[k]}: misfit {on[k].Misfit * mm:F1} mm, {on[k].Under * 100:F0}% under the skin, {on[k].Near * 100:F0}% on it, skeleton {(on[k].Skeleton < 0 ? "-" : $"{on[k].Skeleton * mm:F0} mm")} off" );
		return string.Join( "; ", parts );
	}

	/// <summary>Measures how the garment sits on one stock body. The garment is left as it is.</summary>
	public static Sitting Measure( SkinnedGeometry garment, SkinnedGeometry stock, TriMesh skin )
	{
		var made = AsMade( garment, stock, out float bones );
		var gaps = Gaps( made, garment.Indices, skin );
		var result = new Sitting { Bones = bones, Count = gaps.Count, Skeleton = Skeleton( garment, stock, out int missing ), Missing = missing };
		if ( gaps.Count == 0 ) return result;

		int under = 0, near = 0;
		double misfit = 0;
		foreach ( float gap in gaps )
		{
			if ( gap < -UnderDepth ) under++;
			if ( MathF.Abs( gap ) <= NearBand ) near++;
			misfit += Misfit( gap );
		}
		gaps.Sort();
		result.Median = gaps[gaps.Count / 2];
		result.Under = under / (float)gaps.Count;
		result.Near = near / (float)gaps.Count;
		result.Misfit = (float)(misfit / gaps.Count);
		return result;
	}

	// How far a part of the garment is from lying on the skin: nothing within the band cloth lies
	// in, then the distance past it, worse under the skin, and no worse than a hem hanging free.
	static float Misfit( float gap )
	{
		if ( gap < -UnderDepth ) return MathF.Min( (-gap - UnderDepth) * UnderWeight, FarCap * UnderWeight );
		return Math.Clamp( gap - NearBand, 0f, FarCap );
	}

	// The garment's vertices where bone merging puts them on this body, and how far its bones are
	// from the body's. A copy: ToStockBind moves the garment it is given.
	static Vec3[] AsMade( SkinnedGeometry garment, SkinnedGeometry stock, out float bones )
	{
		var copy = new SkinnedGeometry
		{
			Positions = garment.Positions.ToArray(),
			Normals = garment.Normals?.ToArray(),
			BoneIndex = garment.BoneIndex,
			BoneWeight = garment.BoneWeight,
			Indices = garment.Indices,
			BoneNames = garment.BoneNames,
			BoneParents = garment.BoneParents,
			BonePositions = garment.BonePositions.ToArray(),
			BoneRotations = garment.BoneRotations.ToArray(),
			BoneLocalPositions = garment.BoneLocalPositions?.ToArray(),
			BoneLocalRotations = garment.BoneLocalRotations?.ToArray(),
		};
		GarmentFit.ToStockBind( copy, stock );

		var byName = new Dictionary<string, int>();
		for ( int i = 0; i < stock.BoneNames.Length; i++ ) byName[stock.BoneNames[i]] = i;
		var used = new bool[copy.BoneNames.Length];
		for ( int k = 0; k < copy.BoneIndex.Length; k++ )
			if ( copy.BoneIndex[k] >= 0 && copy.BoneIndex[k] < used.Length && copy.BoneWeight[k] > 0 ) used[copy.BoneIndex[k]] = true;
		var apart = new List<float>();
		for ( int i = 0; i < used.Length; i++ )
			if ( used[i] && byName.TryGetValue( copy.BoneNames[i], out int s ) )
				apart.Add( (copy.BonePositions[i] - stock.BonePositions[s]).Length() );
		apart.Sort();
		bones = apart.Count > 0 ? apart[apart.Count / 2] : -1f;
		return copy.Positions;
	}

	// How far the garment's skeleton, as it was exported, is from the body's, and how many of its
	// bones the body has no bone of that name for.
	static float Skeleton( SkinnedGeometry garment, SkinnedGeometry stock, out int missing )
	{
		var byName = new Dictionary<string, int>();
		for ( int i = 0; i < stock.BoneNames.Length; i++ ) byName[stock.BoneNames[i]] = i;
		var apart = new List<float>();
		missing = 0;
		for ( int i = 0; i < garment.BoneNames.Length; i++ )
		{
			if ( byName.TryGetValue( garment.BoneNames[i], out int s ) ) apart.Add( (garment.BonePositions[i] - stock.BonePositions[s]).Length() );
			else missing++;
		}
		apart.Sort();
		return apart.Count > 0 ? apart[apart.Count / 2] : -1f;
	}

	// Signed gaps off the skin of the vertices the triangles use, at most MostSamples of them
	// spread evenly over the vertex list.
	static List<float> Gaps( Vec3[] positions, int[] tris, TriMesh skin )
	{
		var seen = new bool[positions.Length];
		var drawn = new List<int>();
		foreach ( int v in tris )
			if ( v >= 0 && v < seen.Length && !seen[v] ) { seen[v] = true; drawn.Add( v ); }
		drawn.Sort();
		int step = Math.Max( 1, (drawn.Count + MostSamples - 1) / MostSamples );
		var gaps = new List<float>( drawn.Count / step + 1 );
		for ( int k = 0; k < drawn.Count; k += step )
			gaps.Add( skin.SignedGap( positions[drawn[k]], out _ ) );
		return gaps;
	}
}
