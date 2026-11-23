using System;

namespace LuminaDB.Core
{
    /// <summary>What went wrong, in categories the UI and the Doctor can act on.</summary>
    public enum ErrorKind
    {
        Unknown,
        FolderNotWritable,
        EngineMissing,
        EngineFileMissing,
        DataFolderInitFailed,
        DataFolderBelongsToOtherEngine,
        NoFreePort,
        ServerFailedToStart,
        ServerStartTimeout,
        ServerNotRunning,
        ShutdownFailed,
        UnsupportedWindows,
        DataFolderInUse,
        WrongRootPassword,
        RootPasswordUnknown,
        InvalidInput,
    }

    /// <summary>
    /// An exception whose Message is already written for the user in plain English.
    /// Technical detail goes in <see cref="Detail"/> (e.g. the last lines of the server log).
    /// </summary>
    public sealed class LuminaException : Exception
    {
        public LuminaException(ErrorKind kind, string message, Exception inner = null, string detail = null)
            : base(message, inner)
        {
            Kind = kind;
            Detail = detail;
        }

        public ErrorKind Kind { get; }
        public string Detail { get; }
    }
}
