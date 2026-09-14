namespace Claustrum.Casts;

public sealed class CastException(string message, Exception? innerException = null) : Exception(message, innerException);
