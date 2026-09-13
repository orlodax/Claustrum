namespace Claustrum.Core.Config;

public sealed class ConfigException(string message, Exception? innerException = null) : Exception(message, innerException);
