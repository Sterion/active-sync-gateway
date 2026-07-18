namespace ActiveSync.Protocol;

/// <summary>
///   A parsed EAS protocol version ("14.1", "16.0", …), comparable so handlers can gate
///   16.x behavior with <c>context.Version >= EasVersion.V160</c>. Unparsable input maps
///   to <see cref="V141" /> — the wire default this gateway has always assumed.
/// </summary>
public readonly record struct EasVersion(int Major, int Minor) : IComparable<EasVersion>
{
	public static readonly EasVersion V121 = new(12, 1);
	public static readonly EasVersion V140 = new(14, 0);
	public static readonly EasVersion V141 = new(14, 1);
	public static readonly EasVersion V160 = new(16, 0);
	public static readonly EasVersion V161 = new(16, 1);

	public static EasVersion Parse(string? value)
	{
		if (value is null)
			return V141;
		int dot = value.IndexOf('.');
		if (dot <= 0 ||
		    !int.TryParse(value.AsSpan(0, dot), out int major) ||
		    !int.TryParse(value.AsSpan(dot + 1), out int minor))
			return V141;
		return new EasVersion(major, minor);
	}

	public int CompareTo(EasVersion other)
	{
		int major = Major.CompareTo(other.Major);
		return major != 0 ? major : Minor.CompareTo(other.Minor);
	}

	public static bool operator <(EasVersion left, EasVersion right) => left.CompareTo(right) < 0;
	public static bool operator >(EasVersion left, EasVersion right) => left.CompareTo(right) > 0;
	public static bool operator <=(EasVersion left, EasVersion right) => left.CompareTo(right) <= 0;
	public static bool operator >=(EasVersion left, EasVersion right) => left.CompareTo(right) >= 0;

	public override string ToString() => $"{Major}.{Minor}";
}
