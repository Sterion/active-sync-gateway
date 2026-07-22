using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F22: MoveItems must patch the previous (replay) snapshot as well as the current one, so a
///   subsequent N-1 replay does not restore the pre-move snapshot and echo the move back to the
///   client that made it.
/// </summary>
public sealed class MoveItemsSnapshotTests : IDisposable
{
	private static readonly XNamespace M = EasNamespaces.Move;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	[Fact]
	public async Task F22_Move_AlsoPatchesPreviousSnapshot()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email),
			new BackendFolder("imap:Archive", "Archive", null, EasFolderType.UserMail, EasClass.Email));
		UserFolder inbox = folders.Single(f => f.BackendKey == "imap:INBOX");
		UserFolder archive = folders.Single(f => f.BackendKey == "imap:Archive");

		Device device = await _harness.State.GetOrCreateDeviceAsync(
			EasHandlerHarness.UserName, "TESTDEVICE01", "TestClient", CancellationToken.None);

		// Two commits so the source collection carries a live previous generation that also holds
		// item "10" (the item about to be moved).
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["10"] = "x" }, 0, CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["10"] = "x" }, 0, CancellationToken.None);

		MoveItemsHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<MoveItemsHandler>.Instance);

		XDocument request = new(new XElement(M + "MoveItems",
			new XElement(M + "Move",
				new XElement(M + "SrcMsgId", $"{inbox.ServerId}:10"),
				new XElement(M + "SrcFldId", inbox.ServerId),
				new XElement(M + "DstFldId", archive.ServerId))));

		await _harness.RunAsync(handler, "MoveItems", request);

		CollectionState? source = await _harness.State.GetCollectionStateAsync(
			device, inbox.ServerId, CancellationToken.None);
		Assert.NotNull(source);

		// The current snapshot loses the moved item (echo suppression for the current key)…
		Assert.DoesNotContain("10", Decompress(source!.SnapshotCompressed).Keys);
		// …and so must the previous generation, or an N-1 replay would restore it and re-Add the
		// move to the very client that performed it.
		Assert.NotNull(source.PreviousSnapshotCompressed);
		Assert.DoesNotContain("10", Decompress(source.PreviousSnapshotCompressed).Keys);
	}

	private static Dictionary<string, string> Decompress(byte[]? compressed)
	{
		if (compressed is null || compressed.Length == 0)
			return new Dictionary<string, string>();
		using MemoryStream input = new(compressed);
		using GZipStream gzip = new(input, CompressionMode.Decompress);
		using MemoryStream output = new();
		gzip.CopyTo(output);
		return JsonSerializer.Deserialize<Dictionary<string, string>>(
			output.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
			?? new Dictionary<string, string>();
	}
}
