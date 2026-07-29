using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   Filesystem primitives every store in this plugin shares. The rules they encode are the
///   filesystem equivalents of what the reference backends get from their protocols: IMAP's APPEND
///   is one atomic command and the local store writes inside a transaction, whereas a naive
///   <c>File.WriteAllBytes</c> onto a live path is observable half-written — by a concurrent read
///   AND by the change watcher, which would sync a truncated message and then "delete" it.
/// </summary>
internal static class AtomicFile
{
	/// <summary>Prefix of the in-flight temp files every write goes through.</summary>
	public const string TempPrefix = ".eas-tmp-";

	private static readonly int[] RetryDelaysMs = [25, 75, 150];

	/// <summary>
	///   Whether a file name is an item rather than plumbing: dotfiles (our temp files, editor
	///   swap files, a Kubernetes projected volume's "..data") are never items. A half-written
	///   temp file that showed up in an enumeration would read as a real item and then, once
	///   renamed away, as a delete to every device.
	/// </summary>
	public static bool IsItemFileName(string fileName)
	{
		return fileName.Length > 0 && fileName[0] != '.';
	}

	/// <summary>Enumerates a directory's item files with the given extension, newest first not guaranteed.</summary>
	public static IEnumerable<FileInfo> EnumerateItemFiles(DirectoryInfo directory, string extension)
	{
		return directory
			.EnumerateFiles("*" + extension, SearchOption.TopDirectoryOnly)
			.Where(file => IsItemFileName(file.Name));
	}

	/// <summary>Writes bytes to <paramref name="path" /> atomically: temp file in the same directory, then rename.</summary>
	/// <remarks>
	///   The temp file MUST live in the destination's own directory — <see cref="File.Move(string,string,bool)" />
	///   is only atomic within one volume, and %TEMP% routinely is another one.
	/// </remarks>
	public static async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
	{
		string directory = Path.GetDirectoryName(path)
		                   ?? throw new BackendException($"local-files: '{path}' has no directory.");
		Directory.CreateDirectory(directory);
		string temp = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N"));
		try
		{
			// WriteThrough rather than a synchronous Flush(true): it reaches the disk without a
			// sync-over-async call, which the repository's analyzer rules forbid outright.
			await using (FileStream stream = new(
				             temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
				             FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(content, ct).ConfigureAwait(false);
				await stream.FlushAsync(ct).ConfigureAwait(false);
			}

			File.Move(temp, path, true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new BackendException($"local-files: could not write '{path}': {ex.Message}", ex);
		}
		finally
		{
			TryDelete(temp);
		}
	}

	/// <summary>Writes UTF-8 text atomically. See <see cref="WriteAsync" />.</summary>
	public static Task WriteTextAsync(string path, string text, CancellationToken ct)
	{
		return WriteAsync(path, System.Text.Encoding.UTF8.GetBytes(text), ct);
	}

	/// <summary>
	///   Reads a file whole, tolerating a writer that holds it open. Returns <c>null</c> when the
	///   file is gone — which the contract's "null = not fetched" rule turns into a retry next
	///   round rather than a delete.
	/// </summary>
	public static async Task<byte[]?> TryReadAllBytesAsync(string path, CancellationToken ct)
	{
		for (int attempt = 0; ; attempt++)
			try
			{
				await using FileStream stream = new(
					path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
					FileOptions.Asynchronous | FileOptions.SequentialScan);
				using MemoryStream buffer = new((int)Math.Min(stream.Length, int.MaxValue));
				await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
				return buffer.ToArray();
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				return null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
			                           && attempt < RetryDelaysMs.Length)
			{
				// Another process is mid-write (on Windows even a read needs the share flags to
				// line up). Back off briefly rather than reporting the item as gone.
				await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
			}
	}

	/// <summary>Reads a file whole as UTF-8 text; <c>null</c> when it is gone. See <see cref="TryReadAllBytesAsync" />.</summary>
	public static async Task<string?> TryReadAllTextAsync(string path, CancellationToken ct)
	{
		byte[]? bytes = await TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
		// A hand-edited file may carry a UTF-8 BOM; the payload classes are stored verbatim, so
		// strip it here rather than letting it reach an iCalendar/vCard parser downstream.
		return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
	}

	/// <summary>
	///   Stats a file, retrying a transient sharing failure. Throws <see cref="BackendException" />
	///   when it still cannot be read: a revision map is the WHOLE truth for its folder, so a file
	///   silently dropped from an enumeration is a delete pushed to every device.
	/// </summary>
	public static async Task<(long Length, long WriteTicks)?> StatAsync(string path, CancellationToken ct)
	{
		for (int attempt = 0; ; attempt++)
		{
			FileInfo info = new(path);
			try
			{
				info.Refresh();
				if (!info.Exists)
					return null;
				return (info.Length, info.LastWriteTimeUtc.Ticks);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				if (attempt >= RetryDelaysMs.Length)
					throw new BackendException($"local-files: could not stat '{path}': {ex.Message}", ex);
				await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Renames within one directory, retrying a transient failure. False when the source vanished.</summary>
	public static async Task<bool> MoveAsync(string source, string destination, CancellationToken ct)
	{
		for (int attempt = 0; ; attempt++)
			try
			{
				File.Move(source, destination, true);
				return true;
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				return false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				if (attempt >= RetryDelaysMs.Length)
					throw new BackendException(
						$"local-files: could not move '{source}' to '{destination}': {ex.Message}", ex);
				await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
			}
	}

	/// <summary>Deletes a file, tolerating its absence.</summary>
	public static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort: a leftover temp file is inert (enumerations skip dotfiles).
		}
	}
}
