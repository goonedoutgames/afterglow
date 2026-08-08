namespace Afterglow.Core;

/// <summary>
/// Exclusive hub connection mode. Remote never starts the embedded sidecar.
/// </summary>
public enum BackendMode
{
    Unconfigured = 0,
    Remote = 1,
    Local = 2,
}
