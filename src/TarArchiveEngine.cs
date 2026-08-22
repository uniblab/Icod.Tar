namespace Icod.Tar;

using System.Formats.Tar;
using System.Globalization;
using Icod.CommandFramework.FileSystem.Mutation;
using Icod.CommandFramework.FileSystem.TransactionalReplacement;
using Icod.CommandFramework.FileSystem.Traversal;

internal sealed class TarArchiveEngine {
	private readonly TextWriter stdout;
	private readonly TextWriter stderr;
	private readonly ReadOnlyPathTraversalEngine traversal = new( SystemReadOnlyFileSystemProvider.Instance );
	private readonly SystemTransactionalReplacementFileSystem replacementFileSystem = SystemTransactionalReplacementFileSystem.Instance;
	private readonly SystemFileSystemMutationProvider mutationFileSystem = SystemFileSystemMutationProvider.Instance;

	public TarArchiveEngine( TextWriter stdout, TextWriter stderr ) {
		this.stdout = stdout ?? throw new ArgumentNullException( nameof( stdout ) );
		this.stderr = stderr ?? throw new ArgumentNullException( nameof( stderr ) );
	}

	public Task<int> ExecuteAsync( TarOptions options, CancellationToken cancellationToken ) {
		ArgumentNullException.ThrowIfNull( options );
		return options.Operation switch {
			TarOperation.Create => CreateAsync( options, cancellationToken ),
			TarOperation.List => ListAsync( options, cancellationToken ),
			TarOperation.Extract => ExtractAsync( options, cancellationToken ),
			TarOperation.Append => AppendOrUpdateAsync( options, update: false, cancellationToken ),
			TarOperation.Update => AppendOrUpdateAsync( options, update: true, cancellationToken ),
			TarOperation.Delete => DeleteAsync( options, cancellationToken ),
			TarOperation.Concatenate => ConcatenateAsync( options, cancellationToken ),
			TarOperation.Compare => CompareAsync( options, cancellationToken ),
			_ => throw new TarUsageException( "An archive operation is required." )
		};
	}

	private async Task<int> CreateAsync( TarOptions options, CancellationToken cancellationToken ) {
		if ( options.Operands.Count == 0 ) {
			throw new TarUsageException( "Cowardly refusing to create an empty archive without file operands." );
		}
		var hadErrors = false;
		if ( options.ArchiveName == "-" ) {
			var standardOutput = Console.OpenStandardOutput();
			await TarCompression.WriteAsync(
				options,
				standardOutput,
				async (stream, token) => {
				hadErrors = await WriteInputArchiveAsync( options, stream, updateTimes: null, token ).ConfigureAwait( false );
			},
				cancellationToken
			).ConfigureAwait( false );
			return hadErrors ? 2 : 0;
		}
		await ReplaceArchiveAsync(
			options,
			async (stream, token) => await TarCompression.WriteAsync(
				options,
				stream,
				async (archive, innerToken) => {
					hadErrors = await WriteInputArchiveAsync( options, archive, updateTimes: null, innerToken ).ConfigureAwait( false );
				},
				token
			).ConfigureAwait( false ),
			cancellationToken
		).ConfigureAwait( false );
		return hadErrors ? 2 : 0;
	}

	private async Task<int> ListAsync( TarOptions options, CancellationToken cancellationToken ) {
		await using var input = await TarCompression.OpenReadAsync( options, cancellationToken ).ConfigureAwait( false );
		using var reader = new TarReader( input.Stream, leaveOpen: true );
		var count = 0;
		TarEntry? entry;
		while ( (entry = reader.GetNextEntry()) is not null ) {
			cancellationToken.ThrowIfCancellationRequested();
			RequireEntryBudget( options, ref count );
			var memberName = TarSparse.GetLogicalName( entry );
			if ( !TarMemberSelection.IsSelected( options, memberName ) ) continue;
			await stdout.WriteLineAsync( options.Verbose ? FormatVerboseEntry( entry, memberName ) : memberName ).ConfigureAwait( false );
		}
		return 0;
	}

	private async Task<int> ExtractAsync( TarOptions options, CancellationToken cancellationToken ) {
		var policy = new TarExtractionPolicy( options.WorkingDirectory );
		var directoryMetadata = new List<(string Path, TarEntry Entry, string Name)>();
		var extractedNames = new Dictionary<string, string>( StringComparer.Ordinal );
		var hadErrors = false;
		var count = 0;
		long logicalBytes = 0;
		await using var input = await TarCompression.OpenReadAsync( options, cancellationToken ).ConfigureAwait( false );
		using var reader = new TarReader( input.Stream, leaveOpen: true );
		TarEntry? entry;
		while ( (entry = reader.GetNextEntry()) is not null ) {
			cancellationToken.ThrowIfCancellationRequested();
			var memberName = TarSparse.GetLogicalName( entry );
			try {
				RequireEntryBudget( options, ref count );
				if ( !TarMemberSelection.IsSelected( options, memberName ) ) continue;
				var resolved = policy.ResolveMember( memberName, options.StripComponents );
				if ( resolved.IsSkipped ) continue;
				if ( options.Verbose ) await stdout.WriteLineAsync( memberName ).ConfigureAwait( false );
				var logicalLength = GetLogicalLength( entry );
				logicalBytes = checked(logicalBytes + logicalLength);
				if ( logicalBytes > options.MaximumExtractBytes ) {
					throw new IOException( "Extraction exceeds --max-extract-bytes." );
				}
				switch ( entry.EntryType ) {
					case TarEntryType.Directory:
					case TarEntryType.DirectoryList:
						ExtractDirectory( policy, resolved, entry );
						directoryMetadata.Add( (resolved.DestinationPath!, entry, memberName) );
						break;
					case TarEntryType.RegularFile:
					case TarEntryType.V7RegularFile:
					case TarEntryType.ContiguousFile:
					case TarEntryType.SparseFile:
						if ( await ExtractRegularFileAsync( policy, resolved, entry, options, cancellationToken ).ConfigureAwait( false ) ) {
							extractedNames[NormalizeMemberName( memberName )] = resolved.DestinationPath!;
						}
						break;
					case TarEntryType.SymbolicLink:
						if ( await ExtractSymbolicLinkAsync( policy, resolved, entry, options, cancellationToken ).ConfigureAwait( false ) ) {
							extractedNames[NormalizeMemberName( memberName )] = resolved.DestinationPath!;
						}
						break;
					case TarEntryType.HardLink:
						if ( await ExtractHardLinkAsync( policy, resolved, entry, options, extractedNames, cancellationToken ).ConfigureAwait( false ) ) {
							extractedNames[NormalizeMemberName( memberName )] = resolved.DestinationPath!;
						}
						break;
					case TarEntryType.CharacterDevice:
					case TarEntryType.BlockDevice:
					case TarEntryType.Fifo:
						throw new IOException( string.Concat( "Refusing to create special file from archive: ", entry.Name ) );
					case TarEntryType.RenamedOrSymlinked:
					case TarEntryType.MultiVolume:
					case TarEntryType.TapeVolume:
						throw new IOException( string.Concat( "Unsupported or unsafe GNU archive member type: ", entry.Name ) );
					default:
						throw new IOException( string.Concat( "Unsupported archive member type ", entry.EntryType, ": ", entry.Name ) );
				}
			} catch ( OverflowException ) {
				hadErrors = true;
				await WriteMemberErrorAsync( memberName, "archive size arithmetic overflow" ).ConfigureAwait( false );
			} catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException ) {
				hadErrors = true;
				await WriteMemberErrorAsync( memberName, exception.Message ).ConfigureAwait( false );
			}
		}
		for ( var index = directoryMetadata.Count - 1; index >= 0; index-- ) {
			try {
				await ApplyMetadataAsync( directoryMetadata[index].Path, directoryMetadata[index].Entry, options, cancellationToken ).ConfigureAwait( false );
			} catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or NotSupportedException ) {
				hadErrors = true;
				await WriteMemberErrorAsync( directoryMetadata[index].Name, exception.Message ).ConfigureAwait( false );
			}
		}
		return hadErrors ? 2 : 0;
	}

	private async Task<int> AppendOrUpdateAsync( TarOptions options, bool update, CancellationToken cancellationToken ) {
		RequireMutableArchive( options );
		if ( options.Operands.Count == 0 ) return 0;
		var archivePath = GetArchivePath( options );
		if ( !File.Exists( archivePath ) ) throw new IOException( string.Concat( "Archive does not exist: ", archivePath ) );
		if ( !options.FormatWasSpecified ) options.Format = await DetectArchiveFormatAsync( archivePath, cancellationToken ).ConfigureAwait( false );
		var latest = update ? await ReadLatestTimesAsync( archivePath, options, cancellationToken ).ConfigureAwait( false ) : null;
		var hadErrors = false;
		await ReplaceArchiveAsync(
			options,
			async (destination, token) => {
				await using var writer = new TarWriter( destination, options.Format, leaveOpen: true );
				await CopyArchiveAsync( archivePath, writer, options, static _ => true, token ).ConfigureAwait( false );
				hadErrors = await WriteInputsAsync( options, writer, latest, token ).ConfigureAwait( false );
			},
			cancellationToken
		).ConfigureAwait( false );
		return hadErrors ? 2 : 0;
	}

	private async Task<int> DeleteAsync( TarOptions options, CancellationToken cancellationToken ) {
		RequireMutableArchive( options );
		if ( options.Operands.Count == 0 ) throw new TarUsageException( "--delete requires at least one member name." );
		var archivePath = GetArchivePath( options );
		if ( !options.FormatWasSpecified ) options.Format = await DetectArchiveFormatAsync( archivePath, cancellationToken ).ConfigureAwait( false );
		await ReplaceArchiveAsync(
			options,
			async (destination, token) => {
				await using var writer = new TarWriter( destination, options.Format, leaveOpen: true );
				await CopyArchiveAsync( archivePath, writer, options, entry => !TarMemberSelection.IsSelected( options, TarSparse.GetLogicalName( entry ) ), token ).ConfigureAwait( false );
			},
			cancellationToken
		).ConfigureAwait( false );
		return 0;
	}

	private async Task<int> ConcatenateAsync( TarOptions options, CancellationToken cancellationToken ) {
		RequireMutableArchive( options );
		if ( options.Operands.Count == 0 ) throw new TarUsageException( "--concatenate requires at least one archive operand." );
		var archivePath = GetArchivePath( options );
		if ( !options.FormatWasSpecified ) options.Format = await DetectArchiveFormatAsync( archivePath, cancellationToken ).ConfigureAwait( false );
		await ReplaceArchiveAsync(
			options,
			async (destination, token) => {
				await using var writer = new TarWriter( destination, options.Format, leaveOpen: true );
				await CopyArchiveAsync( archivePath, writer, options, static _ => true, token ).ConfigureAwait( false );
				foreach ( var operand in options.Operands ) {
					var source = System.IO.Path.GetFullPath( operand.Value, operand.WorkingDirectory );
					if ( PathsEqual( source, archivePath ) ) throw new IOException( "An archive cannot be concatenated with itself." );
					await CopyArchiveAsync( source, writer, options, static _ => true, token ).ConfigureAwait( false );
				}
			},
			cancellationToken
		).ConfigureAwait( false );
		return 0;
	}

	private async Task<int> CompareAsync( TarOptions options, CancellationToken cancellationToken ) {
		var policy = new TarExtractionPolicy( options.WorkingDirectory );
		var differences = false;
		var count = 0;
		await using var input = await TarCompression.OpenReadAsync( options, cancellationToken ).ConfigureAwait( false );
		using var reader = new TarReader( input.Stream, leaveOpen: true );
		TarEntry? entry;
		while ( (entry = reader.GetNextEntry()) is not null ) {
			cancellationToken.ThrowIfCancellationRequested();
			RequireEntryBudget( options, ref count );
			var memberName = TarSparse.GetLogicalName( entry );
			if ( !TarMemberSelection.IsSelected( options, memberName ) ) continue;
			try {
				var resolved = policy.ResolveMember( memberName, options.StripComponents );
				if ( resolved.IsSkipped ) continue;
				if ( !await MemberMatchesAsync( policy, resolved.DestinationPath!, entry, options, cancellationToken ).ConfigureAwait( false ) ) {
					differences = true;
					await stdout.WriteLineAsync( string.Concat( memberName, ": differs" ) ).ConfigureAwait( false );
				}
			} catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException ) {
				differences = true;
				await stdout.WriteLineAsync( string.Concat( memberName, ": ", exception.Message ) ).ConfigureAwait( false );
			}
		}
		return differences ? 1 : 0;
	}

	private async Task<bool> WriteInputArchiveAsync( TarOptions options, Stream destination, IReadOnlyDictionary<string, DateTimeOffset>? updateTimes, CancellationToken cancellationToken ) {
		await using var writer = new TarWriter( destination, options.Format, leaveOpen: true );
		return await WriteInputsAsync( options, writer, updateTimes, cancellationToken ).ConfigureAwait( false );
	}

	private async Task<bool> WriteInputsAsync( TarOptions options, TarWriter writer, IReadOnlyDictionary<string, DateTimeOffset>? updateTimes, CancellationToken cancellationToken ) {
		var roots = options.Operands.Select( (operand, index) => new PathTraversalRoot(
			operand.Value,
			index,
			index,
			System.IO.Path.GetFullPath( operand.Value, operand.WorkingDirectory ),
			operand.Value,
			PathTraversalRootKind.Literal
		) ).ToArray();
		var traversalOptions = new PathTraversalOptions {
			SymbolicLinkMode = options.Dereference ? SymbolicLinkTraversalMode.Always : SymbolicLinkTraversalMode.Never,
			MaximumDepth = options.Recurse ? null : 0,
			MaximumEntriesPerDirectory = options.MaximumEntries,
			ErrorMode = PathTraversalErrorMode.Continue
		};
		var hadErrors = false;
		var count = 0;
		var hardLinkTargets = new Dictionary<(FileSystemIdentity FileSystem, FileSystemEntryIdentity Entry), string>();
		var archivePath = options.ArchiveName == "-" ? null : GetArchivePath( options );
		await foreach ( var item in traversal.TraverseAsync( roots, traversalOptions, cancellationToken ).ConfigureAwait( false ) ) {
			cancellationToken.ThrowIfCancellationRequested();
			if ( item.Kind == PathTraversalEventKind.Error ) {
				hadErrors = true;
				var error = item.Error!;
				await WriteMemberErrorAsync( error.Path, error.Exception?.Message ?? error.Message ).ConfigureAwait( false );
				continue;
			}
			if ( item.Kind == PathTraversalEventKind.Cycle ) {
				hadErrors = true;
				await WriteMemberErrorAsync( item.Entry?.DisplayPath ?? item.Root.DisplayPath, "file system cycle detected" ).ConfigureAwait( false );
				continue;
			}
			if ( item.Kind is not (PathTraversalEventKind.EnterDirectory or PathTraversalEventKind.Entry) ) continue;
			var entry = item.Entry!;
			RequireEntryBudget( options, ref count );
			if ( archivePath is not null && PathsEqual( System.IO.Path.GetFullPath( entry.AccessPath ), archivePath ) ) {
				await stderr.WriteLineAsync( string.Concat( "tar: ", entry.DisplayPath, ": file is the archive; not dumped" ) ).ConfigureAwait( false );
				continue;
			}
			var archiveName = GetArchiveName( options, entry );
			if ( TarMemberSelection.IsExcluded( options, archiveName ) ) continue;
			if ( updateTimes is not null && !ShouldUpdate( entry.AccessPath, archiveName, updateTimes ) ) continue;
			try {
				if ( options.Verbose ) await stdout.WriteLineAsync( archiveName ).ConfigureAwait( false );
				if ( entry.Kind == FileSystemEntryKind.File
					&& TryGetHardLinkTarget( entry, archiveName, hardLinkTargets, out var hardLinkTarget ) ) {
					var hardLink = CreateHardLinkEntry( options.Format, entry.AccessPath, archiveName, hardLinkTarget );
					await writer.WriteEntryAsync( hardLink, cancellationToken ).ConfigureAwait( false );
					continue;
				}
				if ( options.Sparse && entry.Kind == FileSystemEntryKind.File ) {
					var sparse = await TarSparse.TryCreateEntryAsync( entry.AccessPath, archiveName, cancellationToken ).ConfigureAwait( false );
					if ( sparse is not null ) {
						await writer.WriteEntryAsync( sparse, cancellationToken ).ConfigureAwait( false );
						RememberHardLinkTarget( entry, archiveName, hardLinkTargets );
						continue;
					}
				}
				await writer.WriteEntryAsync( entry.AccessPath, archiveName, cancellationToken ).ConfigureAwait( false );
				RememberHardLinkTarget( entry, archiveName, hardLinkTargets );
			} catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException ) {
				hadErrors = true;
				await WriteMemberErrorAsync( entry.DisplayPath, exception.Message ).ConfigureAwait( false );
			}
		}
		return hadErrors;
	}

	private async Task CopyArchiveAsync( string sourcePath, TarWriter writer, TarOptions options, Func<TarEntry, bool> predicate, CancellationToken cancellationToken ) {
		var info = new FileInfo( sourcePath );
		if ( info.Length > options.MaximumArchiveBytes ) throw new IOException( string.Concat( "Archive exceeds --max-archive-bytes: ", sourcePath ) );
		await using var source = new FileStream( sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan );
		using var reader = new TarReader( source, leaveOpen: true );
		var count = 0;
		TarEntry? entry;
		while ( (entry = reader.GetNextEntry()) is not null ) {
			cancellationToken.ThrowIfCancellationRequested();
			RequireEntryBudget( options, ref count );
			if ( predicate( entry ) ) await writer.WriteEntryAsync( entry, cancellationToken ).ConfigureAwait( false );
		}
	}

	private async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadLatestTimesAsync( string archivePath, TarOptions options, CancellationToken cancellationToken ) {
		var result = new Dictionary<string, DateTimeOffset>( StringComparer.Ordinal );
		await using var source = new FileStream( archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan );
		using var reader = new TarReader( source, leaveOpen: true );
		var count = 0;
		TarEntry? entry;
		while ( (entry = reader.GetNextEntry()) is not null ) {
			cancellationToken.ThrowIfCancellationRequested();
			RequireEntryBudget( options, ref count );
			result[NormalizeMemberName( TarSparse.GetLogicalName( entry ) )] = entry.ModificationTime;
		}
		return result;
	}

	private static bool ShouldUpdate( string path, string archiveName, IReadOnlyDictionary<string, DateTimeOffset> latest ) {
		if ( !latest.TryGetValue( NormalizeMemberName( archiveName ), out var archiveTime ) ) return true;
		DateTimeOffset fileTime = Directory.Exists( path ) ? Directory.GetLastWriteTimeUtc( path ) : File.GetLastWriteTimeUtc( path );
		return fileTime > archiveTime;
	}

	private async Task<TarEntryFormat> DetectArchiveFormatAsync( string archivePath, CancellationToken cancellationToken ) {
		await using var source = new FileStream( archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan );
		using var reader = new TarReader( source, leaveOpen: true );
		cancellationToken.ThrowIfCancellationRequested();
		return reader.GetNextEntry()?.Format ?? TarEntryFormat.Pax;
	}

	private static bool TryGetHardLinkTarget(
		PathTraversalEntry entry,
		string archiveName,
		IReadOnlyDictionary<(FileSystemIdentity FileSystem, FileSystemEntryIdentity Entry), string> targets,
		out string target
	) {
		target = string.Empty;
		if ( !entry.FileSystemIdentity.IsAvailable || !entry.EntryIdentity.IsAvailable ) return false;
		if ( !targets.TryGetValue( (entry.FileSystemIdentity, entry.EntryIdentity), out var firstName ) ) return false;
		if ( string.Equals( NormalizeMemberName( archiveName ), NormalizeMemberName( firstName ), StringComparison.Ordinal ) ) return false;
		target = firstName;
		return true;
	}

	private static void RememberHardLinkTarget(
		PathTraversalEntry entry,
		string archiveName,
		Dictionary<(FileSystemIdentity FileSystem, FileSystemEntryIdentity Entry), string> targets
	) {
		if ( !entry.FileSystemIdentity.IsAvailable || !entry.EntryIdentity.IsAvailable ) return;
		targets.TryAdd( (entry.FileSystemIdentity, entry.EntryIdentity), archiveName );
	}

	private static TarEntry CreateHardLinkEntry( TarEntryFormat format, string sourcePath, string archiveName, string targetName ) {
		TarEntry hardLink = format switch {
			TarEntryFormat.V7 => new V7TarEntry( TarEntryType.HardLink, archiveName ),
			TarEntryFormat.Ustar => new UstarTarEntry( TarEntryType.HardLink, archiveName ),
			TarEntryFormat.Pax => new PaxTarEntry( TarEntryType.HardLink, archiveName ),
			TarEntryFormat.Gnu => new GnuTarEntry( TarEntryType.HardLink, archiveName ),
			_ => throw new ArgumentOutOfRangeException( nameof( format ) )
		};
		hardLink.LinkName = targetName;
		hardLink.ModificationTime = File.GetLastWriteTimeUtc( sourcePath );
		if ( !OperatingSystem.IsWindows() ) hardLink.Mode = File.GetUnixFileMode( sourcePath );
		return hardLink;
	}

	private async Task ReplaceArchiveAsync( TarOptions options, Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken ) {
		var path = GetArchivePath( options );
		var observation = await replacementFileSystem.ObserveAsync( path, PathDereferenceMode.NoFollow, cancellationToken ).ConfigureAwait( false );
		var precondition = observation.Exists
			? FileSystemMutationPrecondition.FromObservation( observation.Metadata!.Kind, observation.Metadata.EntryIdentity, PathDereferenceMode.NoFollow )
			: FileSystemMutationPrecondition.DestinationMustNotExist();
		var artifact = new TransactionalReplacementArtifact(
			"tar-archive",
			path,
			TransactionalReplacementAction.Replace,
			precondition,
			async (destination, token) => await writer( destination, token ).ConfigureAwait( false )
		);
		var containment = System.IO.Path.GetDirectoryName( path ) ?? System.IO.Path.GetPathRoot( path ) ?? options.InitialDirectory;
		await using var transaction = new TransactionalFileReplacementTransaction(
			new[] { artifact },
			replacementFileSystem,
			new TransactionalReplacementOptions { ContainmentRootPath = containment }
		);
		var result = await transaction.CommitAsync( cancellationToken ).ConfigureAwait( false );
		if ( !result.Succeeded ) {
			var message = result.Diagnostics.Count == 0
				? string.Concat( "Archive replacement failed: ", result.Outcome )
				: string.Join( "; ", result.Diagnostics.Select( diagnostic => diagnostic.Message ) );
			throw new IOException( message );
		}
	}

	private async Task<bool> ExtractRegularFileAsync( TarExtractionPolicy policy, ResolvedTarPath resolved, TarEntry entry, TarOptions options, CancellationToken cancellationToken ) {
		var path = resolved.DestinationPath!;
		policy.EnsureSafeParents( path );
		var observation = await replacementFileSystem.ObserveAsync( path, PathDereferenceMode.NoFollow, cancellationToken ).ConfigureAwait( false );
		if ( observation.Exists ) {
			if ( options.OverwriteMode == TarOverwriteMode.SkipOldFiles ) return false;
			if ( options.OverwriteMode == TarOverwriteMode.KeepOldFiles ) throw new IOException( "File exists and --keep-old-files was specified." );
		}
		var precondition = observation.Exists
			? FileSystemMutationPrecondition.FromObservation( observation.Metadata!.Kind, observation.Metadata.EntryIdentity, PathDereferenceMode.NoFollow )
			: FileSystemMutationPrecondition.DestinationMustNotExist();
		var sparseState = TarSparse.TryGetMap( entry, out var sparseMap, out var sparseError );
		if ( sparseState && sparseError is not null ) throw new IOException( sparseError );
		var artifact = new TransactionalReplacementArtifact(
			string.Concat( "extract:", resolved.RelativeName ),
			path,
			TransactionalReplacementAction.Replace,
			precondition,
			async (destination, token) => {
				if ( sparseMap is not null ) {
					await TarSparse.WriteSparseAsync( entry.DataStream, destination, sparseMap, token ).ConfigureAwait( false );
				} else {
					await CopyEntryDataAsync( entry, destination, token ).ConfigureAwait( false );
				}
			}
		);
		await using var transaction = new TransactionalFileReplacementTransaction(
			new[] { artifact },
			replacementFileSystem,
			new TransactionalReplacementOptions { ContainmentRootPath = policy.Root }
		);
		var result = await transaction.CommitAsync( cancellationToken ).ConfigureAwait( false );
		if ( !result.Succeeded ) {
			throw new IOException( result.Diagnostics.Count == 0 ? string.Concat( "File replacement failed: ", result.Outcome ) : result.Diagnostics[0].Message );
		}
		await ApplyMetadataAsync( path, entry, options, cancellationToken ).ConfigureAwait( false );
		return true;
	}

	private void ExtractDirectory( TarExtractionPolicy policy, ResolvedTarPath resolved, TarEntry entry ) {
		var path = resolved.DestinationPath!;
		policy.EnsureSafeParents( path );
		if ( PathObjectExists( path ) ) {
			var attributes = File.GetAttributes( path );
			if ( (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0 ) {
				throw new IOException( "Directory destination is not a safe physical directory." );
			}
			return;
		}
		Directory.CreateDirectory( path );
	}

	private async Task<bool> ExtractSymbolicLinkAsync( TarExtractionPolicy policy, ResolvedTarPath resolved, TarEntry entry, TarOptions options, CancellationToken cancellationToken ) {
		if ( string.IsNullOrEmpty( entry.LinkName ) ) throw new IOException( "Symbolic link has an empty target." );
		policy.ValidateSymbolicLinkTarget( resolved, entry.LinkName );
		policy.EnsureSafeParents( resolved.DestinationPath! );
		if ( !PrepareLinkDestination( resolved.DestinationPath!, options ) ) return false;
		var targetIsDirectory = entry.LinkName.EndsWith( '/' );
		var result = await mutationFileSystem.CreateSymbolicLinkAsync(
			resolved.DestinationPath!,
			entry.LinkName,
			targetIsDirectory,
			FileSystemMutationPrecondition.DestinationMustNotExist(),
			cancellationToken
		).ConfigureAwait( false );
		RequireMutationSuccess( result );
		return true;
	}

	private async Task<bool> ExtractHardLinkAsync(
		TarExtractionPolicy policy,
		ResolvedTarPath resolved,
		TarEntry entry,
		TarOptions options,
		IReadOnlyDictionary<string, string> extractedNames,
		CancellationToken cancellationToken
	) {
		if ( string.IsNullOrEmpty( entry.LinkName ) ) throw new IOException( "Hard link has an empty target." );
		var normalizedTarget = NormalizeMemberName( entry.LinkName );
		var target = extractedNames.TryGetValue( normalizedTarget, out var alreadyExtracted )
			? alreadyExtracted
			: policy.ResolveHardLinkTarget( entry.LinkName, options.StripComponents );
		policy.RequireSafeExistingTarget( target );
		policy.EnsureSafeParents( resolved.DestinationPath! );
		if ( !PrepareLinkDestination( resolved.DestinationPath!, options ) ) return false;
		var result = await mutationFileSystem.CreateHardLinkAsync(
			resolved.DestinationPath!,
			target,
			PathDereferenceMode.NoFollow,
			FileSystemMutationPrecondition.DestinationMustNotExist(),
			null,
			cancellationToken
		).ConfigureAwait( false );
		RequireMutationSuccess( result );
		return true;
	}

	private static bool PrepareLinkDestination( string path, TarOptions options ) {
		if ( !PathObjectExists( path ) ) return true;
		if ( options.OverwriteMode == TarOverwriteMode.SkipOldFiles ) return false;
		if ( options.OverwriteMode == TarOverwriteMode.KeepOldFiles ) throw new IOException( "Link destination exists and --keep-old-files was specified." );
		var attributes = File.GetAttributes( path );
		if ( (attributes & FileAttributes.ReparsePoint) != 0 ) throw new IOException( "Refusing to replace an existing pathname-indirection object with an archive link." );
		if ( (attributes & FileAttributes.Directory) != 0 ) throw new IOException( "Refusing to replace an existing directory with an archive link." );
		File.Delete( path );
		return true;
	}

	private async Task ApplyMetadataAsync( string path, TarEntry entry, TarOptions options, CancellationToken cancellationToken ) {
		if ( !options.Touch && entry.ModificationTime != default ) {
			var attributes = File.GetAttributes( path );
			if ( (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0 ) {
				Directory.SetLastWriteTimeUtc( path, entry.ModificationTime.UtcDateTime );
			} else {
				File.SetLastWriteTimeUtc( path, entry.ModificationTime.UtcDateTime );
			}
		}
		if ( options.PreservePermissions && !OperatingSystem.IsWindows() ) {
			File.SetUnixFileMode( path, entry.Mode );
		}
		if ( options.SameOwner && !OperatingSystem.IsWindows() ) {
			var user = entry.Uid < 0 ? null : checked((uint?)entry.Uid);
			var group = entry.Gid < 0 ? null : checked((uint?)entry.Gid);
			if ( user.HasValue || group.HasValue ) {
				var result = await mutationFileSystem.SetOwnershipAsync( path, user, group, PathDereferenceMode.NoFollow, cancellationToken: cancellationToken ).ConfigureAwait( false );
				RequireMutationSuccess( result );
			}
		}
	}

	private static void RequireMutationSuccess( FileSystemMutationResult result ) {
		if ( result.Succeeded ) return;
		throw new IOException( result.Message ?? string.Concat( "Filesystem mutation failed: ", result.ErrorCode ), result.Exception );
	}

	private static async Task CopyEntryDataAsync( TarEntry entry, Stream destination, CancellationToken cancellationToken ) {
		if ( entry.DataStream is null ) {
			if ( entry.Length != 0 ) throw new InvalidDataException( "Archive member is missing its data stream." );
			return;
		}
		await entry.DataStream.CopyToAsync( destination, 128 * 1024, cancellationToken ).ConfigureAwait( false );
	}

	private async Task<bool> MemberMatchesAsync( TarExtractionPolicy policy, string path, TarEntry entry, TarOptions options, CancellationToken cancellationToken ) {
		if ( !PathObjectExists( path ) ) return false;
		var attributes = File.GetAttributes( path );
		return entry.EntryType switch {
			TarEntryType.Directory or TarEntryType.DirectoryList => (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0,
			TarEntryType.SymbolicLink => (attributes & FileAttributes.ReparsePoint) != 0 && string.Equals( GetLinkTarget( path, attributes ), entry.LinkName, StringComparison.Ordinal ),
			TarEntryType.HardLink => await HardLinkMatchesAsync( policy, path, entry, options, cancellationToken ).ConfigureAwait( false ),
			TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => await RegularFileMatchesAsync( path, entry, cancellationToken ).ConfigureAwait( false ),
			TarEntryType.SparseFile => throw new NotSupportedException( "Legacy GNU sparse-header members are not supported; GNU/PAX sparse 0.1 members are supported." ),
			_ => false
		};
	}

	private async Task<bool> HardLinkMatchesAsync( TarExtractionPolicy policy, string path, TarEntry entry, TarOptions options, CancellationToken cancellationToken ) {
		if ( string.IsNullOrEmpty( entry.LinkName ) ) return false;
		var target = policy.ResolveHardLinkTarget( entry.LinkName, options.StripComponents );
		if ( !File.Exists( target ) ) return false;
		var left = await SystemReadOnlyFileSystemProvider.Instance.ObserveAsync( path, PathDereferenceMode.NoFollow, cancellationToken ).ConfigureAwait( false );
		var right = await SystemReadOnlyFileSystemProvider.Instance.ObserveAsync( target, PathDereferenceMode.NoFollow, cancellationToken ).ConfigureAwait( false );
		return left.EntryIdentity.IsAvailable && right.EntryIdentity.IsAvailable && left.EntryIdentity.Equals( right.EntryIdentity );
	}

	private static async Task<bool> RegularFileMatchesAsync( string path, TarEntry entry, CancellationToken cancellationToken ) {
		if ( Directory.Exists( path ) || (File.GetAttributes( path ) & FileAttributes.ReparsePoint) != 0 ) return false;
		var sparseState = TarSparse.TryGetMap( entry, out var sparseMap, out var sparseError );
		if ( sparseState && sparseError is not null ) throw new InvalidDataException( sparseError );
		if ( sparseMap is not null ) return await TarSparse.CompareSparseAsync( path, entry.DataStream, sparseMap, cancellationToken ).ConfigureAwait( false );
		var info = new FileInfo( path );
		if ( info.Length != entry.Length ) return false;
		if ( entry.DataStream is null ) return entry.Length == 0;
		await using var local = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan );
		return await StreamsEqualAsync( local, entry.DataStream, cancellationToken ).ConfigureAwait( false );
	}

	private static async Task<bool> StreamsEqualAsync( Stream left, Stream right, CancellationToken cancellationToken ) {
		var a = new byte[128 * 1024];
		var b = new byte[128 * 1024];
		while ( true ) {
			var ar = await left.ReadAsync( a, cancellationToken ).ConfigureAwait( false );
			var br = await right.ReadAsync( b.AsMemory( 0, ar == 0 ? b.Length : ar ), cancellationToken ).ConfigureAwait( false );
			if ( ar != br ) return false;
			if ( ar == 0 ) return true;
			if ( !a.AsSpan( 0, ar ).SequenceEqual( b.AsSpan( 0, br ) ) ) return false;
		}
	}

	private static string? GetLinkTarget( string path, FileAttributes attributes ) {
		return (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo( path ).LinkTarget : new FileInfo( path ).LinkTarget;
	}

	private static long GetLogicalLength( TarEntry entry ) {
		var sparseState = TarSparse.TryGetMap( entry, out var sparseMap, out var sparseError );
		if ( sparseState && sparseError is not null ) throw new InvalidDataException( sparseError );
		return sparseMap?.LogicalLength ?? Math.Max( 0, entry.Length );
	}

	private static void RequireEntryBudget( TarOptions options, ref int count ) {
		count = checked(count + 1);
		if ( count > options.MaximumEntries ) throw new IOException( "Archive exceeds --max-entries." );
	}

	private static string GetArchiveName( TarOptions options, PathTraversalEntry entry ) {
		var operand = options.Operands[entry.Root.OperandIndex];
		var rootName = operand.Value.Replace( '\\', '/' );
		if ( !options.AbsoluteNames ) {
			if ( System.IO.Path.IsPathRooted( operand.Value ) ) {
				var full = System.IO.Path.GetFullPath( operand.Value, operand.WorkingDirectory );
				var pathRoot = System.IO.Path.GetPathRoot( full ) ?? string.Empty;
				rootName = full[pathRoot.Length..].Replace( '\\', '/' ).TrimStart( '/' );
			} else {
				rootName = TrimDotSlash( rootName );
			}
		}
		if ( rootName.Length == 0 ) rootName = entry.Name;
		var relative = entry.RelativePath.Replace( '\\', '/' );
		if ( relative.Length == 0 ) return rootName;
		if ( rootName == "." ) return string.Concat( "./", relative );
		return string.Concat( rootName.TrimEnd( '/' ), "/", relative );
	}

	private static string TrimDotSlash( string value ) {
		while ( value.StartsWith( "./", StringComparison.Ordinal ) ) value = value[2..];
		return value;
	}

	private static string NormalizeMemberName( string value ) => TrimDotSlash( value.Replace( '\\', '/' ) ).TrimEnd( '/' );

	private static string FormatVerboseEntry( TarEntry entry, string memberName ) {
		var type = entry.EntryType switch {
			TarEntryType.Directory or TarEntryType.DirectoryList => 'd',
			TarEntryType.SymbolicLink => 'l',
			TarEntryType.HardLink => 'h',
			TarEntryType.CharacterDevice => 'c',
			TarEntryType.BlockDevice => 'b',
			TarEntryType.Fifo => 'p',
			_ => '-'
		};
		var mode = Convert.ToString( (int)entry.Mode & 0x1ff, 8 )!.PadLeft( 3, '0' );
		var link = entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink ? string.Concat( " -> ", entry.LinkName ) : string.Empty;
		return string.Format(
			CultureInfo.InvariantCulture,
			"{0}{1} {2}/{3} {4,12} {5:yyyy-MM-dd HH:mm} {6}{7}",
			type,
			mode,
			entry.Uid,
			entry.Gid,
			entry.Length,
			entry.ModificationTime,
			memberName,
			link
		);
	}

	private static void RequireMutableArchive( TarOptions options ) {
		if ( options.ArchiveName == "-" ) throw new TarUsageException( "This operation requires a named seekable archive." );
		if ( TarCompression.ResolveReadKind( options ) != TarCompressionKind.None ) {
			throw new TarUsageException( "Cannot append to, update, delete from, or concatenate a compressed archive." );
		}
	}

	private static string GetArchivePath( TarOptions options ) => System.IO.Path.GetFullPath( options.ArchiveName!, options.InitialDirectory );

	private async Task WriteMemberErrorAsync( string name, string message ) {
		await stderr.WriteLineAsync( string.Concat( "tar: ", name, ": ", message ) ).ConfigureAwait( false );
	}

	private static bool PathsEqual( string left, string right ) => string.Equals(
		System.IO.Path.GetFullPath( left ),
		System.IO.Path.GetFullPath( right ),
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
	);

	private static bool PathObjectExists( string path ) {
		try { _ = File.GetAttributes( path ); return true; }
		catch ( FileNotFoundException ) { return false; }
		catch ( DirectoryNotFoundException ) { return false; }
	}
}
