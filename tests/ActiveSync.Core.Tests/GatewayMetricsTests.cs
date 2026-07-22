using System.Collections;
using System.Diagnostics.Metrics;
using System.Reflection;
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
			    instrument.Name is "activesync_eas_requests" or "activesync_eas_request_duration_seconds")
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

	// K4: every instrument sits under the activesync_ namespace.
	[Fact]
	public void Instruments_AreNamespaced()
	{
		List<string> names = [];
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, _) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName)
				names.Add(instrument.Name);
		};
		listener.Start();
		GatewayMetrics.RecordEasRequest("Sync", 200, "-", 0.01); // touch the meter so instruments publish

		Assert.NotEmpty(names);
		Assert.All(names, n => Assert.StartsWith("activesync_", n));
	}

	// K4: the duration histogram carries the HTTP status dimension (it used to drop it).
	[Fact]
	public void RecordEasRequest_DurationHistogram_CarriesStatus()
	{
		bool sawStatus = false;
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName &&
			    instrument.Name == "activesync_eas_request_duration_seconds")
				l.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
		{
			foreach (KeyValuePair<string, object?> tag in tags)
				if (tag.Key == "status" && Equals(tag.Value, 200))
					sawStatus = true;
		});
		listener.Start();

		GatewayMetrics.RecordEasRequest("Sync", 200, "-", 0.01);

		Assert.True(sawStatus, "the duration histogram must record a status tag");
	}

	// K5: throttle rejections are tagged by source so EAS and WebUi are distinguishable.
	[Fact]
	public void RecordThrottleRejection_CarriesSource()
	{
		string? source = null;
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName &&
			    instrument.Name == "activesync_auth_throttle_rejections")
				l.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
		{
			foreach (KeyValuePair<string, object?> tag in tags)
				if (tag.Key == "source")
					source = tag.Value?.ToString();
		});
		listener.Start();

		GatewayMetrics.RecordThrottleRejection("eas");

		Assert.Equal("eas", source);
	}

	// K5 (coverage — additive capability): auth outcomes are recorded with source + outcome.
	[Fact]
	public void RecordAuthOutcome_RecordsSourceAndOutcome()
	{
		List<(string? Source, string? Outcome)> seen = [];
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName &&
			    instrument.Name == "activesync_auth_outcomes")
				l.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
		{
			string? source = null, outcome = null;
			foreach (KeyValuePair<string, object?> tag in tags)
			{
				if (tag.Key == "source") source = tag.Value?.ToString();
				if (tag.Key == "outcome") outcome = tag.Value?.ToString();
			}

			seen.Add((source, outcome));
		});
		listener.Start();

		GatewayMetrics.RecordAuthOutcome("eas", "success", "-");
		GatewayMetrics.RecordAuthOutcome("eas", "failure", "-");

		Assert.Contains(("eas", "success"), seen);
		Assert.Contains(("eas", "failure"), seen);
	}

	// K5 (coverage — additive capability): the TLS-expiry gauge reflects the wired observer.
	[Fact]
	public void CertificateExpiryGauge_ReflectsTheObserver()
	{
		GatewayMetrics.SetCertificateExpiryObserver(() => DateTimeOffset.UtcNow.AddHours(1));
		double? captured = null;
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName &&
			    instrument.Name == "activesync_tls_certificate_expiry_seconds")
				l.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => captured = measurement);
		listener.Start();
		listener.RecordObservableInstruments();

		Assert.NotNull(captured);
		Assert.InRange(captured!.Value, 3000, 3600); // ~1 hour of seconds, minus test time
	}

	// K2: a disposed long-poll scope removes its dictionary entry, not just zeroes it — otherwise
	// the map keeps one dead slot per distinct user for the process lifetime.
	[Fact]
	public void TrackLongPoll_Dispose_RemovesTheEntry()
	{
		IDictionary dict = (IDictionary)typeof(GatewayMetrics)
			.GetField("ActiveLongPolls", BindingFlags.NonPublic | BindingFlags.Static)!
			.GetValue(null)!;
		GatewayMetrics.PerUserLabels = true;
		string user = "k2-" + Guid.NewGuid().ToString("N");

		IDisposable scope = GatewayMetrics.TrackLongPoll(user);
		Assert.True(dict.Contains(user));
		scope.Dispose();

		Assert.False(dict.Contains(user));
	}
}
