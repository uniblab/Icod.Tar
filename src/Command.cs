namespace Icod.Tar;

/// <summary>GNU-tar-compatible archive command front end.</summary>
public static class Command {
	/// <summary>Executes the command synchronously.</summary>
	public static int Run( string[] args, TextReader? stdin = null, TextWriter? stdout = null, TextWriter? stderr = null ) {
		return RunAsync( args, stdin, stdout, stderr ).GetAwaiter().GetResult();
	}

	/// <summary>Executes the command asynchronously.</summary>
	public static async Task<int> RunAsync(
		string[] args,
		TextReader? stdin = null,
		TextWriter? stdout = null,
		TextWriter? stderr = null,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull( args );
		_ = stdin;
		stdout ??= Console.Out;
		stderr ??= Console.Error;
		try {
			var options = TarCommandLine.Parse( args );
			if ( options.Help ) {
				await stdout.WriteAsync( HelpText ).ConfigureAwait( false );
				return 0;
			}
			if ( options.Version ) {
				await stdout.WriteLineAsync( "tar (Icod.Tar) 1.0.2; GNU tar 1.35 compatibility baseline" ).ConfigureAwait( false );
				return 0;
			}
			var engine = new TarArchiveEngine( stdout, stderr );
			return await engine.ExecuteAsync( options, cancellationToken ).ConfigureAwait( false );
		} catch ( TarUsageException exception ) {
			await stderr.WriteLineAsync( string.Concat( "tar: ", exception.Message ) ).ConfigureAwait( false );
			return 2;
		} catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested ) {
			await stderr.WriteLineAsync( "tar: operation cancelled" ).ConfigureAwait( false );
			return 130;
		} catch ( Exception exception ) when (
			exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or ArgumentException
				or NotSupportedException
				or OverflowException
		) {
			await stderr.WriteLineAsync( string.Concat( "tar: ", exception.Message ) ).ConfigureAwait( false );
			return 2;
		}
	}

	private const string HelpText = """
Usage: tar [OPTION...] [FILE]...
Create or manipulate tar archives.

Main operation mode:
  -c, --create                 create a new archive
  -x, --extract, --get         extract files from an archive
  -t, --list                   list archive contents
  -r, --append                 append files to an archive
  -u, --update                 append files newer than archive copies
      --delete                 delete members from an archive
  -A, --concatenate            append tar archives to an archive
  -d, --compare, --diff        compare archive members with the file system

Archive control:
  -f, --file=ARCHIVE           use ARCHIVE
  -C, --directory=DIR          change directory for following operands
      --format=gnu|ustar|pax   select output archive format
  -z, --gzip                   filter through gzip
  -j, --bzip2                  filter through bzip2
  -J, --xz                     filter through xz
      --zstd                   filter through zstd
      --use-compress-program=P use external compressor P without a shell
  -a, --auto-compress          select compression from archive suffix

Selection and extraction:
      --exclude=PATTERN        exclude matching member names
      --strip-components=N     remove N leading name components on extraction
  -h, --dereference            follow file-system links while archiving
      --no-recursion           do not recurse into directories
  -S, --sparse                 write GNU sparse 0.1 metadata in pax archives
  -p, --preserve-permissions   restore archived Unix mode bits
      --same-owner             restore numeric owner/group when supported
  -m, --touch                  do not restore modification times
  -P, --absolute-names         retain leading roots while creating archives
      --keep-old-files         fail instead of replacing existing files
      --skip-old-files         skip existing regular files
      --overwrite              replace existing ordinary files
  -v, --verbose                list processed member names

Safety extensions:
      --max-entries=N          maximum archive/traversal entries (default 1000000)
      --max-extract-bytes=N    maximum logical extraction bytes (default 1 TiB)
      --max-archive-bytes=N    maximum archive/decompressed bytes (default 1 TiB)

      --help                   display this help and exit
      --version                output version information and exit
""";
}
