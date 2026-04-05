namespace PEPacker;

/// <summary>
/// Exception thrown by PE Packer operations.
/// </summary>
public class PEPackerException : Exception
{
    public PEPackerException(string message) : base(message) { }
    public PEPackerException(string message, Exception innerException) : base(message, innerException) { }
}
