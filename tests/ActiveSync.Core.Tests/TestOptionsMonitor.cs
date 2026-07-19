using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>Fixed-value <see cref="IOptionsMonitor{T}" /> for constructing options-monitor consumers in tests.</summary>
internal static class TestOptionsMonitor
{
	public static IOptionsMonitor<T> Of<T>(T value) => new Fixed<T>(value);

	private sealed class Fixed<T>(T value) : IOptionsMonitor<T>
	{
		public T CurrentValue => value;
		public T Get(string? name) => value;
		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}
}
