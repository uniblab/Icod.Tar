namespace Icod.Tar;

using System.IO.Compression;
using Icod.CommandFramework.Processes;
using Icod.CommandFramework.Temporary;

internal sealed class TarInput : IAsyncDisposable {
	private readonly IAsyncDisposable? owner;
	public TarInput( Stream stream, IAsyncDisposable? owner = null ) {
		Stream = stream;
		this.owner = owner;
	}
	public Stream Stream { get; }
	public async ValueTask DisposeAsync() {
		await Stream.DisposeAsync().ConfigureAwait( false );
		if ( owner is not null ) {
			await owner.DisposeAsync().ConfigureAwait( false );
		}
	}
}

internal static class TarCompression {
	private const int BufferSize = 128 * 1024;

	public static TarCompressionKind ResolveKind( TarOptions options ) {
		if ( options.Compression != TarCompressionKind.None ) return options.Compression;
		return options.AutoCompress ? ResolveArchiveSuffix( options.ArchiveName ) : TarCompressionKind.None;
	}

	public static TarCompressionKind ResolveReadKind( TarOptions options ) {
		if ( options.Compression != TarCompressionKind.None ) return options.Compression;
		return ResolveArchiveSuffix( options.ArchiveName );
	}

	private static TarCompressionKind ResolveArchiveSuffix( string? archiveName ) {
		if ( string.IsNullOrEmpty( archiveName ) || archiveName == "-" ) return TarCompressionKind.None;
		var lower = archiveName.ToLowerInvariant();
		if ( lower.EndsWith( ".tar.gz", StringComparison.Ordinal ) || lower.EndsWith( ".tgz", StringComparison.Ordinal ) ) return TarCompressionKind.GZip;
		if ( lower.EndsWith( ".tar.bz2", StringComparison.Ordinal ) || lower.EndsWith( ".tbz", StringComparison.Ordinal ) || lower.EndsWith( ".tbz2", StringComparison.Ordinal ) ) return TarCompressionKind.BZip2;
		if ( lower.EndsWith( ".tar.xz", StringComparison.Ordinal ) || lower.EndsWith( ".txz", StringComparison.Ordinal ) ) return TarCompressionKind.Xz;
		if ( lower.EndsWith( ".tar.zst", StringComparison.Ordinal ) || lower.EndsWith( ".tzst", StringComparison.Ordinal ) ) return TarCompressionKind.Zstd;
		return TarCompressionKind.None;
	}

	public static async Task WriteAsync(
		TarOptions options,
		Stream destination,
		Func<Stream, CancellationToken, Task> writeArchive,
		CancellationToken cancellationToken
	) {
		var kind = ResolveKind( options );
		if ( kind == TarCompressionKind.None ) {
			await writeArchive( new LimitedWriteStream( destination, options.MaximumArchiveBytes, leaveOpen: true ), cancellationToken ).ConfigureAwait( false );
			return;
		}
		if ( kind == TarCompressionKind.GZip ) {
			await using var gzip = new GZipStream( destination, CompressionLevel.Optimal, leaveOpen: true );
			await writeArchive( new LimitedWriteStream( gzip, options.MaximumArchiveBytes, leaveOpen: true ), cancellationToken ).ConfigureAwait( false );
			await gzip.FlushAsync( cancellationToken ).ConfigureAwait( false );
			return;
		}
		await using var workspace = TemporaryWorkspace.Create( directoryTemplate: "icod-tar-compress.XXXXXXXX", cancellationToken: cancellationToken );
		var temporary = workspace.CreateFile( "archive-XXXXXXXX.tar", cancellationToken );
		await using ( var uncompressed = new FileStream(
			temporary,
			FileMode.Create,
			FileAccess.Write,
			FileShare.None,
			BufferSize,
			FileOptions.Asynchronous | FileOptions.SequentialScan
		) ) {
			await writeArchive( new LimitedWriteStream( uncompressed, options.MaximumArchiveBytes, leaveOpen: true ), cancellationToken ).ConfigureAwait( false );
			await uncompressed.FlushAsync( cancellationToken ).ConfigureAwait( false );
		}
		await using var input = new FileStream(
			temporary,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			BufferSize,
			FileOptions.Asynchronous | FileOptions.SequentialScan
		);
		await RunExternalAsync( options, kind, decompress: false, input, destination, cancellationToken ).ConfigureAwait( false );
	}

	public static async Task<TarInput> OpenReadAsync( TarOptions options, CancellationToken cancellationToken ) {
		var source = OpenArchiveInput( options );
		var kind = ResolveReadKind( options );
		if ( kind == TarCompressionKind.None ) {
			return new TarInput( source );
		}
		if ( kind == TarCompressionKind.GZip ) {
			return new TarInput( new LimitedReadStream( new GZipStream( source, CompressionMode.Decompress, leaveOpen: false ), options.MaximumArchiveBytes ) );
		}
		var workspace = TemporaryWorkspace.Create( directoryTemplate: "icod-tar-decompress.XXXXXXXX", cancellationToken: cancellationToken );
		try {
			var temporary = workspace.CreateFile( "archive-XXXXXXXX.tar", cancellationToken );
			await using ( var output = new FileStream(
				temporary,
				FileMode.Create,
				FileAccess.Write,
				FileShare.None,
				BufferSize,
				FileOptions.Asynchronous | FileOptions.SequentialScan
			) ) {
				await RunExternalAsync( options, kind, decompress: true, source, new LimitedWriteStream( output, options.MaximumArchiveBytes ), cancellationToken ).ConfigureAwait( false );
			}
			await source.DisposeAsync().ConfigureAwait( false );
			var decompressed = new FileStream(
				temporary,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				BufferSize,
				FileOptions.Asynchronous | FileOptions.SequentialScan
			);
			return new TarInput( decompressed, workspace );
		}
		catch {
			await source.DisposeAsync().ConfigureAwait( false );
			await workspace.DisposeAsync().ConfigureAwait( false );
			throw;
		}
	}

	private static Stream OpenArchiveInput( TarOptions options ) {
		if ( options.ArchiveName == "-" ) {
			return new NonDisposingStream( Console.OpenStandardInput() );
		}
		var path = System.IO.Path.GetFullPath( options.ArchiveName!, options.InitialDirectory );
		var info = new FileInfo( path );
		if ( info.Length > options.MaximumArchiveBytes ) {
			throw new IOException( string.Concat( "Archive exceeds --max-archive-bytes: ", path ) );
		}
		return new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			BufferSize,
			FileOptions.Asynchronous | FileOptions.SequentialScan
		);
	}

	private static async Task RunExternalAsync(
		TarOptions options,
		TarCompressionKind kind,
		bool decompress,
		Stream input,
		Stream output,
		CancellationToken cancellationToken
	) {
		var invocation = BuildInvocation( options, kind, decompress );
		var run = new ProcessRunOptions( invocation.FileName ) {
			ResolveExecutable = true,
			ReturnLaunchFailureResult = true,
			StandardInput = input,
			StandardOutput = output,
			CaptureStandardError = true,
			CancellationPolicy = ProcessCancellationPolicy.KillProcessTree
		};
		foreach ( var argument in invocation.Arguments ) {
			run.Arguments.Add( argument );
		}
		var result = await ProcessRunner.RunAsync( run, cancellationToken ).ConfigureAwait( false );
		if ( result.WasCanceled && cancellationToken.IsCancellationRequested ) {
			throw new OperationCanceledException( cancellationToken );
		}
		if ( !result.Started || result.ExitCode.GetValueOrDefault( 1 ) != 0 ) {
			var detail = string.IsNullOrWhiteSpace( result.StandardError ) ? "compression program failed" : result.StandardError.Trim();
			throw new IOException( string.Concat( invocation.FileName, ": ", detail ) );
		}
	}

	private static CompressorInvocation BuildInvocation( TarOptions options, TarCompressionKind kind, bool decompress ) {
		if ( kind == TarCompressionKind.Custom ) {
			var words = SplitCommand( options.CustomCompressionProgram ?? string.Empty );
			if ( words.Count == 0 ) throw new TarUsageException( "--use-compress-program requires a program." );
			var args = words.Skip( 1 ).ToList();
			if ( decompress ) args.Add( "-d" );
			return new CompressorInvocation( words[0], args );
		}
		var fileName = kind switch {
			TarCompressionKind.BZip2 => "bzip2",
			TarCompressionKind.Xz => "xz",
			TarCompressionKind.Zstd => "zstd",
			_ => throw new InvalidOperationException( "An external compressor was not selected." )
		};
		var arguments = new List<string>();
		if ( kind == TarCompressionKind.Zstd ) arguments.Add( "-q" );
		if ( decompress ) arguments.Add( "-d" );
		arguments.Add( "-c" );
		return new CompressorInvocation( fileName, arguments );
	}

	internal static IReadOnlyList<string> SplitCommand( string text ) {
		var result = new List<string>();
		var current = new System.Text.StringBuilder();
		var quote = '\0';
		var escaped = false;
		foreach ( var character in text ) {
			if ( escaped ) {
				current.Append( character );
				escaped = false;
				continue;
			}
			if ( character == '\\' && quote != '\'' ) {
				escaped = true;
				continue;
			}
			if ( quote != '\0' ) {
				if ( character == quote ) quote = '\0'; else current.Append( character );
				continue;
			}
			if ( character is '\'' or '"' ) {
				quote = character;
				continue;
			}
			if ( char.IsWhiteSpace( character ) ) {
				if ( current.Length > 0 ) {
					result.Add( current.ToString() );
					current.Clear();
				}
				continue;
			}
			current.Append( character );
		}
		if ( escaped || quote != '\0' ) throw new TarUsageException( "Unterminated quoting in --use-compress-program." );
		if ( current.Length > 0 ) result.Add( current.ToString() );
		return result;
	}

	private sealed record CompressorInvocation( string FileName, IReadOnlyList<string> Arguments );

	private sealed class LimitedWriteStream : Stream {
		private readonly Stream inner;
		private readonly long limit;
		private readonly bool leaveOpen;
		private long written;
		public LimitedWriteStream( Stream inner, long limit, bool leaveOpen = false ) { this.inner = inner; this.limit = limit; this.leaveOpen = leaveOpen; }
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => written;
		public override long Position { get => written; set => throw new NotSupportedException(); }
		public override void Flush() => inner.Flush();
		public override Task FlushAsync( CancellationToken cancellationToken ) => inner.FlushAsync( cancellationToken );
		public override int Read( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
		public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException();
		public override void SetLength( long value ) => throw new NotSupportedException();
		public override void Write( byte[] buffer, int offset, int count ) {
			RequireCapacity( count );
			inner.Write( buffer, offset, count );
			written += count;
		}
		public override async ValueTask WriteAsync( ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default ) {
			RequireCapacity( buffer.Length );
			await inner.WriteAsync( buffer, cancellationToken ).ConfigureAwait( false );
			written += buffer.Length;
		}
		private void RequireCapacity( int count ) {
			if ( count < 0 || written > limit - count ) throw new IOException( "Decompressed archive exceeds --max-archive-bytes." );
		}
		protected override void Dispose( bool disposing ) { if ( disposing && !leaveOpen ) inner.Dispose(); base.Dispose( disposing ); }
		public override ValueTask DisposeAsync() => leaveOpen ? ValueTask.CompletedTask : inner.DisposeAsync();
	}

	private sealed class LimitedReadStream : Stream {
		private readonly Stream inner;
		private readonly long limit;
		private long read;
		public LimitedReadStream( Stream inner, long limit ) { this.inner = inner; this.limit = limit; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => read; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read( byte[] buffer, int offset, int count ) {
			var amount = inner.Read( buffer, offset, count );
			Account( amount );
			return amount;
		}
		public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default ) {
			var amount = await inner.ReadAsync( buffer, cancellationToken ).ConfigureAwait( false );
			Account( amount );
			return amount;
		}
		private void Account( int amount ) {
			if ( amount < 0 || read > limit - amount ) throw new IOException( "Decompressed archive exceeds --max-archive-bytes." );
			read += amount;
		}
		public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException();
		public override void SetLength( long value ) => throw new NotSupportedException();
		public override void Write( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
		protected override void Dispose( bool disposing ) { if ( disposing ) inner.Dispose(); base.Dispose( disposing ); }
		public override ValueTask DisposeAsync() => inner.DisposeAsync();
	}

	private sealed class NonDisposingStream : Stream {
		private readonly Stream inner;
		public NonDisposingStream( Stream inner ) => this.inner = inner;
		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => inner.CanSeek;
		public override bool CanWrite => inner.CanWrite;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }
		public override void Flush() => inner.Flush();
		public override Task FlushAsync( CancellationToken cancellationToken ) => inner.FlushAsync( cancellationToken );
		public override int Read( byte[] buffer, int offset, int count ) => inner.Read( buffer, offset, count );
		public override ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default ) => inner.ReadAsync( buffer, cancellationToken );
		public override long Seek( long offset, SeekOrigin origin ) => inner.Seek( offset, origin );
		public override void SetLength( long value ) => inner.SetLength( value );
		public override void Write( byte[] buffer, int offset, int count ) => inner.Write( buffer, offset, count );
		public override ValueTask WriteAsync( ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default ) => inner.WriteAsync( buffer, cancellationToken );
		protected override void Dispose( bool disposing ) { }
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
