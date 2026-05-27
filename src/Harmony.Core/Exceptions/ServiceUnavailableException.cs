using System;

namespace Harmony.Core.Exceptions;

public class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message)
        : base(message) { }
}
