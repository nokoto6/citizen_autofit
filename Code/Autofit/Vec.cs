using System;

namespace Sandbox.Autofit;

// The fitting code is plain math and also builds outside the engine, where it is checked
// against the reference implementation. So it doesn't use the engine's vector types, and
// System.Numerics is off the table because the s&box whitelist doesn't allow it.

public struct Vec2
{
	public float X, Y;

	public Vec2( float x, float y ) { X = x; Y = y; }
}

public struct Vec3
{
	public float X, Y, Z;

	public Vec3( float x, float y, float z ) { X = x; Y = y; Z = z; }

	public static readonly Vec3 Zero = new( 0, 0, 0 );
	public static readonly Vec3 One = new( 1, 1, 1 );
	public static readonly Vec3 UnitZ = new( 0, 0, 1 );

	public static Vec3 operator +( Vec3 a, Vec3 b ) => new( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
	public static Vec3 operator -( Vec3 a, Vec3 b ) => new( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
	public static Vec3 operator -( Vec3 a ) => new( -a.X, -a.Y, -a.Z );
	public static Vec3 operator *( Vec3 a, float s ) => new( a.X * s, a.Y * s, a.Z * s );
	public static Vec3 operator *( float s, Vec3 a ) => new( a.X * s, a.Y * s, a.Z * s );
	public static Vec3 operator /( Vec3 a, float s ) => new( a.X / s, a.Y / s, a.Z / s );

	public static float Dot( Vec3 a, Vec3 b ) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
	public static Vec3 Cross( Vec3 a, Vec3 b ) => new( a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X );

	public float LengthSquared() => X * X + Y * Y + Z * Z;
	public float Length() => MathF.Sqrt( LengthSquared() );
}

/// <summary>Quaternion helpers. Quaternions are float[4] as x, y, z, w, the way models store them.</summary>
public static class Quat
{
	public static Vec3 Rotate( float[] q, Vec3 v )
	{
		var u = new Vec3( q[0], q[1], q[2] );
		return v + 2f * Vec3.Cross( u, Vec3.Cross( u, v ) + q[3] * v );
	}

	public static float[] Multiply( float[] a, float[] b ) => new[]
	{
		a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
		a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
		a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
		a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
	};

	public static float[] Inverse( float[] q ) => new[] { -q[0], -q[1], -q[2], q[3] };
}
