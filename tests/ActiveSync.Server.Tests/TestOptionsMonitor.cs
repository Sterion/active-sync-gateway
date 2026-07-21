using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>Fixed-value <see cref="IOptionsMonitor{T}" /> for constructing options-monitor consumers in tests.</summary>
internal static class TestOptionsMonitor
{
	public static IOptionsMonitor<T> Of<T>(T value) => new Fixed<T>(value);

	/// <summary>Fixed-value <see cref="IOptionsSnapshot{T}" />, for the per-request handlers.</summary>
	public static IOptionsSnapshot<T> SnapshotOf<T>(T value) where T : class => new FixedSnapshot<T>(value);

	private sealed class FixedSnapshot<T>(T value) : IOptionsSnapshot<T> where T : class
	{
		public T Value => value;
		public T Get(string? name) => value;
	}

	private sealed class Fixed<T>(T value) : IOptionsMonitor<T>
	{
		public T CurrentValue => value;
		public T Get(string? name) => value;
		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}

	/// <summary>Monitor whose CurrentValue can be swapped, to prove consumers read it per call (live).</summary>
	public sealed class Mutable<T>(T initial) : IOptionsMonitor<T>
	{
		public T CurrentValue { get; set; } = initial;
		public T Get(string? name) => CurrentValue;
		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}
}
