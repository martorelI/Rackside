namespace Rackside.Transport.Console;

/// <summary>An operation on the switch failed. The message is already translated and masked.</summary>
public class SwitchException : Exception
{
    public SwitchException()
    {
    }

    public SwitchException(string message) : base(message)
    {
    }

    public SwitchException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The switch asked something the step did not expect: it was answered NO and the operation stopped.</summary>
public sealed class UnexpectedQuestionException(string message) : SwitchException(message);

/// <summary>The operator did not accept the SSH fingerprint: in a sweep, that switch is SKIPPED.</summary>
public sealed class FingerprintRejectedException(string message) : SwitchException(message);
