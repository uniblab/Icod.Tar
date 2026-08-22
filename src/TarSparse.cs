namespace Icod.Tar;

using System.Formats.Tar;
using System.Globalization;

internal sealed record SparseExtent( long Offset, long Length );

internal sealed record SparseMap( long LogicalLength, IReadOnlyList<SparseExtent> Extents, long StoredLength );

internal static class TarSparse {
	private const int ScanBlockSize = 128 * 1024;
	private const string SizeKey = "GNU.sparse.size";
	private const string CountKey = "GNU.sparse.numblocks";
	private const string MapKey = "GNU.sparse.map";
	private const string NameKey = "GNU.sparse.name";

	public static async Task<PaxTarEntry?> TryCreateEntryAsync(
		string sourcePath,
		string archiveName,
		CancellationToken cancellationToken
	) {
		var info = new FileInfo( sourcePath );
		if ( info.Length < ScanBlockSize ) return null;
		var extents = await ScanAsync( sourcePath, info.Length, cancellationToken ).ConfigureAwait( false );
		var storedLength = extents.Aggregate( 0L, static (total, extent) => checked(total + extent.Length) );
		if ( storedLength >= info.Length - 1024 ) return null;
		var mapText = string.Join(
			",",
				extents.SelectMany( extent => new[] {
				extent.Offset.ToString( CultureInfo.InvariantCulture ),
				extent.Length.ToString( CultureInfo.InvariantCulture )
			} )
		);
		var attributes = new Dictionary<string, string>( StringComparer.Ordinal ) {
			[SizeKey] = info.Length.ToString( CultureInfo.InvariantCulture ),
			[CountKey] = extents.Count.ToString( CultureInfo.InvariantCulture ),
			[MapKey] = mapText,
			[NameKey] = archiveName
		};
		var storedName = CreateStoredName( archiveName );
		var entry = new PaxTarEntry( TarEntryType.RegularFile, storedName, attributes ) {
			ModificationTime = info.LastWriteTimeUtc,
			DataStream = new SparseExtentReadStream( sourcePath, extents, storedLength )
		};
		if ( !OperatingSystem.IsWindows() ) entry.Mode = File.GetUnixFileMode( sourcePath );
		return entry;
	}

	public static string GetLogicalName( TarEntry entry ) {
		if ( entry is PaxTarEntry pax
			&& pax.ExtendedAttributes.ContainsKey( MapKey )
			&& pax.ExtendedAttributes.TryGetValue( NameKey, out var logicalName )
			&& !string.IsNullOrWhiteSpace( logicalName ) ) {
			return logicalName;
		}
		return entry.Name;
	}

	public static bool TryGetMap( TarEntry entry, out SparseMap? map, out string? error ) {
		map = null;
		error = null;
		if ( entry is not PaxTarEntry pax ) return false;
		if ( !pax.ExtendedAttributes.TryGetValue( MapKey, out var mapText ) ) return false;
		if ( !pax.ExtendedAttributes.TryGetValue( NameKey, out var logicalName ) || string.IsNullOrWhiteSpace( logicalName ) ) {
			error = "GNU sparse map is missing its logical member name.";
			return true;
		}
		if ( !TryReadLong( pax.ExtendedAttributes, SizeKey, out var logicalLength ) ) {
			error = "GNU sparse map is missing a valid logical size.";
			return true;
		}
		if ( logicalLength < 0 ) {
			error = "GNU sparse logical size is negative.";
			return true;
		}
		var values = mapText.Length == 0 ? Array.Empty<string>() : mapText.Split( ',' );
		if ( values.Length % 2 != 0 ) {
			error = "GNU sparse map has an odd number of offset/length fields.";
			return true;
		}
		var extents = new List<SparseExtent>( values.Length / 2 );
		long storedLength = 0;
		long previousEnd = 0;
		for ( var index = 0; index < values.Length; index += 2 ) {
			if ( !long.TryParse( values[index], NumberStyles.None, CultureInfo.InvariantCulture, out var offset )
				|| !long.TryParse( values[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var length )
				|| offset < 0 || length < 0 ) {
				error = "GNU sparse map contains an invalid offset or length.";
				return true;
			}
			long end;
			try {
				end = checked(offset + length);
				storedLength = checked(storedLength + length);
			} catch ( OverflowException ) {
				error = "GNU sparse map overflows a 64-bit archive offset.";
				return true;
			}
			if ( offset < previousEnd || end > logicalLength ) {
				error = "GNU sparse map overlaps or extends beyond the logical file size.";
				return true;
			}
			previousEnd = end;
			extents.Add( new SparseExtent( offset, length ) );
		}
		if ( pax.ExtendedAttributes.TryGetValue( CountKey, out var countText )
			&& (!int.TryParse( countText, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredCount ) || declaredCount != extents.Count) ) {
			error = "GNU sparse extent count does not match the sparse map.";
			return true;
		}
		if ( entry.Length != storedLength ) {
			error = "GNU sparse stored length does not match its sparse map.";
			return true;
		}
		map = new SparseMap( logicalLength, extents, storedLength );
		return true;
	}

	public static async Task WriteSparseAsync( Stream? source, Stream destination, SparseMap map, CancellationToken cancellationToken ) {
		if ( source is null && map.StoredLength != 0 ) throw new InvalidDataException( "Sparse archive member is missing its data stream." );
		if ( !destination.CanSeek ) throw new NotSupportedException( "Sparse extraction requires a seekable staging stream." );
		destination.SetLength( map.LogicalLength );
		if ( source is null ) return;
		var buffer = new byte[ScanBlockSize];
		foreach ( var extent in map.Extents ) {
			destination.Position = extent.Offset;
			var remaining = extent.Length;
			while ( remaining > 0 ) {
				var wanted = (int)Math.Min( buffer.Length, remaining );
				var read = await source.ReadAsync( buffer.AsMemory( 0, wanted ), cancellationToken ).ConfigureAwait( false );
				if ( read == 0 ) throw new EndOfStreamException( "Sparse archive member ended before its map was satisfied." );
				await destination.WriteAsync( buffer.AsMemory( 0, read ), cancellationToken ).ConfigureAwait( false );
				remaining -= read;
			}
		}
		destination.Position = map.LogicalLength;
	}

	public static async Task<bool> CompareSparseAsync( string path, Stream? source, SparseMap map, CancellationToken cancellationToken ) {
		var info = new FileInfo( path );
		if ( info.Length != map.LogicalLength ) return false;
		if ( source is null ) return map.StoredLength == 0 && await RegionIsZeroAsync( path, 0, map.LogicalLength, cancellationToken ).ConfigureAwait( false );
		await using var local = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, ScanBlockSize, FileOptions.Asynchronous | FileOptions.RandomAccess );
		var bufferLocal = new byte[ScanBlockSize];
		var bufferArchive = new byte[ScanBlockSize];
		long previousEnd = 0;
		foreach ( var extent in map.Extents ) {
			if ( !await StreamRegionIsZeroAsync( local, previousEnd, extent.Offset - previousEnd, bufferLocal, cancellationToken ).ConfigureAwait( false ) ) return false;
			local.Position = extent.Offset;
			var remaining = extent.Length;
			while ( remaining > 0 ) {
				var wanted = (int)Math.Min( bufferLocal.Length, remaining );
				var localRead = await ReadExactlyAtMostAsync( local, bufferLocal, wanted, cancellationToken ).ConfigureAwait( false );
				var archiveRead = await ReadExactlyAtMostAsync( source, bufferArchive, wanted, cancellationToken ).ConfigureAwait( false );
				if ( localRead != wanted || archiveRead != wanted ) return false;
				if ( !bufferLocal.AsSpan( 0, wanted ).SequenceEqual( bufferArchive.AsSpan( 0, wanted ) ) ) return false;
				remaining -= wanted;
			}
			previousEnd = checked(extent.Offset + extent.Length);
		}
		return await StreamRegionIsZeroAsync( local, previousEnd, map.LogicalLength - previousEnd, bufferLocal, cancellationToken ).ConfigureAwait( false );
	}

	private static async Task<bool> RegionIsZeroAsync( string path, long offset, long length, CancellationToken cancellationToken ) {
		await using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, ScanBlockSize, FileOptions.Asynchronous | FileOptions.RandomAccess );
		return await StreamRegionIsZeroAsync( stream, offset, length, new byte[ScanBlockSize], cancellationToken ).ConfigureAwait( false );
	}

	private static async Task<bool> StreamRegionIsZeroAsync( FileStream stream, long offset, long length, byte[] buffer, CancellationToken cancellationToken ) {
		if ( length <= 0 ) return true;
		stream.Position = offset;
		var remaining = length;
		while ( remaining > 0 ) {
			var wanted = (int)Math.Min( buffer.Length, remaining );
			var read = await stream.ReadAsync( buffer.AsMemory( 0, wanted ), cancellationToken ).ConfigureAwait( false );
			if ( read == 0 ) return false;
			if ( buffer.AsSpan( 0, read ).IndexOfAnyExcept( (byte)0 ) >= 0 ) return false;
			remaining -= read;
		}
		return true;
	}

	private static async Task<int> ReadExactlyAtMostAsync( Stream stream, byte[] buffer, int wanted, CancellationToken cancellationToken ) {
		var read = 0;
		while ( read < wanted ) {
			var amount = await stream.ReadAsync( buffer.AsMemory( read, wanted - read ), cancellationToken ).ConfigureAwait( false );
			if ( amount == 0 ) break;
			read += amount;
		}
		return read;
	}

	private static bool TryReadLong( IReadOnlyDictionary<string, string> values, string key, out long value ) {
		var getVal = values.TryGetValue( key, out var text );
		var parsed = long.TryParse( text, NumberStyles.None, CultureInfo.InvariantCulture, out value );
		return getVal && parsed;
	}

	private static string CreateStoredName( string archiveName ) {
		var normalized = archiveName.Replace( '\\', '/' ).TrimEnd( '/' );
		var depth = normalized.Count( character => character == '/' );
		var basename = normalized[(normalized.LastIndexOf( '/' ) + 1)..];
		return string.Concat( depth.ToString( CultureInfo.InvariantCulture ), "/GNUSparseFile.", Environment.ProcessId.ToString( CultureInfo.InvariantCulture ), "/", basename );
	}

	private static async Task<IReadOnlyList<SparseExtent>> ScanAsync( string path, long length, CancellationToken cancellationToken ) {
		var extents = new List<SparseExtent>();
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			ScanBlockSize,
			FileOptions.Asynchronous | FileOptions.SequentialScan
		);
		var buffer = new byte[ScanBlockSize];
		long offset = 0;
		SparseExtent? active = null;
		while ( offset < length ) {
			var wanted = (int)Math.Min( buffer.Length, length - offset );
			var read = 0;
			while ( read < wanted ) {
				var amount = await stream.ReadAsync( buffer.AsMemory( read, wanted - read ), cancellationToken ).ConfigureAwait( false );
				if ( amount == 0 ) throw new EndOfStreamException( "Source file changed while scanning for sparse extents." );
				read += amount;
			}
			var allZero = buffer.AsSpan( 0, read ).IndexOfAnyExcept( (byte)0 ) < 0;
			if ( !allZero ) {
				if ( active is not null && active.Offset + active.Length == offset ) {
					active = active with { Length = active.Length + read };
					extents[^1] = active;
				} else {
					active = new SparseExtent( offset, read );
					extents.Add( active );
				}
			} else {
				active = null;
			}
			offset += read;
		}
		return extents;
	}

	private sealed class SparseExtentReadStream : Stream {
		private readonly FileStream source;
		private readonly IReadOnlyList<SparseExtent> extents;
		private readonly long length;
		private int extentIndex;
		private long extentPosition;
		private long position;

		public SparseExtentReadStream( string path, IReadOnlyList<SparseExtent> extents, long length ) {
			this.extents = extents;
			this.length = length;
			source = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess );
		}
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position {
			get => position;
			set => SetStoredPosition( value );
		}
		public override void Flush() { }
		public override int Read( byte[] buffer, int offset, int count ) => ReadAsync( buffer.AsMemory( offset, count ) ).AsTask().GetAwaiter().GetResult();
		public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default ) {
			if ( buffer.Length == 0 || extentIndex >= extents.Count ) return 0;
			var extent = extents[extentIndex];
			var remaining = extent.Length - extentPosition;
			var wanted = (int)Math.Min( buffer.Length, remaining );
			source.Position = extent.Offset + extentPosition;
			var read = await source.ReadAsync( buffer[..wanted], cancellationToken ).ConfigureAwait( false );
			if ( read == 0 ) throw new EndOfStreamException( "Source file changed while emitting sparse data." );
			extentPosition += read;
			position += read;
			if ( extentPosition == extent.Length ) { extentIndex++; extentPosition = 0; }
			return read;
		}
		public override long Seek( long offset, SeekOrigin origin ) {
			var basis = origin switch {
				SeekOrigin.Begin => 0L,
				SeekOrigin.Current => position,
				SeekOrigin.End => length,
				_ => throw new ArgumentOutOfRangeException( nameof( origin ) )
			};
			SetStoredPosition( checked(basis + offset) );
			return position;
		}
		private void SetStoredPosition( long value ) {
			if ( value < 0 || value > length ) throw new ArgumentOutOfRangeException( nameof( value ) );
			position = value;
			extentIndex = 0;
			extentPosition = 0;
			var remaining = value;
			while ( extentIndex < extents.Count ) {
				var extentLength = extents[extentIndex].Length;
				if ( remaining < extentLength ) {
					extentPosition = remaining;
					return;
				}
				remaining -= extentLength;
				extentIndex++;
			}
		}
		public override void SetLength( long value ) => throw new NotSupportedException();
		public override void Write( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
		protected override void Dispose( bool disposing ) { if ( disposing ) source.Dispose(); base.Dispose( disposing ); }
		public override ValueTask DisposeAsync() => source.DisposeAsync();
	}
}
