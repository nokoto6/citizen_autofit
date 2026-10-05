using System;
using System.Collections.Generic;
using System.Text;

namespace Sandbox.Autofit;

/// <summary>
/// Reader for the binary KV3 documents inside compiled resources (version 3, LZ4).
/// Objects come back as Dictionary&lt;string, object&gt;, arrays as List&lt;object&gt;, numbers as
/// long or double.
///
/// The document is stored as separate streams: all single bytes, all 4-byte values, all
/// 8-byte values, the string table, then one type byte per value saying which stream it is in.
/// </summary>
public sealed class Kv3
{
	readonly byte[] doc;
	readonly string[] strings;
	int bytesAt, intsAt, longsAt, typesAt;

	Kv3( byte[] doc, string[] strings, int bytesAt, int intsAt, int longsAt, int typesAt )
	{
		this.doc = doc;
		this.strings = strings;
		this.bytesAt = bytesAt;
		this.intsAt = intsAt;
		this.longsAt = longsAt;
		this.typesAt = typesAt;
	}

	public static object Parse( byte[] file, int start )
	{
		if ( BitConverter.ToUInt32( file, start ) != 0x4B563302 )
			throw new InvalidOperationException( "Not a KV3 version 3 document" );

		// After the magic and a 16 byte format id.
		int h = start + 20;
		int compression = BitConverter.ToInt32( file, h );
		int byteCount = BitConverter.ToInt32( file, h + 8 );
		int intCount = BitConverter.ToInt32( file, h + 12 );
		int longCount = BitConverter.ToInt32( file, h + 16 );
		int rawSize = BitConverter.ToInt32( file, h + 28 );
		int packedSize = BitConverter.ToInt32( file, h + 32 );
		int blockCount = BitConverter.ToInt32( file, h + 36 );
		int body = h + 44;
		if ( blockCount != 0 )
			throw new InvalidOperationException( "KV3 documents with binary blocks are not handled" );

		byte[] doc;
		if ( compression == 0 )
		{
			doc = new byte[rawSize];
			Array.Copy( file, body, doc, 0, rawSize );
		}
		else if ( compression == 1 )
		{
			doc = Lz4.Decode( file, body, packedSize, rawSize );
		}
		else
		{
			throw new InvalidOperationException( $"KV3 compression {compression} is not handled" );
		}

		int ints = (byteCount + 3) & ~3;
		int longs = (ints + intCount * 4 + 7) & ~7;
		int at = longs + longCount * 8;

		// The first 4-byte value is the number of strings.
		var strings = new string[BitConverter.ToInt32( doc, ints )];
		for ( int i = 0; i < strings.Length; i++ )
		{
			int end = Array.IndexOf( doc, (byte)0, at );
			strings[i] = Encoding.UTF8.GetString( doc, at, end - at );
			at = end + 1;
		}

		var reader = new Kv3( doc, strings, 0, ints + 4, longs, at );
		return reader.ReadValue( reader.ReadType() );
	}

	int ReadType()
	{
		int type = doc[typesAt++];
		if ( (type & 0x80) != 0 )
			typesAt++;   // a flags byte (marks resource references), not needed here
		return type & 0x3F;
	}

	int ReadInt()
	{
		int v = BitConverter.ToInt32( doc, intsAt );
		intsAt += 4;
		return v;
	}

	long ReadLong()
	{
		long v = BitConverter.ToInt64( doc, longsAt );
		longsAt += 8;
		return v;
	}

	string ReadString()
	{
		int id = ReadInt();
		return id < 0 ? "" : strings[id];
	}

	object ReadValue( int type )
	{
		switch ( type )
		{
			case 1: return null;
			case 2: return doc[bytesAt++] != 0;
			case 3: return ReadLong();
			case 4: return ReadLong();
			case 5: return BitConverter.Int64BitsToDouble( ReadLong() );
			case 6: return ReadString();
			case 7:
				{
					int length = ReadInt();
					var blob = new byte[length];
					Array.Copy( doc, bytesAt, blob, 0, length );
					bytesAt += length;
					return blob;
				}
			case 8:
				{
					int count = ReadInt();
					var list = new List<object>( count );
					for ( int i = 0; i < count; i++ )
						list.Add( ReadValue( ReadType() ) );
					return list;
				}
			case 9:
				{
					int count = ReadInt();
					var map = new Dictionary<string, object>( count );
					for ( int i = 0; i < count; i++ )
					{
						string key = ReadString();
						map[key] = ReadValue( ReadType() );
					}
					return map;
				}
			case 10:
			case 24:
				{
					// Every element has the same type. 24 stores the length in one byte.
					int count = type == 24 ? doc[bytesAt++] : ReadInt();
					int element = ReadType();
					var list = new List<object>( count );
					for ( int i = 0; i < count; i++ )
						list.Add( ReadValue( element ) );
					return list;
				}
			case 11: return (long)ReadInt();
			case 12: return (long)(uint)ReadInt();
			case 13: return true;
			case 14: return false;
			case 15: return 0L;
			case 16: return 1L;
			case 17: return 0.0;
			case 18: return 1.0;
			case 19:
				{
					float v = BitConverter.ToSingle( doc, intsAt );
					intsAt += 4;
					return (double)v;
				}
			case 23: return (long)doc[bytesAt++];
			default:
				throw new InvalidOperationException( $"KV3 value type {type} is not handled" );
		}
	}

	// Small helpers for walking a parsed document.

	public static Dictionary<string, object> Map( object value ) => value as Dictionary<string, object>;
	public static List<object> List( object value ) => value as List<object>;

	public static object Get( object value, string key )
	{
		var map = Map( value );
		return map != null && map.TryGetValue( key, out var v ) ? v : null;
	}

	public static long Int( object value, long fallback = 0 ) => value switch
	{
		long l => l,
		double d => (long)d,
		bool b => b ? 1 : 0,
		_ => fallback,
	};

	public static double Number( object value, double fallback = 0 ) => value switch
	{
		double d => d,
		long l => l,
		_ => fallback,
	};
}
