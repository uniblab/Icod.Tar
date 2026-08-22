namespace Icod.Tar;

using System.Text;
using System.Text.RegularExpressions;

internal sealed class TarExtractionPolicy {
	private readonly string root;
	private readonly string rootWithSeparator;
	private readonly StringComparison pathComparison;
	private readonly Dictionary<string, string> caseMap;

	public TarExtractionPolicy( string rootDirectory ) {
		root = System.IO.Path.GetFullPath( rootDirectory );
		Directory.CreateDirectory( root );
		rootWithSeparator = root.EndsWith( System.IO.Path.DirectorySeparatorChar ) ? root : string.Concat( root, System.IO.Path.DirectorySeparatorChar );
		pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		caseMap = new Dictionary<string, string>( OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal );
	}

	public string Root => root;

	public ResolvedTarPath ResolveMember( string archiveName, int stripComponents ) {
		var normalized = NormalizeArchivePath( archiveName, rejectRooted: true );
		var components = normalized.Split( '/', StringSplitOptions.RemoveEmptyEntries );
		if ( stripComponents >= components.Length ) {
			return ResolvedTarPath.Skipped( archiveName );
		}
		var relative = string.Join( '/', components.Skip( stripComponents ) );
		if ( relative.Length == 0 ) return ResolvedTarPath.Skipped( archiveName );
		RegisterCaseMapping( archiveName, relative );
		var platformRelative = relative.Replace( '/', System.IO.Path.DirectorySeparatorChar );
		var destination = System.IO.Path.GetFullPath( System.IO.Path.Combine( root, platformRelative ) );
		RequireContained( destination, archiveName );
		return new ResolvedTarPath( archiveName, relative, destination, false );
	}

	public string ResolveHardLinkTarget( string linkName, int stripComponents ) {
		var resolved = ResolveMember( linkName, stripComponents );
		if ( resolved.IsSkipped ) throw new IOException( string.Concat( "Hard-link target was removed by --strip-components: ", linkName ) );
		return resolved.DestinationPath!;
	}

	public void ValidateSymbolicLinkTarget( ResolvedTarPath link, string target ) {
		if ( string.IsNullOrEmpty( target ) ) throw new IOException( string.Concat( "Empty symbolic-link target for ", link.ArchiveName ) );
		var normalizedTarget = NormalizeSymbolicLinkTarget( target );
		var parent = GetArchiveDirectoryName( link.RelativeName! );
		var stack = new List<string>();
		if ( parent.Length > 0 ) stack.AddRange( parent.Split( '/', StringSplitOptions.RemoveEmptyEntries ) );
		foreach ( var component in normalizedTarget.Split( '/', StringSplitOptions.RemoveEmptyEntries ) ) {
			if ( component == "." ) continue;
			if ( component == ".." ) {
				if ( stack.Count == 0 ) throw new IOException( string.Concat( "Symbolic link escapes extraction root: ", link.ArchiveName, " -> ", target ) );
				stack.RemoveAt( stack.Count - 1 );
				continue;
			}
			stack.Add( component );
		}
	}

	public void EnsureSafeParents( string destinationPath ) {
		RequireContained( destinationPath, destinationPath );
		var parent = System.IO.Path.GetDirectoryName( destinationPath );
		if ( string.IsNullOrEmpty( parent ) ) return;
		var relative = System.IO.Path.GetRelativePath( root, parent );
		if ( relative == "." ) return;
		var current = root;
		foreach ( var component in relative.Split( new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries ) ) {
			current = System.IO.Path.Combine( current, component );
			if ( PathObjectExists( current ) ) {
				var attributes = File.GetAttributes( current );
				if ( (attributes & FileAttributes.ReparsePoint) != 0 ) {
					throw new IOException( string.Concat( "Extraction parent is pathname indirection: ", current ) );
				}
				if ( (attributes & FileAttributes.Directory) == 0 ) {
					throw new IOException( string.Concat( "Extraction parent is not a directory: ", current ) );
				}
			} else {
				Directory.CreateDirectory( current );
			}
		}
	}

	public void RequireSafeExistingTarget( string path ) {
		RequireContained( path, path );
		EnsureSafeParents( path );
		if ( !PathObjectExists( path ) ) throw new FileNotFoundException( "Hard-link target has not been extracted.", path );
		if ( (File.GetAttributes( path ) & FileAttributes.ReparsePoint) != 0 ) throw new IOException( string.Concat( "Hard-link target is pathname indirection: ", path ) );
	}

	private void RegisterCaseMapping( string archiveName, string relative ) {
		if ( caseMap.TryGetValue( relative, out var previous ) && !string.Equals( previous, relative, StringComparison.Ordinal ) ) {
			throw new IOException( string.Concat( "Archive members collide on this filesystem: '", previous, "' and '", archiveName, "'." ) );
		}
		caseMap[relative] = relative;
	}

	private void RequireContained( string fullPath, string display ) {
		var normalized = System.IO.Path.GetFullPath( fullPath );
		if ( string.Equals( normalized, root, pathComparison ) ) return;
		if ( !normalized.StartsWith( rootWithSeparator, pathComparison ) ) {
			throw new IOException( string.Concat( "Archive member escapes extraction root: ", display ) );
		}
	}

	private static string NormalizeArchivePath( string value, bool rejectRooted ) {
		if ( string.IsNullOrEmpty( value ) ) throw new IOException( "Archive contains an empty member name." );
		if ( value.Contains( '\0' ) ) throw new IOException( "Archive member contains a NUL character." );
		var normalized = value.Replace( '\\', '/' );
		if ( rejectRooted && IsArchiveRooted( normalized ) ) throw new IOException( string.Concat( "Refusing rooted archive pathname: ", value ) );
		var result = new List<string>();
		foreach ( var component in normalized.Split( '/', StringSplitOptions.RemoveEmptyEntries ) ) {
			if ( component == "." ) continue;
			if ( component == ".." ) throw new IOException( string.Concat( "Refusing '..' in archive pathname: ", value ) );
			if ( component.Contains( ':' ) && result.Count == 0 ) throw new IOException( string.Concat( "Refusing platform-root-like archive pathname: ", value ) );
			result.Add( component );
		}
		return string.Join( '/', result );
	}

	private static string NormalizeSymbolicLinkTarget( string value ) {
		if ( string.IsNullOrEmpty( value ) ) throw new IOException( "Archive contains an empty link target." );
		if ( value.Contains( '\0' ) ) throw new IOException( "Archive link target contains a NUL character." );
		var normalized = value.Replace( '\\', '/' );
		if ( IsArchiveRooted( normalized ) ) throw new IOException( string.Concat( "Refusing rooted symbolic-link target: ", value ) );
		var first = normalized.Split( '/', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault();
		if ( first is not null && first.Contains( ':' ) ) throw new IOException( string.Concat( "Refusing platform-root-like symbolic-link target: ", value ) );
		return normalized;
	}

	private static bool IsArchiveRooted( string value ) {
		if ( value.StartsWith( '/' ) || value.StartsWith( "//", StringComparison.Ordinal ) ) return true;
		return value.Length >= 2 && char.IsAsciiLetter( value[0] ) && value[1] == ':';
	}

	private static string GetArchiveDirectoryName( string name ) {
		var slash = name.LastIndexOf( '/' );
		return slash < 0 ? string.Empty : name[..slash];
	}

	private static bool PathObjectExists( string path ) {
		try { _ = File.GetAttributes( path ); return true; }
		catch ( FileNotFoundException ) { return false; }
		catch ( DirectoryNotFoundException ) { return false; }
	}
}

internal sealed record ResolvedTarPath(
	string ArchiveName,
	string? RelativeName,
	string? DestinationPath,
	bool IsSkipped
) {
	public static ResolvedTarPath Skipped( string archiveName ) => new( archiveName, null, null, true );
}

internal static class TarMemberSelection {
	public static bool IsSelected( TarOptions options, string name ) {
		var normalized = TrimDotSlash( name.Replace( '\\', '/' ) );
		if ( options.Exclusions.Any( pattern => MatchPattern( normalized, pattern ) ) ) return false;
		if ( options.Operation is TarOperation.Create or TarOperation.Append or TarOperation.Update or TarOperation.Concatenate ) return true;
		if ( options.Operands.Count == 0 ) return true;
		return options.Operands.Any( operand => {
			var selected = TrimDotSlash( operand.Value.Replace( '\\', '/' ) ).TrimEnd( '/' );
			return string.Equals( normalized.TrimEnd( '/' ), selected, StringComparison.Ordinal )
				|| normalized.StartsWith( string.Concat( selected, "/" ), StringComparison.Ordinal );
		} );
	}

	public static bool IsExcluded( TarOptions options, string name ) => options.Exclusions.Any( pattern => MatchPattern( name.Replace( '\\', '/' ), pattern ) );

	private static string TrimDotSlash( string value ) {
		while ( value.StartsWith( "./", StringComparison.Ordinal ) ) value = value[2..];
		return value;
	}

	private static bool MatchPattern( string value, string pattern ) {
		var normalizedPattern = pattern.Replace( '\\', '/' );
		var regex = new StringBuilder( "^" );
		foreach ( var character in normalizedPattern ) {
			switch ( character ) {
				case '*': regex.Append( "[^/]*" ); break;
				case '?': regex.Append( "[^/]" ); break;
				default: regex.Append( Regex.Escape( character.ToString() ) ); break;
			}
		}
		regex.Append( '$' );
		if ( Regex.IsMatch( value, regex.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 1 ) ) ) return true;
		if ( !normalizedPattern.Contains( '/' ) ) {
			var slash = value.LastIndexOf( '/' );
			var basename = slash >= 0 ? value[(slash + 1)..] : value;
			return Regex.IsMatch( basename, regex.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 1 ) );
		}
		return false;
	}
}
