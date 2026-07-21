using ActiveSync.Core.Options;
using ActiveSync.Core.State;

namespace ActiveSync.Server.Eas;

/// <summary>
///   The one place that answers "may this client write into this folder?".
///   Two independent sources say no:
///   <list type="bullet">
///     <item>
///       <description>
///         <see cref="ActiveSyncOptions.ReadOnly" /> — global mirror mode, every write suppressed.
///       </description>
///     </item>
///     <item>
///       <description>
///         <see cref="Contracts.IBackendSession.IsReadOnlyFolder" /> — a per-folder grant
///         (shared collections; the owning store opts in via <c>IReadOnlyCollectionSource</c>).
///       </description>
///     </item>
///   </list>
///   The second used to be consulted only by <c>SyncHandler</c>, so every other mutating
///   command wrote straight through a read-only grant. Both are checked together here so a
///   handler cannot honour one and forget the other.
/// </summary>
internal static class WritePermission
{
	/// <summary>True when a client write into this folder must be refused.</summary>
	public static bool IsBlocked(EasContext context, ActiveSyncOptions options, string folderBackendKey)
	{
		return options.ReadOnly || context.Session.IsReadOnlyFolder(folderBackendKey);
	}

	/// <summary>True when a client write into this folder must be refused.</summary>
	public static bool IsBlocked(EasContext context, ActiveSyncOptions options, UserFolder folder)
	{
		return IsBlocked(context, options, folder.BackendKey);
	}
}
