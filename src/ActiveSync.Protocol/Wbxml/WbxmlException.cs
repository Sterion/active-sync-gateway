namespace ActiveSync.Protocol.Wbxml;

public sealed class WbxmlException : Exception
{
	public WbxmlException(string message) : base(message)
	{
	}

	public WbxmlException(string message, Exception inner) : base(message, inner)
	{
	}
}
