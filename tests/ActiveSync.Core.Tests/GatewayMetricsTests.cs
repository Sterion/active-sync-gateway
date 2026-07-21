using System.Diagnostics.Metrics;
using ActiveSync.Core.Observability;

namespace ActiveSync.Core.Tests;

public sealed class GatewayMetricsTests
{
	/// <summary>
	///   Collects the "command" tag of every eas_requests / eas_request_duration_seconds
	///   measurement recorded while <paramref name="record" /> runs. Other tests in this
	///   assembly never touch the EAS instruments, so the window is effectively private.
	/// </summary>
	private static List<string> CollectCommandLabels(Action record)
	{
		List<string> commands = [];
		object gate = new();
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName &&
			    instrument.Name is "eas_requests" or "eas_request_duration_seconds")
				l.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Capture(tags));
		listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Capture(tags));
		listener.Start();
		record();
		listener.Dispose();
		return commands;

		void Capture(ReadOnlySpan<KeyValuePair<string, object?>> tags)
		{
			foreach (KeyValuePair<string, object?> tag in tags)
				if (tag.Key == "command")
					lock (gate)
					{
						commands.Add(tag.Value?.ToString() ?? "");
					}
		}
	}

	[Fact]
	public void RecordEasRequest_UnknownCommand_NeverReachesTheLabel()
	{
		// K1/E2: an unauthenticated caller picks the query string, so the raw command text
		// becomes a Prometheus label value — one new time series per distinct string.
		const string evil = "<script>cardinality-bomb-1</script>";
		List<string> commands = CollectCommandLabels(
			() => GatewayMetrics.RecordEasRequest(evil, 401, "-", 0.01));

		Assert.DoesNotContain(evil, commands);
		Assert.Equal(2, commands.Count); // counter + duration histogram
		Assert.All(commands, c => Assert.Equal("other", c));
	}

	[Fact]
	public void RecordEasRequest_KnownCommand_IsNormalizedToItsCanonicalCasing()
	{
		List<string> commands = CollectCommandLabels(
			() => GatewayMetrics.RecordEasRequest("fOlDeRsYnC", 200, "-", 0.01));

		Assert.Equal(2, commands.Count);
		Assert.All(commands, c => Assert.Equal("FolderSync", c));
	}
}
