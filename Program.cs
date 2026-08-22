namespace Icod.Tar;

using System;

/// <summary>
/// Provides the <c>tar [OPTION...] [FILE]...</c> process entry point.
/// </summary>
/// <remarks>
/// <para>Usage: <c>tar [OPTION...] [FILE]...</c></para>
/// <para>
/// The command creates or manipulates tar archives. Exactly one primary archive
/// operation is required unless <c>--help</c> or <c>--version</c> is specified.
/// Supported primary operations are create (<c>-c</c>, <c>--create</c>),
/// extract (<c>-x</c>, <c>--extract</c>, <c>--get</c>), list
/// (<c>-t</c>, <c>--list</c>), append (<c>-r</c>, <c>--append</c>), update
/// (<c>-u</c>, <c>--update</c>), delete (<c>--delete</c>), concatenate
/// (<c>-A</c>, <c>--concatenate</c>), and compare
/// (<c>-d</c>, <c>--compare</c>, <c>--diff</c>).
/// </para>
/// <para>
/// Archive control includes <c>-f</c>/<c>--file=ARCHIVE</c>,
/// <c>-C</c>/<c>--directory=DIR</c>, <c>--format=gnu|ustar|pax</c>,
/// gzip (<c>-z</c>/<c>--gzip</c>), bzip2 (<c>-j</c>/<c>--bzip2</c>),
/// xz (<c>-J</c>/<c>--xz</c>), <c>--zstd</c>,
/// <c>--use-compress-program=P</c>, and <c>-a</c>/<c>--auto-compress</c>.
/// </para>
/// <para>
/// Selection and extraction options include <c>--exclude=PATTERN</c>,
/// <c>--strip-components=N</c>, <c>-h</c>/<c>--dereference</c>,
/// <c>--no-recursion</c>, <c>-S</c>/<c>--sparse</c>,
/// <c>-p</c>/<c>--preserve-permissions</c>, <c>--same-owner</c>,
/// <c>-m</c>/<c>--touch</c>, <c>-P</c>/<c>--absolute-names</c>,
/// <c>--keep-old-files</c>, <c>--skip-old-files</c>, <c>--overwrite</c>,
/// and <c>-v</c>/<c>--verbose</c>.
/// </para>
/// <para>
/// Safety extensions include <c>--max-entries=N</c>,
/// <c>--max-extract-bytes=N</c>, and <c>--max-archive-bytes=N</c>.
/// </para>
/// </remarks>
public static class Program
{
	/// <summary>
	/// Runs the GNU-tar-compatible archive command.
	/// </summary>
	/// <param name="args">
	/// The command-line arguments supplied to <c>tar</c>, following the
	/// <c>tar [OPTION...] [FILE]...</c> syntax.
	/// </param>
	/// <returns>
	/// The process exit status: <c>0</c> for success, <c>1</c> when compare mode
	/// finds differences, <c>2</c> for a controlled usage/archive/filesystem
	/// error, or <c>130</c> when the operation is cancelled.
	/// </returns>
	public static int Main(string[] args)
	{
		return Command.Run(args);
	}
}
