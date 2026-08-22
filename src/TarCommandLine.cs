namespace Icod.Tar;

using System.Globalization;
using System.Formats.Tar;

internal enum TarOperation {
	None,
	Create,
	Extract,
	List,
	Append,
	Update,
	Delete,
	Concatenate,
	Compare
}

internal enum TarCompressionKind {
	None,
	GZip,
	BZip2,
	Xz,
	Zstd,
	Custom
}

internal enum TarOverwriteMode {
	Default,
	KeepOldFiles,
	SkipOldFiles,
	Overwrite
}

internal sealed record TarOperand( string Value, string WorkingDirectory );

internal sealed class TarOptions {
	public TarOperation Operation { get; set; }
	public string? ArchiveName { get; set; } = "-";
	public TarEntryFormat Format { get; set; } = TarEntryFormat.Gnu;
	public bool FormatWasSpecified { get; set; }
	public TarCompressionKind Compression { get; set; }
	public string? CustomCompressionProgram { get; set; }
	public bool AutoCompress { get; set; }
	public bool Verbose { get; set; }
	public bool Dereference { get; set; }
	public bool Recurse { get; set; } = true;
	public bool Sparse { get; set; }
	public string SparseVersion { get; set; } = "0.1";
	public bool PreservePermissions { get; set; }
	public bool SameOwner { get; set; }
	public bool Touch { get; set; }
	public bool AbsoluteNames { get; set; }
	public int StripComponents { get; set; }
	public TarOverwriteMode OverwriteMode { get; set; }
	public int MaximumEntries { get; set; } = 1_000_000;
	public long MaximumExtractBytes { get; set; } = 1L << 40;
	public long MaximumArchiveBytes { get; set; } = 1L << 40;
	public bool Help { get; set; }
	public bool Version { get; set; }
	public string InitialDirectory { get; init; } = Directory.GetCurrentDirectory();
	public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();
	public IList<TarOperand> Operands { get; } = new List<TarOperand>();
	public IList<string> Exclusions { get; } = new List<string>();
}

internal sealed class TarUsageException : Exception {
	public TarUsageException( string message ) : base( message ) {
	}
}

internal static class TarCommandLine {
	public static TarOptions Parse( string[] args ) {
		ArgumentNullException.ThrowIfNull( args );
		var options = new TarOptions();
		var normalized = NormalizeOldStyle( args );
		var endOfOptions = false;
		for ( var index = 0; index < normalized.Length; index++ ) {
			var argument = normalized[index];
			if ( endOfOptions || argument.Length == 0 || argument[0] != '-' || argument == "-" ) {
				options.Operands.Add( new TarOperand( argument, options.WorkingDirectory ) );
				continue;
			}
			if ( argument == "--" ) {
				endOfOptions = true;
				continue;
			}
			if ( argument.StartsWith( "--", StringComparison.Ordinal ) ) {
				ParseLong( normalized, ref index, argument, options );
				continue;
			}
			ParseShortCluster( normalized, ref index, argument, options );
		}
		if ( options.Help || options.Version ) {
			return options;
		}
		if ( options.Operation == TarOperation.None ) {
			throw new TarUsageException( "You must specify one of the '-Acdtrux' or '--delete' options." );
		}
		if ( options.Sparse && (options.Operation is TarOperation.Create or TarOperation.Append or TarOperation.Update) ) {
			if ( !options.FormatWasSpecified ) options.Format = TarEntryFormat.Pax;
			if ( options.Format != TarEntryFormat.Pax ) {
				throw new TarUsageException( "--sparse creation currently requires --format=pax." );
			}
		}
		if ( options.Sparse && options.SparseVersion != "0.1" ) {
			throw new TarUsageException( "The implemented sparse writer supports GNU sparse version 0.1 only." );
		}
		if ( options.StripComponents < 0 ) {
			throw new TarUsageException( "--strip-components requires a non-negative value." );
		}
		return options;
	}

	private static string[] NormalizeOldStyle( string[] args ) {
		if ( args.Length == 0 || args[0].Length == 0 || args[0][0] == '-' ) return args;
		var first = args[0];
		if ( !first.Any( character => "Acdtrux".Contains( character ) ) ) return args;
		var normalized = new List<string>();
		var valueIndex = 1;
		foreach ( var option in first ) {
			normalized.Add( string.Concat( "-", option ) );
			if ( option is not ('f' or 'C') ) continue;
			if ( valueIndex >= args.Length ) throw new TarUsageException( string.Concat( "-", option, " requires an argument." ) );
			normalized.Add( args[valueIndex++] );
		}
		for ( ; valueIndex < args.Length; valueIndex++ ) normalized.Add( args[valueIndex] );
		return normalized.ToArray();
	}

	private static void ParseLong( string[] args, ref int index, string argument, TarOptions options ) {
		var equals = argument.IndexOf( '=' );
		var name = equals >= 0 ? argument[..equals] : argument;
		var inlineValue = equals >= 0 ? argument[(equals + 1)..] : null;
		switch ( name ) {
			case "--create": SetOperation( options, TarOperation.Create ); break;
			case "--extract":
			case "--get": SetOperation( options, TarOperation.Extract ); break;
			case "--list": SetOperation( options, TarOperation.List ); break;
			case "--append": SetOperation( options, TarOperation.Append ); break;
			case "--update": SetOperation( options, TarOperation.Update ); break;
			case "--delete": SetOperation( options, TarOperation.Delete ); break;
			case "--concatenate":
			case "--catenate": SetOperation( options, TarOperation.Concatenate ); break;
			case "--compare":
			case "--diff": SetOperation( options, TarOperation.Compare ); break;
			case "--file": options.ArchiveName = RequireValue( args, ref index, name, inlineValue ); break;
			case "--directory": options.WorkingDirectory = ResolveWorkingDirectory( options.WorkingDirectory, RequireValue( args, ref index, name, inlineValue ) ); break;
			case "--format": SetFormat( options, RequireValue( args, ref index, name, inlineValue ) ); break;
			case "--gzip":
			case "--ungzip": SetCompression( options, TarCompressionKind.GZip, null ); break;
			case "--bzip2": SetCompression( options, TarCompressionKind.BZip2, null ); break;
			case "--xz": SetCompression( options, TarCompressionKind.Xz, null ); break;
			case "--zstd": SetCompression( options, TarCompressionKind.Zstd, null ); break;
			case "--use-compress-program": SetCompression( options, TarCompressionKind.Custom, RequireValue( args, ref index, name, inlineValue ) ); break;
			case "--auto-compress": options.AutoCompress = true; break;
			case "--verbose": options.Verbose = true; break;
			case "--dereference": options.Dereference = true; break;
			case "--no-recursion": options.Recurse = false; break;
			case "--sparse": options.Sparse = true; break;
			case "--sparse-version": options.SparseVersion = RequireValue( args, ref index, name, inlineValue ); break;
			case "--preserve-permissions":
			case "--same-permissions": options.PreservePermissions = true; break;
			case "--same-owner": options.SameOwner = true; break;
			case "--touch": options.Touch = true; break;
			case "--absolute-names": options.AbsoluteNames = true; break;
			case "--strip-components": options.StripComponents = ParseNonNegativeInt( RequireValue( args, ref index, name, inlineValue ), name ); break;
			case "--exclude": options.Exclusions.Add( RequireValue( args, ref index, name, inlineValue ) ); break;
			case "--keep-old-files": SetOverwriteMode( options, TarOverwriteMode.KeepOldFiles ); break;
			case "--skip-old-files": SetOverwriteMode( options, TarOverwriteMode.SkipOldFiles ); break;
			case "--overwrite": SetOverwriteMode( options, TarOverwriteMode.Overwrite ); break;
			case "--max-entries": options.MaximumEntries = ParsePositiveInt( RequireValue( args, ref index, name, inlineValue ), name ); break;
			case "--max-extract-bytes": options.MaximumExtractBytes = ParseSize( RequireValue( args, ref index, name, inlineValue ), name ); break;
			case "--max-archive-bytes": options.MaximumArchiveBytes = ParseSize( RequireValue( args, ref index, name, inlineValue ), name ); break;
			case "--help": options.Help = true; break;
			case "--version": options.Version = true; break;
			default: throw new TarUsageException( string.Concat( "unrecognized option '", name, "'" ) );
		}
	}

	private static void ParseShortCluster( string[] args, ref int index, string argument, TarOptions options ) {
		for ( var offset = 1; offset < argument.Length; offset++ ) {
			var option = argument[offset];
			switch ( option ) {
				case 'c': SetOperation( options, TarOperation.Create ); break;
				case 'x': SetOperation( options, TarOperation.Extract ); break;
				case 't': SetOperation( options, TarOperation.List ); break;
				case 'r': SetOperation( options, TarOperation.Append ); break;
				case 'u': SetOperation( options, TarOperation.Update ); break;
				case 'A': SetOperation( options, TarOperation.Concatenate ); break;
				case 'd': SetOperation( options, TarOperation.Compare ); break;
				case 'v': options.Verbose = true; break;
				case 'h': options.Dereference = true; break;
				case 'p': options.PreservePermissions = true; break;
				case 'm': options.Touch = true; break;
				case 'P': options.AbsoluteNames = true; break;
				case 'S': options.Sparse = true; break;
				case 'a': options.AutoCompress = true; break;
				case 'z': SetCompression( options, TarCompressionKind.GZip, null ); break;
				case 'j': SetCompression( options, TarCompressionKind.BZip2, null ); break;
				case 'J': SetCompression( options, TarCompressionKind.Xz, null ); break;
				case 'f': {
					var value = TakeClusterValue( args, ref index, argument, offset, "-f" );
					options.ArchiveName = value;
					return;
				}
				case 'C': {
					var value = TakeClusterValue( args, ref index, argument, offset, "-C" );
					options.WorkingDirectory = ResolveWorkingDirectory( options.WorkingDirectory, value );
					return;
				}
				default: throw new TarUsageException( string.Concat( "invalid option -- '", option, "'" ) );
			}
		}
	}

	private static void SetOperation( TarOptions options, TarOperation operation ) {
		if ( options.Operation != TarOperation.None && options.Operation != operation ) {
			throw new TarUsageException( "You may not specify more than one archive operation." );
		}
		options.Operation = operation;
	}

	private static void SetCompression( TarOptions options, TarCompressionKind kind, string? program ) {
		if ( options.Compression != TarCompressionKind.None && options.Compression != kind ) {
			throw new TarUsageException( "Conflicting compression options were specified." );
		}
		options.Compression = kind;
		options.CustomCompressionProgram = program;
	}

	private static void SetOverwriteMode( TarOptions options, TarOverwriteMode mode ) {
		if ( options.OverwriteMode != TarOverwriteMode.Default && options.OverwriteMode != mode ) {
			throw new TarUsageException( "Conflicting overwrite options were specified." );
		}
		options.OverwriteMode = mode;
	}

	private static void SetFormat( TarOptions options, string value ) {
		options.Format = value.ToLowerInvariant() switch {
			"gnu" => TarEntryFormat.Gnu,
			"ustar" => TarEntryFormat.Ustar,
			"pax" or "posix" => TarEntryFormat.Pax,
			_ => throw new TarUsageException( string.Concat( "Unknown archive format '", value, "'." ) )
		};
		options.FormatWasSpecified = true;
	}

	private static string RequireValue( string[] args, ref int index, string option, string? inlineValue ) {
		if ( inlineValue is not null ) {
			if ( inlineValue.Length == 0 ) {
				throw new TarUsageException( string.Concat( option, " requires a nonempty argument." ) );
			}
			return inlineValue;
		}
		if ( ++index >= args.Length ) {
			throw new TarUsageException( string.Concat( option, " requires an argument." ) );
		}
		return args[index];
	}

	private static string TakeClusterValue( string[] args, ref int index, string cluster, int offset, string option ) {
		if ( offset + 1 < cluster.Length ) {
			return cluster[(offset + 1)..];
		}
		if ( ++index >= args.Length ) {
			throw new TarUsageException( string.Concat( option, " requires an argument." ) );
		}
		return args[index];
	}

	private static string ResolveWorkingDirectory( string current, string value ) {
		try {
			return System.IO.Path.IsPathRooted( value ) ? System.IO.Path.GetFullPath( value ) : System.IO.Path.GetFullPath( value, current );
		}
		catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException or PathTooLongException ) {
			throw new TarUsageException( string.Concat( "Invalid directory '", value, "': ", exception.Message ) );
		}
	}

	private static int ParsePositiveInt( string text, string option ) {
		if ( !int.TryParse( text, NumberStyles.None, CultureInfo.InvariantCulture, out var value ) || value <= 0 ) {
			throw new TarUsageException( string.Concat( option, " requires a positive integer." ) );
		}
		return value;
	}

	private static int ParseNonNegativeInt( string text, string option ) {
		if ( !int.TryParse( text, NumberStyles.None, CultureInfo.InvariantCulture, out var value ) || value < 0 ) {
			throw new TarUsageException( string.Concat( option, " requires a non-negative integer." ) );
		}
		return value;
	}

	internal static long ParseSize( string text, string option ) {
		if ( string.IsNullOrWhiteSpace( text ) ) {
			throw new TarUsageException( string.Concat( option, " requires a size." ) );
		}
		var digitCount = 0;
		while ( digitCount < text.Length && char.IsAsciiDigit( text[digitCount] ) ) {
			digitCount++;
		}
		if ( digitCount == 0 || !long.TryParse( text.AsSpan( 0, digitCount ), NumberStyles.None, CultureInfo.InvariantCulture, out var number ) ) {
			throw new TarUsageException( string.Concat( "Invalid size '", text, "'." ) );
		}
		var suffix = text[digitCount..];
		long multiplier = suffix switch {
			"" => 1,
			"K" or "k" or "KiB" or "kiB" => 1024L,
			"KB" or "kB" => 1000L,
			"M" or "MiB" => 1024L * 1024L,
			"MB" => 1000L * 1000L,
			"G" or "GiB" => 1024L * 1024L * 1024L,
			"GB" => 1000L * 1000L * 1000L,
			"T" or "TiB" => 1024L * 1024L * 1024L * 1024L,
			"TB" => 1000L * 1000L * 1000L * 1000L,
			_ => throw new TarUsageException( string.Concat( "Unknown size suffix in '", text, "'." ) )
		};
		try {
			return checked(number * multiplier);
		}
		catch ( OverflowException ) {
			throw new TarUsageException( string.Concat( "Size '", text, "' is too large." ) );
		}
	}
}
