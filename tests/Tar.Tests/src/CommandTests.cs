namespace Icod.Tar.Tests;

using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using Xunit;

public sealed class CommandTests {
	[Fact]
	public void HelpAndVersionReportThePinnedBaseline() {
		var output = new StringWriter();
		Assert.Equal( 0, Command.Run( new[] { "--help" }, stdout: output ) );
		Assert.Contains( "Usage: tar", output.ToString() );
		output.GetStringBuilder().Clear();
		Assert.Equal( 0, Command.Run( new[] { "--version" }, stdout: output ) );
		Assert.Contains( "GNU tar 1.35", output.ToString() );
	}

	[Fact]
	public void CreatesListsAndExtractsDirectoryTree() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		File.WriteAllText( System.IO.Path.Combine( source, "one.txt" ), "one" );
		Directory.CreateDirectory( System.IO.Path.Combine( source, "nested" ) );
		File.WriteAllText( System.IO.Path.Combine( source, "nested", "two.txt" ), "two" );
		var archive = tree.PathFor( "roundtrip.tar" );
		var verbose = new StringWriter();
		Assert.Equal( 0, Command.Run( new[] { "cvf", archive, "-C", source, "one.txt", "nested" }, stdout: verbose ) );
		Assert.Contains( "one.txt", verbose.ToString() );
		var listing = new StringWriter();
		Assert.Equal( 0, Command.Run( new[] { "-tf", archive }, stdout: listing ) );
		Assert.Contains( "nested/two.txt", NormalizeLines( listing.ToString() ) );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 0, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.Equal( "one", File.ReadAllText( System.IO.Path.Combine( destination, "one.txt" ) ) );
		Assert.Equal( "two", File.ReadAllText( System.IO.Path.Combine( destination, "nested", "two.txt" ) ) );
	}

	[Theory]
	[InlineData( "gnu", TarEntryFormat.Gnu )]
	[InlineData( "ustar", TarEntryFormat.Ustar )]
	[InlineData( "pax", TarEntryFormat.Pax )]
	[InlineData( "posix", TarEntryFormat.Pax )]
	public void WritesRequestedArchiveFormat( string format, TarEntryFormat expected ) {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		File.WriteAllText( System.IO.Path.Combine( source, "file.txt" ), "format" );
		var archive = tree.PathFor( string.Concat( format, ".tar" ) );
		Assert.Equal( 0, Command.Run( new[] { "-cf", archive, "--format", format, "-C", source, "file.txt" } ) );
		using var stream = File.OpenRead( archive );
		using var reader = new TarReader( stream );
		Assert.Equal( expected, Assert.IsAssignableFrom<TarEntry>( reader.GetNextEntry() ).Format );
	}

	[Fact]
	public void AppendAndUpdateKeepHistoricalMemberVersions() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		var file = System.IO.Path.Combine( source, "value.txt" );
		File.WriteAllText( file, "one" );
		var stableTime = DateTime.UtcNow.AddMinutes( -1 );
		stableTime = new DateTime( stableTime.Ticks - (stableTime.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc );
		File.SetLastWriteTimeUtc( file, stableTime );
		var archive = tree.PathFor( "versions.tar" );
		Assert.Equal( 0, Command.Run( new[] { "-cf", archive, "-C", source, "value.txt" } ) );
		File.WriteAllText( file, "two" );
		File.SetLastWriteTimeUtc( file, stableTime.AddSeconds( 2 ) );
		Assert.Equal( 0, Command.Run( new[] { "-rf", archive, "-C", source, "value.txt" } ) );
		var afterAppend = ReadMemberNames( archive ).Count( name => name == "value.txt" );
		Assert.Equal( 2, afterAppend );
		Assert.Equal( 0, Command.Run( new[] { "-uf", archive, "-C", source, "value.txt" } ) );
		Assert.Equal( 2, ReadMemberNames( archive ).Count( name => name == "value.txt" ) );
		File.WriteAllText( file, "three" );
		File.SetLastWriteTimeUtc( file, stableTime.AddSeconds( 4 ) );
		Assert.Equal( 0, Command.Run( new[] { "-uf", archive, "-C", source, "value.txt" } ) );
		Assert.Equal( 3, ReadMemberNames( archive ).Count( name => name == "value.txt" ) );
	}

	[Fact]
	public void DeleteAndConcatenateRewriteArchives() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		File.WriteAllText( System.IO.Path.Combine( source, "a.txt" ), "a" );
		File.WriteAllText( System.IO.Path.Combine( source, "b.txt" ), "b" );
		var first = tree.PathFor( "first.tar" );
		var second = tree.PathFor( "second.tar" );
		Assert.Equal( 0, Command.Run( new[] { "-cf", first, "-C", source, "a.txt", "b.txt" } ) );
		Assert.Equal( 0, Command.Run( new[] { "--delete", "-f", first, "a.txt" } ) );
		Assert.Equal( new[] { "b.txt" }, ReadMemberNames( first ) );
		Assert.Equal( 0, Command.Run( new[] { "-cf", second, "-C", source, "a.txt" } ) );
		Assert.Equal( 0, Command.Run( new[] { "-Af", first, second } ) );
		Assert.Equal( new[] { "b.txt", "a.txt" }, ReadMemberNames( first ) );
	}

	[Fact]
	public void CompareReportsDifferences() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		var file = System.IO.Path.Combine( source, "file.txt" );
		File.WriteAllText( file, "same" );
		var archive = tree.PathFor( "compare.tar" );
		Assert.Equal( 0, Command.Run( new[] { "-cf", archive, "-C", source, "file.txt" } ) );
		Assert.Equal( 0, Command.Run( new[] { "-df", archive, "-C", source } ) );
		File.WriteAllText( file, "different" );
		var output = new StringWriter();
		Assert.Equal( 1, Command.Run( new[] { "-df", archive, "-C", source }, stdout: output ) );
		Assert.Contains( "file.txt: differs", output.ToString() );
	}

	[Fact]
	public void GzipRoundTripsAndAutodetectsNamedArchiveOnRead() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		File.WriteAllText( System.IO.Path.Combine( source, "file.txt" ), "gzip" );
		var archive = tree.PathFor( "archive.tar.gz" );
		Assert.Equal( 0, Command.Run( new[] { "-czf", archive, "-C", source, "file.txt" } ) );
		var listing = new StringWriter();
		Assert.Equal( 0, Command.Run( new[] { "-tf", archive }, stdout: listing ) );
		Assert.Contains( "file.txt", listing.ToString() );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 0, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.Equal( "gzip", File.ReadAllText( System.IO.Path.Combine( destination, "file.txt" ) ) );
	}

	[Fact]
	public void ExclusionsAndMemberSelectionAreApplied() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		File.WriteAllText( System.IO.Path.Combine( source, "keep.txt" ), "keep" );
		File.WriteAllText( System.IO.Path.Combine( source, "drop.log" ), "drop" );
		var archive = tree.PathFor( "select.tar" );
		Assert.Equal( 0, Command.Run( new[] { "-cf", archive, "--exclude=*.log", "-C", source, "keep.txt", "drop.log" } ) );
		Assert.Equal( new[] { "keep.txt" }, ReadMemberNames( archive ) );
		var listing = new StringWriter();
		Assert.Equal( 0, Command.Run( new[] { "-tf", archive, "keep.txt" }, stdout: listing ) );
		Assert.Equal( "keep.txt", listing.ToString().Trim() );
	}

	[Theory]
	[InlineData( "../escape.txt" )]
	[InlineData( "/absolute.txt" )]
	[InlineData( "C:/drive-root.txt" )]
	[InlineData( "//server/share.txt" )]
	public void ExtractionRejectsEscapingOrRootedMemberNames( string memberName ) {
		using var tree = new TestTree();
		var archive = tree.PathFor( "unsafe.tar" );
		WriteArchive( archive, RegularEntry( memberName, "payload" ) );
		var destination = tree.CreateDirectory( "destination" );
		var error = new StringWriter();
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination }, stderr: error ) );
		Assert.Contains( "tar:", error.ToString() );
	}

	[Theory]
	[InlineData( "gnu" )]
	[InlineData( "ustar" )]
	[InlineData( "pax" )]
	public void CreationPreservesHardLinkIdentityOnUnix( string format ) {
		if ( OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		var original = System.IO.Path.Combine( source, "original.txt" );
		var linked = System.IO.Path.Combine( source, "linked.txt" );
		File.WriteAllText( original, "linked" );
		var startInfo = new ProcessStartInfo {
			FileName = "ln",
			UseShellExecute = false,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add( original );
		startInfo.ArgumentList.Add( linked );
		using ( var process = Process.Start( startInfo ) ?? throw new InvalidOperationException( "Unable to start ln." ) ) {
			process.WaitForExit();
			Assert.True( process.ExitCode == 0, process.StandardError.ReadToEnd() );
		}
		var archive = tree.PathFor( "hardlinks.tar" );
		Assert.Equal( 0, Command.Run( new[] { "-cf", archive, "--format", format, "-C", source, "original.txt", "linked.txt" } ) );
		using var stream = File.OpenRead( archive );
		using var reader = new TarReader( stream );
		var first = Assert.IsAssignableFrom<TarEntry>( reader.GetNextEntry() );
		var second = Assert.IsAssignableFrom<TarEntry>( reader.GetNextEntry() );
		Assert.Equal( TarEntryType.RegularFile, first.EntryType );
		Assert.Equal( TarEntryType.HardLink, second.EntryType );
		Assert.Equal( "original.txt", second.LinkName );
	}

	[Fact]
	public void ExtractionRejectsSymbolicAndHardLinkEscapes() {
		if ( OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var archive = tree.PathFor( "links.tar" );
		var symlink = new PaxTarEntry( TarEntryType.SymbolicLink, "sub/link" ) { LinkName = "../../outside" };
		var hardlink = new PaxTarEntry( TarEntryType.HardLink, "hard" ) { LinkName = "../outside" };
		WriteArchive( archive, symlink, hardlink );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.False( File.Exists( System.IO.Path.Combine( destination, "sub", "link" ) ) );
		Assert.False( File.Exists( System.IO.Path.Combine( destination, "hard" ) ) );
	}

	[Fact]
	public void ExtractsContainedSymbolicAndHardLinks() {
		if ( OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var archive = tree.PathFor( "contained-links.tar" );
		var target = RegularEntry( "target.txt", "linked" );
		var symlink = new PaxTarEntry( TarEntryType.SymbolicLink, "symbolic.txt" ) { LinkName = "target.txt" };
		var hardlink = new PaxTarEntry( TarEntryType.HardLink, "hard.txt" ) { LinkName = "target.txt" };
		WriteArchive( archive, target, symlink, hardlink );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 0, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.Equal( "linked", File.ReadAllText( System.IO.Path.Combine( destination, "symbolic.txt" ) ) );
		Assert.Equal( "linked", File.ReadAllText( System.IO.Path.Combine( destination, "hard.txt" ) ) );
	}

	[Fact]
	public void ExtractionRejectsSymlinkedParentDirectory() {
		if ( OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var archive = tree.PathFor( "parent-link.tar" );
		WriteArchive( archive, RegularEntry( "redirect/file.txt", "archive" ) );
		var destination = tree.CreateDirectory( "destination" );
		var outside = tree.CreateDirectory( "outside" );
		Directory.CreateSymbolicLink( System.IO.Path.Combine( destination, "redirect" ), outside );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.False( File.Exists( System.IO.Path.Combine( outside, "file.txt" ) ) );
	}

	[Fact]
	public void ExistingSymlinkCannotRedirectRegularFileOverwrite() {
		if ( OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var archive = tree.PathFor( "overwrite.tar" );
		WriteArchive( archive, RegularEntry( "victim", "archive" ) );
		var destination = tree.CreateDirectory( "destination" );
		var outside = tree.PathFor( "outside.txt" );
		File.WriteAllText( outside, "outside" );
		File.CreateSymbolicLink( System.IO.Path.Combine( destination, "victim" ), outside );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.Equal( "outside", File.ReadAllText( outside ) );
	}

	[Fact]
	public void SpecialFilesAreNotMaterialized() {
		if ( OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var archive = tree.PathFor( "special.tar" );
		WriteArchive( archive, new PaxTarEntry( TarEntryType.Fifo, "pipe" ) );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.False( File.Exists( System.IO.Path.Combine( destination, "pipe" ) ) );
	}

	[Fact]
	public void ExtractionRestoresModificationTimeAndRequestedPermissions() {
		using var tree = new TestTree();
		var archive = tree.PathFor( "metadata.tar" );
		var expectedTime = new DateTimeOffset( 2024, 2, 3, 4, 5, 6, TimeSpan.Zero );
		var entry = RegularEntry( "metadata.txt", "metadata" );
		entry.ModificationTime = expectedTime;
		if ( !OperatingSystem.IsWindows() ) entry.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
		WriteArchive( archive, entry );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 0, Command.Run( new[] { "-xf", archive, "--preserve-permissions", "-C", destination } ) );
		var path = System.IO.Path.Combine( destination, "metadata.txt" );
		Assert.Equal( expectedTime.UtcDateTime, File.GetLastWriteTimeUtc( path ) );
		if ( !OperatingSystem.IsWindows() ) {
			Assert.Equal( entry.Mode, File.GetUnixFileMode( path ) );
		}
	}

	[Fact]
	public void CaseFoldingCollisionsAreRejectedOnWindows() {
		if ( !OperatingSystem.IsWindows() ) return;
		using var tree = new TestTree();
		var archive = tree.PathFor( "case.tar" );
		WriteArchive( archive, RegularEntry( "Name.txt", "one" ), RegularEntry( "name.txt", "two" ) );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
	}

	[Fact]
	public void MalformedSparseMapsAreRejectedBeforePublication() {
		using var tree = new TestTree();
		var archive = tree.PathFor( "bad-sparse.tar" );
		var attributes = new Dictionary<string, string> {
			["GNU.sparse.size"] = "1024",
			["GNU.sparse.numblocks"] = "1",
			["GNU.sparse.map"] = "9223372036854775800,100",
			["GNU.sparse.name"] = "sparse.bin"
		};
		var entry = new PaxTarEntry( TarEntryType.RegularFile, "0/GNUSparseFile.1/sparse.bin", attributes ) {
			DataStream = new MemoryStream( new byte[100], writable: false )
		};
		WriteArchive( archive, entry );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.False( File.Exists( System.IO.Path.Combine( destination, "sparse.bin" ) ) );
	}

	[Fact]
	public void SparsePaxRoundTripRestoresLogicalContent() {
		using var tree = new TestTree();
		var source = tree.CreateDirectory( "source" );
		var sparse = System.IO.Path.Combine( source, "sparse.bin" );
		using ( var stream = new FileStream( sparse, FileMode.Create, FileAccess.Write ) ) {
			stream.SetLength( 512 * 1024 );
			stream.Position = 128 * 1024 + 7;
			stream.Write( "sparse-data"u8 );
		}
		var archive = tree.PathFor( "sparse.tar" );
		Assert.Equal( 0, Command.Run( new[] { "-cSf", archive, "-C", source, "sparse.bin" } ) );
		var listing = new StringWriter();
		Assert.Equal( 0, Command.Run( new[] { "-tf", archive }, stdout: listing ) );
		Assert.Equal( "sparse.bin", listing.ToString().Trim() );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 0, Command.Run( new[] { "-xf", archive, "-C", destination } ) );
		Assert.Equal( File.ReadAllBytes( sparse ), File.ReadAllBytes( System.IO.Path.Combine( destination, "sparse.bin" ) ) );
	}

	[Fact]
	public void ExtractionBudgetAndDecompressionFailuresAreControlled() {
		using var tree = new TestTree();
		var archive = tree.PathFor( "budget.tar" );
		WriteArchive( archive, RegularEntry( "large.txt", "12" ) );
		var destination = tree.CreateDirectory( "destination" );
		Assert.Equal( 2, Command.Run( new[] { "-xf", archive, "--max-extract-bytes=1", "-C", destination } ) );
		Assert.False( File.Exists( System.IO.Path.Combine( destination, "large.txt" ) ) );
		var compressed = tree.PathFor( "limited.tar.gz" );
		var source = tree.CreateDirectory( "source" );
		File.WriteAllText( System.IO.Path.Combine( source, "payload.txt" ), new string( 'x', 64 * 1024 ) );
		Assert.Equal( 0, Command.Run( new[] { "-czf", compressed, "-C", source, "payload.txt" } ) );
		Assert.Equal( 2, Command.Run( new[] { "-tf", compressed, "--max-archive-bytes=1024" } ) );
		var invalidGzip = tree.PathFor( "invalid.tar.gz" );
		File.WriteAllText( invalidGzip, "not gzip" );
		Assert.Equal( 2, Command.Run( new[] { "-tf", invalidGzip } ) );
	}

	[Fact]
	public async Task CancellationReturnsSignalStyleStatus() {
		using var tree = new TestTree();
		var archive = tree.PathFor( "cancel.tar" );
		WriteArchive( archive, RegularEntry( "file.txt", "x" ) );
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		Assert.Equal( 130, await Command.RunAsync( new[] { "-tf", archive }, cancellationToken: cancellation.Token ) );
	}

	private static PaxTarEntry RegularEntry( string name, string contents ) {
		var bytes = Encoding.UTF8.GetBytes( contents );
		return new PaxTarEntry( TarEntryType.RegularFile, name ) {
			DataStream = new MemoryStream( bytes, writable: false )
		};
	}

	private static void WriteArchive( string path, params TarEntry[] entries ) {
		using var stream = new FileStream( path, FileMode.Create, FileAccess.Write, FileShare.None );
		using var writer = new TarWriter( stream, TarEntryFormat.Pax, leaveOpen: false );
		foreach ( var entry in entries ) writer.WriteEntry( entry );
	}

	private static IReadOnlyList<string> ReadMemberNames( string path ) {
		var result = new List<string>();
		using var stream = File.OpenRead( path );
		using var reader = new TarReader( stream );
		TarEntry? entry;
		while ( (entry = reader.GetNextEntry()) is not null ) {
			if ( entry is PaxTarEntry pax && pax.ExtendedAttributes.TryGetValue( "GNU.sparse.name", out var sparseName ) ) result.Add( sparseName );
			else result.Add( entry.Name );
		}
		return result;
	}

	private static string NormalizeLines( string value ) => value.Replace( "\\", "/", StringComparison.Ordinal );

	private sealed class TestTree : IDisposable {
		private readonly string root = System.IO.Path.Combine( System.IO.Path.GetTempPath(), string.Concat( "icod-tar-tests-", Guid.NewGuid().ToString( "N" ) ) );
		public TestTree() => Directory.CreateDirectory( root );
		public string PathFor( string relative ) => System.IO.Path.Combine( root, relative );
		public string CreateDirectory( string relative ) {
			var path = PathFor( relative );
			Directory.CreateDirectory( path );
			return path;
		}
		public void Dispose() {
			try { Directory.Delete( root, recursive: true ); } catch { }
	}
}
